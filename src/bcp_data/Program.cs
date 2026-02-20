using ibsCompiler;
using ibsCompiler.Configuration;
using ibsCompiler.Database;

if (!VersionCheck.CheckForUpdates("bcp_data", args)) return 0;

var arguments = args.ToList();
var profileMgr = new ProfileManager();

var cmdvars = ibs_compiler_common.bcp_data_variables(arguments, profileMgr);
if (string.IsNullOrEmpty(cmdvars.Server))
{
    Console.Error.WriteLine("Usage: bcp_data <IN|OUT> <server/profile> [-U user] [-P pass] [-O outfile] [-MSSQL|-SYBASE]");
    return 1;
}

if (!profileMgr.ValidateProfile(cmdvars.Server)) return 1;
var profile = profileMgr.Resolve(cmdvars);
using var executor = SqlExecutorFactory.Create(profile);
// bcp_data runs BulkCopy based on direction
Console.Error.WriteLine("bcp_data: Not yet fully implemented in .NET 8.");
return 1;
