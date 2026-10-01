using System.Data;
using System.Text;
using SqlTest;

namespace SqlTest.Tests;

public class CompareOutputTests
{
    [Theory]
    [InlineData(null, "NULL")]
    [InlineData("NULL", "'NULL'")]
    [InlineData("a\nb", @"'a\nb'")]
    [InlineData("a\tb\r", @"'a\tb\r'")]
    [InlineData("it's", "'it''s'")]
    [InlineData(@"c:\x", @"'c:\\x'")]
    [InlineData("ab  ", "'ab  '")]
    [InlineData(true, "1")]
    [InlineData(false, "0")]
    [InlineData(42, "42")]
    public void RenderValue_scalars(object? v, string expected) =>
        Assert.Equal(expected, CompareOutput.RenderValue(v));

    [Fact]
    public void RenderValue_dbnull() => Assert.Equal("NULL", CompareOutput.RenderValue(DBNull.Value));

    [Fact]
    public void RenderValue_datetime_keeps_milliseconds() =>
        Assert.Equal("2026-09-30 10:11:12.345000", CompareOutput.RenderValue(new DateTime(2026, 9, 30, 10, 11, 12, 345)));

    // AseClient delivers convert(money, 1.5) as 1.5, not 1.5000.
    [Fact]
    public void RenderValue_money_as_the_driver_delivers_it() => Assert.Equal("1.5", CompareOutput.RenderValue(1.5m));

    [Fact]
    public void RenderValue_decimal() => Assert.Equal("-12.30", CompareOutput.RenderValue(-12.30m));

    [Fact]
    public void RenderValue_double_round_trips() => Assert.Equal("0.1", CompareOutput.RenderValue(0.1d));

    [Fact]
    public void RenderValue_bytes() => Assert.Equal("0x01AB", CompareOutput.RenderValue(new byte[] { 0x01, 0xAB }));

    [Fact]
    public void RenderValue_timespan() => Assert.Equal("01:02:03", CompareOutput.RenderValue(new TimeSpan(1, 2, 3)));

    private static DataTable Table(string[] cols, Type[] types, params object?[][] rows)
    {
        var t = new DataTable();
        for (int i = 0; i < cols.Length; i++) t.Columns.Add(cols[i], types[i]);
        foreach (var r in rows) t.Rows.Add(r.Select(v => v ?? DBNull.Value).ToArray());
        return t;
    }

    private static string DrainText(bool includePrint, params DataTable[] tables)
    {
        var w = new CanonicalWriter(includePrint);
        using var reader = new DataTableReader(tables);
        CompareOutput.Drain(reader, w);
        return Encoding.UTF8.GetString(w.ToBytes());
    }

    [Fact]
    public void Drain_two_result_sets_and_rc()
    {
        var a = Table(new[] { "id", "name" }, new[] { typeof(int), typeof(string) }, new object?[] { 1, "abc" }, new object?[] { 2, null });
        var b = Table(new[] { "m" }, new[] { typeof(decimal) }, new object?[] { 1.5000m });
        var rc = Table(new[] { CompareOutput.RcColumn }, new[] { typeof(int) }, new object?[] { 7 });
        Assert.Equal(
            "resultset 1\ncolumns: id int32, name string\nrow: 1\t'abc'\nrow: 2\tNULL\nrows: 2\n" +
            "resultset 2\ncolumns: m decimal\nrow: 1.5000\nrows: 1\n" +
            "rc: 7\n",
            DrainText(false, a, b, rc));
    }

    [Fact]
    public void Drain_without_rc_table_gives_rc_none()
    {
        var a = Table(new[] { "id" }, new[] { typeof(int) });
        Assert.Equal("resultset 1\ncolumns: id int32\nrows: 0\nrc: none\n", DrainText(false, a));
    }

    [Fact]
    public void Null_rc_renders_none()
    {
        var rc = Table(new[] { CompareOutput.RcColumn }, new[] { typeof(int) }, new object?[] { null });
        Assert.Equal("rc: none\n", DrainText(false, rc));
    }

    [Fact]
    public void Rc_is_last_even_when_errors_follow()
    {
        var w = new CanonicalWriter(false);
        w.ReturnCode(3);
        w.Error(50000, 16, "boom\nline2");
        w.TranCount(0, 1);
        Assert.Equal("error: Msg 50000, Level 16: boom\\nline2\ntrancount: 0 (expected 1)\nrc: 3\n", Encoding.UTF8.GetString(w.ToBytes()));
    }

    [Fact]
    public void Only_the_final_rc_shaped_set_is_the_rc()
    {
        var own = Table(new[] { CompareOutput.RcColumn }, new[] { typeof(int) }, new object?[] { 5 });
        var a = Table(new[] { "id" }, new[] { typeof(int) }, new object?[] { 1 });
        var rc = Table(new[] { CompareOutput.RcColumn }, new[] { typeof(int) }, new object?[] { 7 });
        Assert.Equal(
            "resultset 1\ncolumns: sql_test_rc int32\nrow: 5\nrows: 1\n" +
            "resultset 2\ncolumns: id int32\nrow: 1\nrows: 1\n" +
            "rc: 7\n",
            DrainText(false, own, a, rc));
    }

    [Fact]
    public void HasReturnCode_only_for_a_non_null_rc()
    {
        var none = new CanonicalWriter(false);
        Assert.False(none.HasReturnCode);
        var nul = new CanonicalWriter(false);
        nul.ReturnCode(null);
        Assert.False(nul.HasReturnCode);
        var zero = new CanonicalWriter(false);
        zero.ReturnCode(0);
        Assert.True(zero.HasReturnCode);
    }

