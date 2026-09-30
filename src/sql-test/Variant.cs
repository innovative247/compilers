using System.Text.RegularExpressions;

namespace SqlTest;

/// <summary>
/// One `-- @variant:` line (design §3.2). <paramref name="Chain"/> is caller-first, from the proc
/// the test calls to the proc that carries the gate; <paramref name="Sources"/> holds the
/// `-- @variant-source:` overrides, relative to the source root.
/// </summary>
public sealed record VariantSpec(
    IReadOnlyDictionary<string, bool> Options,
    string Tag,
    IReadOnlyList<string> Chain,
    IReadOnlyDictionary<string, string> Sources)
{
    public string ScratchName(string proc) => $"{proc}__{Tag}";

    public string Describe => $"variant {Tag}: {string.Join(" > ", Chain)}";

    /// <summary>Two tests may share a tag only with this spec (design §3.2).</summary>
    public bool SameAs(VariantSpec o) =>
        Tag == o.Tag
        && Chain.SequenceEqual(o.Chain, StringComparer.OrdinalIgnoreCase)
        && Options.Count == o.Options.Count && Options.All(kv => o.Options.TryGetValue(kv.Key, out var v) && v == kv.Value)
        && Sources.Count == o.Sources.Count && Sources.All(kv => o.Sources.TryGetValue(kv.Key, out var p) && p == kv.Value);
}

/// <summary>One located chain member: its file, relative to the source root, and the file text.</summary>
public sealed record VariantSource(string RelPath, string Text);

