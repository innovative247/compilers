using System.Globalization;
using System.Text;
using ibsCompiler.Database;

namespace ibsCompiler
{
    /// <summary>
    /// Live path for the config compilers: compute the target rows on the client, diff them by key
    /// against the database, apply only the differences as keyed statements in short transactions,
    /// then re-run the diff, which must come out empty. No truncate, no unkeyed statement, so it is
    /// safe on a live or replicated server. The legacy whole-table reload procs stay behind --reload.
    /// </summary>
    public static class LiveCompile
    {
        /// <summary>The mode set_options/compile_options use when neither --live nor --reload is given.</summary>
        public const LiveMode DefaultMode = LiveMode.Live;
        public const int DefaultBatchSize = 50;
        // Row triggers write a ba_gen_chg_log row per statement inside the transaction.
        public const int MaxBatchSize = 1000;
        public const int ReportCap = 50;
        public const int MaxPauseMs = 60000;
        // PostgresExecutor commits each statement on its own, so a batch is not one transaction.
        public const string PostgresRefused = "live import not supported on Postgres; use --reload";

        public static readonly string[] BoolFlagNames = { "--live", "--reload", "--dry-run" };
        public const string FlagsUsage = "[--live|--reload] [--batch-size N] [--pause-ms N] [--dry-run [--plan-file F]]";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        #region Arguments
        /// <summary>Consumes the live-path flags from <paramref name="args"/>.</summary>
        public static bool ParseArgs(List<string> args, out LiveSettings s, out string error)
        {
            s = new LiveSettings();
            error = "";
            bool live = CliArgs.HasFlag(args, "--live"), reload = CliArgs.HasFlag(args, "--reload");
            if (live && reload) { error = "--live and --reload are mutually exclusive"; return false; }
            if (live) s.Mode = LiveMode.Live;
            if (reload) s.Mode = LiveMode.Reload;
            s.DryRun = CliArgs.HasFlag(args, "--dry-run");
            s.PlanFile = CliArgs.GetOption(args, "--plan-file");
            var bs = CliArgs.GetOption(args, "--batch-size");
            if (bs != null && (!int.TryParse(bs, NumberStyles.None, Inv, out s.BatchSize) || s.BatchSize < 1 || s.BatchSize > MaxBatchSize))
            { error = $"--batch-size must be 1..{MaxBatchSize}"; return false; }
            var pm = CliArgs.GetOption(args, "--pause-ms");
            if (pm != null && (!int.TryParse(pm, NumberStyles.None, Inv, out s.PauseMs) || s.PauseMs < 0 || s.PauseMs > MaxPauseMs))
            { error = $"--pause-ms must be 0..{MaxPauseMs} milliseconds"; return false; }
            if (s.Mode == LiveMode.Reload && (s.DryRun || bs != null || pm != null || s.PlanFile != null))
            { error = "--dry-run, --plan-file, --batch-size and --pause-ms apply to the live path, not --reload"; return false; }
            if (s.PlanFile != null && !s.DryRun) { error = "--plan-file needs --dry-run"; return false; }
            return true;
        }
        #endregion

        #region SQL text
        /// <param name="text">DATA_CHARSET transform for string values (SybaseExecutor.DataCharsetTransform); null sends text as it is.</param>
        public static string Literal(string? v, LiveColType t, SQLServerTypes d, Func<string, string>? text = null)
        {
            if (v == null) return "NULL";
            if (t == LiveColType.Int)
                return long.TryParse(v.Trim(), NumberStyles.Integer, Inv, out var n) ? n.ToString(Inv)
                    : throw new FormatException($"'{v}' is not a whole number");
            if (text != null) v = text(v);
            var q = "'" + v.Replace("'", "''") + "'";
            // N'...' is MSSQL syntax; ASE and Postgres take a plain literal.
            return t == LiveColType.NVarChar && d == SQLServerTypes.MSSQL ? "N" + q : q;
        }

        public static string Col(string name, SQLServerTypes d) =>
            d == SQLServerTypes.POSTGRES ? ibs_compiler_common.PgQuoteIdentifierIfNeeded(name) : name;

        private static string KeyWhere(LiveTable t, string?[] v, SQLServerTypes d, Func<string, string>? text)
        {
            if (t.Key.Length == 0) throw new InvalidOperationException($"{t.Label}: no key, refusing an unkeyed statement");
            return string.Join(" and ", t.Key.Select(k => v[k] == null
                ? $"{Col(t.Columns[k].Name, d)} is null"
                : $"{Col(t.Columns[k].Name, d)} = {Literal(v[k], t.Columns[k].Type, d, text)}"));
        }