    [Fact]
    public void Print_and_error_escape_backslash_before_newline()
    {
        var a = new CanonicalWriter(true);
        a.Print("a\\nb");
        a.Error(1, 16, "x\\ny");
        var b = new CanonicalWriter(true);
        b.Print("a\nb");
        b.Error(1, 16, "x\ny");
        Assert.Equal("print: a\\\\nb\nerror: Msg 1, Level 16: x\\\\ny\nrc: none\n", Encoding.UTF8.GetString(a.ToBytes()));
        Assert.NotEqual(a.ToBytes(), b.ToBytes());
    }

    [Fact]
    public void Concurrent_writers_lose_no_line()
    {
        var w = new CanonicalWriter(true);
        Parallel.For(0, 4, t => { for (int i = 0; i < 2000; i++) w.Print($"{t}:{i}"); });
        var lines = Encoding.UTF8.GetString(w.ToBytes()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(8001, lines.Length);
        Assert.All(lines[..^1], l => Assert.Matches(@"^print: \d:\d+$", l));
    }

    [Fact]
    public void Print_off_leaves_bytes_unchanged()
    {
        var plain = new CanonicalWriter(false);
        var printing = new CanonicalWriter(false);
        printing.Print("compare-fx 1");
        Assert.Equal(plain.ToBytes(), printing.ToBytes());
    }

    [Fact]
    public void Print_on_adds_print_line()
    {
        var w = new CanonicalWriter(true);
        w.Print("compare-fx 1");
        Assert.Equal("print: compare-fx 1\nrc: none\n", Encoding.UTF8.GetString(w.ToBytes()));
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void FirstDifference_identical_is_null() =>
        Assert.Null(CompareOutput.FirstDifference(B("a\nb\n"), B("a\nb\n")));

    [Fact]
    public void FirstDifference_swapped_rows_gives_offset_on_row_line()
    {
        const string head = "resultset 1\ncolumns: id int32\n";
        var oldS = head + "row: 1\nrow: 2\nrows: 2\nrc: none\n";
        var newS = head + "row: 2\nrow: 1\nrows: 2\nrc: none\n";
        var d = CompareOutput.FirstDifference(B(oldS), B(newS))!;
        Assert.Equal(head.Length + "row: ".Length, d.Offset);
        Assert.Equal(3, d.Line);
        Assert.Equal(new[] { "columns: id int32", "row: 1", "row: 2" }, d.OldLines);
        Assert.Equal(new[] { "columns: id int32", "row: 2", "row: 1" }, d.NewLines);

        var text = CompareOutput.FormatDiff("one", d);
        Assert.Equal($"compare one: differs at byte {d.Offset} (line 3)", text[0]);
        Assert.Contains("  old 3: row: 1", text);
        Assert.Contains("  new 3: row: 2", text);
    }

    [Fact]
    public void FirstDifference_prefix_gives_end_of_output()
    {
        var d = CompareOutput.FirstDifference(B("a\nb\n"), B("a\nb\nc\n"))!;
        Assert.Equal(4, d.Offset);
        Assert.Equal(3, d.Line);
        Assert.Equal(new[] { "b", CompareOutput.EndOfOutput }, d.OldLines);
        Assert.Equal(new[] { "b", "c", CompareOutput.EndOfOutput }, d.NewLines);
    }

    [Fact]
    public void FirstDifference_on_first_line_has_no_line_before()
    {
        var d = CompareOutput.FirstDifference(B("x\ny\n"), B("z\ny\n"))!;
        Assert.Equal(0, d.Offset);
        Assert.Equal(1, d.Line);
        Assert.Equal(new[] { "x", "y" }, d.OldLines);
    }
}

public class SvnCliTests
{
    [Fact]
    public void Non_numeric_rev_is_refused_without_a_process()
    {
        var r = new SvnCli(TimeSpan.FromSeconds(1), "svn-does-not-exist-sqltest").Cat("/x", "1;rm");
        Assert.Equal(-1, r.ExitCode);
        Assert.Contains("invalid revision", r.Error);
    }

    [Fact]
    public void Missing_binary_is_reported()
    {
        var r = new SvnCli(TimeSpan.FromSeconds(1), "svn-does-not-exist-sqltest").Cat("/x", "1");
        Assert.Equal(-1, r.ExitCode);
        Assert.Equal("svn-does-not-exist-sqltest not found on PATH", r.Error);
    }

    [Fact]
    public void Decode_strips_utf8_bom() =>
        Assert.Equal("abc", SvnCli.Decode(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'b', (byte)'c' }));

    // runsql reads source with new StreamReader(path); svn cat text must compile the same.
    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x61, 0xC3, 0xA6 })]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x61, 0x00, 0xE6, 0x00 })]
    [InlineData(new byte[] { 0x61, 0xE6, 0x62 })]
    [InlineData(new byte[] { 0x61, 0x0D, 0x0A, 0x62 })]
    public void Decode_matches_how_runsql_reads_a_source_file(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sqltest-decode-{Guid.NewGuid():N}.sql");
        try
        {
            File.WriteAllBytes(path, bytes);
            using var runsql = new StreamReader(path);
            Assert.Equal(runsql.ReadToEnd(), SvnCli.Decode(bytes));
        }
        finally { File.Delete(path); }
    }
}
