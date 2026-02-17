using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace ibsCompiler
{
    /// <summary>
    /// Port of F4.8 isqlline.cs.
    /// Executes a single SQL command via ADO.NET (replaces writing to temp file + exec_process(isql/sqlcmd)).
    /// Unlike runsql, isqlline does NOT inject change log SQL (matching Unix behavior).
    /// </summary>
    public static class isqlline_main
    {
        public static ExecReturn Run(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor, bool redirectOutput = false)
        {
            var result = new ExecReturn { Output = "", Returncode = false };

            // In raw mode, skip option file processing entirely
            Options? myOptions = null;
            if (!profile.RawMode)
            {
                myOptions = new Options(cmdvars, profile);
                if (!myOptions.GenerateOptionFiles()) return result;
            }

            // Build SQL text (no changelog — isqlline is for ad-hoc commands)
            var sqlLines = new List<string>();
            string resolvedCommand;
            if (!profile.RawMode)
            {
                resolvedCommand = myOptions!.ReplaceOptions(cmdvars.Command);
            }
            else
            {
                resolvedCommand = cmdvars.Command;
            }
            sqlLines.Add(resolvedCommand);
            sqlLines.Add("go");

            var sqlText = string.Join(Environment.NewLine, sqlLines);

            // Echo mode: print command with line numbers
            if (cmdvars.EchoInput)
                EchoSql(sqlText, cmdvars.OutFile);

            // Execute via ADO.NET
            result = executor.ExecuteSql(sqlText, cmdvars.Database, redirectOutput, cmdvars.OutFile);

            if (!result.Returncode)
                ibs_compiler_common.WriteLine("ERROR! Failed to Run.", cmdvars.OutFile);

            return result;
        }

        /// <summary>
        /// Overload that accepts existing Options to avoid regenerating them.
        /// Used by runcreate and other callers that already have options built.
        /// </summary>
        public static ExecReturn Run(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor, Options? existingOptions, bool redirectOutput = false)
        {
            var result = new ExecReturn { Output = "", Returncode = false };

            var sqlLines = new List<string>();
            string resolvedCommand;
            if (!profile.RawMode)
            {
                resolvedCommand = existingOptions!.ReplaceOptions(cmdvars.Command);
            }
            else
            {
                resolvedCommand = cmdvars.Command;
            }
            sqlLines.Add(resolvedCommand);
            sqlLines.Add("go");

            var sqlText = string.Join(Environment.NewLine, sqlLines);

            // Echo mode: print command with line numbers
            if (cmdvars.EchoInput)
                EchoSql(sqlText, cmdvars.OutFile);

            result = executor.ExecuteSql(sqlText, cmdvars.Database, redirectOutput, cmdvars.OutFile);

            if (!result.Returncode)
                ibs_compiler_common.WriteLine("ERROR! Failed to Run.", cmdvars.OutFile);

            return result;
        }

        private static void EchoSql(string sqlText, string outFile)
        {
            var lines = sqlText.Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = $"{i + 1}> {lines[i]}";
                if (!string.IsNullOrEmpty(outFile))
                    ibs_compiler_common.WriteLineToDisk(outFile, line);
                else
                    Console.WriteLine(line);
            }
        }
    }
}
