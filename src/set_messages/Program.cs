using ibsCompiler;
using ibsCompiler.Configuration;
using ibsCompiler.Database;

if (!VersionCheck.CheckForUpdates("set_messages", args)) return 0;

var arguments = args.ToList();
var profileMgr = new ProfileManager();

var cmdvars = ibs_compiler_common.compile_variables(arguments, profileMgr);
if (string.IsNullOrEmpty(cmdvars.Server))
{
    Console.Error.WriteLine("Usage: set_messages <server/profile> [-U user] [-P pass] [-O outfile] [-MSSQL|-SYBASE]");
    return 1;
}

var profile = profileMgr.Resolve(cmdvars);
using var executor = SqlExecutorFactory.Create(profile);
return InteractiveMenus.RunSetMessages(cmdvars, profile, executor);
