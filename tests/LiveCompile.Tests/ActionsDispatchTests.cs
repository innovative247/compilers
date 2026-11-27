using ibsCompiler;
using ibsCompiler.Configuration;

namespace LiveCompile.Tests;

public sealed class ActionsDispatchTests : IDisposable
{
    private static readonly string[] Tables = { "actions", "menus", "action_profiles", "action_profile_details", "gui_messages", "messages", "sysvalues" };
    private static readonly string[] ActLines = { ":>0005 001 Hi<<3>>foo" };
    private static readonly string[] DtlLines = { ":>0005 001 002 00777 003" };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "livecompile-" + Guid.NewGuid().ToString("N"));
    private readonly string _setup, _out;
    private readonly CommandVariables _cmd;

    public ActionsDispatchTests()
    {
        _setup = Path.Combine(_root, "css", "setup");
        _out = Path.Combine(_root, "run.out");
        _cmd = new() { OutFile = _out, Server = "nosuchserver" };
        Directory.CreateDirectory(_setup);
        File.WriteAllText(Path.Combine(_setup, "table_locations"), "");
        File.WriteAllLines(Path.Combine(_setup, "actions"), ActLines);
        File.WriteAllLines(Path.Combine(_setup, "actions_dtl"), DtlLines);
        WriteOptions("Admin");
    }

    private void WriteOptions(string? allAct)
    {
        var opts = new List<string> { "v:dbpro <<sbnpro>> pro database",
            "v:w#actions <<sbnwork..w_actions>> work", "v:w#actions_dtl <<sbnwork..w_actions_dtl>> work" };
        opts.AddRange(Tables.Select(n => $"v:{n} <<sbnmaster..{n}>> table"));
        if (allAct != null) opts.Add($"v:allact <<{allAct}>> all-actions profile");
        File.WriteAllLines(Path.Combine(_setup, "options.101"), opts);
    }

    private ResolvedProfile Profile(SQLServerTypes d) => new()
    {
        ProfileName = "livetest" + Path.GetFileName(_root), IsProfile = true, Company = "101", ServerType = d, IRPath = _root,
    };

    public void Dispose()
    {
        foreach (var d in new[] { SQLServerTypes.MSSQL, SQLServerTypes.POSTGRES })
            try { File.Delete(ibs_compiler_common.GetPath_ResolvedOptions(_cmd, Profile(d))); } catch { }
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Log() => File.Exists(_out) ? File.ReadAllText(_out) : "";

    /// <summary>Profile 1 'Admin', no actions yet; a write batch makes the tables read as the target.</summary>
    private MsgDb Site()
    {
        var db = new MsgDb();
        foreach (var n in Tables) db.Tables[n] = new();
        db.Tables["action_profiles"] = new() { new[] { "1", "Admin" } };
        var targets = LiveActions.BuildTargets(ActLines, DtlLines, s => s, SQLServerTypes.MSSQL, db.Company, db.Language,
            Array.Empty<(int, int, int)>(), new[] { 1 }, new[] { 1 }, db.ChgTm, new List<string>(), new List<string>());
        db.OnExecute = sql =>
        {
            if (sql.Contains("begin tran")) foreach (var t in targets) db.Tables[t.Table.Label] = t.Rows.ToList();
        };
        return db;
    }

    [Fact]
    public void Reload_UsesBulkCopyAndProc_NeverReads()
    {
        var db = new MsgDb();
        Assert.Equal(0, compile_actions_main.Run(_cmd, Profile(SQLServerTypes.MSSQL), db, new LiveSettings { Mode = LiveMode.Reload }));
        Assert.Empty(db.Queries);
        Assert.Equal(new[] { "sbnwork..w_actions", "sbnwork..w_actions_dtl" }, db.BulkCopies);
        Assert.Contains(db.Executed, s => s.Contains("sbnpro..ba_compile_actions"));
    }

    [Fact]
    public void Reload_MalformedDetail_Rc1_NoBulkCopy()
    {
        File.WriteAllLines(Path.Combine(_setup, "actions_dtl"), new[] { ":>0005 001" });
        var db = new MsgDb();
        Assert.Equal(1, compile_actions_main.Run(_cmd, Profile(SQLServerTypes.MSSQL), db, new LiveSettings { Mode = LiveMode.Reload }));
        Assert.Empty(db.BulkCopies);
        Assert.DoesNotContain(db.Executed, s => s.Contains("ba_compile_actions"));
    }

    [Fact]
    public void Live_Reads_NeverBulkCopies_ChgTmFromServer()
    {
        var db = Site();
        Assert.Equal(0, compile_actions_main.Run(_cmd, Profile(SQLServerTypes.MSSQL), db, new LiveSettings()));
        Assert.NotEmpty(db.Queries);
        Assert.Contains(db.Queries, q => q.Contains("datediff(ss,'800101',getdate())"));
        Assert.Empty(db.BulkCopies);
        Assert.DoesNotContain(db.Executed, s => s.Contains("ba_compile_actions"));
        Assert.Contains(db.Executed, s => s.Contains("insert sbnmaster..gui_messages") && s.Contains("4242"));
        Assert.DoesNotContain("no action profile named", Log());
    }

    [Fact]
    public void Live_AllActWarning_OnlyWhenSetAndUnmatched()
    {
        WriteOptions("Nobody");
        compile_actions_main.Run(_cmd, Profile(SQLServerTypes.MSSQL), Site(), new LiveSettings { DryRun = true, PlanFile = Path.Combine(_root, "plan.sql") });
        Assert.Contains("no action profile named by option allact ('Nobody')", Log());

        File.Delete(_out);
        WriteOptions(null);
        compile_actions_main.Run(_cmd, Profile(SQLServerTypes.MSSQL), Site(), new LiveSettings { DryRun = true, PlanFile = Path.Combine(_root, "plan.sql") });
        Assert.DoesNotContain("no action profile named", Log());
    }

    [Fact]
    public void Runcreate_Postgres_ReloadsActions()
    {
        var db = new MsgDb();
        var p = Profile(SQLServerTypes.POSTGRES);
        Assert.Equal(0, compile_actions_main.Run(_cmd, p, db, runcreate_main.ImportSettings(p)));
        Assert.Empty(db.Queries);
        Assert.Equal(new[] { "sbnwork..w_actions", "sbnwork..w_actions_dtl" }, db.BulkCopies);
    }

    [Fact]
    public void Live_Postgres_Refused_NoExecutorCalls()
    {
        var db = Site();
        Assert.Equal(1, compile_actions_main.Run(_cmd, Profile(SQLServerTypes.POSTGRES), db, new LiveSettings()));
        Assert.Empty(db.Queries);
        Assert.Empty(db.Executed);
        Assert.Empty(db.BulkCopies);
    }
}
