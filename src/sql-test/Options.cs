namespace SqlTest;

public class Options
{
    public string Pattern { get; set; } = @"test\_%";
    public string? Exclude { get; set; }
    public string Database { get; set; } = "sbntest";
    public string Server { get; set; } = "";
    public int Parallel { get; set; } = 1;
    public int TimeoutSeconds { get; set; } = 600;
    public string? JunitPath { get; set; }
    public bool ListOnly { get; set; }
    public bool Verbose { get; set; }
    public bool RegenerateCaptureTables { get; set; }
    public bool PrintCaptureDdl { get; set; }
    public string User { get; set; } = "";
    public string Pass { get; set; } = "";
    public string? BenchPattern { get; set; }
    public int Count { get; set; } = 5;
    public string? BenchOut { get; set; }
    public string? BenchBaseline { get; set; }
    public bool BenchUpdateBaseline { get; set; }
    public bool SweepWriterJournal { get; set; }
    public string? SourceRoot { get; set; }
    public string? VariantProfile { get; set; }
    public string? CompareRev { get; set; }
    public string? Proc { get; set; }
    public string? Calls { get; set; }
    public bool ComparePrint { get; set; }

    public const string Usage =
        "Usage: sql-test <database> <server/profile>\n" +
        "                [--pattern <like>]   (default: 'test\\_%')\n" +
        "                [--exclude <regex>]\n" +
        "                [--parallel <n>]     (default: 1)\n" +
        "                [--timeout <sec>]    (default: 600)\n" +
        "                [--junit <path>]\n" +
        "                [--list]\n" +
        "                [--verbose]\n" +
        "                [--regenerate-capture-tables]\n" +
        "                [--print-capture-ddl]\n" +
        "                [--bench <like>]     (run bench_* procs instead of tests; --pattern ignored)\n" +
        "                [--count <n>]        (measured runs per benchmark after one warm-up; default: 5)\n" +
        "                [--bench-out <file>] (write results as JSON)\n" +
        "                [--bench-baseline <file>] (compare against a --bench-out file; exit 1 when flagged)\n" +
        "                [--bench-update-baseline] (then merge PASS results into the --bench-baseline file; created if missing)\n" +
        "                [--sweep-writer-journal] (restore dead runners' writer journal rows, then exit)\n" +
        "                [--source-root <dir>] (SBN_IR tree for @variant sources; default: the profile's IR path)\n" +
        "                [--variant-profile <name>] (options layer @variant compiles with; default: the runner's profile)\n" +
        "                [--compare-rev <rev> --proc <name> --calls <json>] (diff each call's output at <rev> and at the working copy; runs alone)\n" +
        "                [--compare-print]    (with --compare-rev: include print output in the comparison)\n" +
        "                [-U user] [-P pass]";

