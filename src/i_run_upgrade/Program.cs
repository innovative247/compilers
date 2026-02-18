using ibsCompiler;
using ibsCompiler.Configuration;
using ibsCompiler.Database;

if (!VersionCheck.CheckForUpdates("i_run_upgrade", args)) return 0;

var arguments = args.ToList();
var profileMgr = new ProfileManager();

var cmdvars = ibs_compiler_common.i_run_upgrade_variables(arguments, profileMgr);
cmdvars.CommandName = "i_run_upgrade";
if (string.IsNullOrEmpty(cmdvars.Server))
{
    Console.Error.WriteLine("Usage: i_run_upgrade <server/profile> <upgrade_no> <script> [-D database] [-U user] [-P pass] [-O outfile] [-MSSQL|-SYBASE]");
    return 1;
}

var profile = profileMgr.Resolve(cmdvars);
using var executor = SqlExecutorFactory.Create(profile);
var upgrade = new i_run_upgrade_main();
var success = upgrade.Run(cmdvars, profile, executor);
return success ? 0 : 1;
