using SqlTest;

namespace SqlTest.Tests;

public class SybaseIoMeterTests
{
    private readonly SybaseIoMeter _m = new();

    [Fact]
    public void TableLine_with_alias_strips_alias_and_uses_totals()
    {
        const string text = "Table: sbnmaster..billing_accounts (a) scan count 1, logical reads: (regular=752 apf=0 total=752), physical reads: (regular=0 apf=0 total=0), apf IOs used=0";
        Assert.True(_m.TryParse(3615, text, out var line));
        Assert.Equal(new TableLine("sbnmaster..billing_accounts", 752, 0), line);
    }

    [Fact]
    public void Worktable_with_apf_uses_totals()
    {
        const string text = "Table: Worktable1 scan count 2, logical reads: (regular=260 apf=7 total=267), physical reads: (regular=3 apf=5 total=8), apf IOs used=5";
        Assert.True(_m.TryParse(3615, text, out var line));
        Assert.Equal(new TableLine("Worktable1", 267, 8), line);
    }

    [Fact]
    public void WritesLine_parsed_from_3614()
    {
        Assert.True(_m.TryParse(3614, "Total writes for this command: 338", out var line));
        Assert.Equal(new WritesLine(338), line);
    }

    [Theory]
    [InlineData("$a")]
    [InlineData("#t1")]
    [InlineData("@x")]
    [InlineData("a b")]
    public void TableLine_with_any_alias_is_parsed(string alias)
    {
        var text = $"Table: sbnmaster..users ({alias}) scan count 1, logical reads: (regular=11 apf=0 total=11), physical reads: (regular=0 apf=0 total=0), apf IOs used=0";
        Assert.True(_m.TryParse(3615, text, out var line));
        Assert.Equal(new TableLine("sbnmaster..users", 11, 0), line);
    }

    [Fact]
    public void Print_starting_with_Table_is_not_counted()
    {
        const string text = "Table: sbnmaster..users scan count 1, logical reads: (regular=11 apf=0 total=11), physical reads: (regular=0 apf=0 total=0), apf IOs used=0";
        Assert.False(_m.TryParse(0, text, out _));
        Assert.False(_m.TryParse(0, "Total writes for this command: 5", out _));
    }
}

public class MeasureAccumulatorTests
{
    internal static string T(string table, long logical, long physical = 0) =>
        $"Table: {table} scan count 1, logical reads: (regular={logical} apf=0 total={logical}), physical reads: (regular={physical} apf=0 total={physical}), apf IOs used=0";

    private static MeasureAccumulator New() => new(new SybaseIoMeter());

    [Fact]
    public void No_marker_measures_whole_batch()
    {
        var a = New();
        a.Feed(3614, "Total writes for this command: 0");
        a.Feed(3615, T("sbnmaster..users", 11, 2));
        a.Feed(3615, T("sbnmaster..addresses", 303));
        a.Feed(3614, "Total writes for this command: 4");
        var m = a.Current;
        Assert.Equal(314, m.LogicalReads);
        Assert.Equal(2, m.PhysicalReads);
        Assert.Equal(4, m.Writes);
        Assert.Null(a.Error);
        Assert.Empty(a.Failures);
    }

    [Fact]
    public void Measure_start_discards_setup_lines()
    {
        var a = New();
        a.Feed(3615, T("sbnmaster..options", 500, 9));
        a.Feed(3614, "Total writes for this command: 7");
        a.Feed(0, "@sql-test:measure-start");
        a.Feed(3615, T("sbnmaster..users", 11));
        Assert.Equal(11, a.Current.LogicalReads);
        Assert.Equal(0, a.Current.PhysicalReads);
        Assert.Equal(0, a.Current.Writes);
        Assert.Single(a.Current.Tables);
    }

    [Fact]
    public void Second_measure_start_resets_region()
    {
        var a = New();
        a.Feed(0, "@sql-test:measure-start");
        a.Feed(3615, T("sbnmaster..users", 11));
        a.Feed(0, "@sql-test:measure-start");
        a.Feed(3615, T("sbnmaster..addresses", 303));
        Assert.Equal(303, a.Current.LogicalReads);
        Assert.Equal("sbnmaster..addresses", Assert.Single(a.Current.Tables).Table);
    }

    [Fact]
    public void Assert_without_measure_start_is_error()
    {
        var a = New();
        a.Feed(3615, T("sbnmaster..users", 11));
        a.Feed(0, "@sql-test:assert-max-reads 6000 list");
        Assert.Equal("budget without pro_test_measure_start", a.Error);
    }

    [Fact]
    public void Assert_over_budget_fails_and_later_asserts_still_evaluated()
    {
        var a = New();
        a.Feed(0, "@sql-test:measure-start");
        a.Feed(3615, T("sbnmaster..billing_accounts", 752));
        a.Feed(0, "@sql-test:assert-max-reads 700 list reads");
        a.Feed(0, "@sql-test:assert-max-reads 800 second");
        Assert.Equal(new[] { "list reads (max_reads=700 actual=752)" }, a.Failures);
        Assert.Equal(2, a.Budgets.Count);
        Assert.False(a.Budgets[1].Failed);
    }

    [Fact]
    public void Multiple_asserts_each_see_region_at_that_point()
    {
        var a = New();
        a.Feed(0, "@sql-test:measure-start");
        a.Feed(3615, T("sbnmaster..users", 11));
        a.Feed(0, "@sql-test:assert-max-reads 20 first");
        a.Feed(3615, T("sbnmaster..addresses", 303));
        a.Feed(0, "@sql-test:assert-max-reads 20 second");
        Assert.Equal(11, a.Budgets[0].Actual);
        Assert.Equal(314, a.Budgets[1].Actual);
        Assert.Equal(new[] { "second (max_reads=20 actual=314)" }, a.Failures);
    }

    [Theory]
    [InlineData(3615, "Table: sbnmaster..users scan count 1, logical reads: 11")]
    [InlineData(3614, "Total writes: 4")]
    public void Unparseable_stat_line_is_an_error(int number, string text)
    {
        var a = New();
        a.Feed(number, text);
        Assert.StartsWith("unparseable I/O statistics line:", a.Error);
    }

    [Fact]
    public void Aliased_and_unaliased_table_merge()
    {
        var a = New();
        a.Feed(3615, T("sbnmaster..users", 11, 1));
        a.Feed(3615, T("sbnmaster..users (a)", 4, 2));
        var t = Assert.Single(a.Current.Tables);
        Assert.Equal(new TableIo("sbnmaster..users", 15, 3), t);
    }
}
