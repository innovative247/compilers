using System.Text.RegularExpressions;
using ibsCompiler;
using ibsCompiler.Database;

namespace LiveCompile.Tests;

/// <summary>Reads from an in-memory table; ExecuteSql records batches and runs <see cref="OnExecute"/>.</summary>
internal sealed class FakeDb : ISqlExecutor, ISqlRowReader
{
    public List<object?[]> Rows = new();
    public readonly List<string> Queries = new(), Executed = new();
    public Action<string>? OnExecute;
    public bool Fail;

    /// <summary>Per-query rows; when null every query reads <see cref="Rows"/>.</summary>
    public Func<string, List<object?[]>>? Query;

    public List<object?[]> QueryRows(string sql, string database)
    {
        Queries.Add(sql);
        return (Query?.Invoke(sql) ?? Rows).Select(r => (object?[])r.Clone()).ToList();
    }
    public ExecReturn ExecuteSql(string sqlText, string database, bool captureOutput = false, string outputFile = "")
    {
        Executed.Add(sqlText);
        OnExecute?.Invoke(sqlText);
        return new ExecReturn { Returncode = !Fail, Output = "" };
    }
    public void OpenConnection(string database) { }
    public ExecReturn ExecuteBatch(string batch, bool captureOutput = false, string outputFile = "") => throw new NotSupportedException();
    public void CloseConnection() { }
    public ExecReturn BulkCopy(string table, BcpDirection direction, string dataFile, string formatFile = "", string fieldTerminator = "\t", int batchSize = 0) => throw new NotSupportedException();
    public void Dispose() { }
}

public class EngineTests
{
    private static readonly LiveTable T = new()
    {
        Label = "t", Placeholder = "&t&",
        Columns = new LiveColumn[]
        {
            new("k1", LiveColType.VarChar), new("k2", LiveColType.Int),
            new("txt", LiveColType.NVarChar), new("flg", LiveColType.Char),
            new("opt", LiveColType.VarChar, LiveNullCompare.BlankOnSybase),
        },
        Key = new[] { 0, 1 },
    };

    private static string Resolve(string s) => s.Replace("&t&", "db..t").Replace("&options&", "sbnmaster..options");
    private static string?[] R(params string?[] v) => v;
    private static int Count(string s, string what) => Regex.Matches(s, Regex.Escape(what)).Count;

    [Fact]
    public void Diff_DeletesUpdatesInserts_ChangedColumnsOnly()
    {
        var target = new List<string?[]> { R("a", "1", "same", "+", "x"), R("b", "2", "new", "-", "y"), R("c", "3", "ins", "+", null) };
        var current = new List<string?[]> { R("a", "1", "same  ", "+", "x"), R("b", "02", "old", "-", "y"), R("z", "9", "gone", "+", "q") };
        var d = global::ibsCompiler.LiveCompile.Diff(T, target, current, SQLServerTypes.MSSQL);
        Assert.Equal("z/9", Assert.Single(d.Deletes).KeyText);
        var u = Assert.Single(d.Updates);
        Assert.Equal(new[] { 2 }, u.Changed);          // int key 02 == 2, trailing blanks ignored on "same  "
        Assert.Equal("c/3", Assert.Single(d.Inserts).KeyText);
    }

    [Fact]
    public void Diff_NullVersusBlank_ByColumnRuleAndDialect()
    {
        var target = new List<string?[]> { R("a", "1", null, "+", null) };
        var current = new List<string?[]> { R("a", "1", " ", "+", " ") };
        // txt is Strict: NULL vs blank differs everywhere; opt is BlankOnSybase.
        Assert.Equal(new[] { 2 }, Assert.Single(global::ibsCompiler.LiveCompile.Diff(T, target, current, SQLServerTypes.SYBASE).Updates).Changed);
        Assert.Equal(new[] { 2, 4 }, Assert.Single(global::ibsCompiler.LiveCompile.Diff(T, target, current, SQLServerTypes.MSSQL).Updates).Changed);
    }

