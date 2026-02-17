using ibsCompiler;
using ibsCompiler.Configuration;
using ibsCompiler.Database;

if (!VersionCheck.CheckForUpdates("isqlline", args)) return 0;

var arguments = args.ToList();

// Extract -t/--timer before common argument parsing
int timer = 0;
for (int i = 0; i < arguments.Count; i++)
{
    if ((arguments[i] == "-t" || arguments[i] == "--timer") && i + 1 < arguments.Count)
    {
        timer = int.Parse(arguments[i + 1]);
        arguments.RemoveAt(i);
        arguments.RemoveAt(i);
        break;
    }
    if (arguments[i].StartsWith("-t") && int.TryParse(arguments[i].Substring(2), out var t))
    {
        timer = t;
        arguments.RemoveAt(i);
        break;
    }
}

var profileMgr = new ProfileManager();

var cmdvars = ibs_compiler_common.isql_variables(arguments, profileMgr);
if (string.IsNullOrEmpty(cmdvars.Server))
{
    Console.Error.WriteLine("Usage: isqlline <command> <database> <server/profile> [-U user] [-P pass] [-O outfile] [-e] [-t seconds] [--changelog] [--preview] [-MSSQL|-SYBASE]");
    return 1;
}

var profile = profileMgr.Resolve(cmdvars);
using var executor = SqlExecutorFactory.Create(profile);

ExecReturn result;
do
{
    result = isqlline_main.Run(cmdvars, profile, executor);
    if (timer > 0)
    {
        Console.WriteLine();
        Thread.Sleep(timer * 1000);
    }
} while (timer > 0);

return result.Returncode ? 0 : 1;
