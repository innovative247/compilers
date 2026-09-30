using SqlTest;

namespace SqlTest.Tests;

public class SpikeRunTests
{
    // Lines 4-5 are the range; the create line is 3.
    private const string Text = "use sbntest\ngo\ncreate proc pro_a @n int as\nselect @n\nselect @n + 1\ngo\n";
    private const string DropScratch = "if object_id('pro_a__spike') is not null drop proc pro_a__spike";
    private const string VItemDelete = "delete sbntest..tbl_test_writer_item where journal_id = 9 and seq = 1";
    private const string JournalDelete = "delete sbntest..tbl_test_writer_journal where journal_id = 9";
    private const string Grant = "grant execute on pro_a__spike to tester_u";

    private static readonly string Open = Spike.Markers("pro_a", "pro_a__spike", 4, 5).Open;
    private static readonly string Close = Spike.Markers("pro_a", "pro_a__spike", 4, 5).Close;
    private static readonly (int, string)[] OnePass =
    {
        (0, Open),
        (3615, "Table: t scan count 1, logical reads: (regular=3 apf=0 total=3), physical reads: (regular=1 apf=0 total=1)"),
        (3614, "Total writes for this command: 0"),
        (0, Close),
    };

    private sealed class FakeCompiler : IScratchCompiler
    {
        public Func<ScratchCompile, string?> OnCompile = _ => null;
        public List<ScratchCompile> Compiled = new();
        public bool HasOption(string db, string option) => false;
        public string? Compile(ScratchCompile c) { Compiled.Add(c); return OnCompile(c); }
        public string? DeployedText(string db, string proc) => null;
    }

    private sealed class Nothing : IDisposable { public void Dispose() { } }

    // Journal insert gets identity 9; the scratch name is free; restore tables exist; the control login is dbo.
    private sealed class Env
    {
        public readonly FakeExec X = new();
        public readonly FakeCompiler C = new();
        public Func<string, SpikeRun> Side = _ => Run(OnePass);
        public readonly List<(string? Db, bool Tran, string Sql)> Sides = new();
        public readonly List<int> SideAt = new();
        public readonly StringWriter Report = new(), Error = new();
        public string? AsLogin;
        public object? Suid = 7;
        public int DboCount, AliasCount, SaRole;
        public string? UserName = "tester_u";
        public int Opens;
        public int CompiledAt = -1;

        public Env()
        {
            X.OnScalar = sql =>
                sql.StartsWith("insert sbntest..tbl_test_writer_journal") ? 9m
                : sql == "select user_name()" ? "dbo"
                : sql.Contains("..sysobjects where name") ? 0
                : sql.StartsWith("select suser_id(") ? Suid
                : sql.Contains("master..sysloginroles") ? SaRole
                : sql.Contains("master..sysdatabases") ? DboCount
                : sql.Contains("..sysalternates") ? AliasCount
                : 1;
            X.OnRows = sql =>
                sql.Contains("syscolumns") ? new() { new object?[] { "a", "int", 0 } }
                : sql.Contains("..sysusers") ? (UserName == null ? new() : new() { new object?[] { UserName } })
                : new();
            C.OnCompile = _ => { CompiledAt = X.Log.Count; return null; };
        }

        public int Go(string callsJson, int from = 4, int to = 5) => TestScratch.Use(dir =>
        {
            var ss = Path.Combine(dir, "css", "ss", "x");
            Directory.CreateDirectory(ss);
            File.WriteAllText(Path.Combine(ss, "pro_a.sql"), Text);
            var calls = Path.Combine(dir, "calls.json");
            File.WriteAllText(calls, callsJson);
            var deps = new SpikeDeps(new SourceLocator(dir), C, "sbntest",
                () => { Opens++; return (X, X, new Nothing()); },
                (db, tran, sql, onOpen) =>
                {
                    Sides.Add((db, tran, sql));
                    SideAt.Add(X.Log.Count);
                    onOpen(30 + Sides.Count, 300 + Sides.Count);
                    return Side(sql);
                },
                AsLogin, new SybaseIoMeter());
            return Spike.Run(new SpikeRequest("pro_a", from, to, calls, AsLogin == null ? null : "ALIAS", false, 30), deps, Report, Error);
        });

        public bool Journaled => X.Log.Any(l => l.StartsWith("insert sbntest..tbl_test_writer_journal"));
    }

