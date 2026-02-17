using ibsCompiler;
using ibsCompiler.Configuration;
using ibsCompiler.Database;

if (!VersionCheck.CheckForUpdates("runsql", args)) return 0;

var arguments = args.ToList();
var profileMgr = new ProfileManager();

var cmdvars = ibs_compiler_common.isql_variables(arguments, profileMgr);
if (string.IsNullOrEmpty(cmdvars.Server))
{
    Console.Error.WriteLine("Usage: runsql <script> <database> <server/profile> [-U user] [-P pass] [-O outfile] [-e] [-F first] [-L last] [--changelog] [--preview] [-MSSQL|-SYBASE]");
    return 1;
}

var profile = profileMgr.Resolve(cmdvars);
using var executor = SqlExecutorFactory.Create(profile);
var runsql = new runsql_main();
var success = runsql.Run(cmdvars, profile, executor);
return success ? 0 : 1;
