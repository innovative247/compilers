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
            // In raw mode, skip option file processing entirely
            Options? myOptions = null;
            if (!profile.RawMode)
            {
                myOptions = new Options(cmdvars, profile);
                if (!myOptions.GenerateOptionFiles()) return new ExecReturn { Output = "", Returncode = false };
            }

            // No changelog — isqlline is for ad-hoc commands
            var resolvedCommand = profile.RawMode ? cmdvars.Command : myOptions!.ReplaceOptions(cmdvars.Command);
            return Execute(cmdvars, profile, executor, resolvedCommand, redirectOutput);
        }

        /// <summary>
        /// Overload that accepts existing Options to avoid regenerating them.
        /// Used by runcreate and other callers that already have options built.
        /// </summary>
        public static ExecReturn Run(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor, Options? existingOptions, bool redirectOutput = false)
        {
            var resolvedCommand = profile.RawMode ? cmdvars.Command : existingOptions!.ReplaceOptions(cmdvars.Command);
            return Execute(cmdvars, profile, executor, resolvedCommand, redirectOutput);
        }

        private static ExecReturn Execute(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor, string resolvedCommand, bool redirectOutput)
        {
            // Every platform prints what legacy isqlline (isql) printed on Sybase
            // (docs/architecture.md, "isql output format").
            // The batch legacy isqlline hands isql: an empty line, then the command (its wrapper
            // builds "use db / go / <newline> cmd / go"). The leading line is why the server
            // reports a one-line command's errors at Line 2 - kept so messages match.
            var batch = Environment.NewLine + resolvedCommand + Environment.NewLine;
            if (cmdvars.EchoInput)
                WriteEcho(IsqlFormatter.EchoLines(batch), cmdvars.OutFile);
            return executor.ExecuteSql(batch + "go", cmdvars.Database, redirectOutput, cmdvars.OutFile);
        }

        private static void WriteEcho(IEnumerable<string> lines, string outFile)
        {
            foreach (var line in lines)
            {
                if (!string.IsNullOrEmpty(outFile))
                    ibs_compiler_common.WriteLineToDisk(outFile, line);
                else
                    Console.WriteLine(line);
            }
        }
    }
}
