using ibsCompiler;
using ibsCompiler.Configuration;
using ibsCompiler.Database;

const string Usage = "Usage: set_messages <server/profile> [-U user] [-P pass] [-O outfile] [-MSSQL|-SYBASE]\n"
    + "       set_messages <server/profile> --add --type <ibs|gui|sql|sqr|jam> --group <grp> --text <msg> [--lang N] [--cmpy N] [--upd-flg C] [--dry-run]";
if (!VersionCheck.CheckForUpdates("set_messages", args, Usage)) return 0;

var arguments = args.ToList();
var profileMgr = new ProfileManager();

// Snapshot args so RunSetMessages can inspect long-flag actions; then strip the
// long flags before compile_variables so they don't poison the positional fallback.
var headlessArgs = arguments.ToList();
CliArgs.StripLongFlags(arguments, InteractiveMenus.SetMessagesBoolFlagNames);

var cmdvars = ibs_compiler_common.compile_variables(arguments, profileMgr);
if (string.IsNullOrEmpty(cmdvars.Server))
{
    Console.Error.WriteLine(Usage);
    return 1;
}

if (!profileMgr.ValidateProfile(cmdvars.Server)) return 1;
var profile = profileMgr.Resolve(cmdvars);
using var executor = SqlExecutorFactory.Create(profile);
return InteractiveMenus.RunSetMessages(cmdvars, profile, executor, headlessArgs);