    [Fact]
    public void Diff_KeepRule_NeverDeletesOrUpdatesKeptRow()
    {
        var target = new List<string?[]> { R("a", "1", "file", "+", "") };
        var current = new List<string?[]> { R("a", "1", "db", "+", ""), R("b", "2", "db", "+", "") };
        var d = global::ibsCompiler.LiveCompile.Diff(T, target, current, SQLServerTypes.MSSQL, (cur, tgt) => cur[3] == "+");
        Assert.True(d.IsEmpty);
        Assert.Equal(2, d.Kept);
    }

    [Fact]
    public void Diff_DuplicateDatabaseKey_DeleteThenReinsert()
    {
        var target = new List<string?[]> { R("a", "1", "t", "+", "") };
        var current = new List<string?[]> { R("a", "1", "t", "+", ""), R("a ", "1", "u", "+", "") };
        var d = global::ibsCompiler.LiveCompile.Diff(T, target, current, SQLServerTypes.MSSQL);
        Assert.Single(d.Deletes);
        Assert.Single(d.Inserts);
        Assert.Empty(d.Updates);
    }

    [Fact]
    public void Diff_DuplicateTargetKey_Throws() =>
        Assert.Throws<ArgumentException>(() => global::ibsCompiler.LiveCompile.Diff(T,
            new List<string?[]> { R("a", "1", "", "", ""), R("a", "1", "", "", "") }, new List<string?[]>(), SQLServerTypes.MSSQL));

    private static LiveDiff Mixed(int n)
    {
        var target = Enumerable.Range(0, n).Select(i => R("u" + i, "1", "new", "+", "")).Concat(
                     Enumerable.Range(0, n).Select(i => R("i" + i, "1", "ins", "+", ""))).ToList();
        var current = Enumerable.Range(0, n).Select(i => R("u" + i, "1", "old", "+", "")).Concat(
                      Enumerable.Range(0, n).Select(i => R("d" + i, "1", "del", "+", ""))).ToList();
        return global::ibsCompiler.LiveCompile.Diff(T, target, current, SQLServerTypes.SYBASE);
    }