        /// <summary>One keyed statement for a change, without terminator or error check.</summary>
        public static string Statement(LiveChange c, SQLServerTypes d, Func<string, string> resolve, Func<string, string>? text = null)
        {
            var t = c.Table;
            var tbl = resolve(t.Placeholder);
            bool pg = d == SQLServerTypes.POSTGRES;
            switch (c.Kind)
            {
                case LiveChangeKind.Delete:
                    return $"{(pg ? "DELETE FROM" : "delete")} {tbl} {(pg ? "WHERE" : "where")} {KeyWhere(t, c.Current, d, text)}";
                case LiveChangeKind.Update:
                    var set = string.Join(", ", c.Changed.Select(i => $"{Col(t.Columns[i].Name, d)} = {Literal(c.Target![i], t.Columns[i].Type, d, text)}"));
                    return $"{(pg ? "UPDATE" : "update")} {tbl} {(pg ? "SET" : "set")} {set} {(pg ? "WHERE" : "where")} {KeyWhere(t, c.Current, d, text)}";
                default:
                    var cols = string.Join(", ", t.Columns.Select(x => Col(x.Name, d)));
                    var vals = string.Join(", ", t.Columns.Select((x, i) => Literal(c.Target![i], x.Type, d, text)));
                    return $"{(pg ? "INSERT INTO" : "insert")} {tbl} ({cols}) {(pg ? "VALUES" : "values")} ({vals})";
            }
        }
        #endregion

        #region Read and diff
        public static string? AsText(object? v) => v switch
        {
            null => null,
            DBNull => null,
            string s => s,
            IFormattable f => f.ToString(null, Inv),
            _ => v.ToString(),
        };

        /// <summary>Plain read-committed select of the target's columns (and rows, when it has a Where).</summary>
        public static List<string?[]> ReadCurrent(LiveTarget target, ISqlRowReader reader, string database,
                                                 SQLServerTypes d, Func<string, string> resolve)
        {
            var t = target.Table;
            var sql = $"select {string.Join(", ", t.Columns.Select(c => Col(c.Name, d)))} from {resolve(t.Placeholder)}" +
                      (string.IsNullOrWhiteSpace(target.Where) ? "" : " where " + resolve(target.Where!));
            return reader.QueryRows(sql, database).Select(r => r.Select(AsText).ToArray()).ToList();
        }

        /// <summary>
        /// Deletes for database rows with no target row, updates (changed columns only) for rows
        /// that differ, inserts for target rows that are missing. A kept row is left alone.
        /// A key held by more than one database row is deleted and its target row re-inserted.
        /// </summary>
        public static LiveDiff Diff(LiveTable t, IReadOnlyList<string?[]> target, IReadOnlyList<string?[]> current,
                                    SQLServerTypes d, LiveKeep? keep = null)
        {
            var diff = new LiveDiff { Table = t };
            var want = new Dictionary<string, string?[]>(StringComparer.Ordinal);
            foreach (var r in target)
                if (!want.TryAdd(t.KeyOf(r, d), r))
                    throw new ArgumentException($"{t.Label}: duplicate target key {t.KeyText(r)}");

            var have = current.GroupBy(r => t.KeyOf(r, d), StringComparer.Ordinal).ToList();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in have)
            {
                seen.Add(g.Key);
                want.TryGetValue(g.Key, out var tgt);
                var rows = g.ToList();
                if (rows.Count > 1)
                {
                    diff.Deletes.Add(new LiveChange { Kind = LiveChangeKind.Delete, Table = t, Current = rows[0] });
                    if (tgt != null) diff.Inserts.Add(new LiveChange { Kind = LiveChangeKind.Insert, Table = t, Target = tgt });
                    continue;
                }
                var cur = rows[0];
                if (keep != null && keep(cur, tgt)) { diff.Kept++; continue; }
                if (tgt == null)
                {
                    diff.Deletes.Add(new LiveChange { Kind = LiveChangeKind.Delete, Table = t, Current = cur });
                    continue;
                }
                var changed = Enumerable.Range(0, t.Columns.Length)
                    .Where(i => !t.Key.Contains(i) && t.Columns[i].Compare && !t.Same(i, cur[i], tgt[i], d)).ToArray();
                if (changed.Length > 0)
                {
                    changed = changed.Concat(Enumerable.Range(0, t.Columns.Length)
                        .Where(i => !t.Key.Contains(i) && !t.Columns[i].Compare)).OrderBy(i => i).ToArray();
                    diff.Updates.Add(new LiveChange { Kind = LiveChangeKind.Update, Table = t, Current = cur, Target = tgt, Changed = changed });
                }
            }
            foreach (var r in target)
                if (!seen.Contains(t.KeyOf(r, d)))
                    diff.Inserts.Add(new LiveChange { Kind = LiveChangeKind.Insert, Table = t, Target = r });
            return diff;
        }

