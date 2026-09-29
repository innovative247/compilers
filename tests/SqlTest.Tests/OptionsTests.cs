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
}
