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
}
