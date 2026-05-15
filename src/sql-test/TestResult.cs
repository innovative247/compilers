namespace SqlTest;

public enum Outcome { PASS, FAIL, SKIP, ERROR, TIMEOUT }

public record TestResult(
    string Name,
    Outcome Outcome,
    string Message,
    double DurationSeconds,
    string Output);

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
    CaptureSpec? Capture        // null when no capture phase
);

public record CaptureSpec(
    string IntoTable,           // permanent capture table in sbntest
    string SourceCall           // proc-call SQL used for FMTONLY introspection
);
