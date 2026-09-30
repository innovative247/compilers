using SqlTest;

namespace SqlTest.Tests;

public class OptionsTests
{
    [Fact]
    public void Bench_flags_parse_and_count_defaults_to_5()
    {
        var o = Options.Parse(new[] { "sbntest", "G", "--bench", "bench\\_%", "--bench-out", "o.json", "--bench-baseline", "b.json" })!;
        Assert.Equal("bench\\_%", o.BenchPattern);
        Assert.Equal(5, o.Count);
        Assert.Equal("o.json", o.BenchOut);
        Assert.Equal("b.json", o.BenchBaseline);

        var o2 = Options.Parse(new[] { "sbntest", "G", "--bench", "bench\\_%", "--count", "3" })!;
        Assert.Equal(3, o2.Count);

        var plain = Options.Parse(new[] { "sbntest", "G" })!;
        Assert.Null(plain.BenchPattern);
        Assert.Contains("--bench", Options.Usage);
    }

    [Fact]
    public void Source_root_parses_and_defaults_to_null()
    {
        Assert.Equal("/src/SBN_IR", Options.Parse(new[] { "sbntest", "G", "--source-root", "/src/SBN_IR" })!.SourceRoot);
        Assert.Null(Options.Parse(new[] { "sbntest", "G" })!.SourceRoot);
        Assert.Throws<ArgumentException>(() => Options.Parse(new[] { "sbntest", "G", "--source-root" }));
        Assert.Contains("--source-root", Options.Usage);
    }

    [Fact]
    public void Bench_update_baseline_parses()
    {
        var o = Options.Parse(new[] { "sbntest", "G", "--bench", "bench\\_%", "--bench-baseline", "b.json", "--bench-update-baseline" })!;
        Assert.True(o.BenchUpdateBaseline);
        Assert.False(Options.Parse(new[] { "sbntest", "G", "--bench", "bench\\_%" })!.BenchUpdateBaseline);
        Assert.Contains("--bench-update-baseline", Options.Usage);
    }

