using SqlTest;

namespace SqlTest.Tests;

public class SpikeTests
{
    // Line numbers:        1              2     3   4                        5     6                 7                8            9                10           11    12                                13
    private const string Source = "use sbntest\ngo\n\ncreate proc px @x int\nas\ndeclare @n int\nselect @n = 1\nif @x = 1\n    select 2\nselect 3\ngo\ngrant execute on px to public\ngo\n";
    private const string Rel = "css/ss/px.sql";
    private const string Scratch = "px__spike";

    private sealed class NoCompiler : IScratchCompiler
    {
        public bool HasOption(string db, string option) => false;
        public string? Compile(ScratchCompile c) => throw new InvalidOperationException("pure test");
        public string? DeployedText(string db, string proc) => null;
    }

    private static ScratchSpec Spec(string text, int from, int to) =>
        new("px", "sbntest", text, Scratch, new Dictionary<string, bool>(), new[] { ("px", Scratch) },
            Spike.MarkerEdit(text, "px", Scratch, from, to));

    private static string Open(int line) => $"begin print '@sql-test:marker {Scratch}:{line} open'";
    private static string Close(int line) => $"print '@sql-test:marker {Scratch}:{line} close' end";

    [Theory]
    [InlineData("10-20", 10, 20)]
    [InlineData("7-7", 7, 7)]
    public void TryParseLines_accepts(string s, int from, int to)
    {
        Assert.True(Spike.TryParseLines(s, out var f, out var t));
        Assert.Equal((from, to), (f, t));
    }

    [Theory]
    [InlineData("20-10")]
    [InlineData("0-5")]
    [InlineData("a-b")]
    [InlineData("5")]
    [InlineData("1234567-1234568")]
    public void TryParseLines_refuses(string s) => Assert.False(Spike.TryParseLines(s, out _, out _));

    [Theory]
    [InlineData(6, 10)]
    [InlineData(9, 9)]
    [InlineData(5, 5)] // header before `as`: left to the compile
    public void RangeRefusal_accepts_a_range_inside_the_create_batch(int from, int to) =>
        Assert.Null(Spike.RangeRefusal(Source, "px", Rel, from, to));

    [Theory]
    [InlineData(4, 6)]   // on the create line
    [InlineData(9, 11)]  // past the batch
    [InlineData(12, 12)] // grant batch
    public void RangeRefusal_refuses_a_range_outside_the_create_batch(int from, int to) =>
        Assert.Equal($"--lines {from}-{to}: the create batch of px is lines 4-10 of {Rel}; the range must start after line 4 (create proc) and end by line 10",
            Spike.RangeRefusal(Source, "px", Rel, from, to));

    [Fact]
    public void RangeRefusal_refuses_from_after_to() =>
        Assert.Equal("--lines 8-7: the range must satisfy 1 <= from <= to", Spike.RangeRefusal(Source, "px", Rel, 8, 7));

    [Fact]
    public void RangeRefusal_refuses_a_proc_that_is_not_created() =>
        Assert.Equal($"py is not created in {Rel}", Spike.RangeRefusal(Source, "py", Rel, 6, 7));

    [Fact]
    public void MarkerEdit_wraps_the_exact_lines_and_leaves_other_batches_alone()
    {
        var batches = ScratchProc.SplitBatches(Source);
        var edited = Spike.MarkerEdit(Source, "px", Scratch, 6, 7)(batches);
        Assert.Equal(batches[0], edited[0]);
        Assert.Equal(batches[2], edited[2]);
        Assert.Equal(new[] { "create proc px @x int", "as", Open(6), "declare @n int", "select @n = 1", Close(7), "if @x = 1", "    select 2", "select 3" },
            edited[1].Split('\n'));
    }

    [Fact]
    public void Render_wraps_a_one_statement_if_body_and_names_the_scratch_once()
    {
        var r = ScratchProc.Render(Spec(Source, 9, 9));
        Assert.Equal("use sbntest\ngo\ncreate proc px__spike @x int\nas\ndeclare @n int\nselect @n = 1\nif @x = 1\n"
                   + Open(9) + "\n    select 2\n" + Close(9) + "\nselect 3\ngo\ngrant execute on px__spike to public\ngo\n", r);
        Assert.DoesNotContain("__spike__spike", r);
        Assert.Equal(2, r.Split($"{Scratch}:9").Length - 1); // once in the open marker, once in the close
    }

