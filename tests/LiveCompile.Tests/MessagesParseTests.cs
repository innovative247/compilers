using ibsCompiler;

namespace LiveCompile.Tests;

public class MessagesParseTests
{
    private const SQLServerTypes Syb = SQLServerTypes.SYBASE, Ms = SQLServerTypes.MSSQL;

    private static LiveMessages.MsgType Ibs => LiveMessages.Types.Single(t => t.Ext == "ibs_msg");

    private static string Rows(SQLServerTypes d, int language, int company, params string[] lines)
    {
        var errs = new List<string>();
        var src = LiveMessages.SourceRows(LiveMessages.ParseFile("t", lines, false, d, errs));
        Assert.Empty(errs);
        return string.Join("|", LiveMessages.MessagesRows(Ibs, src, language, company).Select(r => $"{r[0]}={r[2] ?? "NULL"}"));
    }

    // Shapes as in css.ibs_msg: msgno, lang, cmpy, group, flag (" "), chg_tm, message.
    private const string Real = "2500\t1\t0\tACTKEY\t \t1119185664\t>>NUM     MNE       Long Text";

    [Fact]
    public void Real_Line_ParsesAllSevenColumns()
    {
        var r = LiveMessages.ParseMsgLine(Real, Ms, out var err)!;
        Assert.Equal("", err);
        Assert.Equal(new[] { "2500", "1", "0", "ACTKEY", " ", "1119185664", ">>NUM     MNE       Long Text" }, r);
    }

    [Fact]
    public void Blank_Message_IsKeptAsBlank_NotNull_AndDropsFromMessages()
    {
        foreach (var d in new[] { Syb, Ms })
        {
            Assert.Equal(" ", LiveMessages.ParseMsgLine("11\t1\t0\tG\t \t5\t ", d, out _)![LiveMessages.Message]);
            Assert.Equal("", Rows(d, 1, 0, "11\t1\t0\tG\t \t5\t "));
        }
    }

    [Fact]
    public void Empty_Message_And_ChgTm_PerDialect()
    {
        var line = "12\t1\t0\tG\t\t\t";
        var syb = LiveMessages.ParseMsgLine(line, Syb, out _)!;
        Assert.Equal(new string?[] { " ", null, " " }, new[] { syb[LiveMessages.UpdFlg], syb[LiveMessages.ChgTm], syb[LiveMessages.Message] });
        var ms = LiveMessages.ParseMsgLine(line, Ms, out _)!;
        Assert.Equal(new string?[] { "", "0", "" }, new[] { ms[LiveMessages.UpdFlg], ms[LiveMessages.ChgTm], ms[LiveMessages.Message] });
        // The proc's "delete where message = ''" drops MSSQL's ''; ASE stores ' ' too, so both drop out.
        Assert.Equal("", Rows(Syb, 1, 0, line));
        Assert.Equal("", Rows(Ms, 1, 0, line));
    }

    [Fact]
    public void BlankBaseRow_AndBlankOverrides_ProduceNoMessagesRow()
    {
        foreach (var d in new[] { Syb, Ms })
        {
            Assert.Equal("", Rows(d, 1, 0, "13\t1\t0\tG\t \t0\t "));
            Assert.Equal("", Rows(d, 2, 0, "13\t1\t0\tG\t \t0\tbase", "13\t2\t0\tG\t \t0\t "));
            Assert.Equal("", Rows(d, 1, 5, "13\t1\t0\tG\t \t0\tbase", "13\t1\t5\tG\t \t0\t "));
        }
    }

    [Fact]
    public void ParseMsgLine_Errors_BlankLine_TabInMessage_Trim()
    {
        Assert.Null(LiveMessages.ParseMsgLine("  ", Ms, out var err));
        Assert.Equal("", err);
        Assert.Null(LiveMessages.ParseMsgLine("1\t1\t0\tG\t \t0", Ms, out err));
        Assert.Contains("found 6", err);
        Assert.Null(LiveMessages.ParseMsgLine("x\t1\t0\tG\t \t0\tm", Ms, out err));
        Assert.Contains("column 1 'x'", err);
        Assert.Null(LiveMessages.ParseMsgLine("1\t1\t0\tG\t \tabc\tm", Ms, out err));
        Assert.Contains("column 6", err);
        Assert.Null(LiveMessages.ParseMsgLine("1\t\t0\tG\t \t0\tm", Ms, out err));
        Assert.Contains("column 2 is empty", err);
        var r = LiveMessages.ParseMsgLine(" 1 \t1\t0\tG\t \t0\ta\tb", Ms, out err)!;
        Assert.Equal("1", r[0]);
        Assert.Equal("a\tb", r[LiveMessages.Message]);
    }

    [Fact]
    public void ParseGroupLine_Rows_Errors_Dialect()
    {
        Assert.Equal(new string?[] { "G", "100", "Grp" }, LiveMessages.ParseGroupLine("G\t100\tGrp", Ms, out _));
        Assert.Equal(new string?[] { "G", null, " " }, LiveMessages.ParseGroupLine("G\t\t", Syb, out _));
        Assert.Equal(new string?[] { "G", "0", "" }, LiveMessages.ParseGroupLine("G\t\t", Ms, out _));
        Assert.Null(LiveMessages.ParseGroupLine("", Ms, out var err));
        Assert.Equal("", err);
        Assert.Null(LiveMessages.ParseGroupLine("G\t1", Ms, out err));
        Assert.Contains("found 2", err);
        Assert.Null(LiveMessages.ParseGroupLine(" \t1\td", Ms, out err));
        Assert.Contains("grp is empty", err);
        Assert.Null(LiveMessages.ParseGroupLine("G\tq\td", Ms, out err));
        Assert.Contains("s#minmsg 'q'", err);
    }

    [Fact]
    public void ParseFile_SkipsBlankLines_ReportsNameAndLine()
    {
        var errs = new List<string>();
        var rows = LiveMessages.ParseFile("css.ibs_msg", new[] { "1\t1\t0\tG\t \t0\ta", "", "bad", "2\t1\t0\tG\t \t0\tb" }, false, Ms, errs);
        Assert.Equal(2, rows.Count);
        Assert.Equal("css.ibs_msg:3: expected 7 tab-separated columns, found 1", Assert.Single(errs));
        var g = new List<string>();
        Assert.Single(LiveMessages.ParseFile("g", new[] { "G\t1\td", " " }, true, Ms, g));
        Assert.Empty(g);
    }

    [Fact]
    public void SqrCapWarnings_OnlyRow10000()
    {
        var rows = new List<string?[]> { new[] { "9999", "1", "0" }, new[] { "10000", "2", "5" } };
        var w = Assert.Single(LiveMessages.SqrCapWarnings(rows));
        Assert.StartsWith("sqr_msg 10000 (lang 2, cmpy 5)", w);
        Assert.Contains("duplicate key", w);
    }

    [Fact]
    public void EmptyMessage_InsertStatement_NeverNull_OnSybase()
    {
        foreach (var (d, want) in new[] { (Syb, "' '"), (Ms, "''") })
        {
            var row = LiveMessages.ParseMsgLine("12\t1\t0\tG\t\t5\t", d, out _)!;
            var sql = global::ibsCompiler.LiveCompile.Statement(new LiveChange { Kind = LiveChangeKind.Insert, Table = LiveMessages.GuiMessages, Target = row }, d, t => t);
            Assert.DoesNotContain("NULL", sql);
            Assert.EndsWith(want + ")", sql);
        }
    }
}
