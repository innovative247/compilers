using System.Text.Json;

namespace CheckApiProcs;

public record ReportHeader(string Url, string ApiVersion, string? Consumer, string? Release, string Declaration,
    string? RecordedVersion, string? RecordedAt);

public static class Report
{
    public static string StaleHint(string proc) =>
        $"live map is outdated (compiled after it was mapped); refresh it with remap.py or GET /api/v1/api3/build/{proc}. This check ran neither.";

    public static Dictionary<string, int> Counts(IEnumerable<ProcResult> results)
    {
        var counts = Verdicts.Order.ToDictionary(Verdicts.Label, _ => 0);
        foreach (var r in results) counts[Verdicts.Label(r.Verdict)]++;
        return counts;
    }

    public static string Summary(IReadOnlyCollection<ProcResult> results) =>
        $"{results.Count} procs: " + string.Join(", ", Counts(results).Where(c => c.Value > 0).Select(c => $"{c.Value} {c.Key}"));

    public static void WriteQuiet(TextWriter o, IEnumerable<ProcResult> results)
    {
        foreach (var r in results) o.WriteLine($"{r.Name} {Verdicts.Label(r.Verdict)}");
    }

    public static void WriteText(TextWriter o, ReportHeader h, IReadOnlyList<ProcResult> results)
    {
        o.WriteLine($"url:          {h.Url}");
        o.WriteLine($"apiengine:    {h.ApiVersion}");
        o.WriteLine($"consumer:     {h.Consumer ?? "-"} {h.Release ?? "-"}");
        o.WriteLine($"declaration:  {h.Declaration} (recorded {h.RecordedAt ?? "-"} on {h.RecordedVersion ?? "-"})");
        o.WriteLine();

        var w = Math.Max(4, results.Select(r => r.Name.Length).DefaultIfEmpty(0).Max()) + 2;
        const int vw = 13;
        var cw = Math.Max(8, results.Select(r => (r.CompiledOn ?? "-").Length).DefaultIfEmpty(0).Max()) + 2;
        o.WriteLine($"{"PROC".PadRight(w)}{"VERDICT".PadRight(vw)}{"COMPILED".PadRight(cw)}MAPPED");
        foreach (var r in results)
            o.WriteLine($"{r.Name.PadRight(w)}{Verdicts.Label(r.Verdict).PadRight(vw)}{(r.CompiledOn ?? "-").PadRight(cw)}{r.MappedOn ?? "-"}");
        o.WriteLine();
        o.WriteLine(Summary(results));

        foreach (var r in results.Where(r => r.Verdict != Verdict.Ok))
        {
            o.WriteLine();
            o.WriteLine($"{r.Name}  {Verdicts.Label(r.Verdict)}{(Verdicts.Failing(r.Verdict) ? "" : "  (warning)")}");
            if (r.Error != null) o.WriteLine($"  error: {r.Error}");
            if (r.Verdict == Verdict.Missing) o.WriteLine("  not in /api/v1/api3/list");
            if (r.Stale) o.WriteLine($"  {StaleHint(r.Name)}");
            foreach (var d in r.Details) o.WriteLine($"  ! {d}");
        }
    }

    public static void WriteJson(TextWriter o, ReportHeader h, IReadOnlyList<ProcResult> results)
    {
        var doc = new
        {
            header = new
            {
                url = h.Url, apiengineVersion = h.ApiVersion, consumer = h.Consumer, release = h.Release,
                declaration = h.Declaration, recordedEngineVersion = h.RecordedVersion, recordedAt = h.RecordedAt,
            },
            results = results.Select(r => new
            {
                proc = r.Name,
                verdict = Verdicts.Label(r.Verdict),
                failing = Verdicts.Failing(r.Verdict),
                stale = r.Stale,
                paramDiff = r.ParamDiff,
                resultDiff = r.ResultDiff,
                compiledOn = r.CompiledOn,
                mappedOn = r.MappedOn,
                error = r.Error,
                hint = r.Stale ? StaleHint(r.Name) : null,
                details = r.Details,
            }),
            counts = Counts(results),
        };
        o.WriteLine(JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }
}
