using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AdoNetCore.AseClient;
using ibsCompiler.Configuration;

namespace SqlTest;

/// <summary>
/// Discovers and executes SQL unit-test procs.
///
/// Classification model (priority: FAIL > SKIP > ERROR > PASS):
/// - FAIL is signalled by `raiserror 50001` (the preceding `print 'FAIL: …'`
///   line is captured via InfoMessage and used as the failure message).
/// - SKIP is signalled by `raiserror 50002`.
/// - Any other severity-11+ Sybase error is ERROR.
/// - No exception means PASS.
/// - @@trancount other than 1 (0 for `-- @no-transaction`) after the test turns PASS/SKIP into FAIL
///   (a proc's `rollback tran` ends the runner's tran and later statements commit); ERROR/TIMEOUT keep their outcome with a note.
///
/// Two test shapes are supported:
/// - Singleton: one `test_<name>` proc; runner ExecuteNonQuery's it inside
///   begin tran/rollback tran.
/// - Paired: `test_<name>_capture` + `test_<name>_assert`. The capture proc
///   emits a result set which the runner streams into a permanent capture
///   table (auto-introspected and created on first use via SET FMTONLY ON
///   + IDataReader.GetSchemaTable). The assert proc then runs assertions
///   against the capture table. Both procs run inside the same transaction
///   wrap and the table is cleared by the rollback at end-of-test.
///
/// Writer mode: a `-- @no-transaction` test with `-- @restore: <db>..<table> where <pred>`
/// lines commits its writes. Discovery refuses unsafe specs (see WriterJournal.RefuseReason);
/// RunOne journals and snapshots the rows on a separate control connection before the
/// batch and restores them there after it, whatever the outcome. A restore failure makes
/// the result ERROR and keeps the journal row. Writers run serially under --parallel.
/// </summary>
public class Runner
{
    private const int FailErrorNumber = 50001;
    private const int SkipErrorNumber = 50002;

