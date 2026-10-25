namespace CheckApiProcs;

public enum Verdict { Ok, MapError, Missing, ParamDiff, MapStale, ResultDiff, Unverified }

public static class Verdicts
{
    public static string Label(Verdict v) => v switch
    {
        Verdict.Ok => "OK",
        Verdict.MapError => "MAP-ERROR",
        Verdict.Missing => "MISSING",
        Verdict.ParamDiff => "PARAM-DIFF",
        Verdict.MapStale => "MAP-STALE",
        Verdict.Unverified => "UNVERIFIED",
        _ => "RESULT-DIFF",
    };

    /// <summary>Report order; also the precedence order of the per-proc verdicts.</summary>
    public static readonly Verdict[] Order =
        { Verdict.MapError, Verdict.Missing, Verdict.ParamDiff, Verdict.MapStale, Verdict.ResultDiff, Verdict.Unverified, Verdict.Ok };

    /// <summary>ADR-0019 Decision 4: stale maps, result-column mismatches and unverified marked procs warn only.</summary>
    public static bool Failing(Verdict v) => v is Verdict.MapError or Verdict.Missing or Verdict.ParamDiff;
}

public class ProcResult
{
    public string Name { get; set; } = "";
    public Verdict Verdict { get; set; }
    public string? CompiledOn { get; set; }
    public string? MappedOn { get; set; }
    public bool Stale { get; set; }
    public bool ParamDiff { get; set; }
    public bool ResultDiff { get; set; }
    public string? Error { get; set; }
    public List<string> Details { get; } = new();
}

public static class Checker
{
    /// <summary>Lowercase, whitespace stripped: varchar( 12 ) == VARCHAR(12).</summary>
    public static string Normalize(string s) =>
        new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    private static bool IsTrue(string s) => Normalize(s) == "true";

    /// <summary>The engine copies default text verbatim, so the unquoted keyword NULL is spelled null; quoted literals keep their case.</summary>
    public static string? NormalizeDefault(string? d) =>
        d != null && d.Trim().Equals("null", StringComparison.OrdinalIgnoreCase) ? "null" : d?.Trim();

    /// <summary>Live map parameter in declaration form (format doc § 3); no_default is JSON null, distinct from the keyword null.</summary>
    public static DeclParam Convert(LiveParam p) => new(
        p.Name, p.Type,
        Normalize(p.Default) == "no_default" ? null : NormalizeDefault(p.Default),
        IsTrue(p.Output), IsTrue(p.Required));

    public static string Format(DeclParam p) =>
        $"{p.Name} {p.Type} {(p.Default == null ? "no_default" : $"default {p.Default}")}{(p.Output ? " output" : "")}";

    /// <summary>A never-built map lists as outdated with an empty createdOn; the map GET builds it fresh, so it is not stale.</summary>
    public static bool IsStale(ServerProc? listed) => listed is { Outdated: true } && !string.IsNullOrEmpty(listed.CreatedOn);

    /// <summary>The name as /api/v1/list reports it: api3_geta converts names to varchar(32).</summary>
    internal static string ListName(string name) => name.Length > 32 ? name[..32] : name;

    /// <summary>ADR-0019 Decision 4 for parameters; one detail line per difference.</summary>
    public static List<string> CompareParams(IReadOnlyList<DeclParam> recorded, IReadOnlyList<DeclParam> live)
    {
        var diffs = new List<string>();
        var liveIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < live.Count; i++) liveIndex.TryAdd(live[i].Name, i);

        var found = new List<(DeclParam Rec, int At)>();
        foreach (var r in recorded)
        {
            if (!liveIndex.TryGetValue(r.Name, out var at)) { diffs.Add($"{r.Name}: removed on the engine (recorded {Format(r)})"); continue; }
            found.Add((r, at));
            var l = live[at];
            if (Normalize(r.Type) != Normalize(l.Type)) diffs.Add($"{r.Name}: type {r.Type} -> {l.Type}");
            if (NormalizeDefault(r.Default) != NormalizeDefault(l.Default))
                diffs.Add($"{r.Name}: default {r.Default ?? "no_default"} -> {l.Default ?? "no_default"}");
            if (r.Output != l.Output) diffs.Add($"{r.Name}: output {r.Output.ToString().ToLowerInvariant()} -> {l.Output.ToString().ToLowerInvariant()}");
        }
        // Relative order only, so an extra optional engine parameter may sit anywhere.
        if (found.Zip(found.Skip(1)).Any(p => p.First.At > p.Second.At))
            diffs.Add($"order: recorded {string.Join(", ", found.Select(f => f.Rec.Name))}; engine {string.Join(", ", found.OrderBy(f => f.At).Select(f => f.Rec.Name))}");