    [Fact]
    public void Batches_SizeBoundaries_OrderAndOwnTransaction()
    {
        var batches = global::ibsCompiler.LiveCompile.BuildBatches(new[] { Mixed(5) }, SQLServerTypes.SYBASE, 4, Resolve);
        Assert.Equal(4, batches.Count);                                   // 15 statements / 4
        foreach (var b in batches)
        {
            Assert.Equal(1, Count(b, "begin tran"));
            Assert.Equal(1, Count(b, "commit tran"));
            var statements = b.Split(Environment.NewLine).Count(l => l.StartsWith("delete ") || l.StartsWith("update ") || l.StartsWith("insert "));
            Assert.Equal(Count(b, "rollback tran return"), statements);
            Assert.True(statements <= 4);
        }
        Assert.DoesNotContain("waitfor", string.Concat(batches));   // pause is client-side
        var all = string.Concat(batches);
        int lastDelete = all.LastIndexOf("delete db..t", StringComparison.Ordinal);
        int firstUpdate = all.IndexOf("update db..t", StringComparison.Ordinal);
        int lastUpdate = all.LastIndexOf("update db..t", StringComparison.Ordinal);
        int firstInsert = all.IndexOf("insert db..t", StringComparison.Ordinal);
        Assert.True(lastDelete < firstUpdate && lastUpdate < firstInsert);
        Assert.Contains("update db..t set txt = 'new' where k1 = 'u0' and k2 = 1", all);
        Assert.DoesNotContain("N'", all);
        Assert.DoesNotContain("truncate", all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Batches_EveryDeleteAndUpdateIsKeyedOnTheWholeKey()
    {
        var all = string.Concat(global::ibsCompiler.LiveCompile.BuildBatches(new[] { Mixed(30) }, SQLServerTypes.MSSQL, 1000, Resolve));
        var lines = all.Split(Environment.NewLine).Where(l => l.StartsWith("delete ") || l.StartsWith("update ")).ToList();
        Assert.Equal(60, lines.Count);
        Assert.All(lines, l => Assert.Matches(@" where k1 = '[^']*' and k2 = \d+$", l));
        Assert.Contains("set txt = N'new'", all);   // N'' on MSSQL only
    }

    [Fact]
    public void Batches_Postgres_BeginCommit_NoNLiteral()
    {
        var all = string.Concat(global::ibsCompiler.LiveCompile.BuildBatches(new[] { Mixed(1) }, SQLServerTypes.POSTGRES, 50, Resolve));
        Assert.Contains("BEGIN;", all);
        Assert.Contains("COMMIT;", all);
        Assert.Contains("UPDATE db..t SET txt = 'new' WHERE k1 = 'u0' AND k2 = 1;".Replace(" AND ", " and "), all);
        Assert.DoesNotContain("N'", all);
    }

    [Fact]
    public void BatchSize_OutOfRange_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => global::ibsCompiler.LiveCompile.BuildBatches(new[] { Mixed(1) }, SQLServerTypes.MSSQL, 1001, Resolve));

    [Fact]
    public void Charset_TransformOnlyStringLiterals()
    {
        var tbl = new LiveTable
        {
            Label = "t", Placeholder = "&t&",
            Columns = new LiveColumn[] { new("k1", LiveColType.VarChar), new("k2", LiveColType.Int), new("txt", LiveColType.NVarChar), new("flg", LiveColType.Char), new("opt", LiveColType.VarChar) },
            Key = new[] { 0, 1 },
        };
        var d = global::ibsCompiler.LiveCompile.Diff(tbl, new List<string?[]> { R("k", "7", "é", "+", null) }, new List<string?[]>(), SQLServerTypes.SYBASE);
        var all = string.Concat(global::ibsCompiler.LiveCompile.BuildBatches(new[] { d }, SQLServerTypes.SYBASE, 50, Resolve, s => "<" + s + ">"));
        Assert.Contains("insert db..t (k1, k2, txt, flg, opt) values ('<k>', 7, '<é>', '<+>', NULL)", all);
        Assert.DoesNotContain("<db", all);
    }

    [Fact]
    public void Run_DryRun_WritesPlanOnly()
    {
        var db = new FakeDb { Rows = { new object?[] { "z", 9, "x", "+", "" } } };
        var plan = Path.GetTempFileName();
        try
        {
            var s = new LiveSettings { DryRun = true, PlanFile = plan };
            var rc = global::ibsCompiler.LiveCompile.Run("t", new[] { new LiveTarget { Table = T, Rows = { R("a", "1", "t", "+", "") } } },
                s, db, SQLServerTypes.MSSQL, "master", Resolve, "", "t", () => throw new Exception("no change log on dry run"));
            Assert.Equal(0, rc);
            Assert.Empty(db.Executed);
            Assert.All(db.Queries, q => Assert.StartsWith("select ", q));
            var text = File.ReadAllText(plan);
            Assert.Contains("delete db..t where k1 = 'z' and k2 = 9", text);
            Assert.Contains("insert db..t", text);
        }
        finally { File.Delete(plan); }
    }

    [Fact]
    public void Run_ReadbackEmpty_Succeeds_NonEmpty_FailsWithKeys()
    {
        var target = new LiveTarget { Table = T, Rows = { R("a", "1", "t", "+", "") } };
        bool logged = false;
        var good = new FakeDb();
        good.OnExecute = _ => good.Rows = new() { new object?[] { "a", 1, "t", "+", "" } };
        Assert.Equal(0, global::ibsCompiler.LiveCompile.Run("t", new[] { target }, new LiveSettings(), good, SQLServerTypes.MSSQL, "master", Resolve, "", "t", () => logged = true));
        Assert.True(logged);
        Assert.Single(good.Executed);

        var bad = new FakeDb();
        Assert.Equal(1, global::ibsCompiler.LiveCompile.Run("t", new[] { target }, new LiveSettings(), bad, SQLServerTypes.MSSQL, "master", Resolve, "", "t"));
        var lines = global::ibsCompiler.LiveCompile.Describe(global::ibsCompiler.LiveCompile.DiffAll(new[] { target }, bad, "master", SQLServerTypes.MSSQL, Resolve));
        Assert.Equal("t a/1: missing from the database", Assert.Single(lines));
    }

    [Fact]
    public void Run_StopsAtFirstFailedBatch()
    {
        var target = new LiveTarget { Table = T, Rows = Enumerable.Range(0, 10).Select(i => R("a" + i, "1", "", "", "")).ToList() };
        var db = new FakeDb { Fail = true };
        Assert.Equal(1, global::ibsCompiler.LiveCompile.Run("t", new[] { target }, new LiveSettings { BatchSize = 2 }, db, SQLServerTypes.MSSQL, "master", Resolve, "", "t"));
        Assert.Single(db.Executed);
    }

    [Fact]
    public void Run_Postgres_RefusedBeforeAnyRead()
    {
        var db = new FakeDb();
        var target = new LiveTarget { Table = T, Rows = { R("a", "1", "t", "+", "") } };
        Assert.Equal(1, global::ibsCompiler.LiveCompile.Run("t", new[] { target }, new LiveSettings(), db, SQLServerTypes.POSTGRES, "", Resolve, "", "t",
            () => throw new Exception("no change log on Postgres")));
        Assert.Empty(db.Queries);
        Assert.Empty(db.Executed);
    }

    [Fact]
    public void Run_AlreadyCurrent_NoChangeLog()
    {
        var db = new FakeDb { Rows = { new object?[] { "a", 1, "t", "+", "" } } };
        var target = new LiveTarget { Table = T, Rows = { R("a", "1", "t", "+", "") } };
        Assert.Equal(0, global::ibsCompiler.LiveCompile.Run("t", new[] { target }, new LiveSettings(), db, SQLServerTypes.MSSQL, "master", Resolve, "", "t",
            () => throw new Exception("no change log when nothing changes")));
        Assert.Empty(db.Executed);
    }

    [Fact]
    public void Run_NonNumericInt_FailsCleanly()
    {
        var db = new FakeDb();
        var target = new LiveTarget { Table = T, Rows = { R("a", "x1", "t", "+", "") } };
        Assert.Equal(1, global::ibsCompiler.LiveCompile.Run("t", new[] { target }, new LiveSettings(), db, SQLServerTypes.MSSQL, "master", Resolve, "", "t",
            () => throw new Exception("no change log when the plan cannot be built")));
        Assert.Empty(db.Executed);
        Assert.Throws<FormatException>(() => global::ibsCompiler.LiveCompile.Literal("x1", LiveColType.Int, SQLServerTypes.MSSQL));
    }

    [Fact]
    public void Run_DryRun_UnwritablePlanFile_Returns1()
    {
        var s = new LiveSettings { DryRun = true, PlanFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "no", "p.sql") };
        var target = new LiveTarget { Table = T, Rows = { R("a", "1", "t", "+", "") } };
        Assert.Equal(1, global::ibsCompiler.LiveCompile.Run("t", new[] { target }, s, new FakeDb(), SQLServerTypes.MSSQL, "master", Resolve, "", "t"));
    }

