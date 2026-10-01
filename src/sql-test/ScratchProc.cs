using System.Globalization;
using System.Text.RegularExpressions;
using ibsCompiler;
using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace SqlTest;

/// <summary>
/// One scratch proc to compile (design §3.8). <paramref name="SourceText"/> is the raw file
/// text; <paramref name="Renames"/> maps every chain member to its scratch name.
/// </summary>
public record ScratchSpec(
    string Proc,
    string Db,
    string SourceText,
    string ScratchName,
    IReadOnlyDictionary<string, bool> CompileOptions,
    IReadOnlyList<(string from, string to)> Renames,
    Func<IReadOnlyList<string>, IReadOnlyList<string>>? BatchEdit = null);

/// <summary>A §3.8 refusal; the message is the ERROR reason.</summary>
public sealed class ScratchRefusedException(string message) : Exception(message);

/// <summary>What the compiler seam receives: the selected, renamed text for one scratch proc.</summary>
public record ScratchCompile(string Db, string Proc, string ScratchName, string Text, IReadOnlyDictionary<string, bool> Options);

/// <summary>Compile seam so the primitive is testable without a server.</summary>
public interface IScratchCompiler
{
    /// <summary>True when <paramref name="option"/> has a `c:` line in the merged options.</summary>
    bool HasOption(string db, string option);

    /// <summary>Null on success, else the compiler output.</summary>
    string? Compile(ScratchCompile c);

    /// <summary><paramref name="text"/> with its &amp;tokens&amp; replaced from the merged options for <paramref name="db"/>.</summary>
    string Expand(string db, string text) => text;

    /// <summary>dbo's <paramref name="proc"/> text from <paramref name="db"/>..syscomments; null when it is not deployed.</summary>
    string? DeployedText(string db, string proc);
}

/// <summary>The primitive shared by R7, R10 and R11: compile a renamed copy of a proc, journalled.</summary>
public static class ScratchProc
{
    public const int MaxNameLength = 255; // sysobjects.name, confirmed on ASE 16.0 SP04

    public const string IncludeRefusal = "variant contains $i include; not supported";

