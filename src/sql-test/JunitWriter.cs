using System.Xml.Linq;

namespace SqlTest;

public static class JunitWriter
{
    public static void Write(string path, IReadOnlyList<TestResult> results, double totalSeconds)
    {
        int failures = results.Count(r => r.Outcome == Outcome.FAIL);
        int errors   = results.Count(r => r.Outcome is Outcome.ERROR or Outcome.TIMEOUT);
        int skipped  = results.Count(r => r.Outcome == Outcome.SKIP);

        var suite = new XElement("testsuite",
            new XAttribute("name",     "sql-test"),
            new XAttribute("tests",    results.Count),
            new XAttribute("failures", failures),
            new XAttribute("errors",   errors),
            new XAttribute("skipped",  skipped),
            new XAttribute("time",     totalSeconds.ToString("F3")));

        foreach (var r in results)
        {
            var tc = new XElement("testcase",
                new XAttribute("name", r.Name),
                new XAttribute("time", r.DurationSeconds.ToString("F3")));

            switch (r.Outcome)
            {
                case Outcome.FAIL:
                    tc.Add(new XElement("failure",
                        new XAttribute("message", r.Message),
                        new XCData(r.Output)));
                    break;
                case Outcome.SKIP:
                    tc.Add(new XElement("skipped",
                        new XAttribute("message", r.Message)));
                    break;
                case Outcome.ERROR:
                case Outcome.TIMEOUT:
                    tc.Add(new XElement("error",
                        new XAttribute("message", r.Message),
                        new XCData(r.Output)));
                    break;
            }
            suite.Add(tc);
        }

        new XDocument(new XDeclaration("1.0", "utf-8", null), suite).Save(path);
    }
}
