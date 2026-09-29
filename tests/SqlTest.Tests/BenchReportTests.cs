using SqlTest;

namespace SqlTest.Tests;

public class BenchReportTests
{
    private static BenchResult Bench(string name, double reads, double phys = 0, double writes = 0, int? threshold = null) =>
        new(name, threshold, reads, phys, writes,
            new List<BenchRun> { new((long)reads, (long)phys, (long)writes) },
            new List<BenchTable> { new("sbnmaster..billing_accounts", reads, phys) });

    private static BenchDocument Doc(params BenchResult[] b) =>
        new(1, "GONZO", "sbntest", new DateTime(2026, 9, 29, 10, 12, 0, DateTimeKind.Utc), 5, b.ToList());

    [Fact]
    public void Json_round_trip_keeps_schema_and_runs()
    {
        var runs = new[]
        {
            new IoMeasure(1349, 0, 0, new List<TableIo> { new("sbnmaster..billing_accounts", 752, 0) }),
            new IoMeasure(1351, 2, 1, new List<TableIo> { new("sbnmaster..billing_accounts", 754, 2) }),
        };
        var r = BenchReport.Summarize("bench_x", 10, runs);
        Assert.Equal(1350, r.ReadsPerOp);
        Assert.Equal(1, r.PhysPerOp);
        Assert.Equal(0.5, r.WritesPerOp);

        TestScratch.Use(dir =>
        {
            var path = Path.Combine(dir, "bench.json");
            BenchReport.WriteJson(path, Doc(r));
            var text = File.ReadAllText(path);
            Assert.Contains("\"schema\": 1", text);
            Assert.Contains("\"reads_per_op\"", text);
            Assert.Contains("\"threshold_pct\": 10", text);
            var back = BenchReport.ReadJson(path);
            Assert.Equal(1, back.Schema);
            Assert.Equal("GONZO", back.Server);
            var b = Assert.Single(back.Benchmarks);
            Assert.Equal(2, b.Runs.Count);
            Assert.Equal(new BenchRun(1351, 2, 1), b.Runs[1]);
            Assert.Equal(10, b.ThresholdPct);
            Assert.Equal(753, Assert.Single(b.Tables).ReadsPerOp);
        });
    }

    [Fact]
    public void Threshold_exceeded_flags_reads_and_exits_1()
    {
        var c = BenchReport.Compare(Doc(Bench("bench_x", 5806)), Doc(Bench("bench_x", 28512, threshold: 10)));
        var reads = c.Rows.Single(r => r.Value == "reads");
        Assert.True(reads.Flagged);
        Assert.Equal(1, c.ExitCode);
        var line = BenchReport.FormatComparison(c).Single(l => l.Contains(" reads "));
        Assert.Contains("+391.1%", line);
        Assert.Contains("! (threshold 10%)", line);
    }

    [Fact]
    public void No_threshold_shows_change_without_flag()
    {
        var c = BenchReport.Compare(Doc(Bench("bench_x", 5806)), Doc(Bench("bench_x", 28512)));
        Assert.False(c.Rows.Single(r => r.Value == "reads").Flagged);
        Assert.Equal(0, c.ExitCode);
        var line = BenchReport.FormatComparison(c).Single(l => l.Contains(" reads "));
        Assert.Contains("+391.1%", line);
        Assert.DoesNotContain("!", line);
    }

    [Fact]
    public void Phys_and_writes_deltas_never_flagged()
    {
        var c = BenchReport.Compare(Doc(Bench("bench_x", 100, phys: 10, writes: 10)),
                                    Doc(Bench("bench_x", 100, phys: 50, writes: 90, threshold: 10)));
        Assert.Equal(0, c.ExitCode);
        var lines = BenchReport.FormatComparison(c).ToList();
        Assert.Contains(lines, l => l.Contains(" phys ") && l.Contains("+400.0%") && !l.Contains("!"));
        Assert.Contains(lines, l => l.Contains(" writes ") && l.Contains("+800.0%") && !l.Contains("!"));
        Assert.Contains(lines, l => l.Contains(" reads ") && l.Contains("0.0%"));
    }

    [Fact]
    public void Missing_benchmarks_listed_as_gone_and_new()
    {
        var c = BenchReport.Compare(Doc(Bench("bench_old", 1)), Doc(Bench("bench_new", 1)));
        var lines = BenchReport.FormatComparison(c).ToList();
        Assert.Contains(lines, l => l.StartsWith("bench_old") && l.TrimEnd().EndsWith("gone"));
        Assert.Contains(lines, l => l.StartsWith("bench_new") && l.TrimEnd().EndsWith("new"));
        Assert.Equal(0, c.ExitCode);
    }

    [Fact]
    public void Null_benchmarks_is_unreadable()
    {
        TestScratch.Use(dir =>
        {
            var path = Path.Combine(dir, "baseline.json");
            File.WriteAllText(path, "{\"schema\": 1, \"server\": \"GONZO\", \"database\": \"sbntest\", \"date\": \"2026-09-29T10:12:00Z\", \"count\": 5}");
            Assert.Throws<InvalidDataException>(() => BenchReport.ReadJson(path));
        });
    }

    [Fact]
    public void Failed_benchmark_listed_as_failed_not_gone()
    {
        var c = BenchReport.Compare(Doc(Bench("bench_broken", 1), Bench("bench_ok", 1)), Doc(Bench("bench_ok", 1)),
                                    new[] { "bench_broken" });
        var lines = BenchReport.FormatComparison(c).ToList();
        Assert.Contains(lines, l => l.StartsWith("bench_broken") && l.TrimEnd().EndsWith("failed"));
        Assert.DoesNotContain(lines, l => l.TrimEnd().EndsWith("gone"));
    }

    [Fact]
    public void Baseline_threshold_is_ignored_when_current_has_none()
    {
        var c = BenchReport.Compare(Doc(Bench("bench_x", 100, threshold: 10)), Doc(Bench("bench_x", 200)));
        Assert.False(c.Rows.Single(r => r.Value == "reads").Flagged);
        Assert.Equal(0, c.ExitCode);
    }

    [Fact]
    public void Increase_below_threshold_is_not_flagged()
    {
        var c = BenchReport.Compare(Doc(Bench("bench_x", 100)), Doc(Bench("bench_x", 105, threshold: 10)));
        var reads = c.Rows.Single(r => r.Value == "reads");
        Assert.Equal("+5.0%", reads.Delta);
        Assert.False(reads.Flagged);
        Assert.Equal(0, c.ExitCode);
    }

    [Fact]
    public void Server_mismatch_is_a_warning()
    {
        var baseline = Doc(Bench("bench_x", 1)) with { Server = "OTHER" };
        var c = BenchReport.Compare(baseline, Doc(Bench("bench_x", 1)));
        Assert.NotNull(c.Warning);
        Assert.Equal(0, c.ExitCode);
    }
}
