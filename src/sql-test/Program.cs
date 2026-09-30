using System.Diagnostics;
using ibsCompiler.Configuration;
using SqlTest;

Options opts;
try
{
    var parsed = Options.Parse(args);
    if (parsed == null)
    {
        Console.Error.WriteLine(Options.Usage);
        return 0;
    }
    opts = parsed;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"sql-test: {ex.Message}");
    Console.Error.WriteLine(Options.Usage);
    return 2;
}

var profileMgr = new ProfileManager();
if (!profileMgr.ValidateProfile(opts.Server)) return 2;

var cmdvars = new ibsCompiler.CommandVariables
{
    Server   = opts.Server,
    Database = opts.Database,
    User     = opts.User,
    Pass     = opts.Pass,
};
var profile = profileMgr.Resolve(cmdvars);

Console.Error.WriteLine(opts.BenchPattern == null
    ? $"sql-test: pattern='{opts.Pattern}' db={opts.Database} profile={profile.ProfileName}"
    : $"sql-test: bench='{opts.BenchPattern}' db={opts.Database} profile={profile.ProfileName}");

var runner = new Runner(profile, opts);

// Rows that cannot be restored are warnings, not failures: exit 2 only when the sweep could not run.
if (opts.SweepWriterJournal)
{
    try { runner.SweepWriterJournal(); return 0; }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"sql-test: FATAL: writer journal sweep could not run: {ex.Message}");
        return 2;
    }
}

List<TestCase> cases;
try
{
    cases = runner.Discover();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"sql-test: FATAL: discovery failed: {ex.Message}");
    return 2;
}

// --print-capture-ddl: dump generated DDL for every capture spec and exit.
if (opts.PrintCaptureDdl)
{
    foreach (var c in cases.Where(c => c.Capture != null))
    {
        Console.WriteLine($"-- {c.LogicalName} -> {c.Capture!.IntoTable}");
        try { Console.WriteLine(runner.PrintCaptureDdl(c.Capture)); }
        catch (Exception ex) { Console.Error.WriteLine($"  introspection failed: {ex.Message}"); }
    }
    return 0;
}

// --regenerate-capture-tables: drop every distinct capture target, then exit.
// Subsequent normal runs will re-introspect and re-create on first use.
if (opts.RegenerateCaptureTables)
{
    var targets = cases
        .Where(c => c.Capture != null)
        .Select(c => c.Capture!.IntoTable)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    foreach (var t in targets)
    {
        try { runner.DropCaptureTable(t); Console.Error.WriteLine($"  dropped {t}"); }
        catch (Exception ex) { Console.Error.WriteLine($"  could not drop {t}: {ex.Message}"); }
    }
    Console.Error.WriteLine($"sql-test: dropped {targets.Count} capture table(s)");
    return 0;
}

if (cases.Count == 0)
{
    Console.Error.WriteLine("sql-test: no tests found");
    return 0;
}

if (opts.ListOnly)
{
    foreach (var c in cases)
    {
        if (c.CaptureProc != null)
            Console.WriteLine($"{c.LogicalName}  (capture: {c.CaptureProc} -> {c.Capture!.IntoTable}; assert: {c.AssertProc})");
        else
            Console.WriteLine(c.LogicalName);
    }
    return 0;
}

if (opts.BenchPattern != null)
    return RunBench(runner, cases, opts, profile);

Console.Error.WriteLine($"sql-test: {cases.Count} tests discovered");

var stopwatch = Stopwatch.StartNew();
var results = new List<TestResult>(cases.Count);

if (opts.Parallel <= 1)
{
    foreach (var c in cases)
    {
        var r = runner.RunOne(c);
        results.Add(r);
        PrintResult(r, opts.Verbose);
    }
}
else
{
    var (parallelCases, writerCases) = Runner.PartitionWriters(cases);
    using var gate = new SemaphoreSlim(opts.Parallel);
    var tasks = parallelCases.Select(async c =>
    {
        await gate.WaitAsync();
        try   { return await Task.Run(() => runner.RunOne(c)); }
        finally { gate.Release(); }
    }).ToList();

    foreach (var t in tasks)
    {
        var r = await t;
        results.Add(r);
        PrintResult(r, opts.Verbose);
    }

    if (writerCases.Count > 0)
        Console.Error.WriteLine($"sql-test: running {writerCases.Count} writer test(s) serially after the parallel batch");
    foreach (var c in writerCases)
    {
        var r = runner.RunOne(c);
        results.Add(r);
        PrintResult(r, opts.Verbose);
    }
}
stopwatch.Stop();

PrintSummary(results, stopwatch.Elapsed.TotalSeconds);

if (!string.IsNullOrEmpty(opts.JunitPath))
    JunitWriter.Write(opts.JunitPath, results, stopwatch.Elapsed.TotalSeconds);

bool failed = results.Any(r => r.Outcome is Outcome.FAIL or Outcome.ERROR or Outcome.TIMEOUT);
return failed ? 1 : 0;


