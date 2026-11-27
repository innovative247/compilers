using ibsCompiler;

namespace LiveCompile.Tests;

public class ActionsTargetTests
{
    private static string AL(int act, int type, string text, int menu = 0, string ctrl = "") =>
        $":>{act:D4} {type:D3} {text}<<{menu}>>{ctrl}";

    private static string DL(int act, int typ, int ix, int no, int shix) =>
        $":>{act:D4} {typ:D3} {ix:D3} {no:D5} {shix:D3}";

    private static string Id(string s) => s;
    private static string?[] R(params string?[] v) => v;

    private static List<LiveTarget> Build(string[] act, string[]? dtl = null, int company = 0, int language = 1,
        (int, int, int)[]? current = null, int[]? profiles = null, int[]? allAct = null, List<string>? warnings = null)
    {
        var errors = new List<string>();
        var t = LiveActions.BuildTargets(act, dtl ?? Array.Empty<string>(), Id, SQLServerTypes.SYBASE, company, language,
            current ?? Array.Empty<(int, int, int)>(), profiles ?? Array.Empty<int>(), allAct ?? Array.Empty<int>(), 123,
            errors, warnings ?? new List<string>());
        Assert.Empty(errors);
        return t;
    }

    private static string Rows(LiveTarget t) => string.Join("|", t.Rows.Select(r => string.Join(",", r)));

    private static LiveDiff Diff(LiveTarget t, params string?[][] current) =>
        global::ibsCompiler.LiveCompile.Diff(t.Table, t.Rows, current, SQLServerTypes.SYBASE, t.Keep);

