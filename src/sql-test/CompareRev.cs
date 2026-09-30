using System.Text.RegularExpressions;

namespace SqlTest;

public sealed record CompareRequest(string Rev, string Proc, string CallsPath, bool IncludePrint, bool Verbose, int TimeoutSeconds);

/// <param name="OpenControl">The control connection: X for journal and drops, RestoreX (no deadline) for restores.</param>
/// <param name="RunSide">(db, tran, sql, includePrint, onOpen(spid, kpid)) on a fresh connection.</param>
/// <param name="Sweep">The start-of-run journal sweep; a failure is a warning.</param>
internal sealed record CompareDeps(ISvnSource Svn, SourceLocator Locator, IScratchCompiler Compiler, string RunnerDb,
    Func<(ISqlExec X, ISqlExec RestoreX, IDisposable Conn)> OpenControl,
    Func<string?, bool, string, bool, Action<int, int>, CallRun> RunSide,
    Action? Sweep = null);

/// <param name="Incomplete">Why the stream may lack output the driver dropped; null when complete.</param>
/// <param name="TranEnded">The call ended the outer transaction, so its later writes were not rolled back.</param>
/// <param name="NotExecuted">No error and no rc: the call's exec never ran.</param>
/// <param name="Raised">The batch raised a server error; Incomplete then names it.</param>
internal sealed record CallRun(byte[] Output, int Spid, int Kpid, Exception? Fatal,
                               string? Incomplete = null, bool TranEnded = false, bool NotExecuted = false, bool Raised = false);

/// <summary>R10: runs each call against the proc at an SVN revision and at the working copy, and diffs the output.</summary>
internal static class CompareRev
{
    public const string Placeholder = "{proc}";
    private const string RcVar = "@" + CompareOutput.RcColumn;
    private static readonly Regex ExecProc = new(@"\b(?<exec>exec(?:ute)?\s+)(?<qual>(?<qdb>\w+)\.(?:dbo)?\.)?\{proc\}", RegexOptions.IgnoreCase);

    /// <summary>0 all same, 1 any differs, 2 any setup, svn, compile, timeout, restore or drop failure, or a call not compared.</summary>
    internal static int Run(CompareRequest req, CompareDeps deps, TextWriter report, TextWriter? error = null)
    {
        error ??= report;
        int Fail(string msg) { error.WriteLine($"sql-test: {msg}"); return 2; }

        CallList calls;
        try { calls = CallList.Load(req.CallsPath); }
        catch (CallListException ex) { return Fail(ex.Message); }
        foreach (var c in calls.Calls)
        {
            if (CallSqlRefusal(c.Sql) is { } bad) return Fail($"call {c.Name}: {bad}");
            // An unrestored writer would run twice, and the second side would see the first side's writes.
            if (c.NoTran && c.Restore.Count == 0) return Fail($"call {c.Name}: no_tran requires restore lines in --compare-rev");
        }

        VariantSource src;
        try { src = deps.Locator.Locate(req.Proc); }
        catch (ScratchRefusedException ex) { return Fail(ex.Message); }

        // Through the working-copy path: pegged at BASE, so a moved file is still traced.
        var svn = deps.Svn.Cat(Path.GetFullPath(Path.Combine(deps.Locator.Root, src.RelPath)), req.Rev);
        if (svn.ExitCode != 0) return Fail($"svn cat -r {req.Rev} {src.RelPath} failed: {svn.Error.Trim()}");

        var scratchName = $"{req.Proc}__r{req.Rev}";
        ScratchSpec spec;
        try
        {
            if (ScratchProc.CreateBatch(svn.Text, req.Proc) == null)
                return Fail($"{req.Proc} is not created in {src.RelPath}@r{req.Rev}");
            var db = Variants.DbOf(new VariantSource(src.RelPath, svn.Text), deps.Compiler, deps.RunnerDb);
            spec = new ScratchSpec(req.Proc, db, svn.Text, scratchName, new Dictionary<string, bool>(),
                                   new[] { (req.Proc, scratchName) });
        }
        catch (ScratchRefusedException ex) { return Fail(ex.Message); }
        if (ScratchProc.Precheck(spec, deps.Compiler) is { } refusal) return Fail(refusal);
        var callDb = calls.Database ?? deps.RunnerDb;
        foreach (var c in calls.Calls)
            if (QualifierRefusal(c.Sql, spec.Db, callDb) is { } bad) return Fail($"call {c.Name}: {bad}");

        if (deps.Sweep != null)
            try { deps.Sweep(); }
            catch (Exception ex) { error.WriteLine($"sql-test: WARNING: writer journal sweep could not run: {ex.Message}"); }

        var (x, restoreX, conn) = deps.OpenControl();
        using (conn)
        {
            foreach (var c in calls.Calls.Where(c => c.NoTran))
            foreach (var r in c.Restore)
            {
                string? reason;
                try { reason = WriterJournal.RefuseReason(r, WriterJournal.Probe(x, r, deps.RunnerDb)); }
                catch (Exception ex) { reason = $"@restore probe failed for {r.FullName}: {ex.Message}"; }
                if (reason != null) return Fail($"call {c.Name}: {reason}");
            }

            WriterSession journal;
            try { journal = WriterSession.Begin(x, deps.RunnerDb, $"compare-rev {req.Proc}@r{req.Rev}", [], null, null, restoreX); }
            catch (Exception ex) { return Fail($"writer journal setup failed: {ex.Message}"); }

            var scratch = new ScratchSession(x, deps.RunnerDb, journal, deps.Compiler);
            try { ScratchProc.Compile(spec, scratch); }
            catch (Exception ex)
            {
                var reason = ex is ScratchRefusedException ? ex.Message : $"scratch setup failed: {ex.Message}";
                var close = scratch.DropAll() ?? journal.CloseIfEmpty();
                return Fail(close == null ? reason : $"{reason}; {close}");
            }

            int exit = 0;
            try
            {
                foreach (var c in calls.Calls)
                {
                    var (e, stop) = RunCall(c, calls.Database, req, deps, x, restoreX, scratchName, report, error);
                    exit = Math.Max(exit, e);
                    if (stop) break;
                }
                scratch.DropAll();
            }
            finally
            {
                // Each side's connection is closed by now, so a proc a timed-out batch held is free again.
                var dropError = scratch.DropAll();
                if (scratch.Handles.Count > 0)
                {
                    error.WriteLine($"sql-test: {dropError}");
                    exit = 2;
                }
            }
            return exit;
        }
    }

