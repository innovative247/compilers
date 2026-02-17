using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace ibsCompiler
{
    /// <summary>
    /// Port of F4.8 compile_options.cs.
    /// Imports options from flat files into database via managed bulk copy.
    /// </summary>
    public static class compile_options_main
    {
        public static ExecReturn Run(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor)
        {
            var result = new ExecReturn { Returncode = false, Output = "" };

            var myOptions = new Options(cmdvars, profile, true);
            if (!myOptions.GenerateOptionFiles()) return result;

            var optFileSQL = ibs_compiler_common.GetPath_OptionsSQL(cmdvars, profile);
            var optFileCompany = ibs_compiler_common.GetPath_OptionsCompany(profile);
            var optFileServer = ibs_compiler_common.GetPath_OptionsServer(cmdvars, profile);
            var optFileFinal = ibs_compiler_common.GetTempFile();

            if (!File.Exists(optFileCompany))
            {
                ibs_compiler_common.WriteLine("Company Option File Missing! " + optFileCompany, cmdvars.OutFile);
                return result;
            }
            if (!File.Exists(optFileServer))
                ibs_compiler_common.WriteLine("Warning! Server Option File Missing! " + optFileServer, cmdvars.OutFile);

            ibs_compiler_common.WriteLine("Import of options started at " + DateTime.Now, cmdvars.OutFile);

            List<string> tmpOptFileSQL = new();
            if (File.Exists(optFileSQL))
            {
                ibs_compiler_common.WriteLine("Processing SQL Option File (" + optFileSQL + ")...", cmdvars.OutFile);
                tmpOptFileSQL = ibs_compiler_common.GenerateCompileOptionFile(optFileSQL);
            }

            var tmpOptFileCompany = ibs_compiler_common.GenerateImportOptionFile(optFileCompany);
            var tmpOptFileServer = ibs_compiler_common.GenerateImportOptionFile(optFileServer);

            List<string> arrOptions;
            if (tmpOptFileSQL.Count > 0)
                arrOptions = ibs_compiler_common.CombineSQLSrvOptionFiles(tmpOptFileSQL, tmpOptFileCompany, tmpOptFileServer);
            else
                arrOptions = ibs_compiler_common.CombineOptionFiles(tmpOptFileCompany, tmpOptFileServer);

            ibs_compiler_common.SaveArrayToDisk(arrOptions, optFileFinal);

            cmdvars.Database = "master";
            cmdvars.Command = myOptions.ReplaceOptions("delete &w#options&");
            ibs_compiler_common.WriteLine("Executing " + cmdvars.Command, cmdvars.OutFile);
            isqlline_main.Run(cmdvars, profile, executor, myOptions);

            ibs_compiler_common.WriteLine("Starting options insert...", cmdvars.OutFile);
            executor.BulkCopy(myOptions.ReplaceOptions("&w#options&"), BcpDirection.IN, optFileFinal);

            cmdvars.Command = myOptions.ReplaceOptions("exec &dbpro&..i_import_options");
            ibs_compiler_common.WriteLine("Executing " + cmdvars.Command + "...", cmdvars.OutFile);
            isqlline_main.Run(cmdvars, profile, executor, myOptions);

            try { File.Delete(optFileFinal); } catch { }
            ibs_compiler_common.WriteLine("temporary option file deleted", cmdvars.OutFile);
            ibs_compiler_common.WriteLine("Import of options ended at " + DateTime.Now, cmdvars.OutFile);

            // Also compile table_locations (options may have changed database mappings)
            ibs_compiler_common.WriteLine("\nCompiling table_locations...", cmdvars.OutFile);
            var tlResult = compile_table_locations_main.Run(cmdvars, profile, executor);
            if (!tlResult.Returncode)
                ibs_compiler_common.WriteLine("Warning: table_locations compile failed", cmdvars.OutFile);

            return result;
        }
    }
}