    private static SpikeRun Run((int, string)[] messages, int? raised = null, int? tranCount = null, Exception? fatal = null) =>
        new(messages, raised, tranCount, 1, fatal);

    private static string Calls(string extra = "") =>
        $"{{ \"schema\": 1, \"calls\": [ {{ \"name\": \"one\", \"sql\": \"exec {{proc}} @n = 1\"{extra} }} ] }}";

    private const string Restore = ", \"no_tran\": true, \"restore\": [\"sbntest..t where a = 1\"]";

    [Fact]
    public void A_compile_refusal_drops_closes_and_runs_nothing()
    {
        var e = new Env();
        e.C.OnCompile = _ => "Msg 102, syntax error";
        Assert.Equal(2, e.Go(Calls()));
        Assert.Empty(e.Sides);
        Assert.Contains("compile of sbntest..pro_a__spike failed", e.Error.ToString());
        Assert.Contains(DropScratch, e.X.Log);
        Assert.Contains(JournalDelete, e.X.Log);
    }

    [Fact]
    public void A_range_refusal_exits_2_before_the_control_connection()
    {
        var e = new Env();
        Assert.Equal(2, e.Go(Calls(), 1, 2));
        Assert.Equal(0, e.Opens);
        Assert.False(e.Journaled);
        Assert.Contains("--lines 1-2:", e.Error.ToString());
    }

    [Theory]
    [InlineData("unknown", "login tester is unknown")]
    [InlineData("sarole", "--as tester holds sa_role and acts as dbo in every database")]
    [InlineData("dbo", "login tester owns sbntest (dbo)")]
    [InlineData("alias", "login tester is aliased")]
    [InlineData("nouser", "login tester has no user of its own in sbntest")]
    [InlineData("guest", "login tester has no user of its own in sbntest")]
    [InlineData("dbouser", "login tester has no user of its own in sbntest")]
    public void An_as_login_without_a_real_user_is_refused_before_the_journal(string kind, string why)
    {
        var e = new Env { AsLogin = "tester" };
        switch (kind)
        {
            case "unknown": e.Suid = null; break;
            case "sarole": e.SaRole = 1; break;
            case "dbo": e.DboCount = 1; break;
            case "alias": e.AliasCount = 1; break;
            case "nouser": e.UserName = null; break;
            case "guest": e.UserName = "guest"; break;
            case "dbouser": e.UserName = "dbo"; break;
        }
        Assert.Equal(2, e.Go(Calls()));
        Assert.Contains(why, e.Error.ToString());
        Assert.False(e.Journaled);
        Assert.Empty(e.C.Compiled);
        Assert.Empty(e.Sides);
    }

    [Fact]
    public void The_grant_is_issued_after_compile_and_before_the_call()
    {
        var e = new Env { AsLogin = "tester" };
        Assert.Equal(0, e.Go(Calls()));
        var at = e.X.Log.IndexOf(Grant);
        Assert.True(at >= e.CompiledAt && e.CompiledAt >= 0);
        Assert.True(at < e.SideAt[0]);
        Assert.Equal("use sbntest", e.X.Log[at - 1]);
        Assert.Equal("use sbntest", e.X.Log[at + 1]);
    }

    [Fact]
    public void A_failed_grant_exits_2_and_drops_the_proc()
    {
        var e = new Env { AsLogin = "tester" };
        e.X.FailOn = "grant execute";
        Assert.Equal(2, e.Go(Calls()));
        Assert.Empty(e.Sides);
        Assert.Contains("grant execute on sbntest..pro_a__spike to tester_u failed", e.Error.ToString());
        Assert.Contains(DropScratch, e.X.Log);
        Assert.Contains(VItemDelete, e.X.Log);
    }

    [Fact]
    public void A_grant_to_a_non_identifier_user_is_refused_without_sql()
    {
        var e = new Env { AsLogin = "tester", UserName = "bad user" };
        Assert.Equal(2, e.Go(Calls()));
        Assert.Contains("not a plain identifier", e.Error.ToString());
        Assert.DoesNotContain(e.X.Log, l => l.StartsWith("grant"));
        Assert.Empty(e.Sides);
        Assert.Contains(DropScratch, e.X.Log);
    }