    [Fact]
    public void Unholdable_ListsWrittenRowsOnly()
    {
        var target = new List<string?[]> { R("a", "1", "\u0101", "+", ""), R("b", "2", "\u00e9", "+", ""), R("c", "3", "same", "+", "") };
        var current = new List<string?[]> { R("c", "3", "same", "+", "") };
        var d = global::ibsCompiler.LiveCompile.Diff(T, target, current, SQLServerTypes.SYBASE);
        var bad = global::ibsCompiler.LiveCompile.Unholdable(new[] { d }, s => s.Replace("\u0101", "?"));
        Assert.Equal("t a/1", Assert.Single(bad));
    }

    [Fact]
    public void Describe_CapsAtFiftyPlusCount()
    {
        var d = global::ibsCompiler.LiveCompile.Diff(T, Enumerable.Range(0, 70).Select(i => R("a" + i, "1", "", "", "")).ToList(), new List<string?[]>(), SQLServerTypes.MSSQL);
        var lines = global::ibsCompiler.LiveCompile.Describe(new[] { d });
        Assert.Equal(51, lines.Count);
        Assert.Equal("... and 20 more", lines[^1]);
    }
}

public class ArgsTests
{
    private static LiveSettings Parse(params string[] a)
    {
        Assert.True(global::ibsCompiler.LiveCompile.ParseArgs(a.ToList(), out var s, out var e), e);
        return s;
    }

    [Fact]
    public void Default_IsTheNamedConstant_Live()
    {
        Assert.Equal(LiveMode.Live, global::ibsCompiler.LiveCompile.DefaultMode);
        Assert.Equal(global::ibsCompiler.LiveCompile.DefaultMode, Parse().Mode);
        Assert.Equal(50, Parse().BatchSize);
        Assert.Equal(0, Parse().PauseMs);
    }

