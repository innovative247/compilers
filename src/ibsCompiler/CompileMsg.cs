using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace ibsCompiler
{
    /// <summary>
    /// Port of F4.8 compile_msg.cs.
    /// Message compilation with multiple BCP table operations and backups.
    /// </summary>
    public static class compile_msg_main
    {
        public static void Run(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor, bool batch = false)
        {
            var myOptions = new Options(cmdvars, profile, true);
            if (!myOptions.GenerateOptionFiles()) return;

            ibs_compiler_common.WriteLine("Starting compile_msg...", cmdvars.OutFile);

            // Production safety guard — prevent message import into GONZO
            var serverName = profile.IsProfile ? profile.ProfileName : cmdvars.ServerNameOnly;
            if (profile.Company == "101" &&
                serverName.Equals("GONZO", StringComparison.OrdinalIgnoreCase))
            {
                ibs_compiler_common.WriteLine($"Cannot install messages on {serverName}", cmdvars.OutFile);
                return;
            }

            var mainDir = ibs_compiler_common.GetPath_Setup(profile);
            var mainMes = Path.Combine(mainDir, "css");
            var bupMes = Path.Combine(ibs_compiler_common.GetPath_MessageBackup(profile), serverName + "_css");

            // Check all required message files
            ibs_compiler_common.WriteLine($"Source files: {mainMes}.*", cmdvars.OutFile);
            string[] extensions = { ".ibs_msg", ".ibs_msgrp", ".jam_msg", ".jam_msgrp", ".sqr_msg", ".sqr_msgrp", ".sql_msg", ".sql_msgrp", ".gui_msg", ".gui_msgrp" };
            string[] labels = { "IBS Messages", "IBS Message Group", "JAM Messages", "JAM Message Group", "SQR Messages", "SQR Message Group", "SQL Messages", "SQL Message Group", "GUI Messages", "GUI Message Group" };
            for (int i = 0; i < extensions.Length; i++)
            {
                if (!File.Exists(mainMes + extensions[i]))
                {
                    ibs_compiler_common.WriteLine($"{labels[i]} file is missing ({mainMes}{extensions[i]})", cmdvars.OutFile);
                    return;
                }
            }
            ibs_compiler_common.WriteLine("All source files found.", cmdvars.OutFile);

            // Extract database from resolved work table name (e.g., "ibswrk..w#ibs_messages" → "ibswrk")
            var resolvedWorkTable = myOptions.ReplaceOptions("&w#ibs_messages&");
            if (resolvedWorkTable.Contains(".."))
                cmdvars.Database = resolvedWorkTable.Split(new[] { ".." }, 2, StringSplitOptions.None)[0];
            ibs_compiler_common.WriteLine($"Work database: {cmdvars.Database}", cmdvars.OutFile);

            // Check for prior failed compile — saved translations may exist
            bool skipSave = false;
            var saveTable = myOptions.ReplaceOptions("&gui_messages_save&");
            var countSql = $"SELECT COUNT(*) FROM {saveTable}";
            ibs_compiler_common.WriteLine($"Checking for saved translations in {saveTable}...", cmdvars.OutFile);
            var countResult = executor.ExecuteSql(countSql, cmdvars.Database, captureOutput: true);
            int savedCount = 0;
            if (countResult.Returncode && !string.IsNullOrEmpty(countResult.Output))
            {
                foreach (var line in countResult.Output.Split('\n'))
                {
                    if (int.TryParse(line.Trim(), out var n))
                    {
                        savedCount = n;
                        break;
                    }
                }
            }

            if (savedCount == 0)
            {
                ibs_compiler_common.WriteLine("No saved translations found.", cmdvars.OutFile);
            }
            else if (batch)
            {
                // Batch mode (called from runcreate) — auto-select: keep saved translations
                ibs_compiler_common.WriteLine($"WARNING: {saveTable} contains {savedCount} rows from a prior failed compile.", cmdvars.OutFile);
                ibs_compiler_common.WriteLine("Keeping existing saved translations, skipping save step.", cmdvars.OutFile);
                skipSave = true;
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine($"WARNING: {saveTable} contains {savedCount} rows.");
                Console.WriteLine("This means a previous message compile did not complete successfully.");
                Console.WriteLine("Translated messages were saved but never restored.");
                Console.WriteLine();
                Console.WriteLine("How do you want to proceed?");
                Console.WriteLine("  1) Continue, keep saved translations (compile will restore them at the end)");
                Console.WriteLine("  2) Discard saved translations, re-save from current database");
                Console.WriteLine("  3) Cancel (investigate and fix manually)");
                Console.WriteLine();

                while (true)
                {
                    Console.Write("Enter choice (1, 2, or 3): ");
                    var choice = Console.ReadLine()?.Trim() ?? "";
                    if (choice == "1")
                    {
                        ibs_compiler_common.WriteLine("Keeping existing saved translations, skipping save step.", cmdvars.OutFile);
                        skipSave = true;
                        break;
                    }
                    if (choice == "2")
                    {
                        ibs_compiler_common.WriteLine("Discarding saved translations...", cmdvars.OutFile);
                        var deleteSql = $"DELETE {saveTable}";
                        ibs_compiler_common.WriteLine($"Executing {deleteSql}", cmdvars.OutFile);
                        executor.ExecuteSql(deleteSql, cmdvars.Database);
                        break;
                    }
                    if (choice == "3")
                    {
                        Console.WriteLine("Cancelled.");
                        return;
                    }
                    Console.WriteLine("Invalid choice. Please enter 1, 2, or 3.");
                }
            }

            // Preserve translated messages
            if (!skipSave)
            {
                cmdvars.Command = myOptions.ReplaceOptions("&dbpro&..ba_compile_gui_messages_save");
                ibs_compiler_common.WriteLine($"Executing {cmdvars.Command}...", cmdvars.OutFile);
                isqlline_main.Run(cmdvars, profile, executor, myOptions);
            }

            // Backup message tables
            ibs_compiler_common.WriteLine("Making backup files for existing messages...", cmdvars.OutFile);
            long time = DateTime.Now.Ticks;

            string[] tables = { "&ibs_messages&", "&ibs_message_groups&", "&jam_messages&", "&jam_message_groups&", "&sqr_messages&", "&sqr_message_groups&", "&sql_messages&", "&sql_message_groups&", "&gui_messages&", "&gui_message_groups&", "&gui_messages_save&" };
            string[] suffixes = { ".ibs_ms.", ".ibs_mg.", ".jam_ms.", ".jam_mg.", ".sqr_ms.", ".sqr_mg.", ".sql_ms.", ".sql_mg.", ".gui_ms.", ".gui_mg.", ".gui_ms_save." };
            for (int i = 0; i < tables.Length; i++)
            {
                var resolvedTable = myOptions.ReplaceOptions(tables[i]);
                ibs_compiler_common.WriteLine($"Backing up {resolvedTable}...", cmdvars.OutFile);
                var result = executor.BulkCopy(resolvedTable, BcpDirection.OUT, bupMes + suffixes[i] + time);
                if (!result.Returncode)
                    ibs_compiler_common.WriteLine($"Warning: backup of {resolvedTable} failed, continuing...", cmdvars.OutFile);
            }

            // Clear temp message tables
            ibs_compiler_common.WriteLine("Truncating work tables...", cmdvars.OutFile);
            string[] workTables = { "&w#ibs_messages&", "&w#ibs_message_groups&", "&w#jam_messages&", "&w#jam_message_groups&", "&w#sqr_messages&", "&w#sqr_message_groups&", "&w#sql_messages&", "&w#sql_message_groups&", "&w#gui_messages&", "&w#gui_message_groups&" };
            foreach (var wt in workTables)
            {
                cmdvars.Command = myOptions.ReplaceOptions("truncate table " + wt);
                ibs_compiler_common.WriteLine($"Executing {cmdvars.Command}", cmdvars.OutFile);
                isqlline_main.Run(cmdvars, profile, executor, myOptions);
            }

            // BCP flat files into database
            string[] msgTypes = { "ibs_msg", "ibs_msgrp", "jam_msg", "jam_msgrp", "sqr_msg", "sqr_msgrp", "sql_msg", "sql_msgrp", "gui_msg", "gui_msgrp" };
            string[] destTables = { "&w#ibs_messages&", "&w#ibs_message_groups&", "&w#jam_messages&", "&w#jam_message_groups&", "&w#sqr_messages&", "&w#sqr_message_groups&", "&w#sql_messages&", "&w#sql_message_groups&", "&w#gui_messages&", "&w#gui_message_groups&" };
            string[] insertLabels = { "IBS messages", "IBS message groups", "JAM messages", "JAM message groups", "SQR messages", "SQR message groups", "SQL messages", "SQL message groups", "GUI messages", "GUI message groups" };
            for (int i = 0; i < msgTypes.Length; i++)
            {
                ibs_compiler_common.WriteLine($"Starting {insertLabels[i]} insert...", cmdvars.OutFile);

                var tempFile = CreateTempMessageFile(mainMes + "." + msgTypes[i]);
                var result = executor.BulkCopy(myOptions.ReplaceOptions(destTables[i]), BcpDirection.IN, tempFile);
                try { File.Delete(tempFile); } catch { }
                if (!result.Returncode) return;
            }

            // Update statistics on final message tables before compile
            ibs_compiler_common.WriteLine("Updating statistics...", cmdvars.OutFile);
            var dbtbl = myOptions.ReplaceOptions("&dbtbl&");
            string[] statsTables = { "ibs_messages", "jam_messages", "sqr_messages", "sql_messages", "gui_messages" };
            foreach (var tbl in statsTables)
            {
                cmdvars.Command = $"update statistics {dbtbl}..{tbl}";
                ibs_compiler_common.WriteLine($"Executing {cmdvars.Command}", cmdvars.OutFile);
                isqlline_main.Run(cmdvars, profile, executor, myOptions);
            }

            // Compile into database
            cmdvars.Command = myOptions.ReplaceOptions("&dbpro&..i_compile_messages");
            ibs_compiler_common.WriteLine($"Executing {cmdvars.Command}...", cmdvars.OutFile);
            isqlline_main.Run(cmdvars, profile, executor, myOptions);

            cmdvars.Command = myOptions.ReplaceOptions("&dbpro&..i_compile_jam_messages");
            ibs_compiler_common.WriteLine($"Executing {cmdvars.Command}...", cmdvars.OutFile);
            isqlline_main.Run(cmdvars, profile, executor, myOptions);

            cmdvars.Command = myOptions.ReplaceOptions("&dbpro&..i_compile_jrw_messages");
            ibs_compiler_common.WriteLine($"Executing {cmdvars.Command}...", cmdvars.OutFile);
            isqlline_main.Run(cmdvars, profile, executor, myOptions);

            ibs_compiler_common.WriteLine("compile_msg DONE.", cmdvars.OutFile);
        }

        private static string CreateTempMessageFile(string sourceFile)
        {
            var tempFile = ibs_compiler_common.GetTempFile();
            var lines = ibs_compiler_common.BuildArrayFromDisk(sourceFile);
            ibs_compiler_common.SaveArrayToDisk(lines, tempFile);
            return tempFile;
        }
    }
}
