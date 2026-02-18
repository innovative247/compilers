using ibsCompiler;
using ibsCompiler.Configuration;
using ibsCompiler.Database;

if (!VersionCheck.CheckForUpdates("runcreate", args)) return 0;

var arguments = args.ToList();
var profileMgr = new ProfileManager();

var cmdvars = ibs_compiler_common.runcreate_variables(arguments, profileMgr);
cmdvars.CommandName = "runcreate";
if (string.IsNullOrEmpty(cmdvars.Server))
{
    Console.Error.WriteLine("Usage: runcreate <script> <server/profile> [-U user] [-P pass] [-e] [-O outfile | outfile]");
    return 1;
}

if (!string.IsNullOrEmpty(cmdvars.OutFile))
    ibs_compiler_common.DefaultOutFile = cmdvars.OutFile;

var profile = profileMgr.Resolve(cmdvars);
using var executor = SqlExecutorFactory.Create(profile);
var runcreate = new runcreate_main();
var success = runcreate.Run(cmdvars, profile, executor);
return success ? 0 : 1;