    private static readonly Regex CaptureIntoRe   = new(@"^\s*--\s*@capture-into\s*:\s*(\S+)",     RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex CaptureSourceRe = new(@"^\s*--\s*@capture-source\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex NoTranRe        = new(@"^\s*--\s*@no-transaction\b",            RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex BenchThresholdRe = new(@"^\s*--\s*@bench-threshold\b(.*)$",        RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex BenchThresholdValueRe = new(@"^\s*:\s*(\d{1,9})\s*%?\s*$");
    private static readonly Regex BudgetRe        = new(@"\bpro_test_assert_max_reads\b",           RegexOptions.IgnoreCase);

    private const int StatTableMessage  = 3615;
    // A cheap set statement: a connection that cannot run it in this time is unusable anyway.
    private const int DisableMeterTimeoutSeconds = 10;
    private const int StatWritesMessage = 3614;

    internal const string PairBudgetError = "budget not supported in _capture/_assert pairs";
    internal const string UnmeteredMarkerError = "budget marker in an unmetered run (call pro_test_assert_max_reads from the test body)";

    private readonly ResolvedProfile _profile;
    private readonly Options _opts;
    private readonly SourceLocator _locator;
    private readonly IScratchCompiler _scratchCompiler;

    // One mode per invocation: --bench replaces --pattern.
    private string Pattern => _opts.BenchPattern ?? _opts.Pattern;

    // Per-session rebuild cache: each distinct capture table is dropped
    // and recreated exactly once on first reference. Eliminates schema
    // drift -- a stale capture table can never silently outlive a change
    // to the source proc's emitted shape. Cached value is the table's
    // column names + .NET types, used downstream for shape-matched
    // filtering during the capture phase.
    private readonly ConcurrentDictionary<string, Lazy<CaptureTableSchema>> _rebuiltTables =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record CaptureTableSchema(
        IReadOnlyList<string> ColumnNames,
        IReadOnlyList<Type>   ColumnTypes)
    {
        public static CaptureTableSchema FromIntrospectedSchema(DataTable schema)
        {
            var names = new List<string>(schema.Rows.Count);
            var types = new List<Type>(schema.Rows.Count);
            foreach (DataRow row in schema.Rows)
            {
                names.Add((string)row["ColumnName"]);
                types.Add((Type)row["DataType"]);
            }
            return new CaptureTableSchema(names, types);
        }
    }

    public Runner(ResolvedProfile profile, Options opts, ResolvedProfile? variantProfile = null)
    {
        _profile = profile;
        _opts = opts;
        _locator = new SourceLocator(opts.SourceRoot ?? profile.IRPath);
        _scratchCompiler = new RunsqlScratchCompiler(profile, opts.Server, variantProfile, use =>
        {
            using var conn = new AseConnection(BuildConnectionString(_opts.Database));
            conn.Open();
            use(new AseSqlExec(conn, _opts.TimeoutSeconds));
        });
        // Sybase TDS may negotiate a non-UTF8 charset (e.g. cp850); without this
        // the InfoMessage stream throws "unsupported charset" on connection.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public List<TestCase> Discover()
    {
        // A failed sweep never blocks the run: the held check refuses only the tests on its tables.
        if (DiscoverSweepRestoreTimeout(_opts) is { } restoreTimeout)
            try { SweepWriterJournal(restoreTimeout); }
            catch (Exception ex) { Console.Error.WriteLine($"sql-test: WARNING: writer journal sweep could not run: {ex.Message}"); }

        var procNames = QueryProcNames();
        var pretests  = QueryPretests();
        var bodies    = FetchAllProcBodies();   // one round-trip; needed to read per-test directives
        var cases = new List<TestCase>();
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Teardown procs (`<base>_teardown`) are cleanup hooks for @no-transaction
        // tests, never standalone tests — consume them up front so they don't get
        // discovered as their own `test_*`.
        var teardowns = procNames
            .Where(n => n.EndsWith("_teardown", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in teardowns) consumed.Add(t);

        // Pair: for each `*_capture`, find matching `*_assert`.
        // Bench procs are singletons: capture INSERTs would be measured.
        foreach (var name in _opts.BenchPattern != null ? Enumerable.Empty<string>() : procNames)
        {
            if (!name.EndsWith("_capture", StringComparison.OrdinalIgnoreCase)) continue;
            var baseName = name[..^"_capture".Length];
            var assertName = baseName + "_assert";
            if (!procNames.Contains(assertName)) continue;

            var body     = bodies.GetValueOrDefault(name);
            var spec     = ParseCaptureSpecFromBody(body);
            var noTran   = body != null && NoTranRe.IsMatch(body);
            var teardown = teardowns.Contains(baseName + "_teardown") ? baseName + "_teardown" : null;

            // The capture phase runs unmetered, so a pair's budget could never be checked.
            var assertBody = bodies.GetValueOrDefault(assertName);
            var budgetError = HasBudget(body) || HasBudget(assertBody) ? PairBudgetError : null;
            var restores = ResolvePairRestores(body, assertBody, noTran, out var restoreError);
            var variant = ResolvePairVariant(body, assertBody, out var variantError);
            cases.Add(new TestCase(baseName, name, assertName, spec,
                                   ResolvePretest(baseName, pretests), noTran, teardown,
                                   Error: budgetError ?? restoreError ?? variantError, Restores: restores)
                      { Variant = variant });
            consumed.Add(name);
            consumed.Add(assertName);
        }

        // Singletons: everything not consumed by pairing or teardown.
        foreach (var name in procNames)
        {
            if (consumed.Contains(name)) continue;
            var body     = bodies.GetValueOrDefault(name);
            var noTran   = body != null && NoTranRe.IsMatch(body);
            var teardown = teardowns.Contains(name + "_teardown") ? name + "_teardown" : null;
            string? thresholdError = null;
            var threshold = _opts.BenchPattern != null ? ParseBenchThreshold(body, out thresholdError) : null;
            var restores = ResolveRestores(body, noTran, out var restoreError);
            var variant = ResolveVariant(body, _opts.BenchPattern != null, out var variantError);
            cases.Add(new TestCase(name, null, name, null,
                                   ResolvePretest(name, pretests), noTran, teardown,
                                   threshold, HasBudget(body), thresholdError ?? restoreError ?? variantError, restores)
                      { Variant = variant });
        }

        // Before the probe, so an excluded writer neither probes nor creates the journal tables.
        if (!string.IsNullOrEmpty(_opts.Exclude))
        {
            var rx = new Regex(_opts.Exclude);
            cases.RemoveAll(c => rx.IsMatch(c.LogicalName));
        }
        MarkTagConflicts(cases);

        // Only writers open the probe connection: plain runs stay as they were.
        if (cases.Any(c => c.IsWriter && c.Error == null))
            ProbeWriters(cases);

        return cases.OrderBy(c => c.LogicalName).ToList();
    }

    /// <summary>
    /// Restore deadline for the sweep at Discover, or null to skip it: --list and --print-capture-ddl
    /// stay read-only. --timeout, so a locked table cannot hang every run; a cancelled restore rolls back.
    /// </summary>
    internal static int? DiscoverSweepRestoreTimeout(Options o) =>
        o.ListOnly || o.PrintCaptureDdl ? null : o.TimeoutSeconds;

    /// <summary>Restores dead runners' journal rows (design §2.4). Throws when the sweep cannot run.</summary>
    /// <param name="restoreTimeoutSeconds">0 (no deadline) for --sweep-writer-journal.</param>
    public void SweepWriterJournal(int restoreTimeoutSeconds = 0)
    {
        using var conn = new AseConnection(BuildConnectionString(_opts.Database));
        conn.Open();
        var x = new AseSqlExec(conn, _opts.TimeoutSeconds);
        WriterSweep.Sweep(x, new AseSqlExec(conn, restoreTimeoutSeconds), _opts.Database, Console.Error.WriteLine,
                          ScratchProc.SweepDropper(x, _opts.Database));
    }

    /// <summary>Null (not empty) for non-writers so their TestCase is unchanged.</summary>
    internal static IReadOnlyList<RestoreSpec>? ResolveRestores(string? body, bool noTran, out string? error)
    {
        var specs = WriterJournal.ResolveWriter(body, noTran, out error);
        return specs.Count > 0 ? specs : null;
    }

    internal const string AssertRestoreError = "@restore belongs in the _capture proc";

    // The restore wraps the whole pair, so its recipe is read from the capture proc only.
    internal static IReadOnlyList<RestoreSpec>? ResolvePairRestores(string? captureBody, string? assertBody, bool noTran, out string? error)
    {
        var specs = ResolveRestores(captureBody, noTran, out error);
        if (error == null && WriterJournal.HasRestoreLine(assertBody)) error = AssertRestoreError;
        return specs;
    }

    internal const string AssertVariantError = "@variant belongs in the _capture proc";
    internal const string BenchVariantError = "@variant is not supported with --bench";

    internal static VariantSpec? ResolveVariant(string? body, bool bench, out string? error)
    {
        var v = Variants.Parse(body, out error);
        if (v != null && bench) { error = BenchVariantError; return null; }
        return v;
    }

    // The chain wraps the whole pair, so it is read from the capture proc only.
    internal static VariantSpec? ResolvePairVariant(string? captureBody, string? assertBody, out string? error)
    {
        var v = ResolveVariant(captureBody, false, out error);
        if (error == null && (Variants.Parse(assertBody, out var assertError) != null || assertError != null))
        { error = AssertVariantError; return null; }
        return v;
    }

    // Tests sharing a tag share scratch names, so their specs must match (design §3.2).
    internal static void MarkTagConflicts(List<TestCase> cases)
    {
        var byTag = cases.Where(c => c.Variant != null).GroupBy(c => c.Variant!.Tag).ToList();
        for (int i = 0; i < cases.Count; i++)
        {
            var c = cases[i];
            if (c.Variant == null) continue;
            var others = byTag.Single(g => g.Key == c.Variant.Tag)
                              .Where(o => !o.Variant!.SameAs(c.Variant)).Select(o => o.LogicalName).ToList();
            if (others.Count > 0)
                cases[i] = c with { Error = c.Error ?? $"tag conflict: {c.Variant.Tag} differs in {string.Join(", ", others)}" };
        }
    }

    // One control connection for every probe; the first refused spec becomes the case's Error.
    private void ProbeWriters(List<TestCase> cases)
    {
        using var conn = new AseConnection(BuildConnectionString(_opts.Database));
        conn.Open();
        var x = new AseSqlExec(conn, _opts.TimeoutSeconds);
        for (int i = 0; i < cases.Count; i++)
        {
            var tc = cases[i];
            if (!tc.IsWriter || tc.Error != null) continue;
            string? reason = null;
            foreach (var spec in tc.Restores!)
            {
                try { reason = WriterJournal.RefuseReason(spec, WriterJournal.Probe(x, spec, _opts.Database)); }
                catch (Exception ex) { reason = $"@restore probe failed for {spec.FullName}: {ex.Message}"; }
                if (reason != null) break;
            }
            if (reason != null) cases[i] = tc with { Error = reason };
        }
    }

    /// <summary>Restore failure outranks every outcome; the original is kept in the message.</summary>
    internal static TestResult WithRestore(TestResult r, string? restoreError)
    {
        if (restoreError == null) return r;
        var original = r.Outcome == Outcome.PASS ? "" : $" | was {r.Outcome}: {r.Message}";
        return r with { Outcome = Outcome.ERROR, Message = restoreError + original };
    }

    /// <summary>
    /// Writers commit, so two at once could collide on a table; they run serially
    /// after the parallel batch. Same-tag variant tests would refuse each other as held.
    /// </summary>
    internal static (List<TestCase> Parallel, List<TestCase> Serial) PartitionWriters(IEnumerable<TestCase> cases)
    {
        var parallel = new List<TestCase>();
        var serial = new List<TestCase>();
        foreach (var c in cases) (c.IsWriter || c.Variant != null ? serial : parallel).Add(c);
        return (parallel, serial);
    }

    /// <summary>
    /// Fetch every discovered test proc's full body text in a single round-trip,
    /// keyed by proc name. syscomments splits a proc body across rows (ordered by
    /// colid2, colid); we concatenate per object. Needed because the runner reads
    /// per-test directives (@capture-*, @no-transaction) from the body, and doing
    /// that per-proc would be one connection per test at discovery time.
    /// </summary>
    private Dictionary<string, string> FetchAllProcBodies()
    {
        using var conn = new AseConnection(BuildConnectionString(_opts.Database));
        conn.Open();
        using var cmd = new AseCommand(
            "select o.name, c.text from syscomments c " +
            "inner join sysobjects o on o.id = c.id " +
            "where o.type = 'P' and o.name like @pat escape '\\' " +
            "order by o.name, c.colid2, c.colid",
            conn);
        cmd.Parameters.Add("@pat", Pattern);

        var map = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(1)) continue;
            var name = reader.GetString(0);
            if (!map.TryGetValue(name, out var sb)) { sb = new StringBuilder(); map[name] = sb; }
            sb.Append(reader.GetString(1));
        }
        return map.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Discover `pro_test_&lt;area&gt;_pretest` procs, mapping area → proc name.
    /// A pretest is auto-invoked before every test whose name begins
    /// `test_&lt;area&gt;_...`, inside the per-test tran (Go TestMain analog).
    /// Convention: each pretest takes a single `@tstuser_out varchar(8)
    /// output` param; the runner passes the captured value as the test's
    /// first positional parameter.
    /// </summary>
    private Dictionary<string, string> QueryPretests()
    {
        using var conn = new AseConnection(BuildConnectionString(_opts.Database));
        conn.Open();
        using var cmd = new AseCommand(
            "select name from sysobjects " +
            "where type = 'P' and name like 'pro\\_test\\_%\\_pretest' escape '\\'",
            conn);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);            // pro_test_<area>_pretest
            var area = name["pro_test_".Length..^"_pretest".Length];
            if (area.Length > 0) map[area] = name;
        }
        return map;
    }

    /// <summary>
    /// Longest-prefix match: `test_passcard_verify_key_passcard_pk` resolves
    /// to `pro_test_passcard_verify_pretest` if that area is registered.
    /// Walks segment prefixes longest-first; first hit wins.
    /// </summary>
    private static string? ResolvePretest(string testName, Dictionary<string, string> pretests)
    {
        if (pretests.Count == 0) return null;
        if (!testName.StartsWith("test_", StringComparison.OrdinalIgnoreCase)) return null;
        var segments = testName["test_".Length..].Split('_');
        for (int n = segments.Length; n > 0; n--)
        {
            var candidate = string.Join('_', segments[..n]);
            if (pretests.TryGetValue(candidate, out var proc)) return proc;
        }
        return null;
    }

    /// <summary>
    /// Build the exec batch for a test/capture proc, optionally prefixed with
    /// its pretest. When a pretest exists, the runner declares @tstuser,
    /// captures the pretest's output, and passes it as the proc's first arg —
    /// all in one batch so the variable is in scope.
    /// </summary>
    private static string WithPretest(string? pretest, string execTarget)
        => pretest == null
            ? $"exec {execTarget}"
            : $"declare @tstuser varchar(8)\n" +
              $"exec {pretest} @tstuser_out = @tstuser output\n" +
              $"exec {execTarget} @tstuser";

    private HashSet<string> QueryProcNames()
    {
        using var conn = new AseConnection(BuildConnectionString(_opts.Database));
        conn.Open();
        using var cmd = new AseCommand(
            "select name from sysobjects " +
            "where type = 'P' and name like @pat escape '\\' " +
            "order by name",
            conn);
        cmd.Parameters.Add("@pat", Pattern);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private static CaptureSpec? ParseCaptureSpecFromBody(string? body)
    {
        if (body == null) return null;

        var intoMatch   = CaptureIntoRe.Match(body);
        var sourceMatch = CaptureSourceRe.Match(body);
        if (!intoMatch.Success || !sourceMatch.Success) return null;

        return new CaptureSpec(
            IntoTable:  intoMatch.Groups[1].Value.Trim(),
            SourceCall: sourceMatch.Groups[1].Value.Trim());
    }

    internal static bool HasBudget(string? body) => body != null && BudgetRe.IsMatch(body);

    // An unparseable directive is an error, never a silently different threshold.
    internal static int? ParseBenchThreshold(string? body, out string? error)
    {
        error = null;
        var m = body == null ? null : BenchThresholdRe.Match(body);
        if (m is not { Success: true }) return null;
        var v = BenchThresholdValueRe.Match(m.Groups[1].Value);
        if (v.Success && int.TryParse(v.Groups[1].Value, System.Globalization.NumberStyles.None,
                                      System.Globalization.CultureInfo.InvariantCulture, out var pct))
            return pct;
        error = $"invalid @bench-threshold directive: {m.Value.Trim()}";
        return null;
    }

    // A marker printed with no meter attached would otherwise pass silently.
    internal static TestResult CheckUnmeteredMarker(TestResult r, IEnumerable<string> messages)
    {
        if (!messages.Any(m => m.TrimStart().StartsWith(MeasureAccumulator.AssertMarker, StringComparison.Ordinal)))
            return r;
        var outcome = r.Outcome is Outcome.PASS or Outcome.SKIP ? Outcome.ERROR : r.Outcome;
        return r with
        {
            Outcome = outcome,
            Message = string.IsNullOrEmpty(r.Message) ? UnmeteredMarkerError : $"{UnmeteredMarkerError} | {r.Message}"
        };
    }

    /// <summary>
    /// Opens a bench's control connection and journal row once, before the warm-up.
    /// Null with <paramref name="error"/> set when setup fails.
    /// </summary>
    public WriterBench? OpenWriterBench(TestCase tc, out TestResult? error)
    {
        error = null;
        var stopwatch = Stopwatch.StartNew();
        var control = new AseConnection(BuildConnectionString(_opts.Database));
        try
        {
            control.Open();
            // The test pair is set per run, once each run's test connection exists.
            var session = WriterSession.Begin(new AseSqlExec(control, _opts.TimeoutSeconds), _opts.Database,
                                              tc.LogicalName, tc.Restores!, null, null,
                                              restoreX: new AseSqlExec(control, 0));
            return new WriterBench(session, control);
        }
        catch (Exception ex)
        {
            try { control.Dispose(); } catch { }
            error = new TestResult(tc.LogicalName, Outcome.ERROR,
                ex is WriterHeldException ? ex.Message : $"writer setup failed: {ex.Message}",
                stopwatch.Elapsed.TotalSeconds, ex.ToString());
            return null;
        }
    }

    /// <param name="bench">A writer bench's session: the run keep-restores instead of opening its own.</param>
    public TestResult RunOne(TestCase tc, bool measure = false, WriterBench? bench = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var messages = new List<AseError>();

        if (tc.Error != null)
            return new TestResult(tc.LogicalName, Outcome.ERROR, tc.Error, stopwatch.Elapsed.TotalSeconds, "");

        // Statistics are enabled only when measuring, so unbudgeted test output is unchanged.
        IIoMeter? meter = null;
        if (measure || tc.Budgeted)
        {
            meter = IoMeters.For(_profile.ServerType);
            if (meter == null)
                return new TestResult(tc.LogicalName, Outcome.ERROR,
                    $"I/O measurement is not supported for server type {_profile.ServerType}",
                    stopwatch.Elapsed.TotalSeconds, "");
        }
        // The touched-not-restored diagnostic needs the stat lines; a bench reports it from r.Io.
        bool diagnose = tc.IsWriter && _opts.Verbose && bench == null;
        IIoMeter? diagMeter = diagnose && meter == null ? IoMeters.For(_profile.ServerType) : null;
        var batchMeter = meter ?? diagMeter;

        var variantSpecs = new List<ScratchSpec>();
        if (PrepareVariant(tc, _locator, _scratchCompiler, _opts.Database, _opts.Verbose, variantSpecs, stopwatch) is { } refused)
            return refused;

        // Step 0 (pre-tran): ensure the capture table exists. DDL must
        // happen outside the test's begin tran/rollback wrap because
        // ddl in tran is off on sbntest.
        if (tc.Capture != null)
        {
            try { EnsureCaptureTable(tc.Capture); }
            catch (Exception ex)
            {
                return new TestResult(tc.LogicalName, Outcome.ERROR,
                    $"capture table setup failed: {ex.Message}",
                    stopwatch.Elapsed.TotalSeconds, ex.ToString());
            }
        }

        var conn = new AseConnection(BuildConnectionString(_opts.Database));
        AseConnection? control = null;
        ScratchSession? scratch = null;
        try
        {
            conn.InfoMessage += (_, e) =>
            {
                foreach (AseError err in e.Errors)
                {
                    if (err.Severity >= 11) continue;
                    var msg = err.Message ?? "";
                    if (msg.StartsWith("Changed client character set") ||
                        msg.StartsWith("Changed database context") ||
                        msg.StartsWith("Changed language setting"))
                        continue;
                    messages.Add(err);
                }
            };

            try { conn.Open(); }
            catch (Exception ex)
            {
                return new TestResult(tc.LogicalName, Outcome.ERROR,
                    $"connection failed: {ex.Message}",
                    stopwatch.Elapsed.TotalSeconds, ex.ToString());
            }

            // Separate connection so the restore still runs when the test connection
            // timed out, was killed or hangs. Begin commits the recipe before any write.
            WriterSession? writer = null;
            if (tc.IsWriter || tc.Variant != null)
            {
                try
                {
                    var me = new AseSqlExec(conn, _opts.TimeoutSeconds)
                        .Rows("select @@spid, kpid from master..sysprocesses where spid = @@spid").Single();
                    int spid = Convert.ToInt32(me[0], CultureInfo.InvariantCulture);
                    int kpid = Convert.ToInt32(me[1], CultureInfo.InvariantCulture);
                    if (bench != null)
                    {
                        bench.SetTestPair(spid, kpid);
                        writer = bench.Session;
                    }
                    else
                    {
                        control = new AseConnection(BuildConnectionString(_opts.Database));
                        control.Open();
                        // Restore has no deadline: giving up leaves the test's writes in the product table.
                        var journal = WriterSession.Begin(new AseSqlExec(control, _opts.TimeoutSeconds),
                                                          _opts.Database, tc.LogicalName, tc.Restores ?? [], spid, kpid,
                                                          restoreX: new AseSqlExec(control, 0));
                        // Cleanup restores only a writer's row; a scratch-only row closes with its last drop.
                        writer = tc.IsWriter ? journal : null;
                        if (tc.Variant != null)
                            scratch = new ScratchSession(new AseSqlExec(control, _opts.TimeoutSeconds), _opts.Database,
                                                         journal, _scratchCompiler);
                    }
                }
                catch (WriterHeldException ex)
                {
                    return new TestResult(tc.LogicalName, Outcome.ERROR, ex.Message,
                        stopwatch.Elapsed.TotalSeconds, ex.ToString());
                }
                catch (Exception ex)
                {
                    return new TestResult(tc.LogicalName, Outcome.ERROR,
                        $"writer setup failed: {ex.Message}",
                        stopwatch.Elapsed.TotalSeconds, ex.ToString());
                }
            }
            return VariantLifecycle(tc.LogicalName, scratch, variantSpecs, writer, stopwatch, RunBatch,
                                    release: () => { try { conn.Dispose(); } catch { } });

            TestResult RunBatch(Action dropVariants)
            {
                string? restoreError = null;

                // Read-only report builders do `select ... into #tmp`, which Sybase forbids
                // inside a multi-statement transaction (Msg 226). A `-- @no-transaction`
                // test runs WITHOUT begin tran/rollback so those procs are runnable; since
                // it can't lean on rollback to undo writes, isolation is explicit: clear
                // the capture table and run the `<base>_teardown` hook in Cleanup().
                AseTransaction? tx = tc.NoTransaction ? null : conn.BeginTransaction();

                var tranHandled = false;
                // Region ends when the batch returns or throws; later @@trancount/rollback stat lines are not fed.
                int regionEnd = -1;

                void Cleanup()
                {
                    if (tx != null) { if (!tranHandled) try { tx.Rollback(); } catch { } return; }
                    // No-transaction path: undo by hand (best-effort; failures here must not
                    // mask the test's own outcome).
                    if (tc.Capture != null)
                        TryExec(conn, $"delete from {tc.Capture.IntoTable}");
                    if (tc.TeardownProc != null
                        && TryExec(conn, $"exec {tc.TeardownProc}", _opts.TimeoutSeconds) is CommandTimeoutException te)
                        Console.Error.WriteLine($"sql-test: warning: {tc.LogicalName}: teardown {tc.TeardownProc} cut off: {te.Message}");
                    // Not best-effort: a leaked product row is the failure writer mode exists to prevent.
                    if (writer != null)
                    {
                        restoreError = bench != null ? bench.RestoreKeep() : writer.Restore();
                        foreach (var w in writer.Warnings) Console.Error.WriteLine($"sql-test: warning: {tc.LogicalName}: {w}");
                        writer.Warnings.Clear(); // a bench session outlives this run
                    }
                }

                // After the region is fixed; a meter turned on only for this drops its stat lines from the output.
                void Diagnose()
                {
                    if (!diagnose || batchMeter == null) return;
                    var acc = new MeasureAccumulator(batchMeter);
                    int end = regionEnd < 0 ? messages.Count : regionEnd;
                    for (int i = 0; i < end; i++)
                        acc.Feed(messages[i].MessageNumber, messages[i].Message ?? "");
                    foreach (var t in WriterJournal.TouchedNotRestored(acc.Current.Tables.Select(t => t.Table), tc.Restores!))
                        Console.Error.WriteLine($"sql-test: {tc.LogicalName}: {WriterJournal.TouchedLine(t)}");
                    if (diagMeter != null) messages.RemoveAll(m => diagMeter.IsStatMessage(m.MessageNumber));
                }

                // Hooks for sql-bench-shape; null by default. A hook failure after an
                // aborted batch must not mask the batch's own error.
                var afterRegionRan = false;
                void RunAfterRegion()
                {
                    if (afterRegionRan || tc.AfterRegion == null) return;
                    afterRegionRan = true;
                    tc.AfterRegion(conn, regionEnd);
                }

                // See class doc for why this check exists.
                string? CheckTranCount()
                {
                    int expected = tx == null ? 0 : 1, actual;
                    try
                    {
                        using var cmd = new AseCommand("select @@trancount", conn);
                        if (tx != null) cmd.Transaction = tx;
                        cmd.CommandTimeout = _opts.TimeoutSeconds;
                        actual = Convert.ToInt32(CommandDeadline.Run(cmd, _opts.TimeoutSeconds, cmd.ExecuteScalar));
                    }
                    catch { return null; }   // connection unusable: keep the original outcome
                    if (actual == expected) return null;

                    tranHandled = true;
                    var msg = actual < expected
                        ? $"transaction ended inside the test (rollback tran or server abort; @@trancount={actual}, expected {expected}): later statements were not rolled back"
                        : $"transaction left open by the test (@@trancount={actual}, expected {expected}): rolled back";
                    if (actual > 0)
                    {
                        try
                        {
                            if (tx != null) tx.Rollback();
                            else { using var rb = new AseCommand("rollback tran", conn); rb.ExecuteNonQuery(); }
                        }
                        catch (Exception ex) { msg += $"; rollback failed: {ex.Message}"; }
                    }
                    return msg;
                }

                // Only PASS/SKIP get promoted to FAIL; ERROR/TIMEOUT (server already aborted
                // the tran, e.g. deadlock victim) keep their outcome with the note prefixed.
                TestResult WithTranCheck(TestResult r, string? violation)
                {
                    if (violation == null) return r;
                    var outcome = r.Outcome is Outcome.PASS or Outcome.SKIP ? Outcome.FAIL : r.Outcome;
                    return r with
                    {
                        Outcome = outcome,
                        Message = string.IsNullOrEmpty(r.Message) ? violation : $"{violation} | {r.Message}"
                    };
                }

                // Budget violations promote like the tran check; a budget without a start marker is an ERROR.
                TestResult WithMeasure(TestResult r)
                {
                    if (meter == null) return CheckUnmeteredMarker(r, messages.Select(m => m.Message ?? ""));
                    var acc = new MeasureAccumulator(meter);
                    int end = regionEnd < 0 ? messages.Count : regionEnd;
                    for (int i = 0; i < end; i++)
                        acc.Feed(messages[i].MessageNumber, messages[i].Message ?? "");

                    // Report the budget that failed; with none failing, the last one checked.
                    var shown = acc.Budgets.FirstOrDefault(b => b.Failed) ?? (acc.Budgets.Count > 0 ? acc.Budgets[^1] : null);
                    r = r with
                    {
                        Io = shown != null && !measure ? acc.Current with { LogicalReads = shown.Actual } : acc.Current,
                        MaxReads = shown?.MaxReads,
                    };
                    if (r.Outcome is not (Outcome.PASS or Outcome.SKIP)) return r;
                    if (acc.Error != null)
                        return r with { Outcome = Outcome.ERROR, Message = string.IsNullOrEmpty(r.Message) ? acc.Error : $"{acc.Error} | {r.Message}" };
                    if (acc.Failures.Count > 0)
                    {
                        var fail = string.Join(" | ", acc.Failures.Select(f => "FAIL: " + f));
                        return r with { Outcome = Outcome.FAIL, Message = string.IsNullOrEmpty(r.Message) ? fail : $"{fail} | {r.Message}" };
                    }
                    return r;
                }

                // An aborted batch skips the trailing DisableSql; turn it off so the tran check's output stays clean.
                void DisableMeterAfterAbort()
                {
                    if (batchMeter == null) return;
                    try
                    {
                        using var off = new AseCommand(batchMeter.DisableSql, conn);
                        if (tx != null) off.Transaction = tx;
                        off.CommandTimeout = DisableMeterTimeoutSeconds;
                        CommandDeadline.Run(off, DisableMeterTimeoutSeconds, off.ExecuteNonQuery);
                    }
                    catch { }
                }

                try
                {
                    // No-transaction capture tests can't rely on rollback to start clean,
                    // so defensively clear any rows a crashed prior test left this session.
                    if (tx == null && tc.Capture != null)
                        TryExec(conn, $"delete from {tc.Capture.IntoTable}");

                    // Paired: pretest runs in the capture batch (it feeds @tstuser to
                    // the capture proc); the assert proc only reads the capture table.
                    // Singleton: pretest runs in the test batch.
                    if (tc.CaptureProc != null && tc.Capture != null)
                        RunCapturePhase(conn, tx, tc.CaptureProc, tc.Capture, tc.Pretest, _opts.TimeoutSeconds, diagMeter);

                    var assertSql = tc.CaptureProc != null
                        ? $"exec {tc.AssertProc}"
                        : WithPretest(tc.Pretest, tc.AssertProc);
                    if (batchMeter != null)
                        assertSql = $"{batchMeter.EnableSql}\n{assertSql}\n{batchMeter.DisableSql}";
                    tc.BeforeBatch?.Invoke(conn);
                    using var cmd = new AseCommand(assertSql, conn);
                    if (tx != null) cmd.Transaction = tx;
                    cmd.CommandTimeout = _opts.TimeoutSeconds;
                    CommandDeadline.Run(cmd, _opts.TimeoutSeconds, cmd.ExecuteNonQuery);
                    regionEnd = messages.Count;
                    RunAfterRegion();

                    var violation = CheckTranCount();
                    Cleanup();
                    dropVariants();
                    Diagnose();
                    stopwatch.Stop();
                    return WithRestore(WithTranCheck(WithMeasure(new TestResult(tc.LogicalName, Outcome.PASS, "",
                        stopwatch.Elapsed.TotalSeconds, JoinMessages(messages))), violation), restoreError);
                }
                catch (AseException ex)
                {
                    if (regionEnd < 0) regionEnd = messages.Count;
                    DisableMeterAfterAbort();
                    try { RunAfterRegion(); } catch { }
                    var violation = CheckTranCount();
                    Cleanup();
                    dropVariants();
                    Diagnose();
                    stopwatch.Stop();
                    return WithRestore(WithTranCheck(WithMeasure(Classify(tc.LogicalName, ex, messages, stopwatch.Elapsed.TotalSeconds, batchMeter != null)), violation), restoreError);
                }
                catch (Exception ex)
                {
                    if (regionEnd < 0) regionEnd = messages.Count;
                    DisableMeterAfterAbort();
                    try { RunAfterRegion(); } catch { }
                    var violation = CheckTranCount();
                    Cleanup();
                    dropVariants();
                    Diagnose();
                    stopwatch.Stop();
                    var outcome = ClassifyUnexpected(ex);
                    // Keep what the proc printed before the cancel, for --verbose.
                    var output = ex is CommandTimeoutException ? $"{JoinMessages(messages)}\n{ex}" : ex.ToString();
                    return WithRestore(WithTranCheck(WithMeasure(new TestResult(tc.LogicalName, outcome, ex.Message,
                        stopwatch.Elapsed.TotalSeconds, output)), violation), restoreError);
                }
            }
        }
        finally
        {
            try { conn.Dispose(); } catch { }
            // Backstop; DropAll is a no-op once the run has dropped.
            try { scratch?.DropAll(); } catch { }
            try { control?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Compiles the variant chain, runs the batch, and drops the chain in every outcome.
    /// <paramref name="run"/> calls the drop it is handed right after its Cleanup; the finally
    /// calls <paramref name="release"/>, then retries, and a drop error reaches the result only
    /// if handles remain. A refusal compiles nothing, then restores a writer's row or closes a
    /// scratch-only one.
    /// </summary>
    internal static TestResult VariantLifecycle(string test, ScratchSession? scratch, IReadOnlyList<ScratchSpec> specs,
                                                WriterSession? writer, Stopwatch stopwatch, Func<Action, TestResult> run,
                                                Action? release = null)
    {
        if (scratch != null)
        {
            try { Variants.CompileAll(specs, scratch); }
            catch (Exception ex)
            {
                var reason = ex is ScratchRefusedException ? ex.Message : $"variant setup failed: {ex.Message}";
                var close = writer != null ? writer.Restore() : scratch.Journal.CloseIfEmpty();
                return WithRestore(new TestResult(test, Outcome.ERROR, reason, stopwatch.Elapsed.TotalSeconds, ex.ToString()), close);
            }
        }
        TestResult result;
        string? dropError;
        try { result = run(() => scratch?.DropAll()); }
        finally
        {
            // A timed-out batch can still hold the proc until its connection closes.
            if (scratch != null) release?.Invoke();
            dropError = scratch?.DropAll();
        }
        // An earlier drop failure that this retry cleared is not an error.
        return scratch is { Handles.Count: > 0 } ? WithRestore(result, dropError) : result;
    }

    // Best-effort statement on the test connection; swallows errors so cleanup
    // can never turn a PASS into a spurious failure. Returns the swallowed error.
    private static Exception? TryExec(AseConnection conn, string sql, int? timeout = null)
    {
        try
        {
            using var cmd = new AseCommand(sql, conn);
            if (timeout is int t) cmd.CommandTimeout = t;
            CommandDeadline.Run(cmd, timeout ?? 0, cmd.ExecuteNonQuery);
            return null;
        }
        catch (Exception ex) { return ex; }
    }

    private void RunCapturePhase(AseConnection conn, AseTransaction? tx,
                                 string captureProc, CaptureSpec spec, string? pretest, int timeout,
                                 IIoMeter? diag = null)
    {
        // The capture table's schema (column count + per-column .NET
        // type) is the contract. Result sets emitted during the capture
        // proc whose shape doesn't match -- a trigger's `select @insrc,
        // @delrc` (2 unnamed ints), an audit-log emit, an arbitrary
        // debug select -- get filtered out. Only shape-matched result
        // sets are ingested, positionally against the table's column
        // order; reader column names don't have to be aligned.
        var tableSchema = _rebuiltTables[spec.IntoTable].Value;
        var insertSql   = BuildInsertSql(spec.IntoTable, tableSchema.ColumnNames);

        // Pretest (if any) runs in the same batch as the capture proc so the
        // allocated @tstuser is in scope. Its own emits (e.g. tri_users
        // debug select on the &users& INSERT) are shape-filtered out below.
        var captureSql = WithPretest(pretest, captureProc);
        // A pair's writes happen here; only the touched-not-restored diagnostic meters this batch.
        if (diag != null) captureSql = $"{diag.EnableSql}\n{captureSql}\n{diag.DisableSql}";
        using var cmd = new AseCommand(captureSql, conn);
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandTimeout = timeout;

        var rows = CommandDeadline.Run(cmd, timeout, () =>
        {
            var read = new List<object[]>();
            using var reader = cmd.ExecuteReader();
            do
            {
                if (!ResultSetMatchesSchema(reader, tableSchema)) continue;
                read.AddRange(ReadRows(reader));
            } while (reader.NextResult());
            return read;
        });
        // No deadline: each insert is one small row, and a cancel between them would be a no-op anyway.
        InsertRows(rows, conn, tx, insertSql);
    }

    // Non-AseException failures: our deadline, or a driver/socket timeout reported by message.
    internal static Outcome ClassifyUnexpected(Exception ex) =>
        ex is TimeoutException || ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            ? Outcome.TIMEOUT : Outcome.ERROR;

    private static bool ResultSetMatchesSchema(IDataReader reader, CaptureTableSchema schema)
    {
        if (reader.FieldCount != schema.ColumnTypes.Count) return false;
        for (int i = 0; i < reader.FieldCount; i++)
            if (reader.GetFieldType(i) != schema.ColumnTypes[i]) return false;
        return true;
    }

    private static IEnumerable<object[]> ReadRows(IDataReader reader)
    {
        while (reader.Read())
        {
            var row = new object[reader.FieldCount];
            for (int i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
            yield return row;
        }
    }

    private static void InsertRows(List<object[]> rows, AseConnection conn, AseTransaction? tx, string insertSql)
    {
        foreach (var row in rows)
        {
            using var insertCmd = new AseCommand(insertSql, conn);
            if (tx != null) insertCmd.Transaction = tx;
            for (int i = 0; i < row.Length; i++)
                insertCmd.Parameters.Add($"@p{i}", row[i]);
            insertCmd.ExecuteNonQuery();
        }
    }

    private static string BuildInsertSql(string targetTable, IReadOnlyList<string> cols)
    {
        var colList = string.Join(", ", cols.Select(QuoteIdent));
        var placeholders = string.Join(", ", Enumerable.Range(0, cols.Count).Select(i => $"@p{i}"));
        return $"insert into {targetTable} ({colList}) values ({placeholders})";
    }

    public void EnsureCaptureTable(CaptureSpec spec)
    {
        // First reference to a given capture table in this Runner
        // instance does a drop-if-exists + create; subsequent references
        // short-circuit. The table outlives individual tests within the
        // session (rows roll back per test via the test tran), but never
        // outlives the session itself -- so the table's schema is always
        // fresh against the source proc's current emitted shape. DDL is
        // outside any test tran (ddl in tran off on sbntest), hence its
        // own connection. The returned schema is cached for downstream
        // shape-matched filtering during the capture phase.
        var lazy = _rebuiltTables.GetOrAdd(spec.IntoTable,
            _ => new Lazy<CaptureTableSchema>(() => RebuildCaptureTable(spec),
                                              LazyThreadSafetyMode.ExecutionAndPublication));
        _ = lazy.Value;
    }

    private CaptureTableSchema RebuildCaptureTable(CaptureSpec spec)
    {
        using var conn = new AseConnection(BuildConnectionString(_opts.Database));
        conn.Open();

        if (TableExists(conn, spec.IntoTable))
        {
            using var dropCmd = new AseCommand($"drop table {spec.IntoTable}", conn);
            dropCmd.ExecuteNonQuery();
        }

        var schema = IntrospectResultSetSchema(conn, spec.SourceCall);
        var ddl = BuildCreateTable(spec.IntoTable, schema);
        using var createCmd = new AseCommand(ddl, conn);
        createCmd.ExecuteNonQuery();
        return CaptureTableSchema.FromIntrospectedSchema(schema);
    }

    private static bool TableExists(AseConnection conn, string tableName)
    {
        using var cmd = new AseCommand(
            "select 1 from sysobjects where type = 'U' and name = @n", conn);
        cmd.Parameters.Add("@n", tableName);
        return cmd.ExecuteScalar() != null;
    }

    /// <summary>
    /// Issue `set fmtonly on; exec <source>; set fmtonly off` and return
    /// the result-set schema. FMTONLY tells the server to return column
    /// metadata without executing the proc body. Works on both Sybase ASE
    /// and SQL Server (deprecated on the latter but still functional;
    /// future enhancement: switch MSSQL to sp_describe_first_result_set).
    /// </summary>
    private DataTable IntrospectResultSetSchema(AseConnection conn, string sourceCall)
    {
        var sql = $"set fmtonly on\n{sourceCall}\nset fmtonly off";
        using var cmd = new AseCommand(sql, conn);
        using var reader = cmd.ExecuteReader(CommandBehavior.SchemaOnly);
        var schema = reader.GetSchemaTable()
            ?? throw new InvalidOperationException(
                $"FMTONLY returned no schema for: {sourceCall}");
        return schema;
    }

    private static string BuildCreateTable(string tableName, DataTable schema)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"create table {tableName} (");
        for (int i = 0; i < schema.Rows.Count; i++)
        {
            var row      = schema.Rows[i];
            var name     = (string)row["ColumnName"];
            var type     = (Type)row["DataType"];
            var size     = row["ColumnSize"]      is DBNull ? -1 : Convert.ToInt32(row["ColumnSize"]);
            var prec     = row["NumericPrecision"] is DBNull ? 0  : Convert.ToInt32(row["NumericPrecision"]);
            var scale    = row["NumericScale"]    is DBNull ? 0  : Convert.ToInt32(row["NumericScale"]);
            var nullable = row["AllowDBNull"]     is DBNull || Convert.ToBoolean(row["AllowDBNull"]);

            sb.Append($"  {QuoteIdent(name)} {MapType(type, size, prec, scale)}");
            sb.Append(nullable ? " null" : " not null");
            sb.AppendLine(i == schema.Rows.Count - 1 ? "" : ",");
        }
        // `lock datarows` lifts the 254-variable-length-column ceiling that
        // Sybase ASE enforces on allpages-locked tables. Wide-emit procs
        // like g_ma_installations (~280 columns) would otherwise fail to
        // create. Capture tables are single-test scratch space, so the
        // locking scheme has no observable downside.
        sb.AppendLine(") lock datarows");
        return sb.ToString();
    }

    private static string MapType(Type t, int size, int prec, int scale)
    {
        if (t == typeof(int))      return "int";
        if (t == typeof(long))     return "bigint";
        if (t == typeof(short))    return "smallint";
        if (t == typeof(byte))     return "tinyint";
        if (t == typeof(bool))     return "bit";
        if (t == typeof(decimal))  return prec > 0 ? $"numeric({prec},{scale})" : "numeric(18,4)";
        if (t == typeof(double))   return "float";
        if (t == typeof(float))    return "real";
        if (t == typeof(DateTime)) return "datetime";
        if (t == typeof(byte[]))   return size > 0 ? $"varbinary({size})" : "varbinary(8000)";
        // string / fallback
        if (size <= 0 || size > 8000) return "varchar(8000)";
        return $"varchar({size})";
    }

    internal static string QuoteIdent(string name)
    {
        // Sybase identifiers: reject control chars; otherwise return bare.
        // For column names with spaces or reserved words, wrap in brackets.
        // A leading digit does not parse as a bare identifier.
        if (name.Length > 0 && !char.IsDigit(name[0])
            && name.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '#')) return name;
        return $"[{name.Replace("]", "]]")}]";
    }