        public static List<LiveDiff> DiffAll(IReadOnlyList<LiveTarget> targets, ISqlRowReader reader, string database,
                                             SQLServerTypes d, Func<string, string> resolve) =>
            targets.Select(x => Diff(x.Table, x.Rows, ReadCurrent(x, reader, database, d, resolve), d, x.Keep)).ToList();
        #endregion

        #region Plan
        /// <summary>
        /// Deletes (last table first), then updates, then inserts (first table first), so a unique key
        /// is free before it is reused. Each batch of at most <paramref name="batchSize"/> statements is
        /// one short transaction in its own 'go' batch.
        /// </summary>
        public static List<string> BuildBatches(IReadOnlyList<LiveDiff> diffs, SQLServerTypes d, int batchSize,
                                                Func<string, string> resolve, Func<string, string>? text = null)
        {
            if (batchSize < 1 || batchSize > MaxBatchSize) throw new ArgumentOutOfRangeException(nameof(batchSize));
            var changes = diffs.Reverse().SelectMany(x => x.Deletes)
                .Concat(diffs.SelectMany(x => x.Updates))
                .Concat(diffs.SelectMany(x => x.Inserts)).ToList();
            bool pg = d == SQLServerTypes.POSTGRES;
            int total = (changes.Count + batchSize - 1) / batchSize;
            var batches = new List<string>();
            for (int b = 0; b < total; b++)
            {
                var chunk = changes.GetRange(b * batchSize, Math.Min(batchSize, changes.Count - b * batchSize));
                var sb = new StringBuilder();
                sb.AppendLine($"-- batch {b + 1} of {total}: {string.Join(", ", chunk.GroupBy(c => (c.Table.Label, c.Kind)).Select(g => $"{g.Count()} {g.Key.Kind.ToString().ToLowerInvariant()} {g.Key.Label}"))}");
                sb.AppendLine(pg ? "BEGIN;" : "begin tran");
                foreach (var c in chunk)
                {
                    var st = Statement(c, d, resolve, text);
                    if (pg) sb.AppendLine(st + ";");
                    else
                    {
                        sb.AppendLine(st);
                        sb.AppendLine("if @@error != 0 begin rollback tran return end");
                    }
                }
                sb.AppendLine(pg ? "COMMIT;" : "commit tran");
                sb.AppendLine("go");
                batches.Add(sb.ToString());
            }
            return batches;
        }

        /// <summary>Keys of written rows with a string value the data charset cannot hold (<paramref name="text"/> maps it to '?').</summary>
        public static List<string> Unholdable(IReadOnlyList<LiveDiff> diffs, Func<string, string> text)
        {
            bool Bad(string? v)
            {
                if (v == null) return false;
                for (int i = 0; i < v.Length; i++)
                {
                    if (v[i] < 0x80) continue;
                    int n = char.IsHighSurrogate(v[i]) && i + 1 < v.Length && char.IsLowSurrogate(v[i + 1]) ? 2 : 1;
                    if (text(v.Substring(i, n)) == "?") return true;
                    i += n - 1;
                }
                return false;
            }
            bool IsText(LiveColumn c) => c.Type != LiveColType.Int;
            return diffs.SelectMany(x => x.Updates.Where(c => c.Changed.Any(i => IsText(c.Table.Columns[i]) && Bad(c.Target![i])))
                    .Concat(x.Inserts.Where(c => c.Table.Columns.Where((col, i) => IsText(col) && Bad(c.Target![i])).Any())))
                .Select(c => $"{c.Table.Label} {c.KeyText}").ToList();
        }

        public static string Counts(LiveDiff x) =>
            $"{x.Table.Label}: delete {x.Deletes.Count} / update {x.Updates.Count} / insert {x.Inserts.Count}" +
            (x.Kept > 0 ? $" / kept {x.Kept}" : "");

        /// <summary>The differing keys, at most <paramref name="cap"/> lines plus a count of the rest.</summary>
        public static List<string> Describe(IReadOnlyList<LiveDiff> diffs, int cap = ReportCap)
        {
            var all = diffs.SelectMany(x => x.Deletes.Concat(x.Updates).Concat(x.Inserts)).Select(c => c.Kind switch
            {
                LiveChangeKind.Delete => $"{c.Table.Label} {c.KeyText}: in the database, not in the target",
                LiveChangeKind.Insert => $"{c.Table.Label} {c.KeyText}: missing from the database",
                _ => $"{c.Table.Label} {c.KeyText}: differs in {string.Join(", ", c.Changed.Select(i => c.Table.Columns[i].Name))}",
            }).ToList();
            var lines = all.Take(cap).ToList();
            if (all.Count > cap) lines.Add($"... and {all.Count - cap} more");
            return lines;
        }
        #endregion

