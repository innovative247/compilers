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

    [Fact]
    public void ResolveWriter_no_transaction_alone_is_not_a_writer()
    {
        var specs = WriterJournal.ResolveWriter("-- @no-transaction\ncreate proc test_x as select 1", noTran: true, out var error);
        Assert.Empty(specs);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("create proc test_x as select 1", false)]
    [InlineData("-- @no-transaction\ncreate proc test_x as select 1", true)]
    public void ResolveRestores_leaves_non_writers_null(string? body, bool noTran)
    {
        Assert.Null(Runner.ResolveRestores(body, noTran, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void ResolveRestores_returns_specs_for_a_writer()
    {
        var specs = Runner.ResolveRestores("-- @no-transaction\n-- @restore: sbntest..tbl_x where id = 1\n", true, out var error);
        Assert.Null(error);
        Assert.Equal(new RestoreSpec("sbntest", "tbl_x", "id = 1", false), Assert.Single(specs!));
    }

    [Fact]
    public void ResolveRestores_refuses_restore_without_no_transaction()
    {
        Assert.Null(Runner.ResolveRestores("-- @restore: sbntest..tbl_x where id = 1\n", false, out var error));
        Assert.Equal("@restore requires @no-transaction", error);
    }

    [Fact]
    public void TestCase_without_restore_keeps_default_writer_fields()
    {
        var tc = new TestCase("test_x", null, "test_x", null, "pre", true, "test_x_teardown", null, false, null,
                              Runner.ResolveRestores("-- @no-transaction", true, out _));
        Assert.Equal(new TestCase("test_x", null, "test_x", null, "pre", true, "test_x_teardown", null, false, null), tc);
        Assert.False(tc.IsWriter);
        Assert.Null(tc.Shape);
    }

    [Fact]
    public void ResolvePairRestores_refuses_restore_in_the_assert_proc()
    {
        var specs = Runner.ResolvePairRestores("-- @no-transaction\n", "-- @restore: sbntest..tbl_x where id = 1\n", true, out var error);
        Assert.Null(specs);
        Assert.Equal("@restore belongs in the _capture proc", error);

        Runner.ResolvePairRestores("-- @no-transaction\n-- @restore: sbntest..tbl_x where id = 1\n", "-- @restore: bad", true, out error);
        Assert.Equal("@restore belongs in the _capture proc", error);
    }

    [Fact]
    public void ResolvePairRestores_reads_the_capture_proc()
    {
        var specs = Runner.ResolvePairRestores("-- @no-transaction\n-- @restore: sbntest..tbl_x where id = 1\n", "create proc x_assert", true, out var error);
        Assert.Null(error);
        Assert.Single(specs!);
    }

    private static readonly TestResult Pass = new("t", Outcome.PASS, "", 1, "");

    [Fact]
    public void WithRestore_without_error_keeps_the_result() =>
        Assert.Same(Pass, Runner.WithRestore(Pass, null));

    [Fact]
    public void WithRestore_turns_pass_into_error() =>
        Assert.Equal(Pass with { Outcome = Outcome.ERROR, Message = "restore failed: boom; journal row 7 kept" },
                     Runner.WithRestore(Pass, "restore failed: boom; journal row 7 kept"));

    [Fact]
    public void WithRestore_turns_fail_into_error_keeping_the_original()
    {
        var r = Runner.WithRestore(Pass with { Outcome = Outcome.FAIL, Message = "FAIL: x" }, "restore failed: boom; journal row 7 kept");
        Assert.Equal(Outcome.ERROR, r.Outcome);
        Assert.Equal("restore failed: boom; journal row 7 kept | was FAIL: FAIL: x", r.Message);
    }

    [Fact]
    public void PartitionWriters_moves_writers_to_the_serial_list_in_order()
    {
        var spec = new[] { new RestoreSpec("sbntest", "t", "1 = 1", false) };
        var a = new TestCase("a", null, "a", null);
        var w1 = new TestCase("w1", null, "w1", null, NoTransaction: true, Restores: spec);
        var b = new TestCase("b", null, "b", null);
        var w2 = new TestCase("w2", null, "w2", null, NoTransaction: true, Restores: spec, Error: "refused");
        var (parallel, serial) = Runner.PartitionWriters(new[] { a, w1, b, w2 });
        Assert.Equal(new[] { a, b }, parallel);
        Assert.Equal(new[] { w1, w2 }, serial);
    }

    [Fact]
    public void PartitionWriters_without_writers_keeps_every_case_parallel()
    {
        var cases = new[] { new TestCase("a", null, "a", null), new TestCase("b", null, "b", null) };
        var (parallel, serial) = Runner.PartitionWriters(cases);
        Assert.Equal(cases, parallel);
        Assert.Empty(serial);
    }

    [Theory]
    [InlineData("1st", "[1st]")]
    [InlineData("a1", "a1")]
    public void QuoteIdent_brackets_digit_leading_names(string name, string expected) =>
        Assert.Equal(expected, Runner.QuoteIdent(name));
}
