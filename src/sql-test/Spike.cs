using System.Text.RegularExpressions;

namespace SqlTest;

public sealed record SpikeRequest(string Proc, int From, int To, string CallsPath, string? AsAlias, bool Verbose, int TimeoutSeconds);

internal sealed record SpikeSlice(int Passes, int Unclosed, IoMeasure Io, string? Error);

/// <param name="OpenControl">The control connection and the restore connection, both sitting in RunnerDb.</param>
/// <param name="RunSide">Runs one call batch (db, tran, sql, onOpen(spid, kpid)) on its own connection.</param>
/// <param name="AsLogin">The `--as` login the calls run as; null runs them as the control login.</param>
internal sealed record SpikeDeps(SourceLocator Locator, IScratchCompiler Compiler, string RunnerDb,
    Func<(ISqlExec X, ISqlExec RestoreX, IDisposable Conn)> OpenControl,
    Func<string?, bool, string, Action<int, int>, SpikeRun> RunSide,
    string? AsLogin, IIoMeter Meter, Action? Sweep = null);

/// <param name="Messages">Every message the batch delivered, prints and stat lines, in order.</param>
/// <param name="RaisedMsg">The number of a severity 11+ error that aborted the batch; null when none.</param>
/// <param name="TranCount">@@trancount after the batch; null when unknown.</param>
/// <param name="Expected">The @@trancount the side expects after a clean batch.</param>
internal sealed record SpikeRun(IReadOnlyList<(int Number, string Text)> Messages, int? RaisedMsg, int? TranCount, int Expected, Exception? Fatal);

/// <summary>R11: measure the I/O of a line range of one proc, delimited by marker prints in a scratch copy.</summary>
public static class Spike
{
    public const string Tag = "spike";

    private static readonly Regex LinesRe = new(@"^(\d{1,6})-(\d{1,6})$");

    internal static string MarkerText(string scratchName, int line) => $"{MeasureAccumulator.MarkerPrefix}marker {scratchName}:{line}";

    // Distinct texts, so from == to still pairs; unrenamed, as MarkerEdit inserts them.
    private static string OpenText(string scratchName, int from) => MarkerText(scratchName, from) + " open";
    private static string CloseText(string scratchName, int to) => MarkerText(scratchName, to) + " close";

    /// <summary>The open and close texts as the server prints them: Render's whole-word rename also rewrites the prints.</summary>
    internal static (string Open, string Close) Markers(string proc, string scratchName, int from, int to)
    {
        var renames = new[] { (proc, scratchName) };
        return (ScratchProc.Rename(OpenText(scratchName, from), renames), ScratchProc.Rename(CloseText(scratchName, to), renames));
    }

    internal static bool TryParseLines(string s, out int from, out int to)
    {
        from = to = 0;
        var m = LinesRe.Match(s);
        if (!m.Success) return false;
        from = int.Parse(m.Groups[1].Value);
        to = int.Parse(m.Groups[2].Value);
        return from >= 1 && from <= to;
    }

    /// <summary>
    /// Null when lines <paramref name="from"/>-<paramref name="to"/> lie in the create batch of <paramref name="proc"/>
    /// after its create line. A range in the header before `as` passes here and fails at compile.
    /// </summary>
    internal static string? RangeRefusal(string sourceText, string proc, string relPath, int from, int to)
    {
        if (CreateBatch(sourceText, proc) is not { } b) return $"{proc} is not created in {relPath}";
        if (from < 1 || from > to) return $"--lines {from}-{to}: the range must satisfy 1 <= from <= to";
        var (s, c, e) = b;
        return from > c && to <= e ? null
            : $"--lines {from}-{to}: the create batch of {proc} is lines {s}-{e} of {relPath}; the range must start after line {c} (create proc) and end by line {e}";
    }

