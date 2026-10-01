using AdoNetCore.AseClient;

namespace SqlTest;

public enum Outcome { PASS, FAIL, SKIP, ERROR, TIMEOUT }

public record TestResult(
    string Name,
    Outcome Outcome,
    string Message,
    double DurationSeconds,
    string Output)
{
    public IoMeasure? Io { get; init; }
    public long? MaxReads { get; init; }
    public int? Count { get; init; }
}

/// <summary>
/// Per-test metadata resolved at discovery.
///
/// Tests come in two shapes:
///   1. Singleton — one proc `test_<name>`; runner uses ExecuteNonQuery.
///   2. Paired — two procs `test_<name>_capture` + `test_<name>_assert`,
///      where the capture proc emits a result set the runner must INSERT
///      into a permanent capture table before the assert proc runs.
///      Pairing is by name-suffix convention.
///
/// Capture metadata is parsed from the capture proc's body in syscomments:
///   -- @capture-into:   tbl_test_capture_passcards
///   -- @capture-source: sbnapi..g_ma_passcards_for_installation @userid='S', @s_ins=12345
/// </summary>
public record TestCase(
    string LogicalName,         // for reporting; suffix stripped for pairs
    string? CaptureProc,        // null for singleton tests
    string AssertProc,          // always set; equals LogicalName for singletons
    CaptureSpec? Capture,       // null when no capture phase
    string? Pretest = null,     // pro_test_<area>_pretest, run before the test in-tran
    bool NoTransaction = false, // `-- @no-transaction`: run WITHOUT begin tran/rollback
                                // (read-only report builders do `select into`, illegal
                                //  in a multi-statement tran). Cleanup is explicit.
    string? TeardownProc = null, // `<base>_teardown`: cleanup hook for no-transaction
                                 // tests (deletes any rows the test created); best-effort
    int? BenchThresholdPct = null, // `-- @bench-threshold: <n>%` on a bench proc
    bool Budgeted = false,         // body calls pro_test_assert_max_reads: run with the I/O meter
    string? Error = null,          // discovery-time problem: reported as ERROR without running
    IReadOnlyList<RestoreSpec>? Restores = null // `-- @restore:` lines; null for non-writers
)
{
    /// <summary>Writer test: snapshot before the batch, restore after it on a control connection.</summary>
    public bool IsWriter => Restores is { Count: > 0 };

    /// <summary>`-- @variant:` line; the chain is compiled before the batch and dropped after it.</summary>
    public VariantSpec? Variant { get; init; }

    /// <summary>Parsed bench-shape directives; owned by sql-bench-shape.</summary>
    public object? Shape { get; init; }

    /// <summary>Runs on the test connection just before the test batch.</summary>
    public Action<AseConnection>? BeforeBatch { get; init; }

    /// <summary>Runs after the batch with the message index where the measured region ends.</summary>
    public Action<AseConnection, int>? AfterRegion { get; init; }
}

public record CaptureSpec(
    string IntoTable,           // permanent capture table in sbntest
    string SourceCall           // proc-call SQL used for FMTONLY introspection
);