    [Fact]
    public void Flags_SelectMode_AndValues()
    {
        Assert.Equal(LiveMode.Reload, Parse("--reload").Mode);
        Assert.Equal(LiveMode.Live, Parse("--live").Mode);
        var s = Parse("--batch-size", "200", "--pause-ms=500", "--dry-run", "--plan-file", "p.sql");
        Assert.Equal((200, 500, true, "p.sql"), (s.BatchSize, s.PauseMs, s.DryRun, s.PlanFile));
    }

    [Theory]
    [InlineData("--live", "--reload")]
    [InlineData("--batch-size", "0")]
    [InlineData("--batch-size", "1001")]
    [InlineData("--pause-ms", "-1")]
    [InlineData("--reload", "--dry-run")]
    [InlineData("--pause-ms", "60001")]
    [InlineData("--plan-file", "p.sql")]
    [InlineData("--reload", "--dry-run", "--plan-file", "p.sql")]
    [InlineData("--reload", "--plan-file", "p.sql")]
    public void Flags_Rejected(params string[] a) =>
        Assert.False(global::ibsCompiler.LiveCompile.ParseArgs(a.ToList(), out _, out _));

    [Fact]
    public void SetOptions_StripsLiveBoolFlags() =>
        Assert.All(global::ibsCompiler.LiveCompile.BoolFlagNames, f => Assert.Contains(f, InteractiveMenus.SetOptionsBoolFlagNames));
}

public class OptionsTargetTests
{
    // Merged ':>' line as GenerateImportOptionFile writes it: id(8) act if val dyn <<value>> desc.
    private static string Line(string id, string dyn, string value, string act = "-") =>
        $":>{id.PadRight(8)} {act} - + {dyn} <<{value}>> desc {id}";

    private static string?[] Db(string id, string dyn, string? value) => new[] { id, "-", "-", "+", value, dyn, "desc " + id };

    private static LiveDiff DiffOptions(IEnumerable<string> lines, IEnumerable<string?[]> current, SQLServerTypes d = SQLServerTypes.MSSQL)
    {
        var rows = LiveOptions.TargetRows(lines, d, new List<string>());
        return global::ibsCompiler.LiveCompile.Diff(LiveOptions.Table, rows, current.ToList(), d, LiveOptions.Keep);
    }

    [Fact]
    public void StaticDbRowNotInFile_Deleted() =>
        Assert.Equal("old", Assert.Single(DiffOptions(new[] { Line("keep", "-", "1") }, new[] { Db("keep", "-", "1"), Db("old", "-", "x") }).Deletes).KeyText);

    [Fact]
    public void StaticFileRow_Written()
    {
        var d = DiffOptions(new[] { Line("s1", "-", "new"), Line("s2", "-", "v") }, new[] { Db("s1", "-", "old") });
        Assert.Equal(new[] { 4 }, Assert.Single(d.Updates).Changed);
        Assert.Equal("s2", Assert.Single(d.Inserts).KeyText);
    }

    [Fact]
    public void DynamicFileRow_InsertedOnlyIfMissing()
    {
        var d = DiffOptions(new[] { Line("dyn", "+", "file") }, Array.Empty<string?[]>());
        Assert.Equal("file", Assert.Single(d.Inserts).Target![4]);
    }

    [Fact]
    public void ExistingDynamicRow_KeepsDbValue()
    {
        var d = DiffOptions(new[] { Line("dyn", "+", "file") }, new[] { Db("dyn", "+", "site") });
        Assert.True(d.IsEmpty);
        Assert.Equal(1, d.Kept);
    }

    [Fact]
    public void DynamicDbRowTurnedStatic_Replaced()
    {
        var u = Assert.Single(DiffOptions(new[] { Line("x", "-", "file") }, new[] { Db("x", "+", "site") }).Updates);
        Assert.Equal(new[] { 4, 5 }, u.Changed);
    }

    [Fact]
    public void StaticDbRowTurnedDynamic_Replaced() =>
        Assert.Equal(new[] { 4, 5 }, Assert.Single(DiffOptions(new[] { Line("x", "+", "file") }, new[] { Db("x", "-", "old") }).Updates).Changed);