    public string PrintCaptureDdl(CaptureSpec spec)
    {
        using var conn = new AseConnection(BuildConnectionString(_opts.Database));
        conn.Open();
        var schema = IntrospectResultSetSchema(conn, spec.SourceCall);
        return BuildCreateTable(spec.IntoTable, schema);
    }

    public void DropCaptureTable(string tableName)
    {
        using var conn = new AseConnection(BuildConnectionString(_opts.Database));
        conn.Open();
        if (!TableExists(conn, tableName)) return;
        using var cmd = new AseCommand($"drop table {tableName}", conn);
        cmd.ExecuteNonQuery();
    }

    private TestResult Classify(string name, AseException ex, List<AseError> info, double duration, bool metered = false)
    {
        foreach (AseError err in ex.Errors)
        {
            if (err.MessageNumber == FailErrorNumber)
            {
                var msg = LastMatching(info, "FAIL:") ?? "FAIL";
                return new TestResult(name, Outcome.FAIL, msg, duration, JoinMessages(info, ex.Errors, metered));
            }
            if (err.MessageNumber == SkipErrorNumber)
            {
                var msg = LastMatching(info, "SKIP:") ?? "SKIP";
                return new TestResult(name, Outcome.SKIP, msg, duration, JoinMessages(info, ex.Errors, metered));
            }
        }

        var first = ex.Errors.Count > 0 ? ex.Errors[0] : null;
        var headline = first != null
            ? $"Msg {first.MessageNumber}, Level {first.Severity}: {first.Message}"
            : ex.Message;
        return new TestResult(name, Outcome.ERROR, headline, duration, JoinMessages(info, ex.Errors, metered));
    }

