using System.Globalization;
using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace ibsCompiler
{
    /// <summary>
    /// How compile_msg should resolve a pre-existing &gui_messages_save& table:
    ///   Prompt  - interactive 3-way menu (default for human use; original behavior)
    ///   Keep    - keep saved translations, skip the save step (what batch:true uses)
    ///   Discard - DELETE the save table and re-save from current DB
    ///   Cancel  - exit early, no DB changes (headless-only safety brake)
    /// </summary>
    public enum OnSavedTranslations { Prompt, Keep, Discard, Cancel }

    /// <summary>
    /// Port of F4.8 compile_msg.cs.
    /// Message compilation with multiple BCP table operations and backups.
    /// </summary>
    public static class compile_msg_main
    {
        /// <param name="live">Null takes the defaults (LiveCompile.DefaultMode). Returns 0 on success
        /// (or cancel), 1 on failure; on the live path 0 only when the readback matched.</param>
        public static int Run(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor,
                              bool batch = false,
                              OnSavedTranslations onSaved = OnSavedTranslations.Prompt,
                              LiveSettings? live = null)
        {
            live ??= new LiveSettings();
            var myOptions = new Options(cmdvars, profile, true);
            if (!myOptions.GenerateOptionFiles()) return 1;
            var d = profile.ServerType;

            void ChangeLog()
            {
                var chgDb = myOptions.ReplaceOptions("&dbpro&");
                if (chgDb == "&dbpro&") return;
                var chgLines = new List<string>();
                foreach (var l in change_log.compileLines("MESSAGES", "updated messages", d))
                    chgLines.Add(myOptions.ReplaceOptions(l));
                chgLines.Add("go");
                // The live path skips i_compile_gui_messages and i_compile_sql_messages, which write these rows themselves.
                if (live.Mode == LiveMode.Live)
                {
                    chgLines.AddRange(LiveChangeLog.MsgGuiLines("updated messages GUI").Select(myOptions.ReplaceOptions));
                    chgLines.AddRange(LiveChangeLog.MsgGuiLines("updated messages SQL", "MSG_SQL").Select(myOptions.ReplaceOptions));
                }
                executor.ExecuteSql(string.Join(Environment.NewLine, chgLines), chgDb, false, cmdvars.OutFile);
            }
            if (live.Mode == LiveMode.Reload) ChangeLog();

            ibs_compiler_common.WriteLine("Starting compile_msg...", cmdvars.OutFile);

            var serverName = profile.IsProfile ? profile.ProfileName : cmdvars.ServerNameOnly;

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
                    return 1;
                }
            }
            ibs_compiler_common.WriteLine("All source files found.", cmdvars.OutFile);

            // Extract database from resolved work table name (e.g., "ibswrk..w#ibs_messages" → "ibswrk")
            var resolvedWorkTable = myOptions.ReplaceOptions("&w#ibs_messages&");
            if (resolvedWorkTable.Contains(".."))
                cmdvars.Database = resolvedWorkTable.Split(new[] { ".." }, 2, StringSplitOptions.None)[0];
            ibs_compiler_common.WriteLine($"Work database: {cmdvars.Database}", cmdvars.OutFile);

            var saveTable = myOptions.ReplaceOptions("&gui_messages_save&");
            if (live.Mode == LiveMode.Live)
                return RunLive(cmdvars, profile, executor, myOptions, mainMes, extensions, saveTable,
                               batch, onSaved, live, ChangeLog);

            // Check for prior failed compile — saved translations may exist
            bool skipSave = false;
            var countSql = $"SELECT COUNT(*) FROM {saveTable}";
            ibs_compiler_common.WriteLine($"Checking for saved translations in {saveTable}...", cmdvars.OutFile);
            var countResult = executor.ExecuteSql(countSql, cmdvars.Database, captureOutput: true);
            int? parsedCount = null;
            if (countResult.Returncode && !string.IsNullOrEmpty(countResult.Output))
            {
                foreach (var line in countResult.Output.Split('\n'))
                {
                    if (int.TryParse(line.Trim(), out var n))
                    {
                        parsedCount = n;
                        break;
                    }
                }
            }
            // An unreadable save table must not read as empty: the destructive steps below would run blind.
            if (parsedCount == null)
            {
                Console.Error.WriteLine($"Error: could not read {saveTable}; nothing was changed. Server said: "
                    + (string.IsNullOrWhiteSpace(countResult.Output) ? "(no output)" : countResult.Output.Trim()));
                return 1;
            }
            int savedCount = parsedCount.Value;

            switch (ResolveSaved(savedCount, saveTable, batch, onSaved, cmdvars.OutFile))
            {
                case OnSavedTranslations.Keep:
                    skipSave = true;
                    break;
                case OnSavedTranslations.Discard:
                    var deleteSql = $"DELETE {saveTable}";
                    ibs_compiler_common.WriteLine($"Executing {deleteSql}", cmdvars.OutFile);
                    executor.ExecuteSql(deleteSql, cmdvars.Database);
                    break;
                case OnSavedTranslations.Cancel:
                    return 0;
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

                var tempFile = CreateTempMessageFile(mainMes + "." + msgTypes[i], cmdvars.OutFile);
                var result = executor.BulkCopy(myOptions.ReplaceOptions(destTables[i]), BcpDirection.IN, tempFile);
                try { File.Delete(tempFile); } catch { }
                if (!result.Returncode) return 1;
            }

            // Update statistics on final message tables before compile
            ibs_compiler_common.WriteLine("Updating statistics...", cmdvars.OutFile);
            var dbtbl = myOptions.ReplaceOptions("&dbtbl&");
            string[] statsTables = { "ibs_messages", "jam_messages", "sqr_messages", "sql_messages", "gui_messages" };
            foreach (var tbl in statsTables)
            {
                if (profile.ServerType == SQLServerTypes.POSTGRES)
                    cmdvars.Command = $"ANALYZE {dbtbl}.{ibs_compiler_common.PgQuoteIdentifierIfNeeded(tbl)}";
                else
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
            return 0;
        }

        /// <summary>
        /// The choice for rows left in the save table by a prior failed compile: Keep, Discard or
        /// Cancel; null when the table is empty. batch:true is the legacy shortcut for Keep.
        /// </summary>
        private static OnSavedTranslations? ResolveSaved(int savedCount, string saveTable, bool batch,
                                                         OnSavedTranslations onSaved, string outFile)
        {
            if (savedCount == 0)
            {
                ibs_compiler_common.WriteLine("No saved translations found.", outFile);
                return null;
            }
            var resolved = batch ? OnSavedTranslations.Keep : onSaved;
            if (resolved == OnSavedTranslations.Prompt)
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
                    if (choice == "1") { resolved = OnSavedTranslations.Keep;    break; }
                    if (choice == "2") { resolved = OnSavedTranslations.Discard; break; }
                    if (choice == "3") { resolved = OnSavedTranslations.Cancel;  break; }
                    Console.WriteLine("Invalid choice. Please enter 1, 2, or 3.");
                }
            }
            else
            {
                // Deterministic marker for headless callers.
                ibs_compiler_common.WriteLine(
                    $"WARNING: {saveTable} contains {savedCount} rows from a prior failed compile.", outFile);
            }

            ibs_compiler_common.WriteLine(resolved switch
            {
                OnSavedTranslations.Keep => "Keeping existing saved translations, skipping save step.",
                OnSavedTranslations.Discard => "Discarding saved translations...",
                _ => "Cancelled.",
            }, outFile);
            return resolved;
        }

        /// <summary>The live targets for a saved-translation choice (null: none saved); null on cancel.</summary>
        public static List<LiveTarget>? LiveTargets(IReadOnlyDictionary<string, List<string?[]>> files, int language, int company,
                                                    IReadOnlyList<string?[]> saved, int dbOwn, OnSavedTranslations? choice)
        {
            if (choice == OnSavedTranslations.Cancel) return null;
            // Keep skips the save step, so the database's own rows are not saved; discard re-saves them.
            var keep = choice == OnSavedTranslations.Keep;
            return LiveMessages.Targets(files, language, company, keep ? saved : null, !keep && dbOwn > 0);
        }

        private static int RunLive(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor, Options myOptions,
                                   string mainMes, string[] extensions, string saveTable, bool batch,
                                   OnSavedTranslations onSaved, LiveSettings live, Action changeLog)
        {
            var d = profile.ServerType;
            if (d == SQLServerTypes.POSTGRES) { Console.Error.WriteLine("Error: " + LiveCompile.PostgresRefused); return 1; }
            const string db = "master";
            string Resolve(string s) => myOptions.ReplaceOptions(s);
            if (executor is not ISqlRowReader reader)
            { Console.Error.WriteLine("Error: this executor cannot read rows, so the live path cannot diff"); return 1; }

            var errors = new List<string>();
            var files = new Dictionary<string, List<string?[]>>();
            foreach (var ext in extensions)
            {
                var name = ext.TrimStart('.');
                files[name] = LiveMessages.ParseFile(mainMes + ext, ibs_compiler_common.BuildArrayFromDisk(mainMes + ext),
                                                     name.EndsWith("msgrp", StringComparison.Ordinal), d, errors);
            }
            foreach (var w in LiveMessages.SqrCapWarnings(files["sqr_msg"]))
                ibs_compiler_common.WriteLine("Warning: " + w, cmdvars.OutFile);
            if (errors.Count > 0)
            {
                foreach (var e in errors.Take(LiveCompile.ReportCap)) Console.Error.WriteLine("Error: " + e);
                if (errors.Count > LiveCompile.ReportCap) Console.Error.WriteLine($"... and {errors.Count - LiveCompile.ReportCap} more");
                return 1;
            }

            int company, language;
            List<string?[]> saved;
            int dbOwn;
            try
            {
                var sv = reader.QueryRows(Resolve("select company, language from &sysvalues&"), db);
                if (sv.Count == 0) { Console.Error.WriteLine("Error: &sysvalues& has no row"); return 1; }
                company = Convert.ToInt32(sv[0][0], CultureInfo.InvariantCulture);
                language = Convert.ToInt32(sv[0][1], CultureInfo.InvariantCulture);
                ibs_compiler_common.WriteLine($"Company {company}, language {language}", cmdvars.OutFile);
                ibs_compiler_common.WriteLine($"Checking for saved translations in {saveTable}...", cmdvars.OutFile);
                saved = LiveCompile.ReadCurrent(new LiveTarget { Table = LiveMessages.GuiMessagesSave }, reader, db, d, Resolve);
                var own = reader.QueryRows(Resolve($"select count(*) from &gui_messages& where cmpy = {company}"), db);
                dbOwn = own.Count == 0 ? 0 : Convert.ToInt32(own[0][0], CultureInfo.InvariantCulture);
            }
            catch (Exception ex) { Console.Error.WriteLine("Error: reading the current messages failed: " + ex.Message); return 1; }

            var targets = LiveTargets(files, language, company, saved, dbOwn,
                                      ResolveSaved(saved.Count, saveTable, batch, onSaved, cmdvars.OutFile));
            if (targets == null) return 0;
            LiveTarget msgTarget = targets.Single(x => x.Table == LiveMessages.Messages);

            var serverName = profile.IsProfile ? profile.ProfileName : cmdvars.ServerNameOnly;
            bool applied = false, afterFailed = false;
            // Only after gui_messages read back, so a failed run leaves the saved translations in place.
            int EmptySave() => LiveCompile.Run("gui_messages_save", new[] { LiveMessages.SaveTarget() }, live, executor, d, db,
                                               Resolve, cmdvars.OutFile, $"messages_save.{serverName}.{profile.Company}");
            void Derive(IReadOnlyList<LiveDiff> diffs)
            {
                var procs = LiveMessages.ProcsFor(diffs);
                if (procs.Count == 0) ibs_compiler_common.WriteLine("No message type changed; no type proc to run.", cmdvars.OutFile);
                for (int i = 0; i < procs.Count; i++)
                {
                    cmdvars.Command = Resolve($"&dbpro&..{procs[i]}");
                    ibs_compiler_common.WriteLine($"Executing {cmdvars.Command}...", cmdvars.OutFile);
                    if (isqlline_main.Run(cmdvars, profile, executor, myOptions).Returncode) continue;
                    // The tables now read as current, so the next live run would not run these again.
                    Console.Error.WriteLine($"Error: {procs[i]} failed. Still to run: {string.Join(", ", procs.Skip(i))} " +
                                            "(or run compile_msg --reload).");
                    afterFailed = true;
                    return;
                }
                if (procs.Count == 0) return;
                try
                {
                    var check = LiveCompile.DiffAll(new LiveTarget[] { msgTarget }, reader, db, d, Resolve);
                    if (check.All(x => x.IsEmpty)) return;
                    Console.Error.WriteLine("Error: &messages& after the type procs differs from the target:");
                    foreach (var l in LiveCompile.Describe(check)) Console.Error.WriteLine("  " + l);
                }
                catch (Exception ex) { Console.Error.WriteLine("Error: reading &messages& back failed: " + ex.Message); }
                afterFailed = true;
            }
            void AfterApply(IReadOnlyList<LiveDiff> diffs)
            {
                applied = true;
                if (EmptySave() != 0) afterFailed = true;
                Derive(diffs);
            }

            var rc = LiveCompile.Run("messages", targets, live, executor, d, db, Resolve, cmdvars.OutFile,
                                     $"messages.{serverName}.{profile.Company}", changeLog, AfterApply);
            // Already current: the readback match is the empty diff, so the save table still goes.
            if (rc == 0 && !applied && !live.DryRun && saved.Count > 0 && EmptySave() != 0) rc = 1;
            if (rc == 0 && live.DryRun && saved.Count > 0)
                ibs_compiler_common.WriteLine($"would empty {saveTable} ({saved.Count} rows)", cmdvars.OutFile);
            if (afterFailed) rc = 1;
            ibs_compiler_common.WriteLine(rc == 0 ? "compile_msg DONE." : "compile_msg FAILED.", cmdvars.OutFile);
            return rc;
        }

        private static string CreateTempMessageFile(string sourceFile, string? outFile = null)
        {
            var tempFile = ibs_compiler_common.GetTempFile();
            ibs_compiler_common.WriteLine("temp file: " + tempFile, outFile);
            var lines = ibs_compiler_common.BuildArrayFromDisk(sourceFile);
            ibs_compiler_common.SaveArrayToDisk(lines, tempFile);
            return tempFile;
        }
    }
}