    [Fact]
    public void Precheck_passes_the_marked_spec_and_still_refuses_an_include()
    {
        Assert.Null(ScratchProc.Precheck(Spec(Source, 6, 10), new NoCompiler()));
        var withInclude = Source.Replace("select 3\n", "select 3\n$i inc.sql\n");
        Assert.Equal(ScratchProc.IncludeRefusal, ScratchProc.Precheck(Spec(withInclude, 6, 10), new NoCompiler()));
    }

    // Slice
    private const string OpenMsg = "@sql-test:marker px__spike:6";
    private const string CloseMsg = "@sql-test:marker px__spike:7";
    private static (int, string) P(string text) => (0, text);
    private static (int, string) T(string table, long logical, long physical = 0) =>
        (3615, $"Table: {table} scan count 1, logical reads: (regular={logical} apf=0 total={logical}), physical reads: (regular={physical} apf=0 total={physical})");
    private static (int, string) W(long writes) => (3614, $"Total writes for this command: {writes}");
    private static SpikeSlice Slice(params (int, string)[] m) => Spike.Slice(m, OpenMsg, CloseMsg, new SybaseIoMeter());
    private static readonly (string Open, string Close) Same = Spike.Markers("px", Scratch, 9, 9);
    private static SpikeSlice SliceSame(params (int, string)[] m) => Spike.Slice(m, Same.Open, Same.Close, new SybaseIoMeter());

    [Fact]
    public void Slice_one_pass_ignores_stat_lines_outside_the_range()
    {
        var s = Slice(T("sysobjects", 5), P(OpenMsg), T("sysusers", 2, 1), W(0), P(CloseMsg), T("syscolumns", 7));
        Assert.Equal((1, 0, (string?)null), (s.Passes, s.Unclosed, s.Error));
        Assert.Equal((2L, 1L, 0L), (s.Io.LogicalReads, s.Io.PhysicalReads, s.Io.Writes));
        Assert.Equal(new[] { new TableIo("sysusers", 2, 1) }, s.Io.Tables);
    }

    [Fact]
    public void Slice_from_equal_to_pairs_open_with_close()
    {
        Assert.NotEqual(Same.Open, Same.Close);
        var s = SliceSame(P(Same.Open), T("a", 1), P(Same.Close), T("x", 50), P(Same.Open), T("a", 2), P(Same.Close));
        Assert.Equal((2, 0), (s.Passes, s.Unclosed));
        Assert.Equal(new[] { new TableIo("a", 3, 0) }, s.Io.Tables);
    }

    [Fact]
    public void Slice_from_equal_to_with_a_skipped_close_is_unclosed_not_clean()
    {
        // A `continue` or `return` on the one-line range skips the close of the first pass.
        var s = SliceSame(P(Same.Open), T("x", 50), P(Same.Open), T("a", 2), P(Same.Close));
        Assert.Equal((1, 1, 2L), (s.Passes, s.Unclosed, s.Io.LogicalReads));
    }

    [Theory]
    [InlineData("marker")]
    [InlineData("test")]
    [InlineData("open")]
    public void Markers_match_the_rendered_prints_when_the_proc_name_is_a_marker_word(string proc)
    {
        var text = Source.Replace("px", proc);
        var scratch = $"{proc}__spike";
        var spec = new ScratchSpec(proc, "sbntest", text, scratch, new Dictionary<string, bool>(), new[] { (proc, scratch) },
                                   Spike.MarkerEdit(text, proc, scratch, 6, 7));
        var prints = System.Text.RegularExpressions.Regex.Matches(ScratchProc.Render(spec), "print '([^']*)'")
                        .Select(m => (0, m.Groups[1].Value)).ToList();
        Assert.Equal(2, prints.Count);
        var (open, close) = Spike.Markers(proc, scratch, 6, 7);
        var s = Spike.Slice(new[] { prints[0], T("a", 1), prints[1] }, open, close, new SybaseIoMeter());
        Assert.Equal((1, 0), (s.Passes, s.Unclosed));
    }

    [Fact]
    public void Slice_sums_a_loop_with_tables_in_first_seen_order()
    {
        var s = Slice(P(OpenMsg), T("b", 1), P(CloseMsg), P(OpenMsg), T("a", 2), T("b", 1), P(CloseMsg), P(OpenMsg), T("a", 4), P(CloseMsg));
        Assert.Equal(3, s.Passes);
        Assert.Equal(8, s.Io.LogicalReads);
        Assert.Equal(new[] { new TableIo("b", 2, 0), new TableIo("a", 6, 0) }, s.Io.Tables);
    }

