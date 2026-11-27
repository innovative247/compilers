using ibsCompiler;

namespace LiveCompile.Tests;

public class ActionModelTests
{
    private static string AL(int act, int type, string text, int menu = 0, string ctrl = "") =>
        $":>{act:D4} {type:D3} {text}<<{menu}>>{ctrl}";

    private static string DL(int act, int typ, int ix, int no, int shix) =>
        $":>{act:D4} {typ:D3} {ix:D3} {no:D5} {shix:D3}";

    private static ActionModel.ActionDef Def(SQLServerTypes d, string[] lines, string[]? dtl = null)
    {
        var errors = new List<string>();
        var def = LiveActions.ParseAll(lines, dtl ?? Array.Empty<string>(), s => s, d, errors).Single();
        Assert.Empty(errors);
        return def;
    }

    [Theory]
    [InlineData("foo", "Hello (5)")]
    [InlineData("xx i_menu yy", "Hello")]
    [InlineData("#MENU:foo", "Hello")]
    [InlineData("a #MENU", "Hello (5)")]
    [InlineData("XX I_MENU", "Hello")]
    [InlineData("ixmenu", "Hello")]
    [InlineData("#menu 00041", "Hello")]
    public void GuiText_KSuffix(string ctrl, string expected)
    {
        var l = ActionModel.ParseActionLine(1, AL(5, 1, "Hello", 3, ctrl), SQLServerTypes.SYBASE, out var err)!;
        Assert.Equal("", err);
        Assert.Equal(expected, ActionModel.GuiText(l));
    }

    [Theory]
    [InlineData(SQLServerTypes.MSSQL, " Hello ")]
    [InlineData(SQLServerTypes.SYBASE, " Hello (5)")]
    public void MenuMessage_SuffixOnSybaseOnly(SQLServerTypes d, string expected)
    {
        var e = ActionModel.Expect(Def(d, new[] { AL(5, 1, "Hello", 3, "foo") }), 0, 1, d);
        Assert.Equal(expected, e.Menus.Single().Message);
    }

    [Fact]
    public void Dtl_LanguageThenCompanyOverride()
    {
        var def = Def(SQLServerTypes.MSSQL, new[] { AL(5, 1, "Hi") }, new[] { DL(5, 1, 2, 777, 1), DL(5, 2, 2, 888, 1), DL(5, 101, 2, 999, 1) });
        Assert.Equal(999, ActionModel.Expect(def, 101, 1, SQLServerTypes.MSSQL).Dtl.Single().MsgNo);
        Assert.Equal(888, ActionModel.Expect(def, 0, 2, SQLServerTypes.MSSQL).Dtl.Single().MsgNo);
        Assert.Equal(777, ActionModel.Expect(def, 0, 1, SQLServerTypes.MSSQL).Dtl.Single().MsgNo);
    }

    [Fact]
    public void Numbering_IfPrefixes_Comments_Interleaved()
    {
        string R(string s) => s.Replace("&if_on&", "").Replace("&if_off&", "-->");
        var lines = new[]
        {
            "{---------------}", "", AL(5, 1, "A", 3, "a"), "&if_off&" + AL(6, 1, "skipped", 4, "x"), "  " + AL(6, 1, "B", 4, "b"),
            "/* comment :> */", "&if_on&" + AL(5, 2, "A2"), ":", AL(7, 1, "C"), "&if_on&" + AL(6, 2, "B2"),
        };
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, action_files.HeaderLines(lines, R).Select(x => x.LineIx));
        var errors = new List<string>();
        var defs = LiveActions.ParseAll(lines, Array.Empty<string>(), R, SQLServerTypes.SYBASE, errors).ToDictionary(x => x.Act);
        Assert.Empty(errors);
        Assert.Equal(new[] { (1, 1), (3, 2) }, defs[5].Lines.Select(l => (l.LineIx, l.Type)));
        Assert.Equal(new[] { (2, 1), (5, 2) }, defs[6].Lines.Select(l => (l.LineIx, l.Type)));
        Assert.Equal(" B", ActionModel.Expect(defs[6], 0, 1, SQLServerTypes.SYBASE).Message);
    }

    [Fact]
    public void DetailFields_FixedColumns()
    {
        var f = action_files.DetailFields(new[] { "  " + DL(250, 1, 2, 17001, 2) + "              Change", "{---}" }, s => s).Single();
        Assert.Equal(new[] { "0250", "001", "002", "17001", "002", "              Change" }, f);
    }

    [Fact]
    public void MalformedLines_ThrowWithFileAndLine()
    {
        var dtl = Assert.Throws<InvalidDataException>(() =>
            action_files.DetailFields(new[] { "{---}", ":>0250 001" }, s => s, "css.actions_dtl").ToList());
        Assert.Contains("css.actions_dtl:2:", dtl.Message);
        var hdr = Assert.Throws<InvalidDataException>(() =>
            action_files.HeaderLines(new[] { ":>0001 001 ok", "&if_x&" }, s => s == "&if_x&" ? ":" : s, "css.actions").ToList());
        Assert.Contains("css.actions:2:", hdr.Message);
    }
}
