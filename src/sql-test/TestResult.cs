namespace SqlTest;

public enum Outcome { PASS, FAIL, SKIP, ERROR, TIMEOUT }

public record TestResult(
    string Name,
    Outcome Outcome,
    string Message,
    double DurationSeconds,
    string Output);