    // stop: a restore failed, so product rows may be dirty and no further call may run.
    private static (int Exit, bool Stop) RunCall(CallEntry c, string? db, CompareRequest req, CompareDeps deps,
                                                 ISqlExec x, ISqlExec restoreX, string scratchName,
                                                 TextWriter report, TextWriter error)
    {
        var sql = WrapReturnCode(c.Sql);
        var oldSql = Substitute(sql, scratchName);
        var newSql = Substitute(sql, req.Proc);
        if (!c.NoTran)
            return (Report(c.Name,
                           deps.RunSide(db, true, oldSql, req.IncludePrint, (_, _) => { }),
                           deps.RunSide(db, true, newSql, req.IncludePrint, (_, _) => { }),
                           req.Verbose, report), false);

        WriterSession w;
        try { w = WriterSession.Begin(x, deps.RunnerDb, $"compare-rev {req.Proc}@r{req.Rev} {c.Name}", c.Restore, null, null, restoreX); }
        catch (WriterHeldException ex) { error.WriteLine($"sql-test: call {c.Name}: {ex.Message}"); return (2, false); }
        catch (Exception ex) { error.WriteLine($"sql-test: call {c.Name}: writer setup failed: {ex.Message}"); return (2, false); }

        CallRun? o = null, n = null;
        string? restore = null;
        bool restored = false;
        int exit = 0;
        try
        {
            try { o = deps.RunSide(db, false, oldSql, req.IncludePrint, w.SetTestPair); }
            finally { restored = false; restore = w.RestoreKeep(); restored = restore == null; }
            if (restore == null)
            {
                try { n = deps.RunSide(db, false, newSql, req.IncludePrint, w.SetTestPair); }
                finally { restored = false; restore = w.RestoreKeep(); restored = restore == null; }
            }
            if (restore == null) exit = Report(c.Name, o!, n!, req.Verbose, report);
        }
        finally
        {
            foreach (var warn in w.Warnings) error.WriteLine($"sql-test: warning: call {c.Name}: {warn}");
            w.Warnings.Clear();
            if (restore != null) error.WriteLine($"sql-test: call {c.Name}: {restore}");
            // Only after a clean keep-restore: a failed one needs its snapshots for the sweep.
            if (restored && w.Finalise() is { } fin)
            {
                error.WriteLine($"sql-test: call {c.Name}: finalise failed: {fin}; journal row {w.JournalId} kept");
                exit = 2;
            }
            foreach (var warn in w.Warnings) error.WriteLine($"sql-test: warning: call {c.Name}: {warn}");
        }
        return restore != null ? (2, true) : (exit, false);
    }