    /// <summary>
    /// Builds and drift-checks <paramref name="tc"/>'s variant into <paramref name="specs"/>; an ERROR result on refusal.
    /// It takes no connection, so a locate, chain or drift refusal journals and compiles nothing.
    /// </summary>
    internal static TestResult? PrepareVariant(TestCase tc, SourceLocator locator, IScratchCompiler compiler, string db,
                                               bool verbose, List<ScratchSpec> specs, Stopwatch stopwatch)
    {
        if (tc.Variant == null) return null;
        try
        {
            var built = Variants.Build(tc.Variant, locator, compiler, db);
            Variants.CheckDrift(built, compiler, verbose ? m => Console.Error.WriteLine($"sql-test: {tc.LogicalName}: {m}") : null);
            foreach (var (spec, relPath) in built)
            {
                specs.Add(spec);
                if (verbose)
                    Console.Error.WriteLine($"sql-test: {tc.LogicalName}: variant {spec.ScratchName} from {relPath}, " +
                                            string.Join(" ", spec.CompileOptions.Select(o => $"{o.Key}={(o.Value ? "+" : "-")}")));
            }
            return null;
        }
        catch (Exception ex)
        {
            return new TestResult(tc.LogicalName, Outcome.ERROR,
                ex is ScratchRefusedException ? ex.Message : $"variant setup failed: {ex.Message}",
                stopwatch.Elapsed.TotalSeconds, ex.ToString());
        }
    }

