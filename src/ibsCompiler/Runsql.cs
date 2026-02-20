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
        private static readonly Regex ExitRegex = new(@"^\s*(exit|quit)\b", RegexOptions.IgnoreCase);
        private static readonly Regex GoRegex = new(@"^\s*go\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        private static string[] SplitBatches(string sqlText)
        {
            var batches = new List<string>();
            var current = new System.Text.StringBuilder();
            bool inBlockComment = false;

            using var reader = new StringReader(sqlText);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                bool lineStartedInBlockComment = inBlockComment;
                char inString = '\0'; // reset per-line; tracks ' or " delimiter

                // Scan line to track /* */ block comment state.
                // Ignores /* and */ that appear inside string literals or -- comments.
                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    char next = i + 1 < line.Length ? line[i + 1] : '\0';

                    if (inBlockComment)
                    {
                        if (c == '*' && next == '/')
                        {
                            inBlockComment = false;
                            i++;
                        }
                    }
                    else if (inString != '\0')
                    {
                        if (c == inString)
                        {
                            if (next == inString) i++; // escaped quote ('')
                            else inString = '\0';
                        }
                    }
                    else
                    {
                        if (c == '/' && next == '*') { inBlockComment = true; i++; }
                        else if (c == '-' && next == '-') break; // -- comment: skip rest of line
                        else if (c == '\'' || c == '"') inString = c;
                    }
                }

                // Treat as GO separator only when not inside a block comment
                if (!lineStartedInBlockComment && GoRegex.IsMatch(line))
                {
                    var batch = current.ToString();
                    if (!string.IsNullOrWhiteSpace(batch))
                        batches.Add(batch);
                    current.Clear();
                }
                else
                {
                    current.AppendLine(line);
                }
            }

            var remaining = current.ToString();
            if (!string.IsNullOrWhiteSpace(remaining))
                batches.Add(remaining);

            return batches.ToArray();
        }

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
                        // Convert Sybase-style // line comments to MSSQL-compatible --
                        if (profile.ServerType == SQLServerTypes.MSSQL)
                        {
                            int cs = line.Length - line.TrimStart().Length;
                            if (cs < line.Length - 1 && line[cs] == '/' && line[cs + 1] == '/')
                                line = line[..cs] + "--" + line[(cs + 2)..];
                        }
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
                    // Split SQL into GO-delimited batches and execute each one
                    // on a single persistent connection (preserves USE database,
                    // temp tables, session state, etc.)
                    var batches = SplitBatches(sqlText);
                    bool anyFailed = false;

                    try
                    {
                        executor.OpenConnection(cmdvars.Database);

                        for (int batchIndex = 0; batchIndex < batches.Length; batchIndex++)
                        {
                            var batch = batches[batchIndex];
                            var trimmedBatch = batch.Trim('\r', '\n');

                            // Echo mode: print batch lines with per-batch line numbers
                            if (cmdvars.EchoInput)
                            {
                                var batchLines = trimmedBatch.Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.None);
                                for (int i = 0; i < batchLines.Length; i++)
                                {
                                    // Insert blank line between changelog and file content (first batch only)
                                    if (batchIndex == 0 && i == changelogLineCount && changelogLineCount > 0)
                                    {
                                        if (!string.IsNullOrEmpty(cmdvars.OutFile))
                                            ibs_compiler_common.WriteLineToDisk(cmdvars.OutFile, "");
                                        else
                                            Console.WriteLine();
                                    }

                                    var echoLine = $"{i + 1}> {batchLines[i]}";
                                    if (!string.IsNullOrEmpty(cmdvars.OutFile))
                                        ibs_compiler_common.WriteLineToDisk(cmdvars.OutFile, echoLine);
                                    else
                                        Console.WriteLine(echoLine);
                                }

                                // Echo the GO separator
                                var goLine = $"{batchLines.Length + 1}> go";
                                if (!string.IsNullOrEmpty(cmdvars.OutFile))
                                    ibs_compiler_common.WriteLineToDisk(cmdvars.OutFile, goLine);
                                else
                                    Console.WriteLine(goLine);
                            }

                            // Execute this batch on the persistent connection
                            var result = executor.ExecuteBatch(batch, false, cmdvars.OutFile);
                            if (result.Returncode)
                            {
                                ibs_compiler_common.WriteLine("(return status = 0)", cmdvars.OutFile);
                            }
                            else
                            {
                                anyFailed = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        ibs_compiler_common.WriteLine($"ERROR! {ex.Message}", cmdvars.OutFile);
                        anyFailed = true;
                    }
                    finally
                    {
                        executor.CloseConnection();
                    }

                    if (anyFailed)
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
