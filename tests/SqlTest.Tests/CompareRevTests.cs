using System.Text;
using SqlTest;

namespace SqlTest.Tests;

public class CompareRevTests
{
    private const string OldText = "use sbntest\ngo\ncreate proc pro_a @n int as select @n\ngo\n";
    private const string DropScratch = "if object_id('pro_a__r5') is not null drop proc pro_a__r5";
    private const string VItemDelete = "delete sbntest..tbl_test_writer_item where journal_id = 9 and seq = 1";
    private const string JournalDelete = "delete sbntest..tbl_test_writer_journal where journal_id = 9";

    private sealed class FakeSvn(SvnResult result) : ISvnSource
    {
        public List<(string Path, string Rev)> Calls = new();
        public SvnResult Cat(string workingCopyPath, string rev) { Calls.Add((workingCopyPath, rev)); return result; }
    }

    private sealed class FakeCompiler : IScratchCompiler
    {
        public Func<ScratchCompile, string?> OnCompile = _ => null;
        public List<ScratchCompile> Compiled = new();
        public bool HasOption(string db, string option) => false;
        public string? Compile(ScratchCompile c) { Compiled.Add(c); return OnCompile(c); }
        public string? DeployedText(string db, string proc) => null;
    }

    private sealed class Nothing : IDisposable { public void Dispose() { } }

    // Journal insert gets identity 9; the scratch name is free; snapshots and restore tables exist; the login is dbo.
    private sealed class Env
    {
        public readonly FakeExec X = new();
        public readonly FakeCompiler C = new();
        public FakeSvn Svn = new(new SvnResult(0, OldText, ""));
        public Func<string, CallRun> Side = _ => Run("resultset 1\n");
        public readonly List<(string? Db, bool Tran, string Sql)> Sides = new();
        public readonly List<int> SideAt = new();
        public readonly StringWriter Report = new(), Error = new();
        public int Sweeps;

        public Env()
        {
            X.OnScalar = sql =>
                sql.StartsWith("insert sbntest..tbl_test_writer_journal") ? 9m
                : sql == "select user_name()" ? "dbo"
                : sql.Contains("..sysobjects where name") ? 0
                : 1;
            X.OnRows = sql => sql.Contains("syscolumns") ? new() { new object?[] { "a", "int", 0 } } : new();
        }

        public int Go(string callsJson, bool verbose = false) => TestScratch.Use(dir =>
        {
            var ss = Path.Combine(dir, "css", "ss", "x");
            Directory.CreateDirectory(ss);
            File.WriteAllText(Path.Combine(ss, "pro_a.sql"), OldText);
            var calls = Path.Combine(dir, "calls.json");
            File.WriteAllText(calls, callsJson);
            var deps = new CompareDeps(Svn, new SourceLocator(dir), C, "sbntest",
                () => (X, X, new Nothing()),
                (db, tran, sql, _, onOpen) =>
                {
                    Sides.Add((db, tran, sql));
                    SideAt.Add(X.Log.Count);
                    onOpen(30 + Sides.Count, 300 + Sides.Count);
                    return Side(sql);
                },
                () => Sweeps++);
            return CompareRev.Run(new CompareRequest("5", "pro_a", calls, false, verbose, 30), deps, Report, Error);
        });
    }

    private static CallRun Run(string output, Exception? fatal = null, string? incomplete = null, bool tranEnded = false, bool notExecuted = false, bool raised = false) =>
        new(Encoding.UTF8.GetBytes(output), 1, 2, fatal, incomplete, tranEnded, notExecuted, raised);

    private const string Aborted = "batch raised Msg 515; the driver drops result sets and prints delivered before it";