/// <summary>Parse, locate, build and compile R7 option variants (design §3.2-3.4).</summary>
public static class Variants
{
    // `\s*:` keeps `@variant-source` out.
    private static readonly Regex VariantRe = new(@"^\s*--\s*@variant\s*:(.*)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex SourceRe = new(@"^\s*--\s*@variant-source\s*:(.*)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex OptionRe = new(@"^(\w+)=([+-])$");
    private static readonly Regex TagRe = new(@"^[a-z0-9]{1,16}$");
    private static readonly Regex SourceBodyRe = new(@"^\s*(\S+)\s*=\s*(\S.*?)\s*$");
    private static readonly Regex UseRe = new(@"^\s*use\s+(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>Null with no error when the body has no `@variant` line; an unparseable line is an error.</summary>
    public static VariantSpec? Parse(string? body, out string? error)
    {
        error = null;
        if (body == null) return null;
        var lines = VariantRe.Matches(body);
        var sourceLines = SourceRe.Matches(body);
        if (lines.Count == 0)
        {
            if (sourceLines.Count > 0) error = "@variant-source without @variant";
            return null;
        }
        if (lines.Count > 1) { error = $"second @variant line: {lines[1].Value.Trim()}"; return null; }

        var tokens = lines[0].Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var options = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        for (; i < tokens.Length && !Is(tokens[i], "as"); i++)
        {
            var m = OptionRe.Match(tokens[i]);
            if (!m.Success) { error = $"invalid @variant option: {tokens[i]} (expected <opt>=+ or <opt>=-)"; return null; }
            if (!options.TryAdd(m.Groups[1].Value, m.Groups[2].Value == "+"))
            { error = $"duplicate @variant option: {m.Groups[1].Value}"; return null; }
        }
        if (options.Count == 0) { error = "@variant needs at least one <opt>=<+|-> option"; return null; }
        if (i >= tokens.Length) { error = "@variant is missing `as <tag>`"; return null; }
        var tag = i + 1 < tokens.Length ? tokens[i + 1] : "";
        if (!TagRe.IsMatch(tag)) { error = $"invalid @variant tag: '{tag}' (expected [a-z0-9]{{1,16}})"; return null; }
        if (i + 2 >= tokens.Length || !Is(tokens[i + 2], "chain")) { error = "@variant is missing `chain <proc> [> <proc> ...]`"; return null; }

        var chain = string.Join(" ", tokens.Skip(i + 3)).Split('>').Select(s => s.Trim()).ToList();
        foreach (var member in chain)
            if (!WriterJournal.IsIdent(member)) { error = $"invalid @variant chain member: '{member}'"; return null; }

        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match s in sourceLines)
        {
            var m = SourceBodyRe.Match(s.Groups[1].Value);
            if (!m.Success) { error = $"invalid @variant-source directive: {s.Value.Trim()}"; return null; }
            var proc = m.Groups[1].Value;
            if (!chain.Contains(proc, StringComparer.OrdinalIgnoreCase))
            { error = $"@variant-source for {proc}: not in the chain"; return null; }
            if (!sources.TryAdd(proc, m.Groups[2].Value)) { error = $"duplicate @variant-source for {proc}"; return null; }
        }
        return new VariantSpec(options, tag, chain, sources);

        static bool Is(string token, string word) => string.Equals(token, word, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Each member but the last must name the next in its selected batches (§3.3).</summary>
    public static string? ChainIsLinked(IReadOnlyList<(string Proc, string Text)> members)
    {
        for (int i = 0; i + 1 < members.Count; i++)
        {
            var (a, b) = (members[i].Proc, members[i + 1].Proc);
            var next = new Regex($@"\b{Regex.Escape(b)}\b", RegexOptions.IgnoreCase);
            if (!ScratchProc.SelectBatches(members[i].Text, a).Any(next.IsMatch)) return $"chain break: {a} does not call {b}";
        }
        return null;
    }

    /// <summary>
    /// The scratch specs, callee-first, with every member renamed to <c>&lt;m&gt;__&lt;tag&gt;</c>.
    /// Each member's db is its file's first `use` line, expanded through the merged options, else
    /// <paramref name="runnerDb"/>. Throws <see cref="ScratchRefusedException"/>.
    /// </summary>
    public static List<(ScratchSpec Spec, string RelPath)> Build(
        VariantSpec v, SourceLocator locator, IScratchCompiler compiler, string runnerDb)
    {
        var located = v.Chain.Select(m => (Proc: m, Source: locator.Locate(m, v.Sources.GetValueOrDefault(m)))).ToList();
        if (ChainIsLinked(located.Select(l => (l.Proc, l.Source.Text)).ToList()) is { } brk) throw new ScratchRefusedException(brk);

        var renames = v.Chain.Select(m => (m, v.ScratchName(m))).ToList();
        var specs = new List<(ScratchSpec, string)>();
        foreach (var (proc, src) in Enumerable.Reverse(located))
            specs.Add((new ScratchSpec(proc, DbOf(src, compiler, runnerDb), src.Text, v.ScratchName(proc), v.Options, renames),
                       src.RelPath));
        return specs;
    }

    internal static string DbOf(VariantSource src, IScratchCompiler compiler, string runnerDb)
    {
        var use = UseRe.Match(src.Text);
        if (!use.Success) return runnerDb;
        var db = compiler.Expand(runnerDb, use.Groups[1].Value).Trim();
        // An unexpanded token would compile into whatever db the name happens to parse as.
        if (db.Contains('&')) throw new ScratchRefusedException($"use {use.Groups[1].Value} in {src.RelPath} does not resolve in the merged options");
        return db;
    }

    /// <summary>
    /// Refuses when a deployed member's create batch, expanded with unmodified options, differs
    /// from its syscomments text beyond whitespace within lines: the variant would then test code nobody runs.
    /// </summary>
    public static void CheckDrift(IReadOnlyList<(ScratchSpec Spec, string RelPath)> specs, IScratchCompiler compiler,
                                  Action<string>? verbose = null)
    {
        foreach (var (spec, relPath) in specs)
        {
            var deployed = compiler.DeployedText(spec.Db, spec.Proc);
            if (deployed == null)
            {
                verbose?.Invoke($"{spec.Proc} is not deployed in {spec.Db}; drift check skipped");
                continue;
            }
            var local = compiler.Expand(spec.Db, ScratchProc.CreateBatch(spec.SourceText, spec.Proc) ?? "");
            if (DriftExcerpt(local, deployed) is { } excerpt)
                throw new ScratchRefusedException(
                    $"variant source for {spec.Proc} differs from the deployed proc ({relPath}); update the working copy or redeploy, or pass --variant-profile <deploying profile>; {excerpt}");
        }
    }

    private static readonly Regex LineBreak = new(@"\s*\n\s*");
    private static readonly Regex Blanks = new(@"[ \t]+");

    // Line breaks survive: a `--` comment ends at one, so joining lines could hide code in a comment.
    private static string Normalise(string s) => Blanks.Replace(LineBreak.Replace(s, "\n"), " ").Trim();

    /// <summary>Null when the texts match after normalising whitespace within lines, else the first difference in context.</summary>
    internal static string? DriftExcerpt(string local, string deployed)
    {
        var (a, b) = (Normalise(local), Normalise(deployed));
        if (a == b) return null;
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        var start = Math.Max(0, i - 30);
        string Cut(string s) => s.Substring(Math.Min(start, s.Length), Math.Min(60, Math.Max(0, s.Length - start))).Replace("\n", "\\n");
        return $"first difference at character {i}: local \"{Cut(a)}\", deployed \"{Cut(b)}\"";
    }

    /// <summary>
    /// Prechecks every spec, then compiles in order. A pre-DB refusal compiles nothing; any later
    /// failure drops every member compiled so far and rethrows.
    /// </summary>
    public static List<ScratchHandle> CompileAll(IReadOnlyList<ScratchSpec> specs, ScratchSession session)
    {
        foreach (var spec in specs)
            if (ScratchProc.Precheck(spec, session.Compiler) is { } refusal) throw new ScratchRefusedException(refusal);
        var handles = new List<ScratchHandle>();
        try
        {
            foreach (var spec in specs) handles.Add(ScratchProc.Compile(spec, session));
            return handles;
        }
        catch (Exception ex)
        {
            // A compile failure has already dropped the chain; a held or collision refusal has not.
            var dropError = session.DropAll();
            if (dropError != null && ex is ScratchRefusedException && !ex.Message.Contains(dropError))
                throw new ScratchRefusedException($"{ex.Message}; {dropError}");
            throw;
        }
    }
}

/// <summary>
/// Finds the file that creates a proc under <c>css/ss/*/pro_*.sql</c> (design §3.3). The tree is
/// scanned once, on first use; parallel tests share one instance.
/// </summary>
public sealed class SourceLocator
{
    private static readonly Regex CreateRe = new(@"^\s*create\s+(?:or\s+replace\s+)?proc(?:edure)?\s+(\w+)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private readonly string _root;
    private readonly Lazy<Dictionary<string, List<string>>> _index;

    public string Root => _root;

    public SourceLocator(string root)
    {
        _root = root;
        _index = new Lazy<Dictionary<string, List<string>>>(Scan, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The file creating <paramref name="proc"/>; <paramref name="overridePath"/> wins. Throws <see cref="ScratchRefusedException"/>.</summary>
    public VariantSource Locate(string proc, string? overridePath = null)
    {
        if (overridePath != null)
        {
            var full = Path.Combine(_root, overridePath);
            if (!File.Exists(full)) throw new ScratchRefusedException($"variant source for {proc}: {overridePath} not found");
            var text = File.ReadAllText(full);
            if (!Creates(text).Contains(proc, StringComparer.OrdinalIgnoreCase))
                throw new ScratchRefusedException($"variant source {overridePath} does not create {proc}");
            return new VariantSource(overridePath, text);
        }
        if (!_index.Value.TryGetValue(proc, out var files)) throw new ScratchRefusedException($"variant source for {proc} not found");
        if (files.Count > 1) throw new ScratchRefusedException($"ambiguous: {string.Join(", ", files)}");
        return new VariantSource(files[0], File.ReadAllText(Path.Combine(_root, files[0])));
    }

    private Dictionary<string, List<string>> Scan()
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var ss = Path.Combine(_root, "css", "ss");
        if (!Directory.Exists(ss)) return index;
        foreach (var dir in Directory.EnumerateDirectories(ss).Order(StringComparer.Ordinal))
        foreach (var file in Directory.EnumerateFiles(dir, "pro_*.sql").Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(_root, file).Replace('\\', '/');
            foreach (var name in Creates(File.ReadAllText(file)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!index.TryGetValue(name, out var list)) index[name] = list = new List<string>();
                list.Add(rel);
            }
        }
        return index;
    }

    private static IEnumerable<string> Creates(string text) => CreateRe.Matches(text).Select(m => m.Groups[1].Value);
}