    private static readonly Regex UseLine = new(@"^\s*use\s+\S", RegexOptions.IgnoreCase);
    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline);
    // `\b` keeps `$ir` (Runcreate's) out; Runsql passes a `$i` line to the server verbatim.
    private static readonly Regex IncludeLine = new(@"^\s*\$i\b", RegexOptions.Multiline);

    /// <summary>
    /// Journals, checks and compiles one scratch proc. Throws <see cref="ScratchRefusedException"/>
    /// for every §3.8 refusal; a compile failure first drops every name the session compiled.
    /// </summary>
    public static ScratchHandle Compile(ScratchSpec spec, ScratchSession session)
    {
        if (Precheck(spec, session.Compiler) is { } refusal) throw new ScratchRefusedException(refusal);
        return session.Add(spec, Render(spec));
    }

    /// <summary>
    /// The §3.8 refusals that need no database; null when none applies. Run over a whole chain
    /// before the first compile, so such a refusal compiles nothing.
    /// </summary>
    internal static string? Precheck(ScratchSpec spec, IScratchCompiler compiler)
    {
        foreach (var id in new[] { spec.ScratchName, spec.Db })
            if (!WriterJournal.IsIdent(id)) return $"invalid scratch identifier: {id}";
        if (spec.ScratchName.Length > MaxNameLength)
            return $"scratch name {spec.ScratchName} is {spec.ScratchName.Length} characters; the limit is {MaxNameLength}";
        foreach (var o in spec.CompileOptions.Keys)
            if (!compiler.HasOption(spec.Db, o))
                return $"unknown compile option {o}: no c: line in the merged options";
        return IncludeLine.IsMatch(Render(spec)) ? IncludeRefusal : null;
    }

    /// <summary>Selected batches (§3.4), then BatchEdit, then the whole-word rename, rejoined with `go`.</summary>
    internal static string Render(ScratchSpec spec)
    {
        IReadOnlyList<string> batches = SelectBatches(spec.SourceText, spec.Proc);
        if (spec.BatchEdit != null) batches = spec.BatchEdit(batches);
        var renames = spec.Renames.ToList();
        if (!renames.Any(r => string.Equals(r.from, spec.Proc, StringComparison.OrdinalIgnoreCase)))
            renames.Add((spec.Proc, spec.ScratchName));
        return string.Concat(batches.Select(b => Rename(b, renames) + "\ngo\n"));
    }

    // Same shape as Variant.cs CreateRe, so selection and SourceLocator agree on what "creates" means.
    private static readonly Regex CreateProcLine = new(@"^\s*create\s+(?:or\s+replace\s+)?proc(?:edure)?\s+(\w+)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>
    /// Every `use` batch, the batch creating <paramref name="proc"/>, and every other batch naming it
    /// as a whole word unless that batch creates a different proc. Throws <see cref="ScratchRefusedException"/>
    /// when such a batch also names another proc the file creates.
    /// </summary>
    internal static List<string> SelectBatches(string text, string proc)
    {
        var word = new Regex($@"\b{Regex.Escape(proc)}\b", RegexOptions.IgnoreCase);
        var batches = SplitBatches(text);
        var others = batches.SelectMany(b => CreateProcLine.Matches(b).Select(m => m.Groups[1].Value))
                            .Where(n => !string.Equals(n, proc, StringComparison.OrdinalIgnoreCase))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return batches.Where(Keep).ToList();

        bool Keep(string b)
        {
            var creates = CreateProcLine.Matches(b).Select(m => m.Groups[1].Value).ToList();
            // A caller's body names the callee; taking it would compile an unjournaled <caller>__<tag>.
            if (creates.Count > 0) return creates.Contains(proc, StringComparer.OrdinalIgnoreCase);
            if (IsUseBatch(b)) return true;
            if (!word.IsMatch(b)) return false;
            // Renamed, it would still act on the other proc's shared copy.
            var shared = others.FirstOrDefault(o => Regex.IsMatch(b, $@"\b{Regex.Escape(o)}\b", RegexOptions.IgnoreCase));
            if (shared != null)
                throw new ScratchRefusedException($"a batch naming {proc} also names {shared}, which the same file creates; not supported");
            return true;
        }
    }

    /// <summary>The batch creating <paramref name="proc"/>, as selected for the variant; null when the text has none.</summary>
    internal static string? CreateBatch(string text, string proc) =>
        SelectBatches(text, proc).FirstOrDefault(b => CreatesProc(b, proc));

    internal static bool CreatesProc(string batch, string proc) => CreateMatch(batch, proc) != null;

    /// <summary>0-based line index, within <paramref name="batch"/>, of the line creating <paramref name="proc"/>; null when none does.</summary>
    internal static int? CreateLineOffset(string batch, string proc)
    {
        if (CreateMatch(batch, proc) is not { } m) return null;
        // `^\s*` also spans blank lines above, so count up to the `create` token, not the match start.
        var create = m.Index + (m.Value.Length - m.Value.TrimStart().Length);
        return batch.AsSpan(0, create).Count('\n');
    }

    private static Match? CreateMatch(string batch, string proc) =>
        CreateProcLine.Matches(batch).FirstOrDefault(m => string.Equals(m.Groups[1].Value, proc, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// dbo's proc text, rows concatenated in colid order; null when <c>object_id</c> is null.
    /// ASE splits long text over colid, and past 32767 rows over colid2; number 1 is the ungrouped proc.
    /// </summary>
    internal static string? ReadDeployed(ISqlExec x, string db, string proc)
    {
        foreach (var id in new[] { db, proc })
            if (!WriterJournal.IsIdent(id)) throw new ScratchRefusedException($"invalid scratch identifier: {id}");
        var obj = WriterJournal.Lit($"{db}.dbo.{proc}");
        if (x.Scalar($"select object_id({obj})") is null or DBNull) return null;
        var rows = x.Rows($"select text, status from {db}..syscomments where id = object_id({obj}) and number = 1 order by colid2, colid");
        // Hidden text is unreadable, so drift cannot be ruled out; refusing beats a silent pass.
        if (rows.Any(r => r[1] is not (null or DBNull) && (Convert.ToInt32(r[1], CultureInfo.InvariantCulture) & 1) != 0))
            throw new ScratchRefusedException($"deployed text of {proc} is hidden (sp_hidetext); drift cannot be checked");
        return string.Concat(rows.Select(r => r[0] as string ?? ""));
    }

    // First statement only, so a comment line starting with `use` never keeps a batch.
    private static bool IsUseBatch(string b) =>
        BlockComment.Replace(b, "").Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal)) is { } first
        && UseLine.IsMatch(first);

    // Runsql's splitter is private; a `go` line ends a batch, blank batches are dropped.
    internal static List<string> SplitBatches(string text) => SplitBatchesWithLines(text).Select(b => b.Text).ToList();

    /// <summary>Batches as <see cref="SplitBatches"/> returns them, each with the 1-based source line of its first kept line.</summary>
    internal static List<(string Text, int FirstLine)> SplitBatchesWithLines(string text)
    {
        var batches = new List<(string, int)>();
        var current = new List<string>();
        var start = 1;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var n = 0; n < lines.Length; n++)
        {
            if (!string.Equals(lines[n].Trim(), "go", StringComparison.OrdinalIgnoreCase)) { current.Add(lines[n]); continue; }
            Flush();
            start = n + 2;
        }
        Flush();
        return batches;

        void Flush()
        {
            var b = string.Join("\n", current).Trim('\n');
            // Trim('\n') drops only empty lines, so the leading "" count is the offset of the first kept line.
            if (b.Trim().Length > 0) batches.Add((b, start + current.TakeWhile(l => l.Length == 0).Count()));
            current.Clear();
        }
    }

    // One pass, so a scratch name is never renamed again; `\b` stops at `_`, so a_x and x_01 survive a rename of x.
    internal static string Rename(string text, IReadOnlyList<(string from, string to)> renames)
    {
        if (renames.Count == 0) return text;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in renames) map[from] = to;
        var alt = string.Join("|", map.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
        return Regex.Replace(text, $@"\b(?:{alt})\b", m => map[m.Value], RegexOptions.IgnoreCase);
    }

    /// <summary>Null on success. A proc already absent counts as dropped.</summary>
    internal static string? DropProc(ISqlExec x, string home, string db, string name)
    {
        try
        {
            x.Exec($"use {db}");
            try { x.Exec($"if object_id({WriterJournal.Lit(name)}) is not null drop proc {name}"); }
            finally { x.Exec($"use {home}"); }
            return null;
        }
        catch (Exception ex) { return $"scratch proc {db}..{name} not dropped: {ex.Message}"; }
    }

    /// <summary>The sweep's dropVariant: drop the proc, then delete its item row.</summary>
    internal static Func<long, SweepItem, string?> SweepDropper(ISqlExec x, string home) => (journalId, v) =>
    {
        try
        {
            // A dead row's item can share a name a live session has since compiled; that proc is not ours to drop.
            var held = ReadHeldVariant(x, home, v.Db, v.Obj, journalId) != null;
            if (!held && DropProc(x, home, v.Db, v.Obj) is { } err) return err;
        }
        catch (Exception ex) { return ex.Message; }
        try { x.Exec($"delete {home}..{WriterJournal.ItemTable} where journal_id = {journalId} and seq = {v.Seq}"); }
        catch (Exception ex) { return ex.Message; }
        return null;
    };

    /// <summary>A live journal row other than <paramref name="ownJournalId"/> holding a 'V' item for the name.</summary>
    internal static JournalRow? ReadHeldVariant(ISqlExec x, string home, string db, string name, long ownJournalId)
    {
        var cols = string.Join(", ", WriterSweep.RowColumns.Split(", ").Select(c => "j." + c));
        var rows = WriterSweep.ParseRows(x.Rows(
            $"select {cols} from {home}..{WriterJournal.JournalTable} j, {home}..{WriterJournal.ItemTable} i " +
            $"where i.journal_id = j.journal_id and i.kind = 'V' and i.db = {WriterJournal.Lit(db)} " +
            $"and i.obj = {WriterJournal.Lit(name)} and j.journal_id <> {ownJournalId}"));
        return rows.FirstOrDefault(r => WriterSweep.IsAlive(x, r));
    }

    internal static string HeldReason(string db, string name, JournalRow h) =>
        $"scratch proc {db}..{name} is held by journal row {h.JournalId} ({h.Login}@{h.Host}, spid {h.Spid})";

    // object_id sees only our own and dbo's objects, so look the name up across all owners.
    // A dead row's 'V' item marks a journal leftover, which the compile's drop guard replaces.
    internal static string? CollisionReason(ISqlExec x, string home, string db, string name, long ownJournalId)
    {
        var exists = Convert.ToInt32(x.Scalar($"select count(*) from {db}..sysobjects where name = {WriterJournal.Lit(name)}"),
                                     CultureInfo.InvariantCulture) > 0;
        if (!exists) return null;
        var journalled = Convert.ToInt32(x.Scalar(
            $"select count(*) from {home}..{WriterJournal.ItemTable} where kind = 'V' and db = {WriterJournal.Lit(db)} " +
            $"and obj = {WriterJournal.Lit(name)} and journal_id <> {ownJournalId}"), CultureInfo.InvariantCulture) > 0;
        return journalled ? null : $"scratch name {db}..{name} already exists and is not a writer journal leftover";
    }
}

