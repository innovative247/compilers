using ibsCompiler.Configuration;
using SqlTest;

namespace SqlTest.Tests;

public class RunnerTests
{
    [Theory]
    [InlineData("exec pro_test_assert_max_reads 6000, 'list'", true)]
    [InlineData("EXEC PRO_TEST_ASSERT_MAX_READS 1", true)]
    [InlineData("exec pro_test_assert_max_reads_old 1", false)]
    [InlineData("exec pro_test_measure_start", false)]
    [InlineData(null, false)]
    public void HasBudget_detects_the_budget_proc(string? body, bool expected) =>
        Assert.Equal(expected, Runner.HasBudget(body));

    [Theory]
    [InlineData("-- @bench-threshold: 10%\ncreate proc bench_x", 10)]
    [InlineData("-- @bench-threshold: 10\n", 10)]
    [InlineData("  --@bench-threshold:25 % \r\ncreate proc bench_x", 25)]
    [InlineData("-- @bench-threshold: 999999999%", 999999999)]
    public void ParseBenchThreshold_accepts_whole_percentages(string body, int expected)
    {
        Assert.Equal(expected, Runner.ParseBenchThreshold(body, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("-- @bench-threshold: 2.5%")]
    [InlineData("-- @bench-threshold: 9999999999%")]
    [InlineData("-- @bench-threshold: 10% extra")]
    [InlineData("-- @bench-threshold: ten")]
    public void ParseBenchThreshold_rejects_other_values(string body)
    {
        Assert.Null(Runner.ParseBenchThreshold(body, out var error));
        Assert.StartsWith("invalid @bench-threshold directive:", error);
    }

    [Theory]
    [InlineData("create proc bench_x as select 1")]
    [InlineData(null)]
    public void ParseBenchThreshold_without_directive_is_no_threshold(string? body)
    {
        Assert.Null(Runner.ParseBenchThreshold(body, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Case_with_discovery_error_is_error_without_connecting()
    {
        var runner = new Runner(new ResolvedProfile { Host = "unreachable.invalid" }, new Options());
        var tc = new TestCase("test_pair", "test_pair_capture", "test_pair_assert", null, null,
                              Error: Runner.PairBudgetError);
        var r = runner.RunOne(tc);
        Assert.Equal(Outcome.ERROR, r.Outcome);
        Assert.Equal("budget not supported in _capture/_assert pairs", r.Message);
        Assert.Null(r.Io);
    }

    [Fact]
    public void Unmetered_marker_turns_pass_into_error()
    {
        var pass = new TestResult("test_x", Outcome.PASS, "", 0.1, "");
        var r = Runner.CheckUnmeteredMarker(pass, new[] { "setup", "@sql-test:assert-max-reads 6000 list" });
        Assert.Equal(Outcome.ERROR, r.Outcome);
        Assert.Equal("budget marker in an unmetered run (call pro_test_assert_max_reads from the test body)", r.Message);
    }

    [Fact]
    public void Unmetered_marker_keeps_fail_with_note()
    {
        var fail = new TestResult("test_x", Outcome.FAIL, "FAIL: x", 0.1, "");
        var r = Runner.CheckUnmeteredMarker(fail, new[] { "@sql-test:assert-max-reads 1" });
        Assert.Equal(Outcome.FAIL, r.Outcome);
        Assert.EndsWith(" | FAIL: x", r.Message);
    }

    [Fact]
    public void Output_without_marker_is_unchanged()
    {
        var pass = new TestResult("test_x", Outcome.PASS, "", 0.1, "out");
        Assert.Same(pass, Runner.CheckUnmeteredMarker(pass, new[] { "@sql-test:measure-start", "assert-max-reads 1", "FAIL: x" }));
    }

    [Theory]
    [InlineData("1st", "[1st]")]
    [InlineData("a1", "a1")]
    public void QuoteIdent_brackets_digit_leading_names(string name, string expected) =>
        Assert.Equal(expected, Runner.QuoteIdent(name));
}
