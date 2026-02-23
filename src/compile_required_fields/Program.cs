using ibsCompiler;
using ibsCompiler.Configuration;
using ibsCompiler.Database;

const string Usage = "Usage: compile_required_fields <server/profile> [-U user] [-P pass] [-O outfile] [-MSSQL|-SYBASE]";
if (!VersionCheck.CheckForUpdates("compile_required_fields", args, Usage)) return 0;

var arguments = args.ToList();
var profileMgr = new ProfileManager();

var cmdvars = ibs_compiler_common.compile_variables(arguments, profileMgr);
if (string.IsNullOrEmpty(cmdvars.Server))
{
    Console.Error.WriteLine(Usage);
    return 1;
}

if (!profileMgr.ValidateProfile(cmdvars.Server)) return 1;
var profile = profileMgr.Resolve(cmdvars);
using var executor = SqlExecutorFactory.Create(profile);
compile_required_fields_main.Run(cmdvars, profile, executor);
return 0;