    [Fact]
    public void DynamicDbRowNotInFile_Deleted_AsTheProcsLastDelete() =>
        Assert.Equal("gone", Assert.Single(DiffOptions(new[] { Line("a", "-", "1") }, new[] { Db("a", "-", "1"), Db("gone", "+", "v") }).Deletes).KeyText);

    [Fact]
    public void EmptyValue_IsNullOnSybase_BlankElsewhere()
    {
        Assert.Null(LiveOptions.TargetRows(new[] { Line("e", "-", "") }, SQLServerTypes.SYBASE, new())[0][4]);
        Assert.Equal("", LiveOptions.TargetRows(new[] { Line("e", "-", "") }, SQLServerTypes.MSSQL, new())[0][4]);
    }

    [Fact]
    public void DuplicateId_LastWins_Warned()
    {
        var w = new List<string>();
        var rows = LiveOptions.TargetRows(new[] { Line("dup", "-", "1"), Line("dup", "-", "2") }, SQLServerTypes.MSSQL, w);
        Assert.Equal("2", Assert.Single(rows)[4]);
        Assert.Single(w);
    }

    [Fact]
    public void AlreadyCurrent_EmptyDiff()
    {
        var lines = new[] { Line("a", "-", "1"), Line("b", "+", "2") };
        var current = LiveOptions.TargetRows(lines, SQLServerTypes.SYBASE, new()).Select(r => r.Select(v => v == null ? null : v + "  ").ToArray());
        Assert.True(DiffOptions(lines, current, SQLServerTypes.SYBASE).IsEmpty);
    }
}

/// <summary>Targets checked against SBN_IR ibs/ss/pro_compile_{messages,ibs,jam,sqr,sql,gui}_messages.sql and ba_compile_gui_messages_save.</summary>
public class MessagesTargetTests
{
    private static string?[] M(int no, int lang, int cmpy, string? msg, string? upd = "") =>
        new[] { no.ToString(), lang.ToString(), cmpy.ToString(), "G", upd, "0", msg };

    private static LiveMessages.MsgType Type(string ext) => LiveMessages.Types.Single(t => t.Ext == ext);

    private static readonly string?[][] Src =
    {
        M(1, 1, 0, "base1"), M(2, 1, 0, ">>quoted"), M(3, 1, 0, "three"), M(3, 2, 0, "drei"), M(3, 1, 5, "co3"),
        M(4, 1, 0, " "), M(5, 1, 0, null), M(6, 2, 0, "no base row"), M(7, 1, 0, "seven"), M(7, 0, 5, "co7"),
        M(8, 3, 0, "other lang"), M(9, 1, 0, "nine"), M(9, 1, 5, ">>co9"), M(5000, 1, 0, "out of ibs range"),
    };

    private static string Rows(List<string?[]> rows) => string.Join("|", rows.Select(r => $"{r[0]}={r[2] ?? "NULL"}"));

    [Fact]
    public void Ibs_KeepsNullDropsQuotedAndEmpty_LanguageThenCompany() =>
        Assert.Equal("15001=base1|15003=co3|15005=NULL|15007=co7", Rows(LiveMessages.MessagesRows(Type("ibs_msg"), Src, 2, 5)));

    [Fact]
    public void Jam_BaseSkipsQuotedAndNull_OverrideMayBeQuoted() =>
        Assert.Equal("1=base1|3=co3|7=co7|9=>>co9|5000=out of ibs range", Rows(LiveMessages.MessagesRows(Type("jam_msg"), Src, 2, 5)));

    [Fact]
    public void Overrides_OnlyAboveOne()
    {
        Assert.Equal("15001=base1|15003=three|15005=NULL|15007=seven|15009=nine", Rows(LiveMessages.MessagesRows(Type("ibs_msg"), Src, 1, 1)));
        Assert.Equal("20001=base1|20003=drei|20007=seven|20009=nine|25000=out of ibs range", Rows(LiveMessages.MessagesRows(Type("sqr_msg"), Src, 2, 0)));
    }

    [Fact]
    public void EmptyOverride_DeletesTheMessage() =>
        Assert.DoesNotContain("31001", Rows(LiveMessages.MessagesRows(Type("sql_msg"), new[] { M(1, 1, 0, "x"), M(1, 1, 5, "") }, 1, 5)));

