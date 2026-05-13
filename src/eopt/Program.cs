using ibsCompiler;
using ibsCompiler.Configuration;
using ibsCompiler.Database;

const string Usage = "Usage: eopt <server/profile> [-U user] [-P pass] [-O outfile] [-MSSQL|-SYBASE]";
if (!VersionCheck.CheckForUpdates("eopt", args, Usage)) return 0;

var arguments = args.ToList();
var profileMgr = new ProfileManager();

// Snapshot args for the headless CLI dispatcher (RunSetOptions), then strip the
// long flags so compile_variables' positional-server fallback isn't fooled by
// e.g. "--dynamic" appearing as the last argument.
var headlessArgs = arguments.ToList();
CliArgs.StripLongFlags(arguments, InteractiveMenus.SetOptionsBoolFlagNames);

var cmdvars = ibs_compiler_common.compile_variables(arguments, profileMgr);
if (string.IsNullOrEmpty(cmdvars.Server))
{
    Console.Error.WriteLine(Usage);
    return 1;
}

if (!profileMgr.ValidateProfile(cmdvars.Server)) return 1;
var profile = profileMgr.Resolve(cmdvars);
using var executor = SqlExecutorFactory.Create(profile);
return InteractiveMenus.RunSetOptions(cmdvars, profile, executor, headlessArgs);
