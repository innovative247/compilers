using ibsCompiler;
using ibsCompiler.Configuration;
using ibsCompiler.Database;

const string Usage = "Usage: compile_msg <server/profile> [-U user] [-P pass] [-O outfile] [-MSSQL|-SYBASE]";
if (!VersionCheck.CheckForUpdates("compile_msg", args, Usage)) return 0;

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

var profile = profileMgr.Resolve(cmdvars);
using var executor = SqlExecutorFactory.Create(profile);
// compile_msg keeps the legacy Import/Export/Add interactive menu; only
// set_messages routes the interactive path through the file-first MessageBrowser.
return InteractiveMenus.RunSetMessages(cmdvars, profile, executor, headlessArgs, legacyInteractive: true);