/// <summary>
/// The control connection and the test's journal row, shared with its writer restore (one row
/// carries 'R' and 'V' items). Dispose drops whatever handles remain.
/// </summary>
public sealed class ScratchSession : IDisposable
{
    private readonly ISqlExec _x;
    private readonly string _home;
    private readonly List<ScratchHandle> _handles = new();

    public WriterSession Journal { get; }
    public IScratchCompiler Compiler { get; }
    public IReadOnlyList<ScratchHandle> Handles => _handles;

    /// <param name="x">The control connection, sitting in <paramref name="home"/>.</param>
    public ScratchSession(ISqlExec x, string home, WriterSession journal, IScratchCompiler compiler)
    {
        _x = x; _home = home; Journal = journal; Compiler = compiler;
    }

    internal ScratchHandle Add(ScratchSpec spec, string text)
    {
        var (db, name) = (spec.Db, spec.ScratchName);
        // Checked before the item exists, so a crash here never journals someone else's proc for the sweep.
        // object_id and a bare drop resolve against the caller's owner first, so only dbo's procs are sweepable.
        if (!string.Equals(WriterJournal.UserIn(_x, db, _home), "dbo", StringComparison.OrdinalIgnoreCase))
            throw new ScratchRefusedException($"scratch compile needs dbo in {db} (run as GONZO_TEST)");
        if (ScratchProc.ReadHeldVariant(_x, _home, db, name, Journal.JournalId) is { } h)
            throw new ScratchRefusedException(ScratchProc.HeldReason(db, name, h));
        if (ScratchProc.CollisionReason(_x, _home, db, name, Journal.JournalId) is { } collision)
            throw new ScratchRefusedException(collision);

        var seq = Journal.AddVariant(db, name);
        // Re-checked after our insert, as WriterSession.Begin does: two racers both refuse rather than both compile.
        JournalRow? raced;
        try { raced = ScratchProc.ReadHeldVariant(_x, _home, db, name, Journal.JournalId); }
        catch { Journal.RemoveVariant(seq); throw; }
        if (raced != null)
        {
            Journal.RemoveVariant(seq);
            throw new ScratchRefusedException(ScratchProc.HeldReason(db, name, raced));
        }

        var handle = new ScratchHandle(this, db, name, seq);
        _handles.Add(handle);
        string? output;
        try { output = Compiler.Compile(new ScratchCompile(db, spec.Proc, name, text, spec.CompileOptions)); }
        catch (Exception ex) { output = ex.Message; }
        if (output == null) return handle;

        // Nothing of a half-compiled chain stays.
        var dropError = DropAll();
        throw new ScratchRefusedException($"compile of {db}..{name} failed: {output.Trim()}" +
                                          (dropError != null ? $"; {dropError}" : ""));
    }

