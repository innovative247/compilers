using System.Text.RegularExpressions;
using ibsCompiler;
using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace LiveCompile.Tests;

/// <summary>Applies the `s#msgno between a and b` predicates (or'd) the messages reader sends.</summary>
internal static class FakeWhere
{
    public static List<string?[]> Filter(string sql, IEnumerable<string?[]> rows)
    {
        var ranges = Regex.Matches(sql, @"s#msgno between (\d+) and (\d+)")
            .Select(m => (Lo: int.Parse(m.Groups[1].Value), Hi: int.Parse(m.Groups[2].Value))).ToList();
        return rows.Where(r => ranges.Count == 0 || ranges.Any(g => int.Parse(r[0]!) >= g.Lo && int.Parse(r[0]!) <= g.Hi)).ToList();
    }
}

/// <summary>In-memory message tables; every executor call is recorded.</summary>
internal sealed class MsgDb : ISqlExecutor, ISqlRowReader
{
    public Dictionary<string, List<string?[]>> Tables = new();
    public int Company = 5, Language = 1, ChgTm = 4242;
    public Func<string, bool>? FailIf;
    public string FailOutput = "";
    public Action<string>? OnExecute;
    public readonly List<string> Queries = new(), Executed = new(), BulkCopies = new();

    public List<object?[]> QueryRows(string sql, string database)
    {
        Queries.Add(sql);
        if (sql.Contains("sysvalues")) return new() { new object?[] { Company, Language, ChgTm } };
        if (sql.Contains("count(*)"))
            return new() { new object?[] { Tables["gui_messages"].Count(r => r[2] == Company.ToString()) } };
        var name = Regex.Match(sql, @"from \S+\.\.(\w+)").Groups[1].Value;
        var rows = Tables.TryGetValue(name, out var t) ? t : new();
        return FakeWhere.Filter(sql, rows).Select(r => r.Select(v => (object?)v).ToArray()).ToList();
    }
    public ExecReturn ExecuteSql(string sqlText, string database, bool captureOutput = false, string outputFile = "")
    {
        Executed.Add(sqlText);
        if (FailIf?.Invoke(sqlText) == true) return new ExecReturn { Returncode = false, Output = FailOutput };
        OnExecute?.Invoke(sqlText);
        // Reload reads the save-table count through ExecuteSql; an empty save table.
        return new ExecReturn { Returncode = true, Output = sqlText.Contains("COUNT(*)") ? "0" : "" };
    }
    public ExecReturn BulkCopy(string table, BcpDirection direction, string dataFile, string formatFile = "", string fieldTerminator = "\t", int batchSize = 0)
    {
        BulkCopies.Add(table);
        return new ExecReturn { Returncode = true, Output = "" };
    }
    public void OpenConnection(string database) { }
    public ExecReturn ExecuteBatch(string batch, bool captureOutput = false, string outputFile = "") => throw new NotSupportedException();
    public void CloseConnection() { }
    public void Dispose() { }
}

