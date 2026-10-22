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
        private static readonly Regex SingleBangRunsql = new(@"^!runsql\s");

        // keepBlank: also return batches holding only blank lines - isql -e echoes them (as "1> ")
        // though it sends nothing to the server.
        private static string[] SplitBatches(string sqlText, bool trackDollar, bool keepBlank = false)
        {
            var batches = new List<string>();
            var current = new System.Text.StringBuilder();
            bool inBlockComment = false;
            // POSTGRES only: a `go`/`exit` line inside an open dollar-quoted body ($$…$$) is
            // function content, not a batch terminator (SR 52910). null on SYBASE/MSSQL keeps
            // their splitting byte-identical.
            var dollar = trackDollar ? new ibs_compiler_common.PgDollarQuoteTracker() : null;

            using var reader = new StringReader(sqlText);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                // isql OS-command escape: a line beginning with `!!` (column 0, like
                // isql) runs the rest of the line as a shell command at this point in
                // the script — legacy runsql piped into isql, which honored these
                // in-stream (e.g. `!!runsql tbl_dealers.sql &db& &sv&`, options already
                // substituted). Emit it as its own pseudo-batch so the batch loop can
                // dispatch it to the shell instead of the server. isql runs the command
                // as soon as the line is read WITHOUT flushing its batch buffer — SQL
                // accumulated since the last `go` still runs at the NEXT `go`, after
                // the command — so the buffer is left untouched here.
                if (!inBlockComment && !(dollar?.InDollarBody ?? false) && line.StartsWith("!!"))
                {
                    batches.Add(line);
                    continue;
                }
                // One upgrade script (sct_07.84.24634_bef.sql) runs its table script as `!runsql`;
                // the F4.8 compilers ran it, so a single-! runsql line is a shell escape too.
                if (!inBlockComment && !(dollar?.InDollarBody ?? false) && SingleBangRunsql.IsMatch(line))
                {
                    batches.Add("!" + line);
                    continue;
                }

                bool lineStartedInBlockComment = inBlockComment;
                bool inDollarBody = dollar?.InDollarBody ?? false;
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

                // Treat as GO/exit separator only when not inside a block comment AND not inside
                // an open dollar-quoted body (the latter POSTGRES-only, via `dollar`).
                if (!lineStartedInBlockComment && !inDollarBody && GoRegex.IsMatch(line))
                {
                    var batch = current.ToString();
                    if (!string.IsNullOrWhiteSpace(batch) || (keepBlank && batch.Length > 0))
                        batches.Add(batch);
                    current.Clear();
                }
                else if (!lineStartedInBlockComment && !inDollarBody && ExitRegex.IsMatch(line))
                {
                    // exit/quit: flush current batch and stop — same effect as go + end of file
                    var batch = current.ToString();
                    if (!string.IsNullOrWhiteSpace(batch) || (keepBlank && batch.Length > 0))
                        batches.Add(batch);
                    break;
                }
                else
                {
                    current.AppendLine(line);
                }

                dollar?.Consume(line);
            }

            var remaining = current.ToString();
            if (!string.IsNullOrWhiteSpace(remaining) || (keepBlank && remaining.Length > 0))
                batches.Add(remaining);

            return batches.ToArray();
        }

        /// <summary>
        /// Executes an isql `!!` OS-command escape through the platform shell
        /// (cmd on Windows, sh elsewhere), streaming its stdout/stderr to the
        /// normal output sink. Returns true when the command exits 0.
        /// </summary>
        private static bool RunShellEscape(string command, string outFile)
        {
            if (string.IsNullOrWhiteSpace(command)) return true;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                // A nested `!!runsql` must run this install's runsql, not whichever is first on PATH.
                psi.Environment["PATH"] = AppContext.BaseDirectory + Path.PathSeparator +
                                          (Environment.GetEnvironmentVariable("PATH") ?? "");
                if (OperatingSystem.IsWindows())
                {
                    psi.FileName = "cmd.exe";
                    psi.Arguments = "/c " + command;
                }
                else
                {
                    psi.FileName = "/bin/sh";
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add(command);
                }
                using var proc = System.Diagnostics.Process.Start(psi)!;
                proc.OutputDataReceived += (_, e) => { if (e.Data != null) ibs_compiler_common.WriteLine(e.Data, outFile); };
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) ibs_compiler_common.WriteLine(e.Data, outFile); };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();
                // isql's own line once the command has run
                ibs_compiler_common.WriteLine($"[sh:{command}]", outFile);
                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                ibs_compiler_common.WriteLine($"ERROR! !!{command}: {ex.Message}", outFile);
                return false;
            }
        }

        private static void WriteEcho(string line, string outFile)
        {
            if (!string.IsNullOrEmpty(outFile))
                ibs_compiler_common.WriteLineToDisk(outFile, line);
            else
                Console.WriteLine(line);
        }

        // A batch legacy runsql sent ahead of the script: echoed under -e, output shown, and a
        // failure here does not fail the user's run. run: false echoes it only; capture: true runs
        // it with its output discarded.
        private static void RunBookkeeping(ISqlExecutor executor, string batch, CommandVariables cmdvars,
                                           bool run = true, bool capture = false)
        {
            if (cmdvars.EchoInput)
                foreach (var line in IsqlFormatter.EchoLines(batch))
                    WriteEcho(line, cmdvars.OutFile);
            if (run) executor.ExecuteBatch(batch, capture, cmdvars.OutFile);
        }

        public bool Run(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor,
                        bool logFile = true, Options? existingOptions = null)
        {
            bool returncode = true;
            // Output follows legacy runsql + isql on Sybase, on every platform
            // (docs/architecture.md, "isql output format").
            bool isPostgres = profile.ServerType == SQLServerTypes.POSTGRES;

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
                // Legacy runsql named the script by its full path.
                ibs_compiler_common.WriteLine(Path.GetFullPath(cmdvars.Command) + " not found.", cmdvars.OutFile);
                return false;
            }

            // Sequence loop
            var seqStart = cmdvars.SeqFirst;
            while (cmdvars.SeqFirst <= cmdvars.SeqLast)
            {
                var runStart = DateTime.Now;
                string runningMsg;
                {
                    // Legacy runsql's own line: full script path, server, "in <db>", the sequence
                    // number when -F/-L is given, and a trailing space.
                    var dbtxt = string.IsNullOrEmpty(cmdvars.Database) ? "" : $"in {cmdvars.Database}";
                    var seq = cmdvars.SeqLast < 1 ? "" : $" {cmdvars.SeqFirst}";
                    runningMsg = $"Running {Path.GetFullPath(cmdvars.Command)} on {cmdvars.ServerNameOnly} {dbtxt}{seq} ";
                }
                ibs_compiler_common.WriteLine(runningMsg, cmdvars.OutFile);

                // Build the changelog (audit) SQL separately from the user's script.
                // The changelog is internal bookkeeping; it must NOT share the user's
                // diagnostic output stream. When the session has `set showplan on`
                // (or `set statistics io on`, etc.) active, running the changelog's
                // `if exists` guards in-stream makes ASE emit a query plan for that
                // overhead — which the user mistakes for their own statement's plan.
                // We run it below with its output discarded. (Skip in raw mode.)
                // Legacy runsql logged a -F/-L run once, ahead of its first sequence.
                var changelogLines = new List<string>();
                if (!profile.RawMode && cmdvars.SeqFirst == seqStart)
                {
                    foreach (var l in change_log.lines(cmdvars, profile))
                        changelogLines.Add(myOptions!.ReplaceOptions(l, cmdvars.SeqFirst));
                }

                // Read source file and replace options (stop at exit/quit client commands).
                // POSTGRES only: a bare `exit`/`quit` line INSIDE an open dollar-quoted body is a
                // plpgsql loop statement, not a client directive — honoring it there truncates the
                // function body and surfaces as "unterminated dollar-quoted string" (SR 52910).
                var userLines = new List<string>();
                bool exited = false;
                var readDollar = profile.ServerType == SQLServerTypes.POSTGRES
                    ? new ibs_compiler_common.PgDollarQuoteTracker() : null;
                using (var source = new StreamReader(cmdvars.Command))
                {
                    string? line;
                    while ((line = source.ReadLine()) != null)
                    {
                        if (!(readDollar?.InDollarBody ?? false) && ExitRegex.IsMatch(line))
                        {
                            // Treat exit as an implicit go — flush accumulated content as a batch
                            userLines.Add("go");
                            exited = true;
                            break;
                        }
                        // Convert Sybase-style // line comments to MSSQL/PostgreSQL-compatible --
                        if (profile.ServerType == SQLServerTypes.MSSQL || profile.ServerType == SQLServerTypes.POSTGRES)
                        {
                            int cs = line.Length - line.TrimStart().Length;
                            if (cs < line.Length - 1 && line[cs] == '/' && line[cs + 1] == '/')
                                line = line[..cs] + "--" + line[(cs + 2)..];
                        }
                        if (!profile.RawMode)
                        {
                            // Legacy substitute_options also replaced &db& and &sv&, e.g. in an
                            // upgrade script's `!!runsql tbl_x.sql &db& &sv&`.
                            line = myOptions!.ReplaceOptions(line, cmdvars.SeqFirst).Replace("&sv&", cmdvars.Server);
                            if (!string.IsNullOrEmpty(cmdvars.Database)) line = line.Replace("&db&", cmdvars.Database);
                        }
                        userLines.Add(line);
                        readDollar?.Consume(line);
                    }
                }
                // Legacy runsql ends the script with an empty line and a go, so isql -e echoes a
                // final "N> " line; the blank batch itself runs nothing. Its -F/-L form had no
                // empty line.
                if (!exited && cmdvars.SeqLast < 1) userLines.Add("");
                userLines.Add("go");

                var changelogText = string.Join(Environment.NewLine, changelogLines);
                var userText = string.Join(Environment.NewLine, userLines);

                if (cmdvars.Preview)
                {
                    // Preview mode: write compiled SQL to stdout (changelog + user script,
                    // exactly what would run on the server).
                    if (!string.IsNullOrWhiteSpace(changelogText))
                    {
                        Console.Write(changelogText);
                        Console.Write(Environment.NewLine);
                    }
                    Console.Write(userText);
                }
                else
                {
                    // Split the user's script into GO-delimited batches and execute each
                    // on a single persistent connection (preserves USE database, temp
                    // tables, session state, etc.)
                    var batches = SplitBatches(userText, profile.ServerType == SQLServerTypes.POSTGRES, keepBlank: true);
                    bool anyFailed = false;
                    var errorOutput = new System.Text.StringBuilder();

                    try
                    {
                        executor.OpenConnection(cmdvars.Database);

                        // Run the changelog as bookkeeping with its output discarded, so
                        // session diagnostics (showplan / statistics io) never report on
                        // runsql's own overhead. Best-effort: a changelog failure does not
                        // fail the user's run (the `if exists` guards already make it safe).
                        //
                        // Legacy runsql piped "use <db>" and the changelog into isql ahead of the
                        // script, so their echo and output (the changelog proc's return status) are
                        // part of what it printed. The connection is fresh here, so no session
                        // diagnostics can be on yet. POSTGRES: the connection already sits in the
                        // database/schema (a `use` naming the connection database would point the
                        // search_path at a schema that is not there), and the changelog's DO block
                        // prints nothing legacy would - so both are echoed only / run silently.
                        if (!string.IsNullOrEmpty(cmdvars.Database))
                            RunBookkeeping(executor, $"use {cmdvars.Database}" + Environment.NewLine, cmdvars, run: !isPostgres);
                        foreach (var clBatch in SplitBatches(changelogText, isPostgres))
                            RunBookkeeping(executor, clBatch, cmdvars, capture: isPostgres);

                        for (int batchIndex = 0; batchIndex < batches.Length; batchIndex++)
                        {
                            var batch = batches[batchIndex];

                            // Shell pseudo-batch from a `!!` line — run it on the OS,
                            // not the server. A failed command fails the run, matching
                            // how server errors are treated (isql itself ignored the
                            // exit code, but silently swallowing a failed nested
                            // runsql hides real breakage).
                            if (batch.StartsWith("!!"))
                            {
                                if (cmdvars.EchoInput)
                                    ibs_compiler_common.WriteLine("1> " + batch, cmdvars.OutFile);
                                if (!RunShellEscape(batch.Substring(2).Trim(), cmdvars.OutFile))
                                    anyFailed = true;
                                continue;
                            }

                            var trimmedBatch = batch.Trim('\r', '\n');

                            // Echo mode (isql -e): batch lines numbered from 1, go not echoed
                            if (cmdvars.EchoInput)
                            {
                                foreach (var echoLine in IsqlFormatter.EchoLines(batch))
                                    WriteEcho(echoLine, cmdvars.OutFile);
                            }

                            if (string.IsNullOrWhiteSpace(batch)) continue;

                            // Execute this batch on the persistent connection
                            var result = executor.ExecuteBatch(batch, false, cmdvars.OutFile);
                            // isql prints a return status only after a procedure - the executor does that.
                            if (!result.Returncode)
                            {
                                anyFailed = true;
                                if (!string.IsNullOrWhiteSpace(result.Output))
                                    errorOutput.AppendLine(result.Output.TrimEnd('\r', '\n'));
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
                        if (!string.IsNullOrEmpty(cmdvars.ErrFile))
                        {
                            var runElapsed = DateTime.Now - runStart;
                            ibs_compiler_common.WriteLineToDisk(cmdvars.ErrFile, runningMsg);
                            if (errorOutput.Length > 0)
                                ibs_compiler_common.WriteLineToDisk(cmdvars.ErrFile, errorOutput.ToString());
                            ibs_compiler_common.WriteLineToDisk(cmdvars.ErrFile, $"Elapsed: {runElapsed:hh\\:mm\\:ss}");
                            ibs_compiler_common.WriteLineToDisk(cmdvars.ErrFile, "");
                        }
                        returncode = false;
                    }
                }

                cmdvars.SeqFirst++;
            }

            return returncode;
        }
    }
}
