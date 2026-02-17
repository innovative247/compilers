using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace ibsCompiler
{
    /// <summary>
    /// Port of F4.8 compile_required_fields.cs.
    /// Required fields compilation via managed bulk copy.
    /// </summary>
    public static class compile_required_fields_main
    {
        public static void Run(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor)
        {
            var myOptions = new Options(cmdvars, profile);
            if (!myOptions.GenerateOptionFiles()) return;

            ibs_compiler_common.WriteLine("Starting compile_required_fields...", cmdvars.OutFile);

            var mainDir = ibs_compiler_common.GetPath_Setup(profile);
            var mainMes = Path.Combine(mainDir, "css");
            var serverName = profile.IsProfile ? profile.ProfileName : cmdvars.ServerNameOnly;
            var bupMes = Path.Combine(ibs_compiler_common.GetPath_MessageBackup(profile), serverName + "_css");
            long time = DateTime.Now.Ticks;

            if (!File.Exists(mainMes + ".required_fields"))
            {
                ibs_compiler_common.WriteLine($"Required Fields import file is missing ({mainMes}.required_fields)", cmdvars.OutFile);
                return;
            }
            if (!File.Exists(mainMes + ".required_fields_dtl"))
            {
                ibs_compiler_common.WriteLine($"Required Fields Detail import file is missing ({mainMes}.required_fields_dtl)", cmdvars.OutFile);
                return;
            }

            // Backup
            ibs_compiler_common.WriteLine("Making backup files for existing required fields...", cmdvars.OutFile);
            executor.BulkCopy(myOptions.ReplaceOptions("&i_required_fields&"), BcpDirection.OUT, bupMes + ".i_required_fields." + time);
            executor.BulkCopy(myOptions.ReplaceOptions("&i_required_fields_dtl&"), BcpDirection.OUT, bupMes + ".i_required_fields_dtl." + time);

            // Extract database from resolved work table reference
            var resolvedDbWrk = myOptions.ReplaceOptions("&dbwrk&");
            if (!string.IsNullOrEmpty(resolvedDbWrk))
                cmdvars.Database = resolvedDbWrk;

            // Clear temp tables
            ibs_compiler_common.WriteLine("Clearing Required Fields Tables...", cmdvars.OutFile);
            cmdvars.Command = myOptions.ReplaceOptions("delete &dbwrk&..w#i_required_fields");
            isqlline_main.Run(cmdvars, profile, executor, myOptions);

            cmdvars.Command = myOptions.ReplaceOptions("delete &dbwrk&..w#i_required_fields_dtl");
            isqlline_main.Run(cmdvars, profile, executor, myOptions);

            // Import - create temp files with proper line endings
            ibs_compiler_common.WriteLine("Starting required fields insert...", cmdvars.OutFile);
            var bcpFile = BuildTempFileForBcp(mainMes + ".required_fields", "css.required_fields.tmp");
            var result = executor.BulkCopy(myOptions.ReplaceOptions("&w#i_required_fields&"), BcpDirection.IN, bcpFile);
            try { File.Delete(bcpFile); } catch { }
            if (!result.Returncode) return;

            ibs_compiler_common.WriteLine("Starting required fields detail insert...", cmdvars.OutFile);
            var bcpFileDtl = BuildTempFileForBcp(mainMes + ".required_fields_dtl", "css.required_fields_dtl.tmp");
            result = executor.BulkCopy(myOptions.ReplaceOptions("&w#i_required_fields_dtl&"), BcpDirection.IN, bcpFileDtl);
            try { File.Delete(bcpFileDtl); } catch { }
            if (!result.Returncode) return;

            // Compile
            ibs_compiler_common.WriteLine("Installing Required Fields...", cmdvars.OutFile);
            cmdvars.Command = myOptions.ReplaceOptions("&dbpro&..i_required_fields_install");
            isqlline_main.Run(cmdvars, profile, executor, myOptions);

            ibs_compiler_common.WriteLine("compile_required_fields DONE.", cmdvars.OutFile);
        }

        private static string BuildTempFileForBcp(string inputFile, string outputName)
        {
            var destFile = Path.Combine(ibs_compiler_common.GetTempPath(), outputName);
            using var source = new StreamReader(inputFile);
            using var dest = new StreamWriter(destFile, false);
            string? line;
            while ((line = source.ReadLine()) != null)
                dest.WriteLine(line);
            return destFile;
        }
    }
}
