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
                // No changelog of its own: as on legacy, each runsql it dispatches logs its script.
            }

            // Check for existence of files
            var scriptName = cmdvars.Command;
            if (!ibs_compiler_common.FindFile(ref cmdvars.Command))
            {
                ibs_compiler_common.WriteLine("Can't find create script: " + scriptName, cmdvars.OutFile);
                return false;
            }

            // Legacy runcreate's banner: the script and server as given, the outfile pair (empty
            // when output goes to the console), and the date as Unix date prints it.
            var (ou1, ou2) = string.IsNullOrEmpty(cmdvars.OutFileArg) ? ("", "") : (cmdvars.OutFileArg + ".out", cmdvars.OutFileArg + ".err");
            var target = $"{scriptName}  {cmdvars.ServerNameOnly}  >{ou1} {ou2}";
            WriteBanner($">>>>>>> RUNCREATE  {target}", cmdvars.OutFile);

            // Verify connection before processing - fail fast instead of logging errors for every line
            try
            {
                var testResult = executor.ExecuteSql("SELECT 1", profile.AdminDatabase, captureOutput: true);
                if (!testResult.Returncode)
                {
                    ibs_compiler_common.WriteLine("ERROR: Connection failed. " + testResult.Output?.Trim(), cmdvars.OutFile);
                    return false;
                }
            }
            catch (Exception ex)
            {
                ibs_compiler_common.WriteLine("ERROR: Connection failed. " + ex.Message, cmdvars.OutFile);
                return false;
            }

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

                // Dispatch based on line type. Options are built once at the top level; each
                // runsql logs its own script, as the legacy runsql calls did.
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
                            newVars.CommandName = "runsql";
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
                    // `i_run_upgrade <database> &sv& <upgrade_no> <script>` (&sv& already removed)
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        newVars.Database = myOptions != null ? myOptions.ReplaceOptions(parts[0]).Trim() : parts[0];
                        newVars.Upgrade_no = parts[^2];
                        newVars.Command = parts[^1];
                        newVars.CommandName = "i_run_upgrade";
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

            WriteBanner($"<<<<<<< END RUNCREATE  {target}", cmdvars.OutFile);
            // Legacy runcreate always left an empty .err beside the .out (see runcreate_variables).
            if (!string.IsNullOrEmpty(cmdvars.OutFileArg))
            {
                var errFile = Path.ChangeExtension(cmdvars.OutFile, ".err");
                if (!File.Exists(errFile))
                    try { File.WriteAllText(errFile, ""); } catch { }
            }
            return true;
        }

        private static void WriteBanner(string line, string outFile)
        {
            ibs_compiler_common.WriteLine(" ", outFile);
            ibs_compiler_common.WriteLine(line, outFile);
            ibs_compiler_common.WriteLine(UnixDate(DateTime.Now), outFile);
            ibs_compiler_common.WriteLine(" ", outFile);
        }

        /// <summary>The time as Unix <c>date</c> prints it: <c>Wed Oct 21 15:28:41 CDT 2026</c>.</summary>
        internal static string UnixDate(DateTime now)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return $"{now.ToString("ddd MMM", inv)} {now.Day,2} {now.ToString("HH:mm:ss", inv)} {ZoneAbbreviation(now)} {now.ToString("yyyy", inv)}";
        }

        // "Central Daylight Time" -> "CDT"; a name that is already an abbreviation stays as it is.
        private static string ZoneAbbreviation(DateTime now)
        {
            var tz = TimeZoneInfo.Local;
            if (tz.BaseUtcOffset == TimeSpan.Zero && !tz.SupportsDaylightSavingTime) return "UTC";
            var name = tz.IsDaylightSavingTime(now) ? tz.DaylightName : tz.StandardName;
            if (string.IsNullOrWhiteSpace(name)) return "UTC";
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2 || name.Any(char.IsDigit)) return name;
            return string.Concat(words.Select(w => char.ToUpperInvariant(w[0])));
        }
    }
}