// Serial regardless of --parallel: one buffer cache, concurrent runs distort physical reads.
static int RunBench(Runner runner, List<TestCase> cases, Options opts, ResolvedProfile profile)
{
    BenchDocument? baseline = null;
    if (!string.IsNullOrEmpty(opts.BenchBaseline))
    {
        try { baseline = BenchReport.LoadBaseline(opts.BenchBaseline, opts.BenchUpdateBaseline); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"sql-test: FATAL: cannot read baseline: {ex.Message}");
            return 2;
        }
    }
    if (opts.Parallel > 1)
        Console.Error.WriteLine("sql-test: bench mode runs serially; --parallel ignored");
    Console.Error.WriteLine($"sql-test: {cases.Count} benchmarks discovered, 1 warm-up + {opts.Count} measured runs each");

    var stopwatch = Stopwatch.StartNew();
    var results = new List<TestResult>(cases.Count);
    var benches = new List<BenchResult>(cases.Count);
    foreach (var c in cases)
    {
        var runs = new List<IoMeasure>(opts.Count);
        TestResult? failed = null;
        double seconds = 0;
        for (int i = 0; i <= opts.Count; i++)   // run 0 is the discarded warm-up
        {
            var r = runner.RunOne(c, measure: true);
            seconds += r.DurationSeconds;
            if (r.Outcome != Outcome.PASS) { failed = r with { Io = null, MaxReads = null }; break; }
            if (i > 0) runs.Add(r.Io!);
        }
        if (failed != null)
        {
            results.Add(failed);
            PrintResult(failed, opts.Verbose);
            continue;
        }

        var b = BenchReport.Summarize(c.LogicalName, c.BenchThresholdPct, runs);
        benches.Add(b);
        var perOp = new IoMeasure((long)Math.Round(b.ReadsPerOp), (long)Math.Round(b.PhysPerOp),
                                  (long)Math.Round(b.WritesPerOp), Array.Empty<TableIo>());
        results.Add(new TestResult(c.LogicalName, Outcome.PASS, "", seconds, "")
            { Io = perOp, Count = opts.Count });
        Console.Error.WriteLine(BenchReport.FormatLine(b, opts.Count));
        if (opts.Verbose)
            foreach (var line in BenchReport.FormatTables(b)) Console.Error.WriteLine(line);
    }
    stopwatch.Stop();

    PrintSummary(results, stopwatch.Elapsed.TotalSeconds);
    if (!string.IsNullOrEmpty(opts.JunitPath))
        JunitWriter.Write(opts.JunitPath, results, stopwatch.Elapsed.TotalSeconds);

    var now = DateTime.UtcNow;
    var doc = new BenchDocument(BenchReport.Schema,
        string.IsNullOrEmpty(profile.Host) ? profile.ProfileName : profile.Host, opts.Database,
        new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc), opts.Count, benches);
    if (!string.IsNullOrEmpty(opts.BenchOut))
        BenchReport.WriteJson(opts.BenchOut, doc);

    bool anyFailed = results.Any(r => r.Outcome is Outcome.FAIL or Outcome.ERROR or Outcome.TIMEOUT);
    BenchComparison? cmp = null;
    if (baseline != null)
    {
        var failedNames = results.Where(r => r.Outcome is Outcome.FAIL or Outcome.ERROR or Outcome.TIMEOUT)
                                 .Select(r => r.Name).ToList();
        cmp = BenchReport.Compare(baseline, doc, failedNames);
        foreach (var line in BenchReport.FormatComparison(cmp)) Console.Error.WriteLine(line);
    }
    if (opts.BenchUpdateBaseline)
    {
        var passed = results.Where(r => r.Outcome == Outcome.PASS).Select(r => r.Name)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        try { BenchReport.UpdateBaseline(opts.BenchBaseline!, baseline, doc, passed); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"sql-test: FATAL: cannot write baseline: {ex.Message}");
            return 2;
        }
        Console.Error.WriteLine(baseline == null
            ? $"sql-test: baseline created: {opts.BenchBaseline} ({passed.Count} benchmarks)"
            : $"sql-test: baseline updated: {opts.BenchBaseline} ({passed.Count} benchmarks replaced or added)");
    }
    return BenchReport.ExitCode(anyFailed, cmp, opts.BenchUpdateBaseline);
}

static void PrintResult(TestResult r, bool verbose)
{
    Console.Error.WriteLine(
        $"  {r.Outcome,-7} {r.Name,-60} {r.DurationSeconds,5:F2}s");
    if (r.Outcome != Outcome.PASS && !string.IsNullOrEmpty(r.Message))
        Console.Error.WriteLine($"          {r.Message}");
    if (verbose && !string.IsNullOrWhiteSpace(r.Output))
        foreach (var line in r.Output.Split('\n'))
            Console.Error.WriteLine($"          | {line.TrimEnd()}");
}

static void PrintSummary(List<TestResult> results, double total)
{
    int p = results.Count(r => r.Outcome == Outcome.PASS);
    int f = results.Count(r => r.Outcome == Outcome.FAIL);
    int s = results.Count(r => r.Outcome == Outcome.SKIP);
    int e = results.Count(r => r.Outcome is Outcome.ERROR or Outcome.TIMEOUT);
    Console.Error.WriteLine("");
    Console.Error.WriteLine(
        $"{results.Count} tests | {p} passed | {f} failed | " +
        $"{s} skipped | {e} errored | {total:F2}s");
}