    /// <summary>
    /// A <see cref="ScratchSpec.BatchEdit"/> wrapping the range of the create batch in
    /// `begin print '&lt;open&gt;'` ... `print '&lt;close&gt;' end`; every other batch passes through.
    /// </summary>
    internal static Func<IReadOnlyList<string>, IReadOnlyList<string>> MarkerEdit(string sourceText, string proc, string scratchName, int from, int to)
    {
        var first = CreateBatch(sourceText, proc)?.First ?? 0;
        var open = $"begin print '{OpenText(scratchName, from)}'";
        var close = $"print '{CloseText(scratchName, to)}' end";
        // begin/end keeps an if/while body without its own begin as one statement; bare prints would become that body.
        return batches => batches.Select(b => ScratchProc.CreatesProc(b, proc) ? Wrap(b) : b).ToList();

        string Wrap(string batch)
        {
            var lines = batch.Split('\n').ToList();
            int i = from - first, j = to - first;
            // Never throw: BatchEdit runs inside Precheck, where an exception escapes as a crash, not a refusal.
            if (first == 0 || i < 0 || j >= lines.Count || i > j) return batch;
            lines.Insert(j + 1, close);
            lines.Insert(i, open);
            return string.Join("\n", lines);
        }
    }

    // (first line, create line, last line) of the batch creating proc, all 1-based source lines.
    private static (int First, int Create, int Last)? CreateBatch(string sourceText, string proc)
    {
        foreach (var (text, first) in ScratchProc.SplitBatchesWithLines(sourceText))
            if (ScratchProc.CreateLineOffset(text, proc) is { } off)
                return (first, first + off, first + text.Split('\n').Length - 1);
        return null;
    }

    /// <summary>
    /// Pairs each open with the next close and sums the stat lines of closed passes only.
    /// Prints are never fed, so a measure-start inside the range cannot reset the totals.
    /// </summary>
    internal static SpikeSlice Slice(IReadOnlyList<(int Number, string Text)> messages, string open, string close, IIoMeter meter)
    {
        var acc = new MeasureAccumulator(meter);
        var pass = new List<(int, string)>();
        int passes = 0, unclosed = 0;
        var inRange = false;
        foreach (var (n, text) in messages)
        {
            if (meter.IsStatMessage(n)) { if (inRange) pass.Add((n, text)); continue; }
            var msg = text.Trim();
            if (!inRange)
            {
                if (msg == open) { inRange = true; pass.Clear(); }
            }
            else if (msg == close)
            {
                foreach (var (pn, pt) in pass) acc.Feed(pn, pt);
                passes++;
                inRange = false;
            }
            else if (msg == open)
            {
                // A re-entry without a close: the previous pass left the range by return, goto or error.
                unclosed++;
                pass.Clear();
            }
        }
        if (inRange) unclosed++;
        return new SpikeSlice(passes, unclosed, acc.Current, acc.Error);
    }

    internal static IReadOnlyList<string> Format(string call, string proc, int from, int to, SpikeSlice s)
    {
        if (s.Error != null) return new[] { NotMeasured(call, s.Error) };
        var head = $"spike {call}: {proc} lines {from}-{to}:";
        if (s.Passes == 0 && s.Unclosed == 0) return new[] { $"{head} range not reached" };
        var lines = new List<string>();
        if (s.Passes > 0)
        {
            lines.Add($"{head} {s.Passes} pass(es), logical {s.Io.LogicalReads}, physical {s.Io.PhysicalReads}, writes {s.Io.Writes}");
            lines.AddRange(s.Io.Tables.Select(t => $"  {t.Table}  logical {t.LogicalReads}  physical {t.PhysicalReads}"));
        }
        if (s.Unclosed > 0)
            lines.Add($"{head} {s.Unclosed} pass(es) not closed (return, goto or error inside the range); closed passes only");
        return lines;
    }

    internal static string NotMeasured(string call, string why) => $"spike {call}: not measured: {why}";