    private static string Calls(string sql = "exec {proc} @n = 1", string? db = null, string extra = "") =>
        $"{{ \"schema\": 1{(db == null ? "" : $", \"database\": \"{db}\"")}, \"calls\": [ {{ \"name\": \"one\", \"sql\": \"{sql}\"{extra} }} ] }}";

    private static bool IsOld(string sql) => sql.Contains("pro_a__r5");

    // ---- refusals before anything is journalled ----

    [Fact]
    public void Svn_failure_exits_2_and_journals_nothing()
    {
        var e = new Env { Svn = new FakeSvn(new SvnResult(1, "", "svn: E160006: No such revision 5")) };
        Assert.Equal(2, e.Go(Calls()));
        Assert.Contains("sql-test: svn cat -r 5 css/ss/x/pro_a.sql failed: svn: E160006", e.Error.ToString());
        Assert.EndsWith(Path.Combine("css", "ss", "x", "pro_a.sql"), e.Svn.Calls.Single().Path);
        Assert.Empty(e.X.Log);
        Assert.Empty(e.Report.ToString());
    }

    [Fact]
    public void Old_text_without_the_proc_exits_2_and_journals_nothing()
    {
        var e = new Env { Svn = new FakeSvn(new SvnResult(0, "use sbntest\ngo\ncreate proc other as select 1\ngo\n", "")) };
        Assert.Equal(2, e.Go(Calls()));
        Assert.Contains("pro_a is not created in css/ss/x/pro_a.sql@r5", e.Error.ToString());
        Assert.Empty(e.X.Log);
        Assert.Empty(e.C.Compiled);
    }

    [Fact]
    public void No_tran_without_restore_is_refused_before_svn()
    {
        var e = new Env();
        Assert.Equal(2, e.Go(Calls(extra: ", \"no_tran\": true")));
        Assert.Equal("sql-test: call one: no_tran requires restore lines in --compare-rev\n", e.Error.ToString().ReplaceLineEndings("\n"));
        Assert.Empty(e.Svn.Calls);
        Assert.Empty(e.X.Log);
    }

    [Fact]
    public void Call_in_another_db_with_unqualified_proc_is_refused_before_any_compile()
    {
        var e = new Env();
        Assert.Equal(2, e.Go(Calls(db: "sbnmaster")));
        Assert.Equal("sql-test: call one: {proc} must be qualified with sbntest.. because the call runs in sbnmaster\n",
                     e.Error.ToString().ReplaceLineEndings("\n"));
        Assert.Empty(e.C.Compiled);
        Assert.Empty(e.X.Log);
        Assert.Empty(e.Sides);
        Assert.Equal(0, e.Sweeps);
    }

    [Theory]
    [InlineData("exec sbntest..{proc} @n = 1", "exec @sql_test_rc = sbntest..pro_a__r5 @n = 1")]
    [InlineData("exec sbntest.dbo.{proc} @n = 1", "exec @sql_test_rc = sbntest.dbo.pro_a__r5 @n = 1")]
    public void Call_in_another_db_with_qualified_proc_runs(string sql, string oldExec)
    {
        var e = new Env();
        Assert.Equal(0, e.Go(Calls(sql, db: "sbnmaster")));
        Assert.All(e.Sides, s => Assert.Equal("sbnmaster", s.Db));
        Assert.Contains(oldExec, e.Sides[0].Sql);
    }

    [Theory]
    [InlineData("sbntest")]
    [InlineData(null)]
    public void Call_in_the_scratch_db_needs_no_qualifier(string? db)
    {
        var e = new Env();
        Assert.Equal(0, e.Go(Calls(db: db)));
        Assert.Equal(db, e.Sides[0].Db);
    }

    // ---- outcomes ----

    [Fact]
    public void Identical_sides_report_same_and_exit_0()
    {
        var e = new Env();
        Assert.Equal(0, e.Go(Calls()));
        Assert.Equal("compare one: same\n", e.Report.ToString().ReplaceLineEndings("\n"));
        Assert.Equal(2, e.Sides.Count);
        Assert.True(IsOld(e.Sides[0].Sql));
        Assert.False(IsOld(e.Sides[1].Sql));
        Assert.All(e.Sides, s => Assert.True(s.Tran));
        Assert.Equal(1, e.Sweeps);
        Assert.Contains(e.X.Log, l => l.Contains("'compare-rev pro_a@r5'"));
    }

    [Fact]
    public void A_difference_exits_1_and_reports_the_offset()
    {
        var e = new Env { Side = sql => Run(IsOld(sql) ? "row: 1\nrc: 0\n" : "row: 2\nrc: 0\n") };
        Assert.Equal(1, e.Go(Calls()));
        var report = e.Report.ToString();
        Assert.Contains("compare one: differs at byte 5 (line 1)", report);
        Assert.Contains("  old 1: row: 1", report);
        Assert.Contains("  new 1: row: 2", report);
        Assert.DoesNotContain("  old| ", report);
    }

    [Fact]
    public void Verbose_prints_both_streams_after_a_difference()
    {
        var e = new Env { Side = sql => Run(IsOld(sql) ? "row: 1\n" : "row: 2\n") };
        Assert.Equal(1, e.Go(Calls(), verbose: true));
        Assert.Contains("  old| row: 1", e.Report.ToString());
        Assert.Contains("  new| row: 2", e.Report.ToString());
    }

    [Fact]
    public void A_fatal_side_exits_2_as_not_compared()
    {
        var e = new Env { Side = sql => IsOld(sql) ? Run("", new CommandTimeoutException(30, null)) : Run("rc: 0\n") };
        Assert.Equal(2, e.Go(Calls()));
        Assert.StartsWith("compare one: not compared: command timeout", e.Report.ToString());
        Assert.Contains(DropScratch, e.X.Log);
        Assert.Contains(VItemDelete, e.X.Log);
    }

    [Fact]
    public void A_compile_refusal_exits_2_and_closes_the_journal_row()
    {
        var e = new Env();
        e.C.OnCompile = _ => "Msg 102 syntax";
        Assert.Equal(2, e.Go(Calls()));
        Assert.Contains("compile of sbntest..pro_a__r5 failed: Msg 102 syntax", e.Error.ToString());
        Assert.Contains(DropScratch, e.X.Log);
        Assert.Contains(VItemDelete, e.X.Log);
        Assert.Contains(JournalDelete, e.X.Log);
        Assert.Empty(e.Sides);
    }

    [Theory]
    [InlineData("same", 0)]
    [InlineData("differ", 1)]
    [InlineData("fatal", 2)]
    [InlineData("throw", -1)]
    public void Scratch_proc_and_its_item_are_dropped_in_every_outcome(string outcome, int exit)
    {
        var e = new Env
        {
            Side = sql => outcome switch
            {
                "differ" => Run(IsOld(sql) ? "a\n" : "b\n"),
                "fatal" => Run("", new InvalidOperationException("dead")),
                "throw" => throw new InvalidOperationException("boom"),
                _ => Run("a\n"),
            },
        };
        if (exit < 0) Assert.Throws<InvalidOperationException>(() => e.Go(Calls()));
        else Assert.Equal(exit, e.Go(Calls()));
        Assert.Contains(DropScratch, e.X.Log);
        Assert.Contains(VItemDelete, e.X.Log);
        Assert.Contains(JournalDelete, e.X.Log);
    }

    [Fact]
    public void A_no_tran_call_keep_restores_after_each_side_then_finalises()
    {
        var e = new Env();
        Assert.Equal(0, e.Go(Calls(extra: ", \"no_tran\": true, \"restore\": [\"sbntest..t where a = 1\"]")));
        Assert.All(e.Sides, s => Assert.False(s.Tran));
        Assert.Contains(e.X.Log, l => l.Contains("'compare-rev pro_a@r5 one'"));
        Assert.Equal(2, e.X.Log.Count(l => l.StartsWith("update sbntest..tbl_test_writer_journal set test_spid")));
        Assert.Equal(2, e.X.Log.Count(l => l == "delete sbntest..t where a = 1"));
        var firstRestore = e.X.Log.FindIndex(l => l == "delete sbntest..t where a = 1");
        Assert.InRange(firstRestore, e.SideAt[0], e.SideAt[1] - 1);
        var lastRestore = e.X.Log.FindLastIndex(l => l == "delete sbntest..t where a = 1");
        Assert.True(lastRestore >= e.SideAt[1]);
        var finalise = e.X.Log.IndexOf("drop table sbntest..tbl_test_snap_9_1");
        Assert.True(finalise > lastRestore);
        Assert.Equal("compare one: same\n", e.Report.ToString().ReplaceLineEndings("\n"));
        Assert.Contains(DropScratch, e.X.Log);
        Assert.Contains(VItemDelete, e.X.Log);
    }

    [Fact]
    public void A_restore_failure_stops_the_remaining_calls()
    {
        var e = new Env();
        e.X.FailOn = "delete sbntest..t where a = 1";
        var json = "{ \"schema\": 1, \"calls\": [" +
                   " { \"name\": \"one\", \"sql\": \"exec {proc}\", \"no_tran\": true, \"restore\": [\"sbntest..t where a = 1\"] }," +
                   " { \"name\": \"two\", \"sql\": \"exec {proc}\" } ] }";
        Assert.Equal(2, e.Go(json));
        Assert.Single(e.Sides);
        Assert.Contains("sql-test: call one: restore failed:", e.Error.ToString());
        // Neither call reports: one stopped after its old side, two never ran.
        Assert.Empty(e.Report.ToString());
        // Snapshots stay for the sweep: a failed restore is not finalised.
        Assert.DoesNotContain("drop table sbntest..tbl_test_snap_9_1", e.X.Log);
        Assert.Contains(DropScratch, e.X.Log);
        Assert.Contains(VItemDelete, e.X.Log);
    }

    [Fact]
    public void A_no_tran_side_that_throws_is_still_finalised()
    {
        var e = new Env { Side = sql => IsOld(sql) ? Run("a\n") : throw new InvalidOperationException("boom") };
        Assert.Throws<InvalidOperationException>(() => e.Go(Calls(extra: ", \"no_tran\": true, \"restore\": [\"sbntest..t where a = 1\"]")));
        Assert.Equal(2, e.X.Log.Count(l => l == "delete sbntest..t where a = 1"));
        Assert.Contains("drop table sbntest..tbl_test_snap_9_1", e.X.Log);
        Assert.Contains(DropScratch, e.X.Log);
    }

    // ---- incomplete, ended and unexecuted sides ----

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Equal_bytes_with_an_incomplete_side_exit_2(bool oldIncomplete, bool newIncomplete)
    {
        var e = new Env { Side = sql => Run("error: Msg 515\nrc: none\n", incomplete: (IsOld(sql) ? oldIncomplete : newIncomplete) ? Aborted : null) };
        Assert.Equal(2, e.Go(Calls()));
        Assert.Equal($"compare one: not compared: {Aborted}\n", e.Report.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Different_bytes_with_an_incomplete_side_exit_1()
    {
        var e = new Env { Side = sql => Run(IsOld(sql) ? "error: Msg 515\nrc: none\n" : "rc: 0\n", incomplete: IsOld(sql) ? Aborted : null) };
        Assert.Equal(1, e.Go(Calls()));
        Assert.StartsWith("compare one: differs at byte 0", e.Report.ToString());
    }

    [Theory]
    [InlineData("same")]
    [InlineData("differ")]
    public void A_side_that_ended_the_transaction_exits_2_whatever_the_bytes(string bytes)
    {
        var e = new Env { Side = sql => Run(bytes == "differ" && IsOld(sql) ? "a\n" : "b\n", tranEnded: !IsOld(sql)) };
        Assert.Equal(2, e.Go(Calls()));
        Assert.Equal("compare one: not compared: transaction ended inside the call; later statements were not rolled back\n",
                     e.Report.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_raised_error_is_reported_before_the_ended_transaction()
    {
        var e = new Env { Side = sql => Run("error: Msg 515\nrc: none\n", incomplete: Aborted, tranEnded: true, raised: true) };
        Assert.Equal(2, e.Go(Calls()));
        Assert.Equal($"compare one: not compared: {Aborted}\n", e.Report.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void An_ended_transaction_without_an_error_keeps_the_transaction_reason()
    {
        var e = new Env { Side = sql => Run("rc: 0\n", incomplete: "@@trancount is 0 after the call (expected 1)", tranEnded: true) };
        Assert.Equal(2, e.Go(Calls()));
        Assert.StartsWith("compare one: not compared: transaction ended inside the call", e.Report.ToString());
    }

    [Fact]
    public void A_side_whose_exec_never_ran_exits_2()
    {
        var e = new Env { Side = sql => Run("rc: none\n", notExecuted: true) };
        Assert.Equal(2, e.Go(Calls()));
        Assert.Equal("compare one: not compared: {proc} was not executed\n", e.Report.ToString().ReplaceLineEndings("\n"));
    }

    // RunCompareSide's decision part: (raised Msg, @@trancount, expected, rc present).
    [Theory]
    [InlineData(null, 1, 1, true, null, false, false)]
    [InlineData(null, 0, 0, true, null, false, false)]
    [InlineData(515, 1, 1, false, "batch raised Msg 515; the driver drops result sets and prints delivered before it", false, false)]
    [InlineData(515, 0, 1, false, "batch raised Msg 515; the driver drops result sets and prints delivered before it", true, false)]
    [InlineData(null, 0, 1, true, "@@trancount is 0 after the call (expected 1)", true, false)]
    [InlineData(null, 2, 1, true, "@@trancount is 2 after the call (expected 1)", false, false)]
    [InlineData(null, 1, 0, true, "@@trancount is 1 after the call (expected 0)", false, false)]
    [InlineData(null, null, 1, true, "@@trancount could not be read after the call", false, false)]
    [InlineData(null, 1, 1, false, null, false, true)]
    public void Classify_decides_incomplete_ended_and_not_executed(int? raised, int? trancount, int expected, bool hasRc,
                                                                   string? incomplete, bool ended, bool notExecuted)
    {
        var r = CompareRev.Classify(Encoding.UTF8.GetBytes("x\n"), 3, 4, raised, trancount, expected, hasRc);
        Assert.Equal(incomplete, r.Incomplete);
        Assert.Equal(ended, r.TranEnded);
        Assert.Equal(raised != null, r.Raised);
        Assert.Equal(notExecuted, r.NotExecuted);
        Assert.Null(r.Fatal);
        Assert.Equal((3, 4), (r.Spid, r.Kpid));
    }

    // ---- call text ----

    [Theory]
    [InlineData("exec {proc} @n = 1", "exec @sql_test_rc = {proc} @n = 1")]
    [InlineData("EXECUTE {proc}", "EXECUTE @sql_test_rc = {proc}")]
    [InlineData("exec sbntest..{proc}", "exec @sql_test_rc = sbntest..{proc}")]
    public void WrapReturnCode_assigns_the_rc_and_selects_it(string sql, string exec)
    {
        Assert.Equal($"declare @sql_test_rc int\n{exec}\nselect sql_test_rc = @sql_test_rc", CompareRev.WrapReturnCode(sql));
    }

    [Theory]
    [InlineData("exec {proc} exec {proc}")]
    [InlineData("exec {proc} select '{proc}'")]
    [InlineData("select 1")]
    [InlineData("exec other..x.{proc}")]
    public void Call_sql_without_exactly_one_exec_proc_is_refused(string sql)
    {
        Assert.Equal("sql must contain exactly one exec {proc}", CompareRev.CallSqlRefusal(sql));
        Assert.Throws<ArgumentException>(() => CompareRev.WrapReturnCode(sql));
    }

    [Fact]
    public void Old_and_new_sides_get_different_names()
    {
        var sql = CompareRev.WrapReturnCode("exec {proc} @n = 1");
        Assert.Contains("= pro_a__r5 @n", CompareRev.Substitute(sql, "pro_a__r5"));
        Assert.Contains("= pro_a @n", CompareRev.Substitute(sql, "pro_a"));
    }

    [Theory]
    [InlineData("exec {proc}", "sbntest", "sbntest", false)]
    [InlineData("exec {proc}", "sbntest", "sbnmaster", true)]
    [InlineData("exec sbntest..{proc}", "sbntest", "sbnmaster", false)]
    [InlineData("exec sbntest.dbo.{proc}", "sbntest", "sbnmaster", false)]
    [InlineData("exec sbnmaster..{proc}", "sbntest", "sbntest", true)]
    [InlineData("exec other.dbo.{proc}", "sbntest", "sbntest", true)]
    [InlineData("exec {proc}", "sbntest", "SBNTEST", false)]
    public void QualifierRefusal_applies_only_across_databases(string sql, string scratchDb, string callDb, bool refused)
    {
        Assert.Equal(refused, CompareRev.QualifierRefusal(sql, scratchDb, callDb) != null);
    }

    [Theory]
    [InlineData("exec sbnmaster..{proc}")]
    [InlineData("exec other.dbo.{proc}")]
    public void QualifierRefusal_refuses_a_qualifier_naming_another_db(string sql)
    {
        var db = sql.Contains("sbnmaster") ? "sbnmaster" : "other";
        Assert.Equal($"{{proc}} is qualified with {db}, but the scratch proc is compiled in sbntest",
                     CompareRev.QualifierRefusal(sql, "sbntest", "sbnmaster"));
    }

    [Theory]
    [InlineData("exec SBNTEST..{proc}")]
    [InlineData("exec SbnTest.dbo.{proc}")]
    public void QualifierRefusal_matches_the_scratch_db_case_insensitively(string sql) =>
        Assert.Null(CompareRev.QualifierRefusal(sql, "sbntest", "sbnmaster"));

    [Theory]
    [InlineData("sbnmaster")]
    [InlineData(null)]
    public void Call_qualified_with_another_db_is_refused_before_any_compile(string? callDb)
    {
        var e = new Env();
        Assert.Equal(2, e.Go(Calls("exec sbnmaster..{proc} @n = 1", db: callDb)));
        Assert.Equal("sql-test: call one: {proc} is qualified with sbnmaster, but the scratch proc is compiled in sbntest\n",
                     e.Error.ToString().ReplaceLineEndings("\n"));
        Assert.Empty(e.C.Compiled);
        Assert.Empty(e.X.Log);
    }
}