    /// <summary>Null on success; on failure the proc's item row, and so the journal row, stay for the sweep.</summary>
    internal string? Drop(ScratchHandle h)
    {
        if (h.Dropped) return null;
        if (ScratchProc.DropProc(_x, _home, h.Db, h.Name) is { } err) return err;
        if (Journal.RemoveVariant(h.Seq) is { } del) return $"scratch proc {h.Db}..{h.Name} journal item not deleted: {del}";
        h.Dropped = true;
        _handles.Remove(h);
        return null;
    }

    /// <summary>Drops callers before callees. Null on success, else every failure.</summary>
    public string? DropAll()
    {
        var failures = new List<string>();
        foreach (var h in _handles.AsEnumerable().Reverse().ToList())
            if (Drop(h) is { } err) failures.Add(err);
        return failures.Count == 0 ? null : string.Join(" | ", failures);
    }

    /// <summary>Grants execute on the scratch proc to <paramref name="dbUser"/>. Null on success, else the refusal.</summary>
    public string? Grant(ScratchHandle h, string dbUser)
    {
        // The user reaches the grant unquoted.
        if (!WriterJournal.IsIdent(dbUser)) return $"cannot grant execute on {h.Db}..{h.Name} to '{dbUser}': not a plain identifier";
        try
        {
            _x.Exec($"use {h.Db}");
            try { _x.Exec($"grant execute on {h.Name} to {dbUser}"); }
            finally { _x.Exec($"use {_home}"); }
            return null;
        }
        catch (Exception ex) { return $"grant execute on {h.Db}..{h.Name} to {dbUser} failed: {ex.Message}"; }
    }

    public void Dispose() => DropAll();
}

/// <summary>One compiled scratch proc; Dispose drops it and deletes its journal item.</summary>
public sealed class ScratchHandle : IDisposable
{
    private readonly ScratchSession _session;

    public string Db { get; }
    public string Name { get; }
    internal int Seq { get; }
    public bool Dropped { get; internal set; }