    [Fact]
    public void Bench_update_baseline_without_baseline_is_usage_error()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Options.Parse(new[] { "sbntest", "G", "--bench", "bench\\_%", "--bench-update-baseline" }));
        Assert.Contains("--bench-baseline", ex.Message);
    }

    [Fact]
    public void Bench_out_and_baseline_same_path_with_update_is_usage_error()
    {
        var ex = Assert.Throws<ArgumentException>(() => Options.Parse(new[]
            { "sbntest", "G", "--bench", "b", "--bench-out", "x/../b.json", "--bench-baseline", "b.json", "--bench-update-baseline" }));
        Assert.Contains("--bench-out alone", ex.Message);
        Assert.NotNull(Options.Parse(new[] { "sbntest", "G", "--bench", "b", "--bench-out", "b.json", "--bench-baseline", "b.json" }));
    }

    [Theory]
    [InlineData("--count", "3")]
    [InlineData("--bench-out", "o.json")]
    [InlineData("--bench-baseline", "b.json")]
    [InlineData("--bench-update-baseline", null)]
    public void Bench_only_flags_without_bench_are_usage_errors(string flag, string? value)
    {
        var args = new List<string> { "sbntest", "G", flag };
        if (value != null) args.Add(value);
        if (flag == "--bench-update-baseline") args.AddRange(new[] { "--bench-baseline", "b.json" });
        var ex = Assert.Throws<ArgumentException>(() => Options.Parse(args.ToArray()));
        Assert.Contains("--bench", ex.Message);
    }

    [Theory]
    [InlineData("--count", "3")]
    [InlineData("--bench-out", "o.json")]
    [InlineData("--bench-baseline", "b.json")]
    public void Bench_only_flags_are_refused_under_compare_rev(string flag, string value)
    {
        var args = new List<string> { "sbntest", "G", "--compare-rev", "123", "--proc", "pro_a", "--calls", "c.json", flag, value };
        var ex = Assert.Throws<ArgumentException>(() => Options.Parse(args.ToArray()));
        Assert.Equal($"{flag} requires --bench", ex.Message);
    }

    [Fact]
    public void Sweep_writer_journal_parses()
    {
        Assert.True(Options.Parse(new[] { "sbntest", "G", "--sweep-writer-journal" })!.SweepWriterJournal);
        Assert.False(Options.Parse(new[] { "sbntest", "G" })!.SweepWriterJournal);
        Assert.Contains("--sweep-writer-journal", Options.Usage);
    }

    [Theory]
    [InlineData("--bench", "b")]
    [InlineData("--list", null)]
    [InlineData("--print-capture-ddl", null)]
    [InlineData("--regenerate-capture-tables", null)]
    [InlineData("--junit", "out.xml")]
    [InlineData("--pattern", "test\\_x")]
    [InlineData("--exclude", "x")]
    [InlineData("--verbose", null)]
    public void Sweep_writer_journal_refuses_other_modes(string flag, string? value)
    {
        var args = new List<string> { "sbntest", "G", "--sweep-writer-journal", flag };
        if (value != null) args.Add(value);
        var ex = Assert.Throws<ArgumentException>(() => Options.Parse(args.ToArray()));
        Assert.Contains($"--sweep-writer-journal runs alone; drop {flag}", ex.Message);
    }

    [Theory]
    [InlineData(new[] { "--timeout", "45" }, 45)]
    [InlineData(new[] { "--list" }, null)]
    [InlineData(new[] { "--print-capture-ddl" }, null)]
    public void Discover_sweeps_with_the_timeout_except_in_read_only_modes(string[] flags, int? expected) =>
        Assert.Equal(expected, Runner.DiscoverSweepRestoreTimeout(Options.Parse(new[] { "sbntest", "G" }.Concat(flags).ToArray())!));

    [Fact]
    public void Variant_profile_parses_and_defaults_to_null()
    {
        Assert.Equal("GONZO", Options.Parse(new[] { "sbntest", "G", "--variant-profile", "GONZO" })!.VariantProfile);
        Assert.Null(Options.Parse(new[] { "sbntest", "G" })!.VariantProfile);
        Assert.Throws<ArgumentException>(() => Options.Parse(new[] { "sbntest", "G", "--variant-profile" }));
        Assert.Throws<ArgumentException>(() => Options.Parse(new[] { "sbntest", "G", "--sweep-writer-journal", "--variant-profile", "GONZO" }));
        Assert.Contains("--variant-profile", Options.Usage);
    }


    private static readonly string[] Compare = { "sbntest", "G", "--compare-rev", "123", "--proc", "pro_a", "--calls", "c.json" };

    [Theory]
    [InlineData("--compare-rev", "123")]
    [InlineData("--proc", "pro_a")]
    public void Compare_flag_alone_is_refused(string flag, string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => Options.Parse(new[] { "sbntest", "G", flag, value }));
        Assert.Equal("--compare-rev, --proc and --calls go together", ex.Message);
    }

    [Fact]
    public void Compare_flags_together_parse()
    {
        var o = Options.Parse(Compare)!;
        Assert.Equal("123", o.CompareRev);
        Assert.Equal("pro_a", o.Proc);
        Assert.Equal("c.json", o.Calls);
        Assert.False(o.ComparePrint);
        Assert.True(Options.Parse(Compare.Append("--compare-print").ToArray())!.ComparePrint);
        Assert.Contains("--compare-rev", Options.Usage);
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("r123")]
    [InlineData("-1")]
    public void Compare_rev_must_be_numeric(string rev)
    {
        var args = (string[])Compare.Clone();
        args[3] = rev;
        var ex = Assert.Throws<ArgumentException>(() => Options.Parse(args));
        Assert.Equal($"--compare-rev {rev}: not a revision number", ex.Message);
    }

    [Fact]
    public void Compare_proc_must_be_an_identifier()
    {
        var args = (string[])Compare.Clone();
        args[5] = "dbo.pro_a";
        var ex = Assert.Throws<ArgumentException>(() => Options.Parse(args));
        Assert.Equal("--proc dbo.pro_a: not an identifier", ex.Message);
    }

    [Theory]
    [InlineData("--pattern", "test\\_x")]
    [InlineData("--exclude", "x")]
    [InlineData("--bench", "b")]
    [InlineData("--list", null)]
    [InlineData("--junit", "out.xml")]
    [InlineData("--parallel", "2")]
    [InlineData("--regenerate-capture-tables", null)]
    [InlineData("--print-capture-ddl", null)]
    public void Compare_rev_refuses_other_modes(string flag, string? value)
    {
        var args = Compare.Append(flag).ToList();
        if (value != null) args.Add(value);
        var ex = Assert.Throws<ArgumentException>(() => Options.Parse(args.ToArray()));
        Assert.Equal($"--compare-rev runs alone; drop {flag}", ex.Message);
    }

    [Fact]
    public void Compare_rev_with_sweep_is_refused()
    {
        // The sweep check fires first, so only the throw is asserted.
        Assert.Throws<ArgumentException>(() => Options.Parse(Compare.Append("--sweep-writer-journal").ToArray()));
    }

    [Fact]
    public void Compare_print_alone_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => Options.Parse(new[] { "sbntest", "G", "--compare-print" }));
        Assert.Equal("--compare-print requires --compare-rev", ex.Message);
    }

    [Fact]
    public void Compare_rev_accepts_variant_profile() =>
        Assert.Equal("GONZO", Options.Parse(Compare.Concat(new[] { "--variant-profile", "GONZO" }).ToArray())!.VariantProfile);

    private static readonly string[] SpikeArgs = { "sbntest", "G", "--spike", "pro_a", "--lines", "10-20", "--calls", "c.json" };

    [Theory]
    [InlineData("--spike", "pro_a")]
    [InlineData("--lines", "10-20")]
    public void Spike_flag_alone_is_refused(string flag, string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => Options.Parse(new[] { "sbntest", "G", flag, value }));
        Assert.Equal("--spike, --lines and --calls go together", ex.Message);
    }

    [Fact]
    public void Calls_alone_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => Options.Parse(new[] { "sbntest", "G", "--calls", "c.json" }));
        Assert.Equal("--calls requires --compare-rev or --spike", ex.Message);
    }

    [Fact]
    public void Proc_with_calls_names_the_compare_group()
    {
        var ex = Assert.Throws<ArgumentException>(() => Options.Parse(new[] { "sbntest", "G", "--proc", "P", "--calls", "c.json" }));
        Assert.Equal("--compare-rev, --proc and --calls go together", ex.Message);
    }

    [Fact]
    public void Spike_flags_together_parse()
    {
        var o = Options.Parse(SpikeArgs.Concat(new[] { "--as", "RO_USER" }).ToArray())!;
        Assert.Equal(("pro_a", "10-20", "c.json", "RO_USER"), (o.Spike, o.SpikeLines, o.Calls, o.As));
        Assert.Null(o.CompareRev);
        Assert.Null(Options.Parse(SpikeArgs)!.As);
        Assert.Contains("--spike", Options.Usage);
        Assert.Contains("--as", Options.Usage);
    }

    [Fact]
    public void As_without_spike_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => Options.Parse(new[] { "sbntest", "G", "--as", "RO_USER" }));
        Assert.Equal("--as requires --spike", ex.Message);
    }

    [Theory]
    [InlineData("20-10")]
    [InlineData("a-b")]
    [InlineData("0-5")]
    public void Spike_bad_lines_are_refused(string lines)
    {
        var args = (string[])SpikeArgs.Clone();
        args[5] = lines;
        var ex = Assert.Throws<ArgumentException>(() => Options.Parse(args));
        Assert.Equal($"--lines {lines}: not a range <a>-<b> with 1 <= a <= b", ex.Message);
    }

    [Fact]
    public void Spike_proc_must_be_an_identifier()
    {
        var args = (string[])SpikeArgs.Clone();
        args[3] = "dbo.pro_a";
        Assert.Equal("--spike dbo.pro_a: not an identifier", Assert.Throws<ArgumentException>(() => Options.Parse(args)).Message);
    }

    [Theory]
    [InlineData("--compare-rev", "5")]
    [InlineData("--proc", "pro_b")]
    [InlineData("--compare-print", null)]
    [InlineData("--pattern", "test\\_x")]
    [InlineData("--bench", "b")]
    [InlineData("--parallel", "2")]
    [InlineData("--list", null)]
    public void Spike_refuses_other_modes(string flag, string? value)
    {
        var args = SpikeArgs.Append(flag).ToList();
        if (value != null) args.Add(value);
        var ex = Assert.Throws<ArgumentException>(() => Options.Parse(args.ToArray()));
        Assert.Equal($"--spike runs alone; drop {flag}", ex.Message);
    }

    [Fact]
    public void Spike_accepts_timeout_verbose_source_root_variant_profile_and_login()
    {
        var o = Options.Parse(SpikeArgs.Concat(new[] { "--timeout", "30", "--verbose", "--source-root", "/ir",
                                                       "--variant-profile", "V", "-U", "u", "-P", "p" }).ToArray())!;
        Assert.Equal((30, true, "/ir", "V", "u", "p"), (o.TimeoutSeconds, o.Verbose, o.SourceRoot, o.VariantProfile, o.User, o.Pass));
    }
}
