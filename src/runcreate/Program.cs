using ibsCompiler;
using ibsCompiler.Configuration;
using ibsCompiler.Database;

if (!VersionCheck.CheckForUpdates("runcreate", args)) return 0;

// --bg / -bg: re-launch self as independent background process and return immediately
if (args.Any(a => a.Equals("--bg", StringComparison.OrdinalIgnoreCase) || a.Equals("-bg", StringComparison.OrdinalIgnoreCase)))
{
    var filteredArgs = args
        .Where(a => !a.Equals("--bg", StringComparison.OrdinalIgnoreCase) && !a.Equals("-bg", StringComparison.OrdinalIgnoreCase))
        .Append("--bg-quiet");
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = Environment.ProcessPath ?? "runcreate",
        Arguments = string.Join(" ", filteredArgs.Select(QuoteIfNeeded)),
        WorkingDirectory = Environment.CurrentDirectory,
        UseShellExecute = false,
        CreateNoWindow = false
    };
    var bgProc = System.Diagnostics.Process.Start(psi)!;
    Console.WriteLine($"[{bgProc.Id}] runcreate started in background");
    return 0;
}

// --bg-quiet: running as background child — suppress console output (all output goes to file)
// Also ignore Ctrl+C so iwatch can be stopped without killing the background job
if (args.Any(a => a.Equals("--bg-quiet", StringComparison.OrdinalIgnoreCase)))
{
    args = args.Where(a => !a.Equals("--bg-quiet", StringComparison.OrdinalIgnoreCase)).ToArray();
    Console.SetOut(TextWriter.Null);
    Console.SetError(TextWriter.Null);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; };
}

var arguments = args.ToList();
var profileMgr = new ProfileManager();

var cmdvars = ibs_compiler_common.runcreate_variables(arguments, profileMgr);
cmdvars.CommandName = "runcreate";
if (string.IsNullOrEmpty(cmdvars.Server))
{
    Console.Error.WriteLine("Usage: runcreate <script> <server/profile> [-U user] [-P pass] [-e] [-O outfile | outfile] [-bg]");
    return 1;
}

if (!string.IsNullOrEmpty(cmdvars.OutFile))
    ibs_compiler_common.DefaultOutFile = cmdvars.OutFile;
if (!string.IsNullOrEmpty(cmdvars.ErrFile))
    ibs_compiler_common.DefaultErrFile = cmdvars.ErrFile;

if (!profileMgr.ValidateProfile(cmdvars.Server)) return 1;
var profile = profileMgr.Resolve(cmdvars);
using var executor = SqlExecutorFactory.Create(profile);
var runcreate = new runcreate_main();
var success = runcreate.Run(cmdvars, profile, executor);
return success ? 0 : 1;

static string QuoteIfNeeded(string arg)
{
    if (arg.StartsWith('"') || !arg.Contains(' '))
        return arg;
    return $"\"{arg}\"";
}