    /// <summary>
    /// Null when <paramref name="login"/> has a real, non-dbo user in <paramref name="db"/>, returned in
    /// <paramref name="dbUser"/>. The control connection stays in its current database.
    /// </summary>
    internal static string? AsUserRefusal(ISqlExec x, string login, string db, out string? dbUser)
    {
        dbUser = null;
        if (!WriterJournal.IsIdent(db)) return $"--as: database {db} is not a plain identifier";
        var lit = WriterJournal.Lit(login);
        var suidObj = x.Scalar($"select suser_id({lit})");
        if (suidObj == null || suidObj is DBNull) return $"--as: login {login} is unknown";
        var suid = Convert.ToInt32(suidObj);
        if (Convert.ToInt32(x.Scalar("select count(*) from master..sysloginroles l, master..syssrvroles r "
                                     + $"where l.suid = {suid} and l.srid = r.srid and r.name = 'sa_role'")) > 0)
            return $"--as {login} holds sa_role and acts as dbo in every database";
        if (Convert.ToInt32(x.Scalar($"select count(*) from master..sysdatabases where name = {WriterJournal.Lit(db)} and suid = {suid}")) > 0)
            return $"--as: login {login} owns {db} (dbo); a spike --as needs a login without dbo rights";
        if (Convert.ToInt32(x.Scalar($"select count(*) from {db}..sysalternates where suid = {suid}")) > 0)
            return $"--as: login {login} is aliased to another user in {db}; a spike --as needs a login with its own user";
        var name = x.Rows($"select name from {db}..sysusers where suid = {suid}")
                    .Select(r => Convert.ToString(r[0])?.Trim()).FirstOrDefault();
        if (string.IsNullOrEmpty(name) || name.Equals("guest", StringComparison.OrdinalIgnoreCase)
            || name.Equals("dbo", StringComparison.OrdinalIgnoreCase))
            return $"--as: login {login} has no user of its own in {db}";
        dbUser = name;
        return null;
    }