    private static int Report(string name, CallRun o, CallRun n, bool verbose, TextWriter report)
    {
        int NotCompared(string why) { report.WriteLine($"compare {name}: not compared: {why}"); return 2; }

        if ((o.Fatal ?? n.Fatal) is { } fatal) return NotCompared(fatal.Message);
        // A server error is the root cause of an ended transaction, so it is named first.
        if (o.Raised && o.Incomplete != null) return NotCompared(o.Incomplete);
        if (n.Raised && n.Incomplete != null) return NotCompared(n.Incomplete);
        if (o.TranEnded || n.TranEnded) return NotCompared("transaction ended inside the call; later statements were not rolled back");
        if (o.NotExecuted || n.NotExecuted) return NotCompared($"{Placeholder} was not executed");
        var d = CompareOutput.FirstDifference(o.Output, n.Output);
        if (d == null)
        {
            // Equal bytes prove nothing when either side may have lost output.
            if ((o.Incomplete ?? n.Incomplete) is { } why) return NotCompared(why);
            report.WriteLine($"compare {name}: same");
            return 0;
        }
        foreach (var line in CompareOutput.FormatDiff(name, d)) report.WriteLine(line);
        if (verbose)
            foreach (var (side, bytes) in new[] { ("old", o.Output), ("new", n.Output) })
            foreach (var line in System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\n').Split('\n'))
                report.WriteLine($"  {side}| {line}");
        return 1;
    }

    /// <summary>The decision part of one side's run, kept free of the connection.</summary>
    /// <param name="raisedMsg">The first error number of the batch's exception; null when the batch did not throw.</param>
    /// <param name="trancount">@@trancount after the call; null when it could not be read.</param>
    internal static CallRun Classify(byte[] output, int spid, int kpid, int? raisedMsg, int? trancount, int expected, bool hasReturnCode)
    {
        var incomplete =
            raisedMsg is { } msg ? $"batch raised Msg {msg.ToString(System.Globalization.CultureInfo.InvariantCulture)}; the driver drops result sets and prints delivered before it"
            : trancount is not { } t ? "@@trancount could not be read after the call"
            : t != expected ? $"@@trancount is {t} after the call (expected {expected})"
            : null;
        return new CallRun(output, spid, kpid, null, incomplete,
                           TranEnded: trancount < expected,
                           NotExecuted: raisedMsg == null && !hasReturnCode,
                           Raised: raisedMsg != null);
    }

    /// <summary>Null when <paramref name="sql"/> has exactly one <c>exec [db..]{proc}</c> and no other placeholder.</summary>
    internal static string? CallSqlRefusal(string sql)
    {
        var placeholders = Regex.Matches(sql, Regex.Escape(Placeholder)).Count;
        return placeholders == 1 && ExecProc.Matches(sql).Count == 1 ? null : "sql must contain exactly one exec {proc}";
    }

    /// <summary>
    /// Null unless <c>{proc}</c> is qualified with a db other than the scratch db, or the call runs
    /// outside the scratch db and <c>{proc}</c> is unqualified: the old-side name would then not resolve.
    /// </summary>
    internal static string? QualifierRefusal(string sql, string scratchDb, string callDb)
    {
        var qdb = ExecProc.Match(sql).Groups["qdb"];
        if (qdb.Success && !string.Equals(qdb.Value, scratchDb, StringComparison.OrdinalIgnoreCase))
            return $"{Placeholder} is qualified with {qdb.Value}, but the scratch proc is compiled in {scratchDb}";
        if (qdb.Success || string.Equals(scratchDb, callDb, StringComparison.OrdinalIgnoreCase)) return null;
        return $"{Placeholder} must be qualified with {scratchDb}.. because the call runs in {callDb}";
    }

    internal static string Substitute(string sql, string procName) => sql.Replace(Placeholder, procName);

    /// <summary>
    /// Captures the proc's return code as a trailing one-column set. An aborted batch never
    /// reaches the select, so it renders <c>rc: none</c> after its error lines.
    /// </summary>
    internal static string WrapReturnCode(string sql)
    {
        if (CallSqlRefusal(sql) is { } bad) throw new ArgumentException(bad);
        var body = ExecProc.Replace(sql, m => $"{m.Groups["exec"].Value}{RcVar} = {m.Groups["qual"].Value}{Placeholder}", 1);
        return $"declare {RcVar} int\n{body}\nselect {CompareOutput.RcColumn} = {RcVar}";
    }
}