    [Fact]
    public void SourceRows_ClearUpdFlg() =>
        Assert.Equal(new[] { "", null, "", " " },
            LiveMessages.SourceRows(new[] { M(1, 1, 0, "a", "X"), M(2, 1, 0, "a", null), M(3, 1, 0, "a", ""), M(4, 1, 0, "a", " ") })
                .Select(r => r[LiveMessages.UpdFlg]).ToArray());

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Gui_NonTranslatingCompany_FileAsIs(int cmpy)
    {
        var file = new List<string?[]> { M(1, 1, 0, "base"), M(1, 1, cmpy == 0 ? 7 : 101, "file own") };
        var t = LiveMessages.GuiTarget(file, cmpy, new[] { M(1, 1, cmpy, "saved") }, dbHasOwn: true);
        Assert.Equal(file, t.Rows);
        Assert.Null(t.Keep);
    }

    [Fact]
    public void Gui_SavedKeep_ReplacesFileOwnRowsWithSavedOwnRows()
    {
        var t = LiveMessages.GuiTarget(new[] { M(1, 1, 0, "base"), M(1, 1, 5, "file") }, 5,
                                       new[] { M(1, 1, 5, "saved"), M(2, 1, 7, "other company") }, dbHasOwn: false);
        Assert.Equal("1=base|1=saved", Rows(t.Rows.Select(r => new[] { r[0], null, r[6] }).ToList()));
        Assert.Null(t.Keep);
    }

    [Fact]
    public void Gui_DbHasOwn_DropsFileOwnRows_KeepsDbOwnRows()
    {
        var t = LiveMessages.GuiTarget(new[] { M(1, 1, 0, "base"), M(1, 1, 5, "file") }, 5, null, dbHasOwn: true);
        Assert.Equal("base", Assert.Single(t.Rows)[6]);
        Assert.True(t.Keep!(M(1, 1, 5, "db"), null));
        Assert.False(t.Keep!(M(1, 1, 0, "db"), null));
    }

    [Fact]
    public void Gui_NoneSaved_FileOwnRowsStay()
    {
        var t = LiveMessages.GuiTarget(new[] { M(1, 1, 0, "base"), M(1, 1, 5, "file") }, 5, null, dbHasOwn: false);
        Assert.Equal(2, t.Rows.Count);
        Assert.Null(t.Keep);
    }

    private static Dictionary<string, List<string?[]>> Files(List<string?[]>? gui = null, List<string?[]>? ibs = null)
    {
        var f = new[] { "ibs_msg", "ibs_msgrp", "jam_msg", "jam_msgrp", "sqr_msg", "sqr_msgrp", "sql_msg", "sql_msgrp", "gui_msg", "gui_msgrp" }
            .ToDictionary(k => k, _ => new List<string?[]>());
        if (gui != null) f["gui_msg"] = gui;
        if (ibs != null) f["ibs_msg"] = ibs;
        return f;
    }

    private static string Resolve(string s) => Regex.Replace(s, "&(\\w+)&", "db..$1");

    private static FakeDb Db(Dictionary<string, List<string?[]>> tables) => new()
    {
        Query = sql =>
        {
            var name = Regex.Match(sql, @"from db\.\.(\w+)").Groups[1].Value;
            return tables.TryGetValue(name, out var rows)
                ? FakeWhere.Filter(sql, rows).Select(r => r.Select(v => v == null ? null : (object?)v).ToArray()).ToList() : new();
        },
    };

    [Fact]
    public void MessagesWhere_ReadsOnlyTypeRanges_Not30000Or10000To12999()
    {
        string?[] Row(int no) => new[] { no.ToString(), "G", "x" };
        var inside = new[] { 1, 9999, 15001, 19999, 20001, 29999, 30001, 31999 };
        var outside = new[] { 0, 10000, 12999, 15000, 20000, 30000, 32000 };
        var db = new Dictionary<string, List<string?[]>> { ["messages"] = inside.Concat(outside).Select(Row).ToList() };
        var d = DiffAll(LiveMessages.Targets(Files(), 1, 0, null, false), Db(db)).Single(x => x.Table == LiveMessages.Messages);
        Assert.Equal(inside.Select(n => n.ToString()), d.Deletes.Select(c => c.KeyText).OrderBy(int.Parse));
    }

