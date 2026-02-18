using System.Text.RegularExpressions;
using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace ibsCompiler
{
    /// <summary>
    /// Port of F4.8 i_run_upgrade.cs.
    /// Runs upgrade scripts with ba_upgrades_check verification.
    /// </summary>
    public class i_run_upgrade_main
    {
        private static readonly Regex RxUpgradeCheck = new(@"return[\s\r\n-]+(\d)?[\s\r\n]+");

        private static string FindUpgradeReturnValue(string sqlOutput)
        {
            var m = RxUpgradeCheck.Match(sqlOutput);
            return m.Success ? m.Groups[1].Value : "";
        }

        public bool Run(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor, Options? existingOptions = null)
        {
            var originalCommand = cmdvars.Command;

            // Build options
            Options myOptions;
            if (existingOptions == null)
            {
                myOptions = new Options(cmdvars, profile);
                if (!myOptions.GenerateOptionFiles()) return false;
            }
            else
            {
                myOptions = existingOptions;
            }

            if (!ibs_compiler_common.FindFile(ref cmdvars.Command))
            {
                ibs_compiler_common.WriteLine(cmdvars.Command + " not found.", cmdvars.OutFile);
                return false;
            }
            ibs_compiler_common.WriteLine($"Upgrade script: {cmdvars.Command}", cmdvars.OutFile);

            // Execute changelog once at top level
            if (cmdvars.ChangeLog && !profile.RawMode)
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
                cmdvars.ChangeLog = false; // Don't log again for inner runsql calls
            }

            // Check if upgrade has already been run
            cmdvars.Command = myOptions.ReplaceOptions($"&dbpro&..ba_upgrades_check '{cmdvars.Upgrade_no}'");
            ibs_compiler_common.WriteLine($"Executing {cmdvars.Command}...", cmdvars.OutFile);
            var result = isqlline_main.Run(cmdvars, profile, executor, myOptions, redirectOutput: true);

            var upgradeControlValue = FindUpgradeReturnValue(result.Output);
            if (upgradeControlValue == "1")
            {
                ibs_compiler_common.WriteLine(myOptions.ReplaceOptions("Upgrade control table &upgrades& does not exist or control record missing on server."), cmdvars.OutFile);
                return false;
            }
            else if (upgradeControlValue == "2")
            {
                ibs_compiler_common.WriteLine($"Upgrade {cmdvars.Upgrade_no} has already been run!!!", cmdvars.OutFile);
                return false;
            }

            // Loop through upgrade file
            ibs_compiler_common.WriteLine($"Processing upgrade file: {originalCommand}", cmdvars.OutFile);
            using var sourceFile = new StreamReader(originalCommand);
            string? line;
            var batchLines = new List<string>();
            while ((line = sourceFile.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                if (line.StartsWith("use "))
                {
                    var resolvedDb = myOptions.ReplaceOptions(line.Replace("use ", ""));
                    ibs_compiler_common.WriteLine($"Switching database to {resolvedDb}", cmdvars.OutFile);
                    cmdvars.Database = resolvedDb;
                }
                else if (line.Contains("runsql"))
                {
                    cmdvars.Command = line.Replace("runsql", "").Replace("!", "").Replace("&db&", "").Replace("&sv&", "").Trim();
                    GetSeq(ref cmdvars);
                    ibs_compiler_common.WriteLine("Running " + cmdvars.Command, cmdvars.OutFile);
                    var runsql = new runsql_main();
                    runsql.Run(cmdvars, profile, executor, true, myOptions);
                    cmdvars.SeqFirst = -1;
                    cmdvars.SeqLast = -1;
                }
                else if (line.Contains("sp_renametoold"))
                {
                    cmdvars.Command = line;
                    ibs_compiler_common.WriteLine($"Executing {myOptions.ReplaceOptions(line)}", cmdvars.OutFile);
                    isqlline_main.Run(cmdvars, profile, executor, myOptions);
                }
                else if (line.StartsWith("go"))
                {
                    batchLines.Add(line);
                    if (batchLines.Count > 1)
                    {
                        // Write batch to temp file and execute via runsql
                        var tempFile = ibs_compiler_common.GetTempFile();
                        File.WriteAllLines(tempFile, batchLines);
                        cmdvars.Command = tempFile;
                        ibs_compiler_common.WriteLine($"Executing SQL batch ({batchLines.Count - 1} lines)...", cmdvars.OutFile);
                        var runsql = new runsql_main();
                        runsql.Run(cmdvars, profile, executor, false, myOptions);
                        try { File.Delete(tempFile); } catch { }
                    }
                    batchLines.Clear();
                }
                else
                {
                    batchLines.Add(line);
                }
            }

            // Set upgrade end time
            cmdvars.Command = myOptions.ReplaceOptions($"update &upgrades& set end_tm=datediff(ss,'800101',getdate()) where upgrade_no='{cmdvars.Upgrade_no}' and ix=0 and opc=1");
            ibs_compiler_common.WriteLine($"Executing {cmdvars.Command}", cmdvars.OutFile);
            isqlline_main.Run(cmdvars, profile, executor, myOptions, redirectOutput: true);

            ibs_compiler_common.WriteLine($"Upgrade {cmdvars.Upgrade_no} DONE.", cmdvars.OutFile);
            return true;
        }

        private void GetSeq(ref CommandVariables cmd)
        {
            if (cmd.Command.Contains("-F"))
            {
                cmd.Command = cmd.Command.Replace("-F", "");
                try
                {
                    var lIdx = cmd.Command.IndexOf("-L");
                    cmd.SeqFirst = Convert.ToInt16(cmd.Command.Substring(0, lIdx).Trim());
                    cmd.Command = cmd.Command.Substring(lIdx);
                }
                catch
                {
                    cmd.SeqFirst = -1;
                }
            }
            if (cmd.Command.Contains("-L"))
            {
                cmd.Command = cmd.Command.Replace("-L", "");
                string compare = "";
                if (cmd.Command.IndexOf("pro_") > -1) compare = "pro_";
                else if (cmd.Command.IndexOf("tbl_") > -1) compare = "tbl_";
                if (!string.IsNullOrEmpty(compare))
                {
                    cmd.SeqLast = Convert.ToInt16(cmd.Command.Substring(0, cmd.Command.IndexOf(compare)).Trim());
                    cmd.Command = cmd.Command.Substring(cmd.Command.IndexOf(compare));
                }
            }
        }
    }
}