    [Fact]
    public void One_pass_exits_0_and_reports_as_Format()
    {
        var e = new Env();
        Assert.Equal(0, e.Go(Calls()));
        var s = Spike.Slice(OnePass, Open, Close, new SybaseIoMeter());
        var expected = string.Concat(Spike.Format("one", "pro_a", 4, 5, s).Select(l => l + "\n"));
        Assert.Equal(expected, e.Report.ToString().ReplaceLineEndings("\n"));
        Assert.Contains("logical 3", expected);
        var (db, tran, sql) = Assert.Single(e.Sides);
        Assert.Null(db);
        Assert.True(tran);
        Assert.Equal("set statistics io on\nexec pro_a__spike @n = 1\nset statistics io off", sql);
        Assert.Empty(e.Error.ToString());
    }

    [Fact]
    public void A_range_not_reached_exits_1()
    {
        var e = new Env { Side = _ => Run(Array.Empty<(int, string)>()) };
        Assert.Equal(1, e.Go(Calls()));
        Assert.Contains("range not reached", e.Report.ToString());
    }

    [Fact]
    public void A_raised_Msg_is_not_measured_and_not_sliced()
    {
        var e = new Env { Side = _ => Run(OnePass, raised: 515) };
        Assert.Equal(2, e.Go(Calls()));
        Assert.Equal("spike one: not measured: batch raised Msg 515\n", e.Report.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void An_unexpected_trancount_is_not_measured()
    {
        var e = new Env { Side = _ => Run(OnePass, tranCount: 2) };
        Assert.Equal(2, e.Go(Calls()));
        Assert.StartsWith("spike one: not measured: @@trancount 2", e.Report.ToString());
        Assert.DoesNotContain("transaction ended", e.Report.ToString());
    }

    [Fact]
    public void A_trancount_below_expected_says_the_transaction_ended_inside_the_call()
    {
        var e = new Env { Side = _ => Run(OnePass, tranCount: 0) };
        Assert.Equal(2, e.Go(Calls()));
        Assert.Equal("spike one: not measured: @@trancount 0 after the batch, expected 1; the transaction ended inside the call, so later writes were not rolled back\n",
                     e.Report.ToString().ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("success", 0)]
    [InlineData("notreached", 1)]
    [InlineData("raised", 2)]
    [InlineData("fatal", 2)]
    [InlineData("throw", -1)]
    public void Scratch_proc_and_its_item_are_dropped_in_every_outcome(string outcome, int exit)
    {
        var e = new Env
        {
            Side = _ => outcome switch
            {
                "notreached" => Run(Array.Empty<(int, string)>()),
                "raised" => Run(OnePass, raised: 515),
                "fatal" => Run(OnePass, fatal: new InvalidOperationException("dead")),
                "throw" => throw new InvalidOperationException("boom"),
                _ => Run(OnePass),
            },
        };
        if (exit < 0) Assert.Throws<InvalidOperationException>(() => e.Go(Calls()));
        else Assert.Equal(exit, e.Go(Calls()));
        Assert.Contains(DropScratch, e.X.Log);
        Assert.Contains(VItemDelete, e.X.Log);
        Assert.Contains(JournalDelete, e.X.Log);
    }

    [Fact]
    public void A_no_tran_call_keep_restores_then_finalises()
    {
        var e = new Env();
        Assert.Equal(0, e.Go(Calls(Restore)));
        var (_, tran, _) = Assert.Single(e.Sides);
        Assert.False(tran);
        Assert.Contains(e.X.Log, l => l.Contains("'spike pro_a:4-5 one'"));
        Assert.Contains(e.X.Log, l => l.Contains("'spike pro_a:4-5'"));
        var restore = e.X.Log.FindIndex(l => l == "delete sbntest..t where a = 1");
        Assert.True(restore >= e.SideAt[0]);
        var finalise = e.X.Log.IndexOf("drop table sbntest..tbl_test_snap_9_1");
        Assert.True(finalise > restore);
        Assert.Contains(DropScratch, e.X.Log);
    }

    [Fact]
    public void A_no_tran_call_without_restore_is_refused()
    {
        var e = new Env();
        Assert.Equal(2, e.Go(Calls(", \"no_tran\": true")));
        Assert.Contains("call one: no_tran requires restore lines in --spike", e.Error.ToString());
        Assert.Equal(0, e.Opens);
        Assert.Empty(e.Sides);
    }
}