    private static List<LiveDiff> DiffAll(List<LiveTarget> targets, FakeDb db) =>
        global::ibsCompiler.LiveCompile.DiffAll(targets, db, "master", SQLServerTypes.MSSQL, Resolve);

    [Fact]
    public void SaveTable_EmptiedByKeyedDeletes()
    {
        Assert.DoesNotContain(LiveMessages.Targets(Files(), 1, 0, null, false), x => x.Table == LiveMessages.GuiMessagesSave);
        var targets = new List<LiveTarget> { LiveMessages.SaveTarget() };
        var save = DiffAll(targets, Db(new() { ["gui_messages_save"] = new() { M(1, 1, 5, "a"), M(2, 2, 5, "b") } }))
            .Single(x => x.Table == LiveMessages.GuiMessagesSave);
        Assert.Equal(new[] { "1/1/5", "2/2/5" }, save.Deletes.Select(c => c.KeyText.Replace(" ", "")).ToArray());
    }

    [Fact]
    public void ProcsFor_OnlyChangedTypes()
    {
        var db = new Dictionary<string, List<string?[]>>
        {
            ["ibs_messages"] = new() { M(1, 1, 0, "a") },
            ["messages"] = new() { new string?[] { "15001", "G", "a" } },
        };
        var same = LiveMessages.Targets(Files(ibs: new() { M(1, 1, 0, "a") }), 1, 0, null, false);
        Assert.Empty(LiveMessages.ProcsFor(DiffAll(same, Db(db))));

        var ibsChanged = LiveMessages.Targets(Files(ibs: new() { M(1, 1, 0, "b") }), 1, 0, null, false);
        Assert.Equal(new[] { "i_compile_ibs_messages" }, LiveMessages.ProcsFor(DiffAll(ibsChanged, Db(db))));

        // A stray &messages& row in the sqr range counts as a sqr change.
        db["messages"].Add(new string?[] { "20005", "G", "stale" });
        Assert.Equal(new[] { "i_compile_sqr_messages", "i_compile_jrw_messages" }, LiveMessages.ProcsFor(DiffAll(same, Db(db))));
    }

    private static Dictionary<string, List<string?[]>> SiteDb() => new()
    {
        ["gui_messages"] = new() { M(1, 1, 0, "base"), M(1, 1, 5, "db own") },
        ["gui_messages_save"] = new() { M(1, 1, 5, "saved") },
    };

    private static readonly List<string?[]> GuiFile = new() { M(1, 1, 0, "base"), M(1, 1, 5, "file") };

    [Fact]
    public void OnSaved_Keep_RestoresSavedRows()
    {
        var site = SiteDb();
        var diffs = DiffAll(compile_msg_main.LiveTargets(Files(GuiFile), 1, 5, site["gui_messages_save"], 1, OnSavedTranslations.Keep)!, Db(site));
        var gui = diffs.Single(x => x.Table == LiveMessages.GuiMessages);
        Assert.Equal("saved", Assert.Single(gui.Updates).Target![LiveMessages.Message]);
    }

    [Fact]
    public void OnSaved_Discard_KeepsDatabaseOwnRows()
    {
        var site = SiteDb();
        var diffs = DiffAll(compile_msg_main.LiveTargets(Files(GuiFile), 1, 5, site["gui_messages_save"], 1, OnSavedTranslations.Discard)!, Db(site));
        var gui = diffs.Single(x => x.Table == LiveMessages.GuiMessages);
        Assert.True(gui.IsEmpty);
        Assert.Equal(1, gui.Kept);
    }

    [Fact]
    public void OnSaved_Cancel_NoTargets() =>
        Assert.Null(compile_msg_main.LiveTargets(Files(GuiFile), 1, 5, SiteDb()["gui_messages_save"], 1, OnSavedTranslations.Cancel));

    [Fact]
    public void SetMessages_StripsLiveFlags()
    {
        foreach (var f in global::ibsCompiler.LiveCompile.BoolFlagNames)
            Assert.Contains(f, InteractiveMenus.SetMessagesBoolFlagNames);
        Assert.Single(InteractiveMenus.SetMessagesBoolFlagNames, f => f == "--dry-run");
    }
}
