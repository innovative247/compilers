using System.Diagnostics;
using System.Text.RegularExpressions;
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

Console.Error.WriteLine(
    $"sql-test: pattern='{opts.Pattern}' db={opts.Database} profile={profile.ProfileName}");

var runner = new Runner(profile, opts);

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

if (!string.IsNullOrEmpty(opts.Exclude))
{
    var rx = new Regex(opts.Exclude);
    cases = cases.Where(c => !rx.IsMatch(c.LogicalName)).ToList();
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
    using var gate = new SemaphoreSlim(opts.Parallel);
    var tasks = cases.Select(async c =>
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
}
stopwatch.Stop();

PrintSummary(results, stopwatch.Elapsed.TotalSeconds);

if (!string.IsNullOrEmpty(opts.JunitPath))
    JunitWriter.Write(opts.JunitPath, results, stopwatch.Elapsed.TotalSeconds);

bool failed = results.Any(r => r.Outcome is Outcome.FAIL or Outcome.ERROR or Outcome.TIMEOUT);
return failed ? 1 : 0;


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