    [Fact]
    public void Golden_2147_AsOnGonzo()
    {
        var filler = Enumerable.Range(0, 388).Select(_ => AL(1, 1, "filler", 0, "x"));
        var lines = filler.Concat(new[] { "", "{-------------}",
            ":>2147 001 {  X} Bay Purchase Order                          <<56>>#REPT 10447",
            ":>2147 101 {  X} Bay Purchase Order                          <<56>>#REPT 10447" }).ToArray();
        var warnings = new List<string>();
        var t = Build(lines, company: 101, profiles: new[] { 1, 2 }, allAct: new[] { 1 }, warnings: warnings);
        Assert.Equal(new[] { "gui_messages", "messages", "actions", "menus", "action_profile_details" }, t.Select(x => x.Table.Label));
        Assert.Equal("10001,1,0,MENU,,123,filler (1)|12147,1,0,MENU,,123,Bay Purchase Order (2147)|12147,0,101,MENU,,123,Bay Purchase Order (2147)", Rows(t[0]));
        Assert.Equal("10001, , filler|12147, , Bay Purchase Order", Rows(t[1]));
        Assert.Equal("1,0,10001,1|2147,0,12147,389", Rows(t[2]));
        Assert.Equal("389,56,2147,#REPT 10447, Bay Purchase Order (2147),  X", Rows(t[3]));
        Assert.Equal("1,1,0|1,2147,0|1,0,0|2,0,0", Rows(t[4]));
        Assert.Equal("s#aprof in (1) or (s#act = 0 and ix = 0)", t[4].Where);
        Assert.Equal("s#msgno between 10000 and 12999", t[0].Where);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Shix_ExistingRowUsesTypeOneMin_NewRowUsesAnyLoadedType()
    {
        var lines = new[] { AL(5, 101, "Co"), AL(5, 1, "Hi") };
        Assert.Equal("5,0,10005,2", Rows(Build(lines, company: 101, current: new[] { (5, 0, 10005) })[2]));
        Assert.Equal("5,0,10005,1", Rows(Build(lines, company: 101)[2]));
    }

    [Fact]
    public void Detail_Ix0_OverridesBaseRow_AndDetailOnlyActionsKeepTheirRows()
    {
        var t = Build(new[] { AL(5, 1, "Hi") }, new[] { DL(5, 1, 0, 10099, 7), DL(5, 1, 2, 777, 3), DL(9, 1, 1, 555, 4) });
        Assert.Equal("5,0,10099,7|5,2,777,3|9,1,555,4", Rows(t[2]));
    }

    [Fact]
    public void ExistingIx0Row_KeepsItsMsgNo_UnlessDetailIx0Overrides()
    {
        var cur = new[] { (5, 0, 10777) };
        Assert.Equal("5,0,10777,1", Rows(Build(new[] { AL(5, 1, "Hi") }, current: cur)[2]));
        Assert.Equal("5,0,10099,7", Rows(Build(new[] { AL(5, 1, "Hi") }, new[] { DL(5, 1, 0, 10099, 7) }, current: cur)[2]));
    }

    [Fact]
    public void Actions_KeepRule_MatchesProcDeletes()
    {
        // Proc :366 deletes s#act > 0 not loaded; :416 deletes every ix > 0 row without a dtl line.
        var t = Build(new[] { AL(5, 1, "Hi") }, current: new[] { (5, 0, 10005) })[2];
        var d = Diff(t, R("5", "0", "10005", "1"), R("0", "0", "10000", "1"), R("-1", "0", "9999", "1"), R("0", "-2", "1", "1"),
                     R("5", "-1", "1", "1"), R("8", "-1", "1", "1"), R("5", "3", "1", "1"), R("0", "2", "1", "1"));
        Assert.Equal(new[] { "8/-1", "5/3", "0/2" }.OrderBy(x => x), d.Deletes.Select(c => c.KeyText).OrderBy(x => x));
        Assert.Equal(4, d.Kept);
    }

    [Fact]
    public void Gui_CompanyRowsKeptWhenPresent_InsertedWhenMissing()
    {
        var g = Build(new[] { AL(5, 1, "Hi"), AL(5, 101, "Own") }, company: 101)[0];
        var kept = Diff(g, R("10005", "1", "0", "MENU", "", "123", "Hi (5)"), R("10005", "0", "101", "MENU", "", "9", "edited"),
                        R("10006", "1", "0", "MENU", "", "9", "stale"));
        Assert.Equal((1, 0, 0), (kept.Kept, kept.Updates.Count, kept.Inserts.Count));
        Assert.Equal("10006/1/0", Assert.Single(kept.Deletes).KeyText);
        var missing = Diff(g, R("10005", "1", "0", "MENU", "", "123", "Hi (5)"));
        Assert.Equal("10005/0/101", Assert.Single(missing.Inserts).KeyText);
    }

    [Fact]
    public void Reset_MessagelessActionDropsOtherProfiles_StaleRowLeftToTrigger()
    {
        var t = Build(new[] { AL(5, 1, "Hi") }, new[] { DL(7, 1, 1, 10500, 1) },
            current: new[] { (5, 0, 10005), (7, 1, 10500), (8, 0, 10008) }, profiles: new[] { 1, 2 }, allAct: new[] { 1 });
        var acts = Diff(t[2], R("5", "0", "10005", "1"), R("7", "1", "10500", "1"), R("8", "0", "10008", "9"));
        Assert.Equal("8/0", Assert.Single(acts.Deletes).KeyText);
        Assert.Empty(acts.Inserts);

        Assert.Equal("s#aprof in (1) or (s#act = 0 and ix = 0) or s#act in (7)", t[4].Where);
        // Rows the Where reads: (2, 8, 0) is outside it; the actions delete fires the trigger for it.
        var prof = Diff(t[4], R("1", "5", "0"), R("1", "7", "1"), R("2", "7", "1"), R("1", "0", "0"), R("2", "0", "0"));
        Assert.Equal("2/7/1", Assert.Single(prof.Deletes).KeyText);
        Assert.Empty(prof.Inserts);
    }

    [Fact]
    public void ChgTm_NeverCausesUpdate_ButIsWrittenWithOne()
    {
        var g = Build(new[] { AL(5, 1, "Hi") })[0];
        Assert.True(Diff(g, R("10005", "1", "0", "MENU", "", "1", "Hi (5)")).IsEmpty);
        var u = Assert.Single(Diff(g, R("10005", "1", "0", "MENU", "", "1", "old")).Updates);
        Assert.Equal(new[] { "chg_tm", "message" }, u.Changed.Select(i => g.Table.Columns[i].Name));
    }

    [Fact]
    public void Warnings_DuplicateTexts_OverlappingMenus_OutOfRange()
    {
        var w = new List<string>();
        Build(new[] { AL(5, 1, "A", 10, "c"), AL(6, 1, "B", 11, "c"), AL(5, 1, "Other", 10, "c"), AL(3000, 1, "Big") }, warnings: w);
        Assert.Contains(w, x => x.StartsWith("action 5 type 1 has different texts"));
        Assert.Contains(w, x => x.StartsWith("menu 10 (lineix 1-3) is intermingled with menu 11"));
        Assert.Contains(w, x => x.StartsWith("action 3000 is outside 0..2999"));
    }

    [Fact]
    public void NonNumericFields_AreErrors()
    {
        var errors = new List<string>();
        LiveActions.BuildTargets(new[] { ":>00x1 001 Hi<<0>>" }, new[] { ":>0005 001 0x1 00777 001" }, Id, SQLServerTypes.SYBASE,
            0, 1, Array.Empty<(int, int, int)>(), Array.Empty<int>(), Array.Empty<int>(), 0, errors, new List<string>());
        Assert.Equal(2, errors.Count);
    }
}

public class SetActionsArgsTests
{
    [Fact]
    public void SetActions_StripsLiveAndEditFlags()
    {
        foreach (var f in global::ibsCompiler.LiveCompile.BoolFlagNames.Concat(InteractiveMenus.EditCompileBoolFlagNames))
            Assert.Contains(f, InteractiveMenus.SetActionsBoolFlagNames);
    }
}
