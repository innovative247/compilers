using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlTest;

public sealed record BenchRun(long Reads, long Phys, long Writes);
public sealed record BenchTable(string Table, double ReadsPerOp, double PhysPerOp);
public sealed record BenchResult(string Name, int? ThresholdPct, double ReadsPerOp, double PhysPerOp, double WritesPerOp,
                                 List<BenchRun> Runs, List<BenchTable> Tables);
public sealed record BenchDocument(int Schema, string Server, string Database, DateTime Date, int Count, List<BenchResult> Benchmarks);

public sealed record CompareRow(string Name, string Value, double? Old, double? New, string Delta, bool Flagged, int? ThresholdPct);
public sealed record BenchComparison(string BaselineServer, string CurrentServer, IReadOnlyList<CompareRow> Rows, string? Warning)
{
    public int ExitCode => Rows.Any(r => r.Flagged) ? 1 : 0;
}

public static class BenchReport
{
    public const int Schema = 1;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static BenchResult Summarize(string name, int? thresholdPct, IReadOnlyList<IoMeasure> runs)
    {
        int n = Math.Max(runs.Count, 1);
        var tables = runs.SelectMany(r => r.Tables)
            .GroupBy(t => t.Table, StringComparer.OrdinalIgnoreCase)
            .Select(g => new BenchTable(g.Key, (double)g.Sum(t => t.LogicalReads) / n, (double)g.Sum(t => t.PhysicalReads) / n))
            .OrderByDescending(t => t.ReadsPerOp).ThenBy(t => t.Table, StringComparer.Ordinal)
            .ToList();
        return new BenchResult(name, thresholdPct,
            (double)runs.Sum(r => r.LogicalReads) / n,
            (double)runs.Sum(r => r.PhysicalReads) / n,
            (double)runs.Sum(r => r.Writes) / n,
            runs.Select(r => new BenchRun(r.LogicalReads, r.PhysicalReads, r.Writes)).ToList(),
            tables);
    }

    public static string FormatLine(BenchResult r, int count) =>
        string.Format(Inv, "  {0,-44} {1,3} {2,8:F0} reads/op {3,6:F0} phys/op {4,6:F0} writes/op",
            r.Name, count, r.ReadsPerOp, r.PhysPerOp, r.WritesPerOp);

    public static IEnumerable<string> FormatTables(BenchResult r) =>
        r.Tables.Select(t => string.Format(Inv, "          {0,-36} {1,8:F0} reads/op {2,6:F0} phys/op",
            t.Table, t.ReadsPerOp, t.PhysPerOp));

