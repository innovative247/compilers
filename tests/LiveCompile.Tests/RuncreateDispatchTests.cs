using ibsCompiler;
using ibsCompiler.Configuration;

namespace LiveCompile.Tests;

/// <summary>Drives runcreate_main.Run itself, so the import_options / install_msg / compile_actions wiring is covered.</summary>
[Collection("ConsoleError")]
public sealed class RuncreateDispatchTests : IDisposable
{
    private static readonly string[] Exts = { "ibs_msg", "ibs_msgrp", "jam_msg", "jam_msgrp", "sqr_msg", "sqr_msgrp",
        "sql_msg", "sql_msgrp", "gui_msg", "gui_msgrp" };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "livecompile-" + Guid.NewGuid().ToString("N"));
    private readonly string _setup, _out, _create;
    private readonly CommandVariables _cmd;

    public RuncreateDispatchTests()
    {
        _setup = Path.Combine(_root, "css", "setup");
        _out = Path.Combine(_root, "run.out");
        _create = Path.Combine(_root, "create.txt");
        Directory.CreateDirectory(_setup);
        var opts = new List<string> { "v:dbpro <<sbnpro>> pro database", "v:dbtbl <<sbnmaster>> table database",
            "v:w#options <<sbnwork..w_options>> work", "v:options <<sbnmaster..options>> table",
            "v:w#actions <<sbnwork..w_actions>> work", "v:w#actions_dtl <<sbnwork..w_actions_dtl>> work" };
        opts.AddRange(Exts.Where(e => !e.EndsWith("grp")).Select(e => e.Replace("_msg", "")).Select(e => $"v:w#{e}_messages <<sbnwork..w_{e}_messages>> work"));
        opts.AddRange(Exts.Where(e => e.EndsWith("grp")).Select(e => e.Replace("_msgrp", "")).Select(e => $"v:w#{e}_message_groups <<sbnwork..w_{e}_message_groups>> work"));
        File.WriteAllLines(Path.Combine(_setup, "options.101"), opts);
        File.WriteAllText(Path.Combine(_setup, "table_locations"), "");
        File.WriteAllLines(Path.Combine(_setup, "actions"), new[] { ":>0005 001 Hi<<3>>foo" });
        File.WriteAllLines(Path.Combine(_setup, "actions_dtl"), new[] { ":>0005 001 002 00777 003" });
        foreach (var e in Exts) File.WriteAllLines(Path.Combine(_setup, "css." + e), Array.Empty<string>());
        File.WriteAllLines(Path.Combine(_setup, "css.ibs_msg"), new[] { "1\t1\t0\tG\t \t0\thello" });
        File.WriteAllLines(_create, new[] { "import_options x", "install_msg x", "compile_actions x" });
        _cmd = new() { OutFile = _out, Server = "nosuchserver", Command = _create };
    }

    private ResolvedProfile Profile() => new()
    {
        ProfileName = "livetest" + Path.GetFileName(_root), IsProfile = true, Company = "101",
        ServerType = SQLServerTypes.POSTGRES, IRPath = _root,
    };

    public void Dispose()
    {
        try { File.Delete(ibs_compiler_common.GetPath_ResolvedOptions(_cmd, Profile())); } catch { }
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Log() => File.Exists(_out) ? File.ReadAllText(_out) : "";

    private bool RunCreate(MsgDb db)
    {
        var old = Console.Error;
        Console.SetError(new StringWriter());
        try { return new runcreate_main().Run(_cmd, Profile(), db); }
        finally { Console.SetError(old); }
    }

    [Fact]
    public void Postgres_AllThreeImportSteps_Reload()
    {
        var db = new MsgDb();
        Assert.True(RunCreate(db), Log());
        Assert.Empty(db.Queries);
        Assert.Contains("sbnwork..w_options", db.BulkCopies);
        Assert.Contains("sbnwork..w_ibs_messages", db.BulkCopies);
        Assert.Contains("sbnwork..w_actions", db.BulkCopies);
        Assert.DoesNotContain("continuing runcreate", Log());
    }

    [Fact]
    public void FailedStep_IsLogged_NextStepStillRuns()
    {
        var db = new MsgDb { FailIf = s => s.Contains("i_import_options") };
        Assert.True(RunCreate(db), Log());
        Assert.Contains("Error: import_options failed; continuing runcreate.", Log());
        Assert.Contains("sbnwork..w_ibs_messages", db.BulkCopies);
        Assert.Contains("sbnwork..w_actions", db.BulkCopies);
    }
}