[Collection("ConsoleError")]
public sealed class MessagesDispatchTests : IDisposable
{
    private static readonly string[] Names = { "ibs_messages", "jam_messages", "sqr_messages", "sql_messages", "gui_messages",
        "gui_messages_save", "ibs_message_groups", "jam_message_groups", "sqr_message_groups", "sql_message_groups",
        "gui_message_groups", "messages", "sysvalues" };
    private static readonly string[] Exts = { "ibs_msg", "ibs_msgrp", "jam_msg", "jam_msgrp", "sqr_msg", "sqr_msgrp",
        "sql_msg", "sql_msgrp", "gui_msg", "gui_msgrp" };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "livecompile-" + Guid.NewGuid().ToString("N"));
    private readonly string _setup, _out;
    private readonly CommandVariables _cmd;

    public MessagesDispatchTests()
    {
        _setup = Path.Combine(_root, "css", "setup");
        _out = Path.Combine(_root, "run.out");
        _cmd = new() { OutFile = _out, Server = "nosuchserver" };
        Directory.CreateDirectory(_setup);
        var opts = new List<string> { "v:dbpro <<sbnpro>> pro database", "v:dbtbl <<sbnmaster>> table database" };
        opts.AddRange(Names.Select(n => $"v:{n} <<sbnmaster..{n}>> table"));
        opts.AddRange(Exts.Where(e => !e.EndsWith("grp")).Select(e => e.Replace("_msg", "")).Select(e => $"v:w#{e}_messages <<sbnwork..w_{e}_messages>> work"));
        opts.AddRange(Exts.Where(e => e.EndsWith("grp")).Select(e => e.Replace("_msgrp", "")).Select(e => $"v:w#{e}_message_groups <<sbnwork..w_{e}_message_groups>> work"));
        File.WriteAllLines(Path.Combine(_setup, "options.101"), opts);
        File.WriteAllText(Path.Combine(_setup, "table_locations"), "");
        foreach (var e in Exts) Files(e);
        Files("ibs_msg", "1\t1\t0\tG\t \t0\thello");
        Files("ibs_msgrp", "G\t1\tgroup");
        Files("gui_msg", "1\t1\t0\tG\t \t0\tbase");
    }

    private void Files(string ext, params string[] lines) => File.WriteAllLines(Path.Combine(_setup, "css." + ext), lines);

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

    private static string?[] M(int no, int lang, int cmpy, string msg) => new[] { no.ToString(), lang.ToString(), cmpy.ToString(), "G", "", "0", msg };

    private Dictionary<string, List<string?[]>> Parsed()
    {
        var errs = new List<string>();
        var f = Exts.ToDictionary(e => e, e => LiveMessages.ParseFile(e, File.ReadAllLines(Path.Combine(_setup, "css." + e)),
            e.EndsWith("msgrp"), SQLServerTypes.MSSQL, errs));
        Assert.Empty(errs);
        return f;
    }

    /// <summary>A site (company 5) with a saved translation; applying a write batch makes the tables read as the target.</summary>
    private MsgDb Site(bool applyWrites = true)
    {
        var db = new MsgDb();
        foreach (var n in Names) db.Tables[n] = new();
        db.Tables["gui_messages"] = new() { M(1, 1, 0, "base"), M(1, 1, 5, "db own") };
        db.Tables["gui_messages_save"] = new() { M(1, 1, 5, "saved") };
        var targets = compile_msg_main.LiveTargets(Parsed(), db.Language, db.Company, db.Tables["gui_messages_save"], 1, OnSavedTranslations.Keep)!;
        db.OnExecute = sql =>
        {
            if (!sql.Contains("begin tran")) return;
            if (sql.Contains("gui_messages_save")) db.Tables["gui_messages_save"].Clear();
            else if (applyWrites) foreach (var t in targets) db.Tables[t.Table.Label] = t.Rows.ToList();
        };
        return db;
    }

    private static bool Writes(string sql) => sql.Contains("begin tran") && !sql.Contains("gui_messages_save");
    private static bool SaveDelete(string sql) => sql.Contains("begin tran") && sql.Contains("delete sbnmaster..gui_messages_save");

    private int RunLive(MsgDb db, out string err, bool dryRun = false, SQLServerTypes d = SQLServerTypes.MSSQL)
    {
        var old = Console.Error;
        var w = new StringWriter();
        Console.SetError(w);
        try
        {
            return compile_msg_main.Run(_cmd, Profile(d), db, batch: true,
                live: new LiveSettings { Mode = LiveMode.Live, DryRun = dryRun, PlanFile = Path.Combine(_root, "plan.sql") });
        }
        finally { Console.SetError(old); err = w.ToString(); }
    }

    [Fact]
    public void Reload_UsesBulkCopyAndLegacyProcs_NeverReads()
    {
        var db = new MsgDb();
        var rc = compile_msg_main.Run(_cmd, Profile(SQLServerTypes.MSSQL), db, batch: true, live: new LiveSettings { Mode = LiveMode.Reload });
        Assert.True(rc == 0, Log());
        Assert.Empty(db.Queries);
        Assert.Contains("sbnwork..w_ibs_messages", db.BulkCopies);
        Assert.Contains(db.Executed, s => s.Contains("sbnpro..i_compile_messages"));
    }

    [Fact]
    public void Reload_FailedSaveCount_Rc1_BeforeAnyWrite()
    {
        const string denied = "SELECT permission denied on gui_messages_save";
        var db = new MsgDb { FailOutput = denied, FailIf = s => s.Contains("COUNT(*)") };
        var old = Console.Error;
        var w = new StringWriter();
        Console.SetError(w);
        int rc;
        try { rc = compile_msg_main.Run(_cmd, Profile(SQLServerTypes.MSSQL), db, batch: true, live: new LiveSettings { Mode = LiveMode.Reload }); }
        finally { Console.SetError(old); }
        Assert.Equal(1, rc);
        Assert.Contains("sbnmaster..gui_messages_save", w.ToString());
        Assert.Contains(denied, w.ToString());
        Assert.Empty(db.BulkCopies);
        Assert.Contains("COUNT(*)", db.Executed[^1]); // the failed count is the last statement: no save proc, no truncate
    }

    [Fact]
    public void Live_Reads_NeverBulkCopies_NoLegacyCompile()
    {
        var db = Site();
        Assert.True(RunLive(db, out var e0) == 0, Log() + e0);
        Assert.NotEmpty(db.Queries);
        Assert.Empty(db.BulkCopies);
        Assert.DoesNotContain(db.Executed, s => s.Contains("i_compile_messages"));
    }

    [Fact]
    public void Live_Postgres_Refused_NoExecutorCalls()
    {
        var db = new MsgDb();
        Assert.Equal(1, RunLive(db, out var err, d: SQLServerTypes.POSTGRES));
        Assert.Contains(LiveCompile_PostgresRefused, err);
        Assert.Empty(db.Queries);
        Assert.Empty(db.Executed);
        Assert.Empty(db.BulkCopies);
    }

    [Fact]
    public void Runcreate_Postgres_ReloadsMessages_NoRefusal()
    {
        var db = new MsgDb();
        var old = Console.Error;
        var w = new StringWriter();
        Console.SetError(w);
        int rc;
        try { rc = compile_msg_main.Run(_cmd, Profile(SQLServerTypes.POSTGRES), db, batch: true,
                  live: runcreate_main.ImportSettings(Profile(SQLServerTypes.POSTGRES))); }
        finally { Console.SetError(old); }
        Assert.True(rc == 0, Log() + w);
        Assert.DoesNotContain("--reload", w.ToString());
        Assert.Empty(db.Queries);
        Assert.Contains("sbnwork..w_ibs_messages", db.BulkCopies);
        Assert.Contains(db.Executed, s => s.Contains("i_compile_messages"));
    }

    [Fact]
    public void Runcreate_NonPostgres_RunsMessagesLive()
    {
        Assert.Equal(LiveMode.Live, runcreate_main.ImportSettings(Profile(SQLServerTypes.SYBASE)).Mode);
        var db = Site();
        var p = Profile(SQLServerTypes.MSSQL);
        Assert.True(compile_msg_main.Run(_cmd, p, db, batch: true, live: runcreate_main.ImportSettings(p)) == 0, Log());
        Assert.NotEmpty(db.Queries);
        Assert.Empty(db.BulkCopies);
    }

    [Fact]
    public void DryRun_WithSavedRows_LogsWouldEmptySave()
    {
        var db = Site();
        Assert.True(RunLive(db, out var e0, dryRun: true) == 0, Log() + e0);
        Assert.Contains("would empty sbnmaster..gui_messages_save (1 rows)", Log());
    }

    private static readonly string LiveCompile_PostgresRefused = global::ibsCompiler.LiveCompile.PostgresRefused;

    [Fact]
    public void Order_SaveEmptied_AfterMainWrites_BeforeDerive()
    {
        var db = Site();
        Assert.True(RunLive(db, out var e0) == 0, Log() + e0);
        int lastWrite = db.Executed.FindLastIndex(Writes), save = db.Executed.FindIndex(SaveDelete),
            proc = db.Executed.FindIndex(s => s.Contains("i_compile_ibs_messages"));
        Assert.True(lastWrite >= 0 && save > lastWrite && proc > save, $"write {lastWrite}, save {save}, proc {proc}");
        Assert.Empty(db.Tables["gui_messages_save"]);
        Assert.DoesNotContain(db.Executed.Where(Writes), s => s.Contains("gui_messages_save"));
    }

    [Fact]
    public void Order_ReadbackMismatch_SaveTableUntouched()
    {
        var db = Site(applyWrites: false);
        Assert.Equal(1, RunLive(db, out var err));
        Assert.Contains("readback still differs", err);
        Assert.DoesNotContain(db.Executed, SaveDelete);
        Assert.Single(db.Tables["gui_messages_save"]);
    }

    [Fact]
    public void Order_WriteFailure_SaveTableUntouched()
    {
        var db = Site();
        db.FailIf = Writes;
        Assert.Equal(1, RunLive(db, out var err));
        Assert.Contains("failed and was rolled back", err);
        Assert.DoesNotContain(db.Executed, SaveDelete);
        Assert.Single(db.Tables["gui_messages_save"]);
    }

    [Fact]
    public void FailedTypeProc_NamesProcsStillToRun_Rc1()
    {
        Files("sqr_msg", "1\t1\t0\tG\t \t0\tsqr one");
        var db = Site();
        db.FailIf = s => s.Contains("i_compile_sqr_messages");
        Assert.Equal(1, RunLive(db, out var err));
        Assert.Contains("Still to run: i_compile_sqr_messages, i_compile_jrw_messages", err);
        Assert.Contains("compile_msg --reload", err);
        Assert.DoesNotContain(db.Executed, s => s.Contains("i_compile_jrw_messages"));
    }

    [Fact]
    public void AfterDerive_MessagesDiffers_Rc1()
    {
        var db = Site();
        var apply = db.OnExecute;
        db.OnExecute = sql =>
        {
            apply!(sql);
            if (sql.Contains("i_compile_ibs_messages")) db.Tables["messages"].Add(new string?[] { "15999", "G", "stray" });
        };
        Assert.Equal(1, RunLive(db, out var err));
        Assert.Contains("after the type procs differs from the target", err);
        Assert.Contains("15999", err);
    }

    [Fact]
    public void AfterDerive_MessagesMatches_Rc0()
    {
        var db = Site();
        Assert.Equal(0, RunLive(db, out var err));
        Assert.DoesNotContain("differs", err);
    }

    [Fact]
    public void SqrRow10000_WarnsAtParseTime()
    {
        Files("sqr_msg", "10000\t1\t0\tG\t \t0\tcap");
        var db = Site();
        Assert.True(RunLive(db, out var e0, dryRun: true) == 0, Log() + e0);
        var log = File.ReadAllText(_out);
        Assert.Contains("Warning: sqr_msg 10000 (lang 1, cmpy 0)", log);
        Assert.Contains("duplicate key", log);
    }
}
