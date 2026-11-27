using System.Globalization;
using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace ibsCompiler
{
    /// <summary>
    /// Port of F4.8 compile_actions.cs.
    /// BCP-based actions compilation using managed bulk copy.
    /// </summary>
    public static class compile_actions_main
    {
        /// <param name="live">Null takes the defaults (LiveCompile.DefaultMode). Returns 0 on success, 1 on failure;
        /// on the live path 0 only when the readback matched.</param>
        public static int Run(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor, LiveSettings? live = null)
        {
            live ??= new LiveSettings();
            // Build options
            var myOptions = new Options(cmdvars, profile, true);
            if (!myOptions.GenerateOptionFiles()) return 1;

            // Changelog entry
            void ChangeLog()
            {
                var chgDb = myOptions.ReplaceOptions("&dbpro&");
                if (chgDb == "&dbpro&") return;
                var chgLines = new List<string>();
                foreach (var l in change_log.compileLines("ACTIONS", "updated actions", profile.ServerType))
                    chgLines.Add(myOptions.ReplaceOptions(l));
                chgLines.Add("go");
                executor.ExecuteSql(string.Join(Environment.NewLine, chgLines), chgDb, false, cmdvars.OutFile);
            }
            if (live.Mode == LiveMode.Reload) ChangeLog();

            ibs_compiler_common.WriteLine("Starting compile_actions...", cmdvars.OutFile);
            var actHeader = ibs_compiler_common.GetPath_Actions(cmdvars, profile);
            var actDetail = ibs_compiler_common.GetPath_ActionsDetail(profile);

            if (!File.Exists(actHeader))
            {
                ibs_compiler_common.WriteLine("Action file missing! (" + actHeader + ")", cmdvars.OutFile);
                return 1;
            }
            if (!File.Exists(actDetail))
            {
                ibs_compiler_common.WriteLine("Action Detail file missing! (" + actDetail + ")", cmdvars.OutFile);
                return 1;
            }

            if (live.Mode == LiveMode.Live)
                return RunLive(cmdvars, profile, executor, myOptions, actHeader, actDetail, live, ChangeLog);

            var tempPath = ibs_compiler_common.GetTempPath();

            // Build temp files for bulk copy
            ibs_compiler_common.WriteLine("Line Extraction of actions started at " + DateTime.Now, cmdvars.OutFile);
            ibs_compiler_common.WriteLine("temp file: " + Path.Combine(tempPath, "actions.tmp"), cmdvars.OutFile);
            ibs_compiler_common.WriteLine("temp file: " + Path.Combine(tempPath, "actions_dtl.tmp"), cmdvars.OutFile);
            try
            {
                using (var dest = new StreamWriter(Path.Combine(tempPath, "actions.tmp"), false))
                    foreach (var (lineix, line) in action_files.HeaderLines(File.ReadLines(actHeader), s => myOptions.ReplaceOptions(s), actHeader))
                        dest.WriteLine(lineix + "\t" + line);

                using (var dest = new StreamWriter(Path.Combine(tempPath, "actions_dtl.tmp"), false))
                    foreach (var f in action_files.DetailFields(File.ReadLines(actDetail), s => myOptions.ReplaceOptions(s), actDetail))
                        dest.WriteLine(string.Join("\t", f));
            }
            catch (InvalidDataException ex) { Console.Error.WriteLine("Error: " + ex.Message); return 1; }

            // Extract database from resolved work table name (e.g., "ibswrk..w#actions" → "ibswrk")
            var resolvedWorkTable = myOptions.ReplaceOptions("&w#actions&");
            if (resolvedWorkTable.Contains(".."))
                cmdvars.Database = resolvedWorkTable.Split(new[] { ".." }, 2, StringSplitOptions.None)[0];

            // Delete work tables
            ibs_compiler_common.WriteLine("Deletion of work tables started at " + DateTime.Now, cmdvars.OutFile);
            cmdvars.Command = myOptions.ReplaceOptions("truncate table &w#actions&");
            isqlline_main.Run(cmdvars, profile, executor, myOptions);

            cmdvars.Command = myOptions.ReplaceOptions("truncate table &w#actions_dtl&");
            var result = isqlline_main.Run(cmdvars, profile, executor, myOptions);
            if (!result.Returncode) return 1;

            // Bulk copy actions
            ibs_compiler_common.WriteLine("Bulk copy started at " + DateTime.Now, cmdvars.OutFile);
            ibs_compiler_common.WriteLine("Starting actions insert...", cmdvars.OutFile);
            result = executor.BulkCopy(myOptions.ReplaceOptions("&w#actions&"), BcpDirection.IN, Path.Combine(tempPath, "actions.tmp"));
            if (!result.Returncode) return 1;

            ibs_compiler_common.WriteLine("Starting actions detail insert...", cmdvars.OutFile);
            result = executor.BulkCopy(myOptions.ReplaceOptions("&w#actions_dtl&"), BcpDirection.IN, Path.Combine(tempPath, "actions_dtl.tmp"));
            if (!result.Returncode) return 1;

            // Compile actions via stored procedure
            ibs_compiler_common.WriteLine("Insert of actions in database tables started at " + DateTime.Now, cmdvars.OutFile);
            cmdvars.Command = myOptions.ReplaceOptions("&dbpro&..ba_compile_actions");
            ibs_compiler_common.WriteLine("Executing " + cmdvars.Command + "...", cmdvars.OutFile);
            result = isqlline_main.Run(cmdvars, profile, executor, myOptions);
            if (!result.Returncode) return 1;

            ibs_compiler_common.WriteLine("Compiling ended at " + DateTime.Now, cmdvars.OutFile);
            return 0;
        }

        private static int RunLive(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor, Options myOptions,
                                   string actHeader, string actDetail, LiveSettings live, Action changeLog)
        {
            var d = profile.ServerType;
            if (d == SQLServerTypes.POSTGRES) { Console.Error.WriteLine("Error: " + LiveCompile.PostgresRefused); return 1; }
            const string db = "master";
            string Resolve(string s) => myOptions.ReplaceOptions(s);
            if (executor is not ISqlRowReader reader)
            { Console.Error.WriteLine("Error: this executor cannot read rows, so the live path cannot diff"); return 1; }

            static int I(object? v) => Convert.ToInt32(v, CultureInfo.InvariantCulture);
            int company, language, chgTm;
            List<(int, int, int)> keys;
            List<(int Aprof, string Name)> profiles;
            try
            {
                var sv = reader.QueryRows(Resolve(LiveActions.SysValuesSql), db);
                if (sv.Count == 0) { Console.Error.WriteLine("Error: &sysvalues& has no row"); return 1; }
                company = I(sv[0][0]);
                language = I(sv[0][1]);
                chgTm = I(sv[0][2]);
                ibs_compiler_common.WriteLine($"Company {company}, language {language}", cmdvars.OutFile);
                keys = reader.QueryRows(Resolve(LiveActions.ActionKeysSql), db).Select(r => (I(r[0]), I(r[1]), I(r[2]))).ToList();
                profiles = reader.QueryRows(Resolve(LiveActions.ProfilesSql), db)
                    .Select(r => (I(r[0]), Convert.ToString(r[1], CultureInfo.InvariantCulture) ?? "")).ToList();
            }
            catch (Exception ex) { Console.Error.WriteLine("Error: reading the current actions failed: " + ex.Message); return 1; }

            var allActName = Resolve("&allact&");
            var allAct = allActName == "&allact&" ? new List<int>() : LiveActions.AllActProfiles(profiles, allActName);
            if (allActName != "&allact&" && allAct.Count == 0)
                ibs_compiler_common.WriteLine($"Warning: no action profile named by option allact ('{allActName}'); no profile gets the actions", cmdvars.OutFile);

            var errors = new List<string>();
            var warnings = new List<string>();
            List<LiveTarget> targets;
            try
            {
                targets = LiveActions.BuildTargets(File.ReadAllLines(actHeader), File.ReadAllLines(actDetail), Resolve, d,
                    company, language, keys, profiles.Select(p => p.Aprof), allAct, chgTm, errors, warnings);
            }
            catch (InvalidDataException ex) { errors.Add(ex.Message); targets = new(); }
            if (errors.Count > 0)
            {
                foreach (var e in errors.Take(LiveCompile.ReportCap)) Console.Error.WriteLine("Error: " + e);
                if (errors.Count > LiveCompile.ReportCap) Console.Error.WriteLine($"... and {errors.Count - LiveCompile.ReportCap} more");
                return 1;
            }
            foreach (var w in warnings) ibs_compiler_common.WriteLine("Warning: " + w, cmdvars.OutFile);

            var serverName = profile.IsProfile ? profile.ProfileName : cmdvars.ServerNameOnly;
            var rc = LiveCompile.Run("actions", targets, live, executor, d, db, Resolve, cmdvars.OutFile,
                                     $"actions.{serverName}.{profile.Company}", changeLog);
            ibs_compiler_common.WriteLine(rc == 0 ? "compile_actions DONE." : "compile_actions FAILED.", cmdvars.OutFile);
            return rc;
        }
    }

    /// <summary>
    /// The line filter and numbering compile_actions applies before the bulk copy; the live path
    /// uses the same code so its lineix values match the full compile.
    /// </summary>
    public static class action_files
    {
        /// <summary>
        /// Each kept ':>' line of the actions file, resolved, with its lineix (1-based count of kept lines).
        /// A line that resolves to fewer than 2 characters throws InvalidDataException naming file and line.
        /// </summary>
        public static IEnumerable<(int LineIx, string Line)> HeaderLines(IEnumerable<string> raw, Func<string, string> resolve, string source = "actions")
        {
            int i = 0, n = 0;
            foreach (var r in raw)
            {
                n++;
                var line = r.Trim();
                if (line.Length <= 1 || !(line.StartsWith('&') || line.StartsWith(":>", StringComparison.Ordinal))) continue;
                line = resolve(line);
                if (line.Trim().Length < 2)
                    throw new InvalidDataException($"{source}:{n}: line resolves to '{line.Trim()}', shorter than the 2-character ':>' marker");
                if (!line.Trim().StartsWith(":>", StringComparison.Ordinal)) continue;
                yield return (++i, line);
            }
        }

        /// <summary>
        /// Each kept ':>' line of actions_dtl split at the fixed columns: act, typ, ix, msgno, shix, rest.
        /// A ':>' line shorter than the five fields (24 characters) throws InvalidDataException naming file and line.
        /// </summary>
        public static IEnumerable<string[]> DetailFields(IEnumerable<string> raw, Func<string, string> resolve, string source = "actions_dtl")
        {
            int n = 0;
            foreach (var r in raw)
            {
                n++;
                var line = r.Trim();
                if (line.Length <= 2 || !(line.StartsWith('&') || line.StartsWith(":>", StringComparison.Ordinal))) continue;
                var t = resolve(line).Trim();
                if (t.Length < 2)
                    throw new InvalidDataException($"{source}:{n}: line resolves to '{t}', shorter than the 2-character ':>' marker");
                if (!t.StartsWith(":>", StringComparison.Ordinal)) continue;
                if (t.Length < 24)
                    throw new InvalidDataException($"{source}:{n}: '{t}' is {t.Length} characters; a detail line needs 24 (act, typ, ix, msgno, shix)");
                yield return new[] { t.Substring(2, 4), t.Substring(7, 3), t.Substring(11, 3), t.Substring(15, 5), t.Substring(21, 3), t.Substring(24) };
            }
        }
    }
}
