using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace ibsCompiler
{
    /// <summary>
    /// Port of F4.8 compile_actions.cs.
    /// BCP-based actions compilation using managed bulk copy.
    /// </summary>
    public static class compile_actions_main
    {
        public static void Run(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor)
        {
            // Build options
            var myOptions = new Options(cmdvars, profile, true);
            if (!myOptions.GenerateOptionFiles()) return;

            ibs_compiler_common.WriteLine("Starting compile_actions...", cmdvars.OutFile);
            var actHeader = ibs_compiler_common.GetPath_Actions(cmdvars, profile);
            var actDetail = ibs_compiler_common.GetPath_ActionsDetail(profile);

            if (!File.Exists(actHeader))
            {
                ibs_compiler_common.WriteLine("Action file missing! (" + actHeader + ")", cmdvars.OutFile);
                return;
            }
            if (!File.Exists(actDetail))
            {
                ibs_compiler_common.WriteLine("Action Detail file missing! (" + actDetail + ")", cmdvars.OutFile);
                return;
            }

            var tempPath = ibs_compiler_common.GetTempPath();

            // Build temp files for bulk copy
            ibs_compiler_common.WriteLine("Line Extraction of actions started at " + DateTime.Now, cmdvars.OutFile);
            using (var source = new StreamReader(actHeader))
            using (var dest = new StreamWriter(Path.Combine(tempPath, "actions.tmp"), false))
            {
                string? line;
                int i = 0;
                while ((line = source.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length > 1 && (line.StartsWith("&") || line.Substring(0, 2) == ":>"))
                    {
                        line = myOptions.ReplaceOptions(line);
                        if (line.Trim().Substring(0, 2) == ":>")
                        {
                            i++;
                            dest.WriteLine(i + "\t" + line);
                        }
                    }
                }
            }

            using (var source = new StreamReader(actDetail))
            using (var dest = new StreamWriter(Path.Combine(tempPath, "actions_dtl.tmp"), false))
            {
                string? line;
                while ((line = source.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length > 2 && (line.StartsWith("&") || line.Substring(0, 2) == ":>"))
                    {
                        line = myOptions.ReplaceOptions(line);
                        if (line.Trim().Substring(0, 2) == ":>")
                        {
                            var t = line.Trim();
                            dest.WriteLine(t.Substring(2, 4) + "\t" + t.Substring(7, 3) + "\t" + t.Substring(11, 3) + "\t" + t.Substring(15, 5) + "\t" + t.Substring(21, 3) + "\t" + t.Substring(24));
                        }
                    }
                }
            }

            // Extract database from resolved work table name (e.g., "ibswrk..w#actions" → "ibswrk")
            var resolvedWorkTable = myOptions.ReplaceOptions("&w#actions&");
            if (resolvedWorkTable.Contains(".."))
                cmdvars.Database = resolvedWorkTable.Split(new[] { ".." }, 2, StringSplitOptions.None)[0];

            // Delete work tables
            ibs_compiler_common.WriteLine("Deletion of work tables started at " + DateTime.Now, cmdvars.OutFile);
            cmdvars.Command = myOptions.ReplaceOptions("truncate table &w#actions&");
            isqlline_main.Run(cmdvars, profile, executor, myOptions);

            cmdvars.Command = myOptions.ReplaceOptions("truncate table &w#actions_dtl&");
            var result = isqlline_main.Run(cmdvars, profile, executor, myOptions);
            if (!result.Returncode) return;

            // Bulk copy actions
            ibs_compiler_common.WriteLine("Bulk copy started at " + DateTime.Now, cmdvars.OutFile);
            ibs_compiler_common.WriteLine("Starting actions insert...", cmdvars.OutFile);
            result = executor.BulkCopy(myOptions.ReplaceOptions("&w#actions&"), BcpDirection.IN, Path.Combine(tempPath, "actions.tmp"));
            if (!result.Returncode) return;

            ibs_compiler_common.WriteLine("Starting actions detail insert...", cmdvars.OutFile);
            result = executor.BulkCopy(myOptions.ReplaceOptions("&w#actions_dtl&"), BcpDirection.IN, Path.Combine(tempPath, "actions_dtl.tmp"));
            if (!result.Returncode) return;

            // Compile actions via stored procedure
            ibs_compiler_common.WriteLine("Insert of actions in database tables started at " + DateTime.Now, cmdvars.OutFile);
            cmdvars.Command = myOptions.ReplaceOptions("&dbpro&..ba_compile_actions");
            ibs_compiler_common.WriteLine("Executing " + cmdvars.Command + "...", cmdvars.OutFile);
            result = isqlline_main.Run(cmdvars, profile, executor, myOptions);
            if (!result.Returncode) return;

            ibs_compiler_common.WriteLine("Compiling ended at " + DateTime.Now, cmdvars.OutFile);
        }
    }
}
