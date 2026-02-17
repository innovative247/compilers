using System.Text.RegularExpressions;
using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace ibsCompiler
{
    /// <summary>
    /// Port of F4.8 runsql.cs.
    /// Reads a SQL script file, replaces option placeholders, and executes via ADO.NET.
    /// Supports sequence loops (-F/-L) and preview mode.
    /// Replaces: read file → write temp → exec_process(isql/sqlcmd)
    /// With:     read file → replace options in memory → executor.ExecuteSql()
    /// </summary>
    public class runsql_main
    {
        private static readonly Regex ExitRegex = new(@"^\s*(exit|quit)\s*$", RegexOptions.IgnoreCase);

        public bool Run(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor,
                        bool logFile = true, Options? existingOptions = null)
        {
            bool returncode = true;

            ibs_compiler_common.OutputToStdErr = cmdvars.Preview;

            if (!ibs_compiler_common.ValidateSeqFirstLast(cmdvars))
            {
                ibs_compiler_common.WriteLine("Error: -F and -L commands are not valid.", cmdvars.OutFile);
                return false;
            }

            // Build options (skip in raw mode)
            Options? myOptions = null;
            if (!profile.RawMode)
            {
                if (existingOptions == null)
                {
                    myOptions = new Options(cmdvars, profile);
                    if (!myOptions.GenerateOptionFiles()) return false;
                }
                else
                {
                    myOptions = existingOptions;
                }
            }

            // Check for existence of source file
            if (!ibs_compiler_common.FindFile(ref cmdvars.Command))
            {
                ibs_compiler_common.WriteLine(cmdvars.Command + " not found.", cmdvars.OutFile);
                return false;
            }

            // Sequence loop
            while (cmdvars.SeqFirst <= cmdvars.SeqLast)
            {
                if (cmdvars.SeqLast < 1)
                    ibs_compiler_common.WriteLine($"Running: {cmdvars.Command} on {cmdvars.ServerNameOnly}.{cmdvars.Database}", cmdvars.OutFile);
                else
                    ibs_compiler_common.WriteLine($"Running {cmdvars.SeqFirst} of {cmdvars.SeqLast}: {cmdvars.Command} on {cmdvars.ServerNameOnly}.{cmdvars.Database}", cmdvars.OutFile);

                // Build SQL text in memory
                var sqlLines = new List<string>();
                int changelogLineCount = 0;

                // Add changelog lines (skip in raw mode)
                if (!profile.RawMode)
                {
                    foreach (var l in change_log.lines(cmdvars, profile))
                    {
                        sqlLines.Add(myOptions!.ReplaceOptions(l, cmdvars.SeqFirst));
                        changelogLineCount++;
                    }
                }

                // Read source file and replace options (stop at exit/quit client commands)
                using (var source = new StreamReader(cmdvars.Command))
                {
                    string? line;
                    while ((line = source.ReadLine()) != null)
                    {
                        if (ExitRegex.IsMatch(line)) break;
                        sqlLines.Add(profile.RawMode ? line : myOptions!.ReplaceOptions(line, cmdvars.SeqFirst));
                    }
                }
                sqlLines.Add("go");

                var sqlText = string.Join(Environment.NewLine, sqlLines);

                if (cmdvars.Preview)
                {
                    // Preview mode: write compiled SQL to stdout
                    Console.Write(sqlText);
                }
                else
                {
                    // Echo mode: print each line with line numbers before execution
                    if (cmdvars.EchoInput)
                    {
                        var echoLines = sqlText.Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.None);
                        for (int i = 0; i < echoLines.Length; i++)
                        {
                            // Insert blank line between changelog and file content
                            if (i == changelogLineCount && changelogLineCount > 0)
                            {
                                if (!string.IsNullOrEmpty(cmdvars.OutFile))
                                    ibs_compiler_common.WriteLineToDisk(cmdvars.OutFile, "");
                                else
                                    Console.WriteLine();
                            }

                            var echoLine = $"{i + 1}> {echoLines[i]}";
                            if (!string.IsNullOrEmpty(cmdvars.OutFile))
                                ibs_compiler_common.WriteLineToDisk(cmdvars.OutFile, echoLine);
                            else
                                Console.WriteLine(echoLine);
                        }
                    }

                    // Execute via ADO.NET
                    var result = executor.ExecuteSql(sqlText, cmdvars.Database, false, cmdvars.OutFile);
                    if (!result.Returncode)
                    {
                        ibs_compiler_common.WriteLine("ERROR! Failed to Run.", cmdvars.OutFile);
                        returncode = false;
                    }
                }

                cmdvars.SeqFirst++;
            }

            return returncode;
        }
    }
}
