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
        "                [-U user] [-P pass]";

    public static Options? Parse(string[] argv)
    {
        var opts = new Options();
        var positional = new List<string>();

        for (int i = 0; i < argv.Length; i++)
        {
            var a = argv[i];
            string Next(string flag) =>
                i + 1 < argv.Length ? argv[++i] : throw new ArgumentException($"{flag} requires a value");

            switch (a)
            {
                case "--pattern":                    opts.Pattern                 = Next(a); break;
                case "--exclude":                    opts.Exclude                 = Next(a); break;
                case "--parallel":                   opts.Parallel                = int.Parse(Next(a)); break;
                case "--timeout":                    opts.TimeoutSeconds          = int.Parse(Next(a)); break;
                case "--junit":                      opts.JunitPath               = Next(a); break;
                case "--list":                       opts.ListOnly                = true; break;
                case "--verbose":                    opts.Verbose                 = true; break;
                case "--regenerate-capture-tables":  opts.RegenerateCaptureTables = true; break;
                case "--print-capture-ddl":          opts.PrintCaptureDdl         = true; break;
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

        if (positional.Count < 2)
            throw new ArgumentException("missing <database> and/or <server/profile>");
        opts.Database = positional[0];
        opts.Server   = positional[1];
        return opts;
    }
}