    internal static int Run(SpikeRequest req, SpikeDeps deps, TextWriter report, TextWriter? error = null)
    {
        error ??= report;
        int Fail(string msg) { error.WriteLine($"sql-test: {msg}"); return 2; }

        CallList calls;
        try { calls = CallList.Load(req.CallsPath); }
        catch (CallListException ex) { return Fail(ex.Message); }
        foreach (var c in calls.Calls)
        {
            if (CompareRev.CallSqlRefusal(c.Sql) is { } bad) return Fail($"call {c.Name}: {bad}");
            // An unrestored writer leaves its rows behind in a shared database.
            if (c.NoTran && c.Restore.Count == 0) return Fail($"call {c.Name}: no_tran requires restore lines in --spike");
        }

        VariantSource src;
        try { src = deps.Locator.Locate(req.Proc); }
        catch (ScratchRefusedException ex) { return Fail(ex.Message); }
        if (RangeRefusal(src.Text, req.Proc, src.RelPath, req.From, req.To) is { } range) return Fail(range);

        var scratchName = $"{req.Proc}__spike";
        ScratchSpec spec;
        try
        {
            var db = Variants.DbOf(src, deps.Compiler, deps.RunnerDb);
            spec = new ScratchSpec(req.Proc, db, src.Text, scratchName, new Dictionary<string, bool>(),
                                   new[] { (req.Proc, scratchName) },
                                   MarkerEdit(src.Text, req.Proc, scratchName, req.From, req.To));
        }
        catch (ScratchRefusedException ex) { return Fail(ex.Message); }
        if (ScratchProc.Precheck(spec, deps.Compiler) is { } refusal) return Fail(refusal);
        var callDb = calls.Database ?? deps.RunnerDb;
        foreach (var c in calls.Calls)
            if (CompareRev.QualifierRefusal(c.Sql, spec.Db, callDb) is { } bad) return Fail($"call {c.Name}: {bad}");

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

            string? dbUser = null;
            if (deps.AsLogin is { } login)
                foreach (var db in new[] { spec.Db, callDb }.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string? why, user;
                    try { why = AsUserRefusal(x, login, db, out user); }
                    catch (Exception ex) { return Fail($"--as check of {login} in {db} failed: {ex.Message}"); }
                    if (why != null) return Fail(why);
                    // The grant goes on the scratch proc, so the scratch db's user receives it.
                    if (string.Equals(db, spec.Db, StringComparison.OrdinalIgnoreCase)) dbUser = user;
                }

            WriterSession journal;
            try { journal = WriterSession.Begin(x, deps.RunnerDb, $"spike {req.Proc}:{req.From}-{req.To}", [], null, null, restoreX); }
            catch (Exception ex) { return Fail($"writer journal setup failed: {ex.Message}"); }

            var scratch = new ScratchSession(x, deps.RunnerDb, journal, deps.Compiler);
            ScratchHandle handle;
            try { handle = ScratchProc.Compile(spec, scratch); }
            catch (Exception ex)
            {
                var reason = ex is ScratchRefusedException ? ex.Message : $"scratch setup failed: {ex.Message}";
                var close = scratch.DropAll() ?? journal.CloseIfEmpty();
                return Fail(close == null ? reason : $"{reason}; {close}");
            }

            if (dbUser != null && scratch.Grant(handle, dbUser) is { } grant)
            {
                var close = scratch.DropAll() ?? journal.CloseIfEmpty();
                return Fail(close == null ? grant : $"{grant}; {close}");
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
    private static (int Exit, bool Stop) RunCall(CallEntry c, string? db, SpikeRequest req, SpikeDeps deps,
                                                 ISqlExec x, ISqlExec restoreX, string scratchName,
                                                 TextWriter report, TextWriter error)
    {
        var sql = $"{deps.Meter.EnableSql}\n{CompareRev.Substitute(c.Sql, scratchName)}\n{deps.Meter.DisableSql}";
        if (!c.NoTran)
            return (Classify(c.Name, deps.RunSide(db, true, sql, (_, _) => { }), req, deps.Meter, scratchName, report, error), false);

        WriterSession w;
        try { w = WriterSession.Begin(x, deps.RunnerDb, $"spike {req.Proc}:{req.From}-{req.To} {c.Name}", c.Restore, null, null, restoreX); }
        catch (WriterHeldException ex) { error.WriteLine($"sql-test: call {c.Name}: {ex.Message}"); return (2, false); }
        catch (Exception ex) { error.WriteLine($"sql-test: call {c.Name}: writer setup failed: {ex.Message}"); return (2, false); }

        SpikeRun? run = null;
        string? restore = null;
        bool restored = false;
        int exit = 0;
        try
        {
            try { run = deps.RunSide(db, false, sql, w.SetTestPair); }
            finally { restored = false; restore = w.RestoreKeep(); restored = restore == null; }
            if (restore == null) exit = Classify(c.Name, run!, req, deps.Meter, scratchName, report, error);
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

    // Fatal, then a raised Msg, then trancount, then the slice: an aborted batch is never sliced.
    private static int Classify(string call, SpikeRun run, SpikeRequest req, IIoMeter meter, string scratchName,
                                TextWriter report, TextWriter error)
    {
        if (run.Fatal != null) { report.WriteLine(NotMeasured(call, run.Fatal.Message)); return 2; }
        if (run.RaisedMsg is { } msg) { report.WriteLine(NotMeasured(call, $"batch raised Msg {msg}")); return 2; }
        if (run.TranCount is { } tc && tc != run.Expected)
        {
            var ended = tc < run.Expected ? "; the transaction ended inside the call, so later writes were not rolled back" : "";
            report.WriteLine(NotMeasured(call, $"@@trancount {tc} after the batch, expected {run.Expected}{ended}"));
            return 2;
        }
        var (open, close) = Markers(req.Proc, scratchName, req.From, req.To);
        var s = Slice(run.Messages, open, close, meter);
        if (req.Verbose) error.WriteLine($"spike {call}: messages {run.Messages.Count}, passes {s.Passes}, unclosed {s.Unclosed}");
        foreach (var line in Format(call, req.Proc, req.From, req.To, s)) report.WriteLine(line);
        return s.Error != null ? 2 : s.Passes == 0 || s.Unclosed > 0 ? 1 : 0;
    }
}