    private static string? LastMatching(List<AseError> messages, string prefix)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var m = (messages[i].Message ?? "").TrimEnd();
            if (m.StartsWith(prefix)) return m;
        }
        return null;
    }

    // ex.Errors repeats earlier info messages; with the meter on, drop its stat lines so they are not listed twice.
    private static string JoinMessages(List<AseError> info, AseErrorCollection? errors = null, bool metered = false)
    {
        var sb = new StringBuilder();
        foreach (var m in info)
            sb.AppendLine((m.Message ?? "").TrimEnd());
        if (errors != null)
            foreach (AseError e in errors)
                if (!metered || e.MessageNumber is not (StatTableMessage or StatWritesMessage))
                    sb.AppendLine($"Msg {e.MessageNumber}, Level {e.Severity}: {(e.Message ?? "").TrimEnd()}");
        return sb.ToString();
    }

    /// <summary>
    /// Refuses an unknown <c>--variant-profile</c>, or one of another server type than <paramref name="runnerServer"/>.
    /// It reads settings only, so it runs before any Resolve can prompt.
    /// </summary>
    public static void PrecheckVariantProfile(ProfileManager mgr, string? name, string runnerServer)
    {
        if (name == null) return;
        if (mgr.ResolveProfile(name) is not { } v)
            throw new ArgumentException($"unknown --variant-profile {name}: no such profile");
        if (mgr.ResolveProfile(runnerServer) is { } r)
        {
            var (vt, rt) = (ibsCompiler.ibs_compiler_common.ParsePlatform(v.Profile.Platform),
                            ibsCompiler.ibs_compiler_common.ParsePlatform(r.Profile.Platform));
            if (vt != rt)
                throw new ArgumentException($"--variant-profile {v.ProfileName} is {vt} but profile {r.ProfileName} is {rt}; " +
                                            "an options layer only fits its own server type");
        }
    }

    /// <summary>
    /// The profile whose options layer variants compile with: <paramref name="name"/>, else the runner's.
    /// Only its options layer is used, over the runner's SQL source, so the source and its options stay one tree.
    /// </summary>
    public static ResolvedProfile ResolveVariantProfile(ProfileManager mgr, string? name, ResolvedProfile runner)
    {
        if (name == null) return runner;
        PrecheckVariantProfile(mgr, name, runner.ProfileName);
        // The runner's password stops Resolve prompting for one; this profile never connects.
        var p = mgr.Resolve(new ibsCompiler.CommandVariables { Server = name, Pass = string.IsNullOrEmpty(runner.Pass) ? "-" : runner.Pass });
        // The options cache is keyed by profile name only: another tree would poison that profile's cache for runsql too.
        if (!string.IsNullOrEmpty(p.IRPath) && !SamePath(p.IRPath, runner.IRPath))
            throw new ArgumentException($"--variant-profile {p.ProfileName} has SQL source {p.IRPath}, but profile {runner.ProfileName} " +
                                        $"has {runner.IRPath}; the variant profile must share the runner's SQL source");
        p.IRPath = runner.IRPath;
        return p;
    }

    /// <summary>R10: runs the call list against <c>req.Proc</c> at an SVN revision and at the working copy. 0 same, 1 differs, 2 failure.</summary>
    public int CompareRev(CompareRequest req, ISvnSource svn, TextWriter report, TextWriter? error = null)
    {
        var deps = new CompareDeps(svn, _locator, _scratchCompiler, _opts.Database,
            OpenControl: () =>
            {
                var conn = new AseConnection(BuildConnectionString(_opts.Database));
                try { conn.Open(); }
                catch { conn.Dispose(); throw; }
                // Restores run to completion: a cancelled restore leaves product rows dirty.
                return (new AseSqlExec(conn, _opts.TimeoutSeconds), new AseSqlExec(conn, 0), conn);
            },
            RunSide: (db, tran, sql, includePrint, onOpen) => RunCompareSide(db, tran, sql, includePrint, onOpen, req.TimeoutSeconds),
            Sweep: () => { if (DiscoverSweepRestoreTimeout(_opts) is { } t) SweepWriterJournal(t); });
        return SqlTest.CompareRev.Run(req, deps, report, error);
    }

    // A fresh connection per side, so no session state of the old side reaches the new one.
    private CallRun RunCompareSide(string? db, bool tran, string sql, bool includePrint, Action<int, int> onOpen, int timeout)
    {
        var writer = new CanonicalWriter(includePrint);
        int spid = 0, kpid = 0;
        int? trancount = null, raised = null;
        AseTransaction? tx = null;
        using var conn = new AseConnection(BuildConnectionString(db ?? _opts.Database));
        conn.InfoMessage += (_, e) =>
        {
            foreach (AseError err in e.Errors)
            {
                if (err.Severity >= 11) continue;
                var msg = err.Message ?? "";
                if (msg.StartsWith("Changed client character set") ||
                    msg.StartsWith("Changed database context") ||
                    msg.StartsWith("Changed language setting"))
                    continue;
                writer.Print(msg);
            }
        };
        try
        {
            conn.Open();
            var me = new AseSqlExec(conn, timeout).Rows("select @@spid, kpid from master..sysprocesses where spid = @@spid").Single();
            spid = Convert.ToInt32(me[0], CultureInfo.InvariantCulture);
            kpid = Convert.ToInt32(me[1], CultureInfo.InvariantCulture);
            onOpen(spid, kpid);
            if (tran) tx = conn.BeginTransaction();

            using (var cmd = new AseCommand(sql, conn) { CommandTimeout = timeout })
            {
                if (tx != null) cmd.Transaction = tx;
                try { CommandDeadline.Run(cmd, timeout, () => { using var r = cmd.ExecuteReader(); return CompareOutput.Drain(r, writer); }); }
                catch (AseException ex)
                {
                    raised = ex.Errors.Cast<AseError>().FirstOrDefault(e => e.Severity >= 11)?.MessageNumber
                             ?? (ex.Errors.Count > 0 ? ex.Errors[0].MessageNumber : 0);
                    // The driver can repeat one server error across its collection.
                    var seen = new HashSet<(int, string)>();
                    foreach (AseError err in ex.Errors)
                        if (err.Severity >= 11 && seen.Add((err.MessageNumber, err.Message ?? "")))
                            writer.Error(err.MessageNumber, err.Severity, err.Message ?? "");
                }
            }

            // A proc that ends or leaves open a transaction behaves differently; the stream must show it.
            int expected = tran ? 1 : 0;
            try
            {
                using var check = new AseCommand("select @@trancount", conn) { CommandTimeout = timeout };
                if (tx != null) check.Transaction = tx;
                trancount = Convert.ToInt32(CommandDeadline.Run(check, timeout, check.ExecuteScalar), CultureInfo.InvariantCulture);
            }
            catch { /* connection unusable: the output so far stands */ }
            if (trancount is { } actual && actual != expected) writer.TranCount(actual, expected);
            return SqlTest.CompareRev.Classify(writer.ToBytes(), spid, kpid, raised, trancount, expected, writer.HasReturnCode);
        }
        catch (Exception ex)
        {
            return new CallRun(writer.ToBytes(), spid, kpid, ex);
        }
        finally
        {
            try
            {
                if (tx != null) tx.Rollback();
                else if (trancount > 0)
                {
                    using var rb = new AseCommand("rollback tran", conn) { CommandTimeout = timeout };
                    CommandDeadline.Run(rb, timeout, () => rb.ExecuteNonQuery());
                }
            }
            catch { /* closing the connection rolls back what is left */ }
        }
    }

    private static bool SamePath(string a, string b)
    {
        static string Norm(string s) => string.IsNullOrEmpty(s) ? "" : Path.TrimEndingDirectorySeparator(Path.GetFullPath(s));
        return string.Equals(Norm(a), Norm(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private string BuildConnectionString(string database) => BuildConnectionString(_profile, database);

    private string BuildConnectionString(ResolvedProfile p, string database)
    {
        var sb = new StringBuilder();
        sb.Append($"Data Source={p.Host}");
        sb.Append($";Port={p.Port}");
        sb.Append($";User ID={p.User}");
        sb.Append($";Password={p.Pass}");
        if (!string.IsNullOrEmpty(database))
            sb.Append($";Database={database}");
        sb.Append(";Pooling=false");
        return sb.ToString();
    }

    /// <summary>R11: measures the I/O of <c>req.Proc</c> lines From-To per call, as <paramref name="asProfile"/> when set. 0 measured, 1 not reached or unclosed, 2 failure.</summary>
    public int Spike(SpikeRequest req, ResolvedProfile? asProfile, TextWriter report, TextWriter? error = null)
    {
        var meter = IoMeters.For(_profile.ServerType);
        if (meter == null)
        {
            (error ?? report).WriteLine($"sql-test: I/O measurement is not supported for server type {_profile.ServerType}");
            return 2;
        }
        var deps = new SpikeDeps(_locator, _scratchCompiler, _opts.Database,
            OpenControl: () =>
            {
                var conn = new AseConnection(BuildConnectionString(_opts.Database));
                try { conn.Open(); }
                catch { conn.Dispose(); throw; }
                // Restores run to completion: a cancelled restore leaves product rows dirty.
                return (new AseSqlExec(conn, _opts.TimeoutSeconds), new AseSqlExec(conn, 0), conn);
            },
            RunSide: (db, tran, sql, onOpen) => RunSpikeSide(asProfile ?? _profile, db, tran, sql, onOpen, req.TimeoutSeconds),
            AsLogin: asProfile?.User,
            Meter: meter,
            Sweep: () => { if (DiscoverSweepRestoreTimeout(_opts) is { } t) SweepWriterJournal(t); });
        return SqlTest.Spike.Run(req, deps, report, error);
    }

    // A fresh connection per call, as the --as login when set; the stat lines arrive as info messages.
    private SpikeRun RunSpikeSide(ResolvedProfile p, string? db, bool tran, string sql, Action<int, int> onOpen, int timeout)
    {
        var messages = new List<(int, string)>();
        int? trancount = null, raised = null;
        int expected = tran ? 1 : 0;
        AseTransaction? tx = null;
        using var conn = new AseConnection(BuildConnectionString(p, db ?? _opts.Database));
        conn.InfoMessage += (_, e) =>
        {
            foreach (AseError err in e.Errors)
            {
                if (err.Severity >= 11) continue;
                var msg = err.Message ?? "";
                if (msg.StartsWith("Changed client character set") ||
                    msg.StartsWith("Changed database context") ||
                    msg.StartsWith("Changed language setting"))
                    continue;
                messages.Add((err.MessageNumber, msg));
            }
        };
        try
        {
            conn.Open();
            var me = new AseSqlExec(conn, timeout).Rows("select @@spid, kpid from master..sysprocesses where spid = @@spid").Single();
            onOpen(Convert.ToInt32(me[0], CultureInfo.InvariantCulture), Convert.ToInt32(me[1], CultureInfo.InvariantCulture));
            if (tran) tx = conn.BeginTransaction();

            using (var cmd = new AseCommand(sql, conn) { CommandTimeout = timeout })
            {
                if (tx != null) cmd.Transaction = tx;
                try { CommandDeadline.Run(cmd, timeout, () => { cmd.ExecuteNonQuery(); }); }
                catch (AseException ex)
                {
                    raised = ex.Errors.Cast<AseError>().FirstOrDefault(e => e.Severity >= 11)?.MessageNumber
                             ?? (ex.Errors.Count > 0 ? ex.Errors[0].MessageNumber : 0);
                }
            }

            try
            {
                using var check = new AseCommand("select @@trancount", conn) { CommandTimeout = timeout };
                if (tx != null) check.Transaction = tx;
                trancount = Convert.ToInt32(CommandDeadline.Run(check, timeout, check.ExecuteScalar), CultureInfo.InvariantCulture);
            }
            catch { /* connection unusable: trancount stays unknown */ }
            return new SpikeRun(messages, raised, trancount, expected, null);
        }
        catch (Exception ex)
        {
            // CommandTimeoutException carries a readable "command timeout: exceeded Ns" message.
            return new SpikeRun(messages, raised, trancount, expected, ex);
        }
        finally
        {
            try
            {
                if (tx != null) tx.Rollback();
                else if (trancount > 0)
                {
                    using var rb = new AseCommand("rollback tran", conn) { CommandTimeout = timeout };
                    CommandDeadline.Run(rb, timeout, () => rb.ExecuteNonQuery());
                }
            }
            catch { /* closing the connection rolls back what is left */ }
        }
    }

    /// <summary>
    /// Refuses an unknown <c>--as</c> alias, one on another platform, host or port than <paramref name="runnerServer"/>,
    /// or one without a stored password. It reads settings only and never prompts.
    /// </summary>
    public static void PrecheckAsProfile(ProfileManager mgr, string alias, string runnerServer)
    {
        if (mgr.ResolveProfile(alias) is not { } a)
            throw new ArgumentException($"unknown --as {alias}: no such profile");
        if (mgr.ResolveProfile(runnerServer) is not { } r)
            throw new ArgumentException($"--as {a.ProfileName}: runner profile {runnerServer} is not in settings");
        var (at, rt) = (ibsCompiler.ibs_compiler_common.ParsePlatform(a.Profile.Platform),
                        ibsCompiler.ibs_compiler_common.ParsePlatform(r.Profile.Platform));
        if (at != rt)
            throw new ArgumentException($"--as {a.ProfileName} is {at} but profile {r.ProfileName} is {rt}; --as must reach the same server");
        if (!string.Equals(a.Profile.Host, r.Profile.Host, StringComparison.OrdinalIgnoreCase) || a.Profile.Port != r.Profile.Port)
            throw new ArgumentException($"--as {a.ProfileName} is {a.Profile.Host}:{a.Profile.Port} but profile {r.ProfileName} is " +
                                        $"{r.Profile.Host}:{r.Profile.Port}; --as must reach the same server");
        if (string.IsNullOrEmpty(a.Profile.Password))
            throw new ArgumentException($"--as {a.ProfileName} has no stored password; --as never prompts");
    }
}