        #region Run
        /// <summary>Diff, then (unless dry run) apply and read back; 0 only when the readback matched.
        /// beforeApply runs once before the first write; afterApply gets the applied diffs.</summary>
        public static int Run(string label, IReadOnlyList<LiveTarget> targets, LiveSettings s, ISqlExecutor executor,
                              SQLServerTypes d, string database, Func<string, string> resolve, string outFile,
                              string planName, Action? beforeApply = null,
                              Action<IReadOnlyList<LiveDiff>>? afterApply = null)
        {
            if (d == SQLServerTypes.POSTGRES) { Console.Error.WriteLine("Error: " + PostgresRefused); return 1; }
            if (executor is not ISqlRowReader reader)
            { Console.Error.WriteLine("Error: this executor cannot read rows, so the live path cannot diff"); return 1; }

            List<LiveDiff> diffs;
            Func<string, string>? text;
            try
            {
                diffs = DiffAll(targets, reader, database, d, resolve);
                // Literals go out on a server-charset session, so with DATA_CHARSET they take the bytes the
                // bulk copy of the reload path stores (SybaseExecutor.ToServer); QueryRows maps them back.
                text = (executor as SybaseExecutor)?.DataCharsetTransform();
            }
            catch (Exception ex) { Console.Error.WriteLine($"Error: reading the current {label} failed: " + ex.Message); return 1; }
            foreach (var x in diffs) ibs_compiler_common.WriteLine(Counts(x), outFile);

            var bad = text == null ? new List<string>() : Unholdable(diffs, text);
            if (bad.Count > 0)
                ibs_compiler_common.WriteLine($"Warning: {bad.Count} {label} row(s) hold characters the data charset cannot store, " +
                    $"so they will not read back equal: {string.Join(", ", bad.Take(5))}{(bad.Count > 5 ? ", ..." : "")}", outFile);

            if (s.DryRun)
            {
                try
                {
                    var path = Path.GetFullPath(s.PlanFile ?? Path.Combine(ibs_compiler_common.GetTempPath(), $"{planName}.plan.sql"));
                    var header = $"-- {label} live plan ({d}) {DateTime.Now:yyyy-MM-dd HH:mm:ss}; dry run, nothing was written" + Environment.NewLine;
                    File.WriteAllText(path, header + string.Concat(BuildBatches(diffs, d, s.BatchSize, resolve)), new UTF8Encoding(false));
                    ibs_compiler_common.WriteLine("Dry run: planned SQL written to " + path, outFile);
                }
                catch (Exception ex) { Console.Error.WriteLine($"Error: writing the {label} plan failed: " + ex.Message); return 1; }
                return 0;
            }

            if (diffs.All(x => x.IsEmpty))
            {
                ibs_compiler_common.WriteLine($"{label}: already current, nothing to apply", outFile);
                return 0;
            }

            List<string> batches;
            try { batches = BuildBatches(diffs, d, s.BatchSize, resolve, text); }
            catch (Exception ex) { Console.Error.WriteLine($"Error: building the {label} statements failed: " + ex.Message); return 1; }

            beforeApply?.Invoke();
            for (int b = 0; b < batches.Count; b++)
            {
                if (!executor.ExecuteSql(batches[b], database, false, outFile).Returncode)
                {
                    Console.Error.WriteLine($"Error: {label} batch {b + 1} of {batches.Count} failed and was rolled back; later batches were not run.");
                    break;
                }
                // Client-side, so no batch holds a connection or transaction while it waits.
                if (s.PauseMs > 0 && b < batches.Count - 1) Thread.Sleep(s.PauseMs);
            }

            List<LiveDiff> after;
            try { after = DiffAll(targets, reader, database, d, resolve); }
            catch (Exception ex) { Console.Error.WriteLine($"Error: reading {label} back failed: " + ex.Message); return 1; }
            if (after.All(x => x.IsEmpty))
            {
                ibs_compiler_common.WriteLine($"{label}: readback matches the target", outFile);
                afterApply?.Invoke(diffs);
                return 0;
            }
            Console.Error.WriteLine($"Error: {label} readback still differs from the target:");
            foreach (var l in Describe(after)) Console.Error.WriteLine("  " + l);
            return 1;
        }
        #endregion
    }
}