    [Fact]
    public void Slice_counts_an_open_pass_at_the_end_as_unclosed()
    {
        var s = Slice(P(OpenMsg), T("a", 1), P(CloseMsg), P(OpenMsg), T("a", 9));
        Assert.Equal((1, 1, 1L), (s.Passes, s.Unclosed, s.Io.LogicalReads));
    }

    [Fact]
    public void Slice_counts_a_reopen_as_unclosed_and_drops_its_buffer()
    {
        var s = Slice(P(OpenMsg), T("a", 9), P(OpenMsg), T("a", 1), P(CloseMsg));
        Assert.Equal((1, 1, 1L), (s.Passes, s.Unclosed, s.Io.LogicalReads));
    }

    [Fact]
    public void Slice_ignores_a_close_while_closed()
    {
        var s = Slice(P(CloseMsg), T("a", 9), P(OpenMsg), T("a", 1), P(CloseMsg), P(CloseMsg));
        Assert.Equal((1, 0, 1L), (s.Passes, s.Unclosed, s.Io.LogicalReads));
    }

    [Fact]
    public void Slice_never_feeds_prints_so_measure_start_does_not_reset()
    {
        var s = Slice(P(OpenMsg), T("a", 3), P("@sql-test:measure-start"), T("a", 1), P("@sql-test:assert-max-reads 0 x"), P(CloseMsg));
        Assert.Equal((4L, (string?)null), (s.Io.LogicalReads, s.Error));
    }

    [Fact]
    public void Slice_sets_Error_on_an_unparseable_stat_line_inside_the_range()
    {
        Assert.Null(Slice((3615, "garbage"), P(OpenMsg), P(CloseMsg)).Error);
        Assert.Equal("unparseable I/O statistics line: garbage", Slice(P(OpenMsg), (3615, "garbage"), P(CloseMsg)).Error);
    }

    [Fact]
    public void Slice_counts_writes()
    {
        var s = Slice(P(OpenMsg), W(3), P(CloseMsg), P(OpenMsg), W(2), P(CloseMsg));
        Assert.Equal(5, s.Io.Writes);
    }

    // Format
    private static IoMeasure Io(params TableIo[] t) => new(t.Sum(x => x.LogicalReads), t.Sum(x => x.PhysicalReads), 4, t);

    [Fact]
    public void Format_measured()
    {
        var s = new SpikeSlice(3, 0, Io(new TableIo("sysusers", 6, 1), new TableIo("sysobjects", 2, 0)), null);
        Assert.Equal(new[]
        {
            "spike three: px lines 6-7: 3 pass(es), logical 8, physical 1, writes 4",
            "  sysusers  logical 6  physical 1",
            "  sysobjects  logical 2  physical 0",
        }, Spike.Format("three", "px", 6, 7, s));
    }

    [Fact]
    public void Format_not_reached() =>
        Assert.Equal(new[] { "spike zero: px lines 6-7: range not reached" }, Spike.Format("zero", "px", 6, 7, new SpikeSlice(0, 0, Io(), null)));

    [Fact]
    public void Format_unclosed_follows_the_closed_passes()
    {
        Assert.Equal(new[]
        {
            "spike c: px lines 6-7: 1 pass(es), logical 2, physical 0, writes 4",
            "  a  logical 2  physical 0",
            "spike c: px lines 6-7: 2 pass(es) not closed (return, goto or error inside the range); closed passes only",
        }, Spike.Format("c", "px", 6, 7, new SpikeSlice(1, 2, Io(new TableIo("a", 2, 0)), null)));
        Assert.Equal(new[] { "spike c: px lines 6-7: 1 pass(es) not closed (return, goto or error inside the range); closed passes only" },
            Spike.Format("c", "px", 6, 7, new SpikeSlice(0, 1, Io(), null)));
    }

    [Fact]
    public void Format_not_measured() =>
        Assert.Equal(new[] { "spike c: not measured: unparseable I/O statistics line: x" },
            Spike.Format("c", "px", 6, 7, new SpikeSlice(1, 0, Io(), "unparseable I/O statistics line: x")));
}
