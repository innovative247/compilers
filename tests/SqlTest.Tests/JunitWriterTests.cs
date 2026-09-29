using System.Xml.Linq;
using SqlTest;

namespace SqlTest.Tests;

public class JunitWriterTests
{
    private static string WriteXml(params TestResult[] results) =>
        TestScratch.Use(dir =>
        {
            var path = Path.Combine(dir, "junit.xml");
            JunitWriter.Write(path, results, 1.5);
            return File.ReadAllText(path).Replace("\r\n", "\n");
        });

    private static Dictionary<string, string> Props(XDocument doc, string testcase) =>
        doc.Root!.Elements("testcase").Single(e => (string?)e.Attribute("name") == testcase)
            .Elements("properties").Elements("property")
            .ToDictionary(p => (string)p.Attribute("name")!, p => (string)p.Attribute("value")!);

    [Fact]
    public void Unmeasured_testcases_are_unchanged()
    {
        var xml = WriteXml(
            new TestResult("test_a", Outcome.PASS, "", 0.5, "out"),
            new TestResult("test_b", Outcome.FAIL, "FAIL: x", 0.25, "log"));
        const string expected =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<testsuite name=\"sql-test\" tests=\"2\" failures=\"1\" errors=\"0\" skipped=\"0\" time=\"1.500\">\n" +
            "  <testcase name=\"test_a\" time=\"0.500\" />\n" +
            "  <testcase name=\"test_b\" time=\"0.250\">\n" +
            "    <failure message=\"FAIL: x\"><![CDATA[log]]></failure>\n" +
            "  </testcase>\n" +
            "</testsuite>";
        Assert.Equal(expected, xml);
    }

    [Fact]
    public void Measured_testcases_get_their_own_properties()
    {
        var io = new IoMeasure(5806, 1, 0, new List<TableIo>());
        var doc = XDocument.Parse(WriteXml(
            new TestResult("bench_x", Outcome.PASS, "", 0.5, "") { Io = io, Count = 5 },
            new TestResult("test_budget", Outcome.PASS, "", 0.5, "") { Io = io, MaxReads = 6000 },
            new TestResult("test_plain", Outcome.PASS, "", 0.5, "")));

        Assert.Equal(new Dictionary<string, string>
        {
            ["reads_per_op"] = "5806", ["phys_per_op"] = "1", ["writes_per_op"] = "0", ["count"] = "5",
        }, Props(doc, "bench_x"));
        Assert.Equal(new Dictionary<string, string>
        {
            ["logical_reads"] = "5806", ["max_reads"] = "6000",
        }, Props(doc, "test_budget"));
        Assert.Empty(Props(doc, "test_plain"));
    }
}