    public static Options? Parse(string[] argv)
    {
        var opts = new Options();
        var positional = new List<string>();
        var benchOnly = new List<string>();
        var notWithSweep = new List<string>();
        var seen = new HashSet<string>();

        for (int i = 0; i < argv.Length; i++)
        {
            var a = argv[i];
            string Next(string flag) =>
                i + 1 < argv.Length ? argv[++i] : throw new ArgumentException($"{flag} requires a value");

            seen.Add(a);
            switch (a)
            {
                case "--pattern":                    notWithSweep.Add(a); opts.Pattern                 = Next(a); break;
                case "--exclude":                    notWithSweep.Add(a); opts.Exclude                 = Next(a); break;
                case "--parallel":                   opts.Parallel                = int.Parse(Next(a)); break;
                case "--timeout":                    opts.TimeoutSeconds          = int.Parse(Next(a)); break;
                case "--junit":                      notWithSweep.Add(a); opts.JunitPath               = Next(a); break;
                case "--list":                       notWithSweep.Add(a); opts.ListOnly                = true; break;
                case "--verbose":                    notWithSweep.Add(a); opts.Verbose                 = true; break;
                case "--regenerate-capture-tables":  notWithSweep.Add(a); opts.RegenerateCaptureTables = true; break;
                case "--print-capture-ddl":          notWithSweep.Add(a); opts.PrintCaptureDdl         = true; break;
                case "--bench":                      notWithSweep.Add(a); opts.BenchPattern            = Next(a); break;
                case "--count":                      benchOnly.Add(a); opts.Count                   = int.Parse(Next(a)); break;
                case "--bench-out":                  benchOnly.Add(a); opts.BenchOut                = Next(a); break;
                case "--bench-baseline":             benchOnly.Add(a); opts.BenchBaseline           = Next(a); break;
                case "--bench-update-baseline":      benchOnly.Add(a); opts.BenchUpdateBaseline = true; break;
                case "--sweep-writer-journal":       opts.SweepWriterJournal      = true; break;
                case "--source-root":                notWithSweep.Add(a); opts.SourceRoot              = Next(a); break;
                case "--variant-profile":            notWithSweep.Add(a); opts.VariantProfile          = Next(a); break;
                case "--compare-rev":                notWithSweep.Add(a); opts.CompareRev              = Next(a); break;
                case "--proc":                       notWithSweep.Add(a); opts.Proc                    = Next(a); break;
                case "--calls":                      notWithSweep.Add(a); opts.Calls                   = Next(a); break;
                case "--compare-print":              notWithSweep.Add(a); opts.ComparePrint            = true; break;
                case "-h":
                case "--help":                       return null;
                default:
                    if (a.StartsWith("-U")) opts.User = a.Length > 2 ? a[2..] : Next(a);
                    else if (a.StartsWith("-P")) opts.Pass = a.Length > 2 ? a[2..] : Next(a);
                    else if (a.StartsWith("-")) throw new ArgumentException($"Unknown flag: {a}");
                    else positional.Add(a);
                    break;
            }
        }

        if (opts.Count < 1)
            throw new ArgumentException("--count must be at least 1");
        if (opts.BenchPattern == null && benchOnly.Count > 0)
            throw new ArgumentException($"{benchOnly[0]} requires --bench");
        if (opts.BenchUpdateBaseline && !string.IsNullOrEmpty(opts.BenchOut) && !string.IsNullOrEmpty(opts.BenchBaseline)
            && Path.GetFullPath(opts.BenchOut) == Path.GetFullPath(opts.BenchBaseline))
            throw new ArgumentException("--bench-out and --bench-baseline are the same file with --bench-update-baseline; use --bench-out alone for a full reset");
        if (opts.BenchUpdateBaseline && string.IsNullOrEmpty(opts.BenchBaseline))
            throw new ArgumentException("--bench-update-baseline requires --bench-baseline <file>");
        if (opts.SweepWriterJournal && notWithSweep.Count > 0)
            throw new ArgumentException($"--sweep-writer-journal runs alone; drop {string.Join(", ", notWithSweep.Distinct())}");
        var compareSet = new[] { "--compare-rev", "--proc", "--calls" }.Where(seen.Contains).ToList();
        if (compareSet.Count is > 0 and < 3)
            throw new ArgumentException("--compare-rev, --proc and --calls go together");
        if (opts.ComparePrint && opts.CompareRev == null)
            throw new ArgumentException("--compare-print requires --compare-rev");
        if (opts.CompareRev != null)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(opts.CompareRev, "^[0-9]{1,10}$"))
                throw new ArgumentException($"--compare-rev {opts.CompareRev}: not a revision number");
            if (!WriterJournal.IsIdent(opts.Proc!))
                throw new ArgumentException($"--proc {opts.Proc}: not an identifier");
            var excluded = new[] { "--pattern", "--exclude", "--bench", "--list", "--junit", "--parallel",
                                   "--regenerate-capture-tables", "--print-capture-ddl", "--sweep-writer-journal" }
                .Where(seen.Contains).ToList();
            if (excluded.Count > 0)
                throw new ArgumentException($"--compare-rev runs alone; drop {string.Join(", ", excluded)}");
        }
        if (positional.Count < 2)
            throw new ArgumentException("missing <database> and/or <server/profile>");
        opts.Database = positional[0];
        opts.Server   = positional[1];
        return opts;
    }
}