        var recordedNames = new HashSet<string>(recorded.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var l in live.Where(l => !recordedNames.Contains(l.Name) && l.Required))
            diffs.Add($"{l.Name}: required by the engine, not declared ({Format(l)})");
        return diffs;
    }

    /// <summary>Only recorded sets are compared; an extra live set is ignored, except on a framework engine, whose merged list must match exactly.</summary>
    public static List<string> CompareResultSets(IReadOnlyList<DeclResultSet> recorded, LiveMap live)
    {
        var diffs = new List<string>();
        if (live.FlatResultSet is { } flat)
        {
            var all = recorded.SelectMany(s => s.Columns).ToList();
            if (!all.SequenceEqual(flat, StringComparer.OrdinalIgnoreCase))
                diffs.Add($"result sets (merged on this engine): columns {string.Join(", ", all)} -> {string.Join(", ", flat)}");
            return diffs;
        }
        foreach (var s in recorded)
        {
            if (!live.ResultSets.TryGetValue(s.Name, out var cols)) { diffs.Add($"result set {s.Name}: missing on the engine"); continue; }
            if (!s.Columns.SequenceEqual(cols, StringComparer.OrdinalIgnoreCase))
                diffs.Add($"result set {s.Name}: columns {string.Join(", ", s.Columns)} -> {string.Join(", ", cols)}");
        }
        return diffs;
    }

    /// <summary>Precedence: MAP-ERROR > MISSING > PARAM-DIFF > MAP-STALE > RESULT-DIFF > OK; a marked proc is MISSING or UNVERIFIED.</summary>
    public static ProcResult Evaluate(DeclProc decl, ServerProc? listed, LiveMap? map, string? mapError)
    {
        var r = new ProcResult
        {
            Name = decl.Name,
            CompiledOn = listed?.CompiledOn,
            MappedOn = map?.CreatedOn ?? listed?.CreatedOn,
            Stale = !decl.Unrecorded && IsStale(listed),
        };
        // A marked proc only has to be present; its signature is never compared (ADR-0019 Decision 4).
        if (decl.Unrecorded) { r.Verdict = listed == null ? Verdict.Missing : Verdict.Unverified; return r; }
        if (mapError != null) { r.Verdict = Verdict.MapError; r.Error = mapError; return r; }
        if (listed == null || map == null) { r.Verdict = Verdict.Missing; return r; }

        var pd = CompareParams(decl.Parameters ?? new(), map.Parameters.Select(Convert).ToList());
        var rd = CompareResultSets(decl.ResultSets ?? new(), map);
        r.ParamDiff = pd.Count > 0;
        r.ResultDiff = rd.Count > 0;
        r.Details.AddRange(pd);
        r.Details.AddRange(rd);
        r.Verdict = r.ParamDiff ? Verdict.ParamDiff : r.Stale ? Verdict.MapStale : r.ResultDiff ? Verdict.ResultDiff : Verdict.Ok;
        return r;
    }

    /// <summary>Fetches every listed proc's map except marked procs, at most <paramref name="concurrency"/> at a time; null map with the error when the call fails.</summary>
    public static async Task<List<(DeclProc Proc, ServerProc? Listed, LiveMap? Map, string? Error)>> FetchAsync(
        ApiClient api, IReadOnlyList<DeclProc> procs, List<ServerProc> list, int concurrency)
    {
        var byName = new Dictionary<string, ServerProc>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in list) byName.TryAdd(p.Name, p);

        using var gate = new SemaphoreSlim(concurrency);
        async Task<(DeclProc, ServerProc?, LiveMap?, string?)> One(DeclProc proc)
        {
            byName.TryGetValue(ListName(proc.Name), out var listed);
            // Presence is the list lookup alone; a marked proc's map is never fetched.
            if (listed == null || proc.Unrecorded) return (proc, listed, null, null);
            await gate.WaitAsync();
            try { return (proc, listed, await api.MapAsync(proc.Name), null); }
            catch (ApiException ex) { return (proc, listed, null, ex.Message); }
            finally { gate.Release(); }
        }
        return (await Task.WhenAll(procs.Select(One))).ToList();
    }

    /// <summary>Results in declaration order.</summary>
    public static async Task<List<ProcResult>> RunAsync(ApiClient api, IReadOnlyList<DeclProc> procs, List<ServerProc> list, int concurrency) =>
        (await FetchAsync(api, procs, list, concurrency)).Select(f => Evaluate(f.Proc, f.Listed, f.Map, f.Error)).ToList();
}
