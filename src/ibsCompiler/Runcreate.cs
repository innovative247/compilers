using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace ibsCompiler
{
    /// <summary>
    /// Port of F4.8 runcreate.cs.
    /// Parses create files and dispatches to runsql/runcreate/i_run_upgrade/compile_* based on line type.
    /// </summary>
    public class runcreate_main
    {
        public bool Run(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor, bool logFile = true,
                        Options? existingOptions = null)
        {
            // Build options once at top level; reuse for nested calls
            Options? myOptions = null;
            if (existingOptions != null)
            {
                myOptions = existingOptions;
            }
            else if (!profile.RawMode)
            {
                myOptions = new Options(cmdvars, profile);
                if (!myOptions.GenerateOptionFiles()) return false;

                // Execute changelog once at top level
                if (cmdvars.ChangeLog)
                {
                    var chgDb = cmdvars.Database;
                    if (string.IsNullOrEmpty(chgDb))
                        chgDb = myOptions.ReplaceOptions("&dbpro&");

                    if (!string.IsNullOrEmpty(chgDb))
                    {
                        var chgLines = new List<string>();
                        foreach (var l in change_log.lines(cmdvars, profile))
                            chgLines.Add(myOptions.ReplaceOptions(l, cmdvars.SeqFirst));
                        chgLines.Add("go");
                        var chgSql = string.Join(Environment.NewLine, chgLines);
                        executor.ExecuteSql(chgSql, chgDb, false, cmdvars.OutFile);
                    }
                }
            }

            // Check for existence of files
            if (!ibs_compiler_common.FindFile(ref cmdvars.Command))
            {
                ibs_compiler_common.WriteLine(cmdvars.Command + " not found.", cmdvars.OutFile);
                return false;
            }

            var scriptName = cmdvars.Command;
            ibs_compiler_common.WriteLine(scriptName + " started", cmdvars.OutFile);
            var startTime = DateTime.Now;

            using var sourceFile = new StreamReader(cmdvars.Command);
            string? line;
            while ((line = sourceFile.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.StartsWith("#NT")) line = line.Substring(3).Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                var newVars = cmdvars;

                // Check if line is based on an option
                if (myOptions != null && line.StartsWith("&"))
                {
                    int j = line.IndexOf("&", 1);
                    var optValue = line.Substring(0, j + 1);
                    line = line.Replace(optValue, myOptions.ReplaceWord(optValue).Trim()).Trim();
                }

                // Extract line type
                var spaceIdx = line.IndexOf(" ");
                if (spaceIdx < 0) continue;
                var strType = line.Substring(0, spaceIdx);
                line = line.Substring(spaceIdx).Trim();

                // Replace basic elements
                line = line.Replace("\t", " ");
                line = line.Replace("$1", "");
                line = line.Replace("-o", "");

                // Extract -F -L flags
                if (line.Contains("-F"))
                {
                    var fIdx = line.IndexOf("-F");
                    var irIdx = line.IndexOf("$ir");
                    if (irIdx > fIdx)
                    {
                        newVars.Command = line.Substring(fIdx, irIdx - fIdx).Trim();
                        line = line.Replace(newVars.Command, " ").Trim();
                        try
                        {
                            var lIdx = newVars.Command.IndexOf("-L");
                            newVars.SeqFirst = Convert.ToInt32(newVars.Command.Substring(2, lIdx - 2).Trim());
                            newVars.SeqLast = Convert.ToInt32(newVars.Command.Substring(lIdx + 2).Trim());
                        }
                        catch
                        {
                            newVars.SeqFirst = -1;
                            newVars.SeqLast = -1;
                        }
                    }
                }

                // Replace $ir with IRPath
                if (line.Contains("$ir"))
                {
                    var irIdx = line.IndexOf("$ir");
                    var spIdx = line.IndexOf(" ", irIdx);
                    if (spIdx > -1)
                        newVars.Command = line.Substring(0, spIdx).Trim();
                    else
                        newVars.Command = line.Substring(irIdx).Trim();
                    line = line.Replace(newVars.Command, "").Trim();
                    newVars.Command = newVars.Command
                        .Replace("$ir", profile.IRPath)
                        .Replace(">", Path.DirectorySeparatorChar.ToString())
                        .Trim();
                }

                // Remove server references
                line = line.Replace("-S&sv&", "").Replace("-S &sv&", "").Replace("&sv&", "").Trim();

                // Dispatch based on line type
                // Changelog and options already handled at top level — skip in child calls
                if (strType == "runsql")
                {
                    var databases = line.Replace("-d", "-D").Replace("-D", "|").Replace(" ", "").Trim().Split('|');
                    var uniqDb = new List<string>();
                    foreach (var mydb in databases)
                    {
                        var resolvedDb = myOptions != null ? myOptions.ReplaceOptions(mydb) : mydb;
                        if (mydb != "" && !uniqDb.Contains(resolvedDb))
                        {
                            uniqDb.Add(resolvedDb);
                            newVars.Database = resolvedDb;
                            newVars.ChangeLog = false;
                            var runsql = new runsql_main();
                            runsql.Run(newVars, profile, executor, true, myOptions);
                        }
                    }
                }
                else if (strType == "runcreate")
                {
                    var runcreate = new runcreate_main();
                    runcreate.Run(newVars, profile, executor, true, myOptions);
                }
                else if (strType == "i_run_upgrade")
                {
                    var sctIdx = line.IndexOf("sct_");
                    if (sctIdx >= 0)
                    {
                        newVars.Command = line.Substring(sctIdx).Trim();
                        newVars.Upgrade_no = line.Replace(newVars.Command, "").Replace("&dbsta&", "").Trim();
                        var iupgrade = new i_run_upgrade_main();
                        iupgrade.Run(newVars, profile, executor, myOptions);
                    }
                }
                else if (strType == "import_options")
                {
                    if (!profile.RawMode) compile_options_main.Run(cmdvars, profile, executor);
                }
                else if (strType == "create_tbl_locations")
                {
                    if (!profile.RawMode) compile_table_locations_main.Run(newVars, profile, executor);
                }
                else if (strType == "install_msg")
                {
                    if (!profile.RawMode) compile_msg_main.Run(newVars, profile, executor, batch: true);
                }
                else if (strType == "compile_actions")
                {
                    if (!profile.RawMode) compile_actions_main.Run(cmdvars, profile, executor);
                }
                else if (strType == "install_required_fields")
                {
                    if (!profile.RawMode) compile_required_fields_main.Run(cmdvars, profile, executor);
                }
            }

            var elapsed = DateTime.Now - startTime;
            ibs_compiler_common.WriteLine($"{scriptName} total time {elapsed:hh\\:mm\\:ss}", cmdvars.OutFile);
            return true;
        }
    }
}
