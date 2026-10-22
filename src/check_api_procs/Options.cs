namespace CheckApiProcs;

public class Options
{
    public string Url { get; set; } = "";
    public string? Declaration { get; set; }
    public List<string> Procs { get; } = new();
    public bool Json { get; set; }
    public bool Quiet { get; set; }
    public int Concurrency { get; set; } = 8;
    public int TimeoutSeconds { get; set; } = 30;
    public bool Record { get; set; }
    public string? Names { get; set; }
    public string? Consumer { get; set; }
    public string? Release { get; set; }
    public string? Out { get; set; }
    public string User { get; private set; } = "";
    public string Password { get; private set; } = "";

    public const string Usage =
        "Usage: check_api_procs <engine-url> --declaration <api3-procs.json>\n" +
        "                [--proc <name>]...     (check only these declared procs)\n" +
        "                [--json]               (machine-readable report)\n" +
        "                [--quiet]              (<proc> <verdict> lines only)\n" +
        "                [--concurrency <n>]    (default: 8)\n" +
        "                [--timeout <sec>]      (per request; default: 30)\n" +
        "       check_api_procs <engine-url> --record --names <hand-kept api3-procs.json>\n" +
        "                --consumer <name> --release <version> --out <path>\n" +
        "Credentials: env SBN_USERNAME / SBN_PASSWORD.\n" +
        "Exit: 0 pass (warnings allowed), 1 failing verdict or record failure, 2 usage or bad declaration, 3 connectivity/auth/list.";

    /// <summary>Null when help was asked for; ArgumentException on a usage error.</summary>
    public static Options? Parse(string[] argv)
    {
        var opts = new Options();
        for (int i = 0; i < argv.Length; i++)
        {
            var a = argv[i];
            string Next(string flag) =>
                i + 1 < argv.Length ? argv[++i] : throw new ArgumentException($"{flag} requires a value");
            int NextInt(string flag) =>
                int.TryParse(Next(flag), out var n) && n > 0 ? n : throw new ArgumentException($"{flag} requires a positive integer");

            switch (a)
            {
                case "--declaration": opts.Declaration = Next(a); break;
                case "--proc":        opts.Procs.Add(Next(a)); break;
                case "--json":        opts.Json = true; break;
                case "--quiet":       opts.Quiet = true; break;
                case "--concurrency": opts.Concurrency = NextInt(a); break;
                case "--timeout":     opts.TimeoutSeconds = NextInt(a); break;
                case "--record":      opts.Record = true; break;
                case "--names":       opts.Names = Next(a); break;
                case "--consumer":    opts.Consumer = Next(a); break;
                case "--release":     opts.Release = Next(a); break;
                case "--out":         opts.Out = Next(a); break;
                case "-h": case "--help": return null;
                default:
                    if (a.StartsWith('-')) throw new ArgumentException($"unknown argument '{a}'");
                    // Not echoed: the redactor does not exist yet and the value may hold credentials.
                    if (opts.Url != "") throw new ArgumentException("unexpected extra argument");
                    opts.Url = a;
                    break;
            }
        }

        if (opts.Url == "") throw new ArgumentException("<engine-url> is required");
        if (!Uri.TryCreate(opts.Url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
            throw new ArgumentException("<engine-url> is not an http(s) URL");
        // R16: POST /auth carries the password, so plain http only to this machine.
        if (u.Scheme == "http" && !u.IsLoopback)
            throw new ArgumentException("<engine-url> must be https (http only for localhost)");

        if (opts.Record)
        {
            if (opts.Declaration != null) throw new ArgumentException("--declaration is check mode only; record reads --names");
            if (opts.Procs.Count > 0) throw new ArgumentException("--proc is check mode only");
            if (opts.Json || opts.Quiet) throw new ArgumentException("--json and --quiet are check mode only");
            if (opts.Names == null) throw new ArgumentException("--record requires --names");
            if (string.IsNullOrWhiteSpace(opts.Consumer)) throw new ArgumentException("--record requires --consumer");
            if (string.IsNullOrWhiteSpace(opts.Release)) throw new ArgumentException("--record requires --release");
            if (opts.Out == null) throw new ArgumentException("--record requires --out");
        }
        else
        {
            if (opts.Names != null || opts.Consumer != null || opts.Release != null || opts.Out != null)
                throw new ArgumentException("--names, --consumer, --release and --out require --record");
            if (opts.Declaration == null) throw new ArgumentException("--declaration is required");
            if (opts.Json && opts.Quiet) throw new ArgumentException("--json and --quiet cannot be combined");
        }
        return opts;
    }

    /// <summary>R15: credentials from env only; ArgumentException when either is missing.</summary>
    public void ResolveCredentials(Func<string, string?> env)
    {
        User = env("SBN_USERNAME") ?? "";
        Password = env("SBN_PASSWORD") ?? "";
        if (User == "" || Password == "")
            throw new ArgumentException("credentials required: env SBN_USERNAME and SBN_PASSWORD");
    }
}