    internal ScratchHandle(ScratchSession session, string db, string name, int seq)
    {
        _session = session; Db = db; Name = name; Seq = seq;
    }

    /// <summary>Null on success, else the error; the journal keeps the item for the sweep.</summary>
    public string? Drop() => _session.Drop(this);

    public void Dispose() => Drop();
}

/// <summary>
/// The in-process compile of design §3.5, through <c>runsql_main.Run</c>. <paramref name="optionsProfile"/>
/// (the <c>--variant-profile</c>) supplies only the options layer; the compile connects as <paramref name="profile"/>,
/// whose dbo login keeps owner resolution for the drop and the sweep correct.
/// </summary>
public sealed class RunsqlScratchCompiler(
    ResolvedProfile profile, string server, ResolvedProfile? optionsProfile = null,
    Action<Action<ISqlExec>>? withControl = null,
    Func<CommandVariables, ResolvedProfile, ibsCompiler.Options, bool>? run = null) : IScratchCompiler
{
    private ResolvedProfile OptionsProfile => optionsProfile ?? profile;

    public bool HasOption(string db, string option) => WithOptions(db, o => o.GetCompileOption(option) != null);

    public string Expand(string db, string text) => WithOptions(db, o => o.ReplaceWord(text));

    public string? DeployedText(string db, string proc)
    {
        if (withControl == null) throw new InvalidOperationException("no control connection for the drift check");
        string? text = null;
        withControl(x => text = ScratchProc.ReadDeployed(x, db, proc));
        return text;
    }

    // The option chatter goes to a throwaway file, not stdout.
    private T WithOptions<T>(string db, Func<ibsCompiler.Options, T> use)
    {
        var vars = Vars(db, "");
        vars.OutFile = Path.Combine(Path.GetTempPath(), $"sql-test-options-{Environment.ProcessId}-{Guid.NewGuid():N}.out");
        try
        {
            var opts = new ibsCompiler.Options(vars, OptionsProfile);
            if (!opts.GenerateOptionFiles()) throw new ScratchRefusedException("compile options could not be generated");
            return use(opts);
        }
        finally { try { File.Delete(vars.OutFile); } catch { /* temp file */ } }
    }

    public string? Compile(ScratchCompile c)
    {
        var stem = Path.Combine(Path.GetTempPath(), $"sql-test-variant-{Environment.ProcessId}-{c.ScratchName}");
        var (sql, outFile, errFile) = (stem + ".sql", stem + ".out", stem + ".err");
        try
        {
            File.WriteAllText(sql, c.Text);
            var cmdvars = Vars(c.Db, sql);
            cmdvars.OutFile = outFile;   // runsql reports errors to files, not its return value
            cmdvars.ErrFile = errFile;
            var opts = new ibsCompiler.Options(cmdvars, OptionsProfile);
            if (!opts.GenerateOptionFiles()) return "options could not be generated";
            if (ForceOptions(opts, c.Options) is { } bad) return bad;
            if ((run ?? Runsql)(cmdvars, profile, opts)) return null;
            var err = StripFraming(File.Exists(errFile) ? File.ReadAllText(errFile) : "");
            return err.Length > 0 ? err : StripFraming(File.Exists(outFile) ? File.ReadAllText(outFile) : "") is { Length: > 0 } o ? o : "runsql failed";
        }
        finally
        {
            foreach (var f in new[] { sql, outFile, errFile })
                try { File.Delete(f); } catch { /* temp file */ }
        }
    }

    /// <summary>Null when every option was forced, else the refusal for the first unknown one.</summary>
    internal static string? ForceOptions(ibsCompiler.Options opts, IReadOnlyDictionary<string, bool> options)
    {
        foreach (var (k, v) in options)
            if (!opts.SetCompileOption(k, v)) return $"unknown compile option {k}: no c: line in the merged options";
        return null;
    }

    private static readonly Regex Framing = new(@"^(Running( \d+ of \d+)?:|Elapsed:).*$\n?", RegexOptions.Multiline);

    // runsql brackets its errors with a `Running: <temp path>` line and an `Elapsed:` line.
    internal static string StripFraming(string text) => Framing.Replace(text, "").Trim();

    private static bool Runsql(CommandVariables cmdvars, ResolvedProfile p, ibsCompiler.Options opts)
    {
        using var exec = SqlExecutorFactory.Create(p);
        return new runsql_main().Run(cmdvars, p, exec, existingOptions: opts);
    }

    private CommandVariables Vars(string db, string command) =>
        new() { Server = server, Database = db, Command = command };
}