    // Temp file + move in the same directory, so a crash never leaves a truncated baseline.
    public static void WriteJson(string path, BenchDocument doc)
    {
        var full = Path.GetFullPath(path);
        var tmp = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Json));
            File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public static BenchDocument ReadJson(string path)
    {
        var doc = JsonSerializer.Deserialize<BenchDocument>(File.ReadAllText(path), Json)
                  ?? throw new InvalidDataException($"{path}: empty benchmark document");
        if (doc.Schema != Schema)
            throw new InvalidDataException($"{path}: unsupported schema {doc.Schema} (expected {Schema})");
        if (doc.Benchmarks == null)
            throw new InvalidDataException($"{path}: missing benchmarks");
        return doc;
    }

    // failed: benchmarks that ended FAIL/ERROR/TIMEOUT this run, so they are not mistaken for removed ones.
    public static BenchComparison Compare(BenchDocument baseline, BenchDocument current, IReadOnlyList<string>? failed = null)
    {
        var rows = new List<CompareRow>();
        var old = baseline.Benchmarks.ToDictionary(b => b.Name, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cur in current.Benchmarks)
        {
            seen.Add(cur.Name);
            if (!old.TryGetValue(cur.Name, out var b))
            {
                rows.Add(new CompareRow(cur.Name, "", null, null, "new", false, cur.ThresholdPct));
                continue;
            }
            // Threshold comes from the current run's directive; only logical reads are flagged.
            rows.Add(Row(cur.Name, "reads",  b.ReadsPerOp,  cur.ReadsPerOp,  cur.ThresholdPct));
            rows.Add(Row(cur.Name, "phys",   b.PhysPerOp,   cur.PhysPerOp,   null));
            rows.Add(Row(cur.Name, "writes", b.WritesPerOp, cur.WritesPerOp, null));
        }
        foreach (var name in failed ?? Array.Empty<string>())
            if (seen.Add(name))
                rows.Add(new CompareRow(name, "", null, null, "failed", false, null));
        foreach (var b in baseline.Benchmarks.Where(b => !seen.Contains(b.Name)))
            rows.Add(new CompareRow(b.Name, "", null, null, "gone", false, null));

        string? warning = string.Equals(baseline.Server, current.Server, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"warning: baseline server '{baseline.Server}' differs from current server '{current.Server}'";
        return new BenchComparison(baseline.Server, current.Server, rows, warning);
    }

    private static CompareRow Row(string name, string value, double old, double cur, int? thresholdPct)
    {
        double? pct = old == 0 ? (cur == 0 ? 0 : null) : (cur - old) / old * 100;
        string delta = pct is double p ? (p > 0 ? "+" : "") + p.ToString("F1", Inv) + "%" : "n/a";
        bool flagged = thresholdPct is int t && pct is double d && d > t;
        return new CompareRow(name, value, old, cur, delta, flagged, thresholdPct);
    }

    public static IEnumerable<string> FormatComparison(BenchComparison c)
    {
        if (c.Warning != null) yield return c.Warning;
        yield return $"baseline server={c.BaselineServer}  current server={c.CurrentServer}";
        yield return string.Format(Inv, "{0,-44} {1,-6} {2,9} {3,9} {4,9}", "name", "value", "old", "new", "delta");
        foreach (var r in c.Rows)
        {
            var line = string.Format(Inv, "{0,-44} {1,-6} {2,9} {3,9} {4,9}",
                r.Name, r.Value, r.Old?.ToString("F0", Inv) ?? "", r.New?.ToString("F0", Inv) ?? "", r.Delta);
            if (r.Flagged) line += $"  ! (threshold {r.ThresholdPct}%)";
            yield return line;
        }
    }

    // Header from the current run; entries merged by name so benchmarks not run (or failed) keep their reference.
    public static BenchDocument Merge(BenchDocument? baseline, BenchDocument current, IReadOnlySet<string> passed)
    {
        var fresh = current.Benchmarks.Where(b => passed.Contains(b.Name))
                                      .ToDictionary(b => b.Name, StringComparer.OrdinalIgnoreCase);
        var merged = new List<BenchResult>();
        foreach (var b in baseline?.Benchmarks ?? new List<BenchResult>())
            merged.Add(fresh.Remove(b.Name, out var cur) ? cur : b);
        merged.AddRange(current.Benchmarks.Where(b => fresh.ContainsKey(b.Name)));
        return current with { Schema = Schema, Benchmarks = merged };
    }

    // An update accepts the flagged deltas, so only a benchmark's own failure fails the run.
    public static int ExitCode(bool anyFailed, BenchComparison? cmp, bool updateBaseline) =>
        anyFailed ? 1 : updateBaseline ? 0 : cmp?.ExitCode ?? 0;

    // Null means "no baseline yet": only valid when the run is going to create it.
    // The create target is checked here so a bad path fails before the benchmarks run, not after.
    public static BenchDocument? LoadBaseline(string path, bool updateBaseline)
    {
        if (!updateBaseline || File.Exists(path)) return ReadJson(path);
        if (Directory.Exists(path))
            throw new IOException($"baseline path is a directory: {path}");
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException($"baseline directory does not exist: {parent}");
        return null;
    }

    public static void UpdateBaseline(string path, BenchDocument? baseline, BenchDocument current, IReadOnlySet<string> passed) =>
        WriteJson(path, Merge(baseline, current, passed));
}
