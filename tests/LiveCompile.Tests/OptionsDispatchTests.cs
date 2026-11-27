using ibsCompiler;
using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace LiveCompile.Tests;

/// <summary>Records every executor call; QueryRows answers from <see cref="Rows"/>.</summary>
internal sealed class RecordingDb : ISqlExecutor, ISqlRowReader
{
    public List<object?[]> Rows = new();
    public readonly List<string> Queries = new(), Executed = new(), BulkCopies = new();
    public Action<string>? OnExecute;

    public List<object?[]> QueryRows(string sql, string database)
    {
        Queries.Add(sql);
        return Rows.Select(r => (object?[])r.Clone()).ToList();
    }
    public ExecReturn ExecuteSql(string sqlText, string database, bool captureOutput = false, string outputFile = "")
    {
        Executed.Add(sqlText);
        OnExecute?.Invoke(sqlText);
        return new ExecReturn { Returncode = true, Output = "" };
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

public sealed class OptionsDispatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "livecompile-" + Guid.NewGuid().ToString("N"));
    private readonly string _companyFile;
    private CommandVariables _cmd = new() { OutFile = "", Server = "nosuchserver" };

    public OptionsDispatchTests()
    {
        var setup = Path.Combine(_root, "css", "setup");
        Directory.CreateDirectory(setup);
        _companyFile = Path.Combine(setup, "options.101");
        File.WriteAllLines(_companyFile, new[]
        {
            "v:dbpro <<sbnpro>> pro database",
            "v:w#options <<sbnwork..w_options>> work table",
            "v:options <<sbnmaster..options>> options table",
            "v:sample <<abc>> a static option",
        });
        File.WriteAllText(Path.Combine(setup, "table_locations"), "");
    }

    private ResolvedProfile Profile(SQLServerTypes d) => new()
    {
        ProfileName = "livetest" + Path.GetFileName(_root), IsProfile = true, Company = "101",
        ServerType = d, IRPath = _root,
    };

    public void Dispose()
    {
        foreach (var d in new[] { SQLServerTypes.MSSQL, SQLServerTypes.POSTGRES })
            try { File.Delete(ibs_compiler_common.GetPath_ResolvedOptions(_cmd, Profile(d))); } catch { }
        try { Directory.Delete(_root, true); } catch { }
    }

    private List<object?[]> TargetRows(SQLServerTypes d) =>
        LiveOptions.TargetRows(ibs_compiler_common.GenerateImportOptionFile(_companyFile), d, new List<string>())
            .Select(r => r.Cast<object?>().ToArray()).ToList();

    private static bool TouchesTableLocations(RecordingDb db) =>
        db.Executed.Concat(db.BulkCopies).Any(s => s.Contains("table_locations", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Reload_UsesProcAndBulkCopy_NeverReads()
    {
        var db = new RecordingDb();
        var r = compile_options_main.Run(_cmd, Profile(SQLServerTypes.MSSQL), db, new LiveSettings { Mode = LiveMode.Reload });
        Assert.True(r.Returncode);
        Assert.Empty(db.Queries);
        Assert.Contains("sbnwork..w_options", db.BulkCopies);
        Assert.Contains(db.Executed, s => s.Contains("sbnpro..i_import_options"));
    }

    [Fact]
    public void Live_ReadsAndWritesKeyed_NoBulkCopy_NoTableLocations()
    {
        var db = new RecordingDb();
        db.OnExecute = _ => db.Rows = TargetRows(SQLServerTypes.MSSQL);
        var r = compile_options_main.Run(_cmd, Profile(SQLServerTypes.MSSQL), db, new LiveSettings());
        Assert.True(r.Returncode);
        Assert.NotEmpty(db.Queries);
        Assert.Empty(db.BulkCopies);
        Assert.Contains(db.Executed, s => s.Contains("insert sbnmaster..options"));
        Assert.False(TouchesTableLocations(db));
    }

    [Fact]
    public void Live_AlreadyCurrent_WritesNothing()
    {
        var db = new RecordingDb { Rows = TargetRows(SQLServerTypes.MSSQL) };
        Assert.True(compile_options_main.Run(_cmd, Profile(SQLServerTypes.MSSQL), db, new LiveSettings()).Returncode);
        Assert.Empty(db.Executed);
        Assert.Empty(db.BulkCopies);
    }

    [Fact]
    public void Runcreate_Postgres_ReloadsOptions()
    {
        var db = new RecordingDb();
        var p = Profile(SQLServerTypes.POSTGRES);
        Assert.True(compile_options_main.Run(_cmd, p, db, runcreate_main.ImportSettings(p)).Returncode);
        Assert.Empty(db.Queries);
        Assert.Contains("sbnwork..w_options", db.BulkCopies);
    }

    [Fact]
    public void Runcreate_NonPostgres_RunsLive()
    {
        var db = new RecordingDb();
        var p = Profile(SQLServerTypes.MSSQL);
        Assert.Equal(LiveMode.Live, runcreate_main.ImportSettings(p).Mode);
        db.OnExecute = _ => db.Rows = TargetRows(SQLServerTypes.MSSQL);
        Assert.True(compile_options_main.Run(_cmd, p, db, runcreate_main.ImportSettings(p)).Returncode);
        Assert.NotEmpty(db.Queries);
        Assert.Empty(db.BulkCopies);
    }

    [Fact]
    public void Live_Postgres_Refused_NoExecutorCalls()
    {
        var db = new RecordingDb();
        Assert.False(compile_options_main.Run(_cmd, Profile(SQLServerTypes.POSTGRES), db, new LiveSettings()).Returncode);
        Assert.Empty(db.Queries);
        Assert.Empty(db.Executed);
        Assert.Empty(db.BulkCopies);
    }
}
