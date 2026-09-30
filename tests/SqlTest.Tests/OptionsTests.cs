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
}
