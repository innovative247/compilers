using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace ibsCompiler
{
    /// <summary>
    /// Port of F4.8 compile_options.cs.
    /// Imports options from flat files into the database: by default through the live path
    /// (keyed diff, LiveCompile), or with --reload through bulk copy and i_import_options.
    /// </summary>
    public static class compile_options_main
    {
        /// <param name="live">Null takes the defaults (LiveCompile.DefaultMode). Returncode is true when
        /// the live readback matched, or when the reload's delete, bulk copy and i_import_options succeeded.</param>
        public static ExecReturn Run(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor, LiveSettings? live = null)
        {
            live ??= new LiveSettings();
            var result = new ExecReturn { Returncode = false, Output = "" };

            var myOptions = new Options(cmdvars, profile, true);
            if (!myOptions.GenerateOptionFiles()) return result;
            myOptions.ReportResolvedOptionsPath();

            var optFileSQL = ibs_compiler_common.GetPath_OptionsSQL(cmdvars, profile);
            var optFileCompany = ibs_compiler_common.GetPath_OptionsCompany(profile);
            var optFileServer = ibs_compiler_common.GetPath_OptionsServer(cmdvars, profile);

            // Named rather than random (GetTempFile) so the file is findable while the import
            // is running; the PID keeps parallel compiles off each other's input file.
            var optFileFinal = Path.Combine(ibs_compiler_common.GetTempPath(),
                $"options.import.{profile.Company}.{Environment.ProcessId}.tmp");

            if (!File.Exists(optFileCompany))
            {
                ibs_compiler_common.WriteLine("Company Option File Missing! " + optFileCompany, cmdvars.OutFile);
                return result;
            }
            if (!File.Exists(optFileServer))
                ibs_compiler_common.WriteLine("Warning! Server Option File Missing! " + optFileServer, cmdvars.OutFile);

            // Changelog entry
            void ChangeLog()
            {
                var chgDb = myOptions.ReplaceOptions("&dbpro&");
                if (chgDb == "&dbpro&") return;
                var chgLines = new List<string>();
                foreach (var l in change_log.compileLines("OPTIONS", "updated options", profile.ServerType))
                    chgLines.Add(myOptions.ReplaceOptions(l));
                chgLines.Add("go");
                executor.ExecuteSql(string.Join(Environment.NewLine, chgLines), chgDb, false, cmdvars.OutFile);
            }
            if (live.Mode == LiveMode.Reload) ChangeLog();

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

            if (live.Mode == LiveMode.Live)
                return RunLive(cmdvars, profile, executor, myOptions, arrOptions, live, ChangeLog);

            ibs_compiler_common.SaveArrayToDisk(arrOptions, optFileFinal);
            ibs_compiler_common.WriteLine("options import file: " + optFileFinal, cmdvars.OutFile);

            cmdvars.Database = "master";
            cmdvars.Command = myOptions.ReplaceOptions("delete &w#options&");
            ibs_compiler_common.WriteLine("Executing " + cmdvars.Command, cmdvars.OutFile);
            bool ok = isqlline_main.Run(cmdvars, profile, executor, myOptions).Returncode;

            ibs_compiler_common.WriteLine("Starting options insert...", cmdvars.OutFile);
            ok &= executor.BulkCopy(myOptions.ReplaceOptions("&w#options&"), BcpDirection.IN, optFileFinal).Returncode;

            cmdvars.Command = myOptions.ReplaceOptions("exec &dbpro&..i_import_options");
            ibs_compiler_common.WriteLine("Executing " + cmdvars.Command + "...", cmdvars.OutFile);
            ok &= isqlline_main.Run(cmdvars, profile, executor, myOptions).Returncode;

            try { File.Delete(optFileFinal); } catch { }
            ibs_compiler_common.WriteLine("deleting options import file: " + optFileFinal, cmdvars.OutFile);
            ibs_compiler_common.WriteLine("Import of options ended at " + DateTime.Now, cmdvars.OutFile);

            // Also compile table_locations (options may have changed database mappings)
            ibs_compiler_common.WriteLine("\nCompiling table_locations...", cmdvars.OutFile);
            var tlResult = compile_table_locations_main.Run(cmdvars, profile, executor);
            if (!tlResult.Returncode)
                ibs_compiler_common.WriteLine("Warning: table_locations compile failed", cmdvars.OutFile);

            result.Returncode = ok;
            return result;
        }

        private static ExecReturn RunLive(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor,
                                          Options myOptions, List<string> arrOptions, LiveSettings live, Action changeLog)
        {
            var d = profile.ServerType;
            var warnings = new List<string>();
            var rows = LiveOptions.TargetRows(arrOptions, d, warnings);
            foreach (var w in warnings) ibs_compiler_common.WriteLine("Warning: " + w, cmdvars.OutFile);
            // i_import_options returns without touching &options& when w#options is empty.
            if (rows.Count == 0)
            {
                ibs_compiler_common.WriteLine("Warning: no option lines to import; options left as they are", cmdvars.OutFile);
                return new ExecReturn { Returncode = true, Output = "" };
            }

            var planName = $"options.{(profile.IsProfile ? profile.ProfileName : cmdvars.ServerNameOnly)}.{profile.Company}";
            var rc = LiveCompile.Run("options", new[] { LiveOptions.Target(rows) }, live, executor, d,
                "master", s => myOptions.ReplaceOptions(s), cmdvars.OutFile, planName, changeLog);
            ibs_compiler_common.WriteLine("Import of options ended at " + DateTime.Now, cmdvars.OutFile);
            // No table_locations recompile here: it truncates ibs..table_locations (--reload only).
            return new ExecReturn { Returncode = rc == 0, Output = "" };
        }
    }
}
