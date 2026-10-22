using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using Npgsql.Schema;
using ibsCompiler.Configuration;

namespace ibsCompiler.Database
{
    /// <summary>
    /// PostgreSQL executor (Npgsql). Mirrors MssqlExecutor's shape — result sets in isql's
    /// layout through IsqlFormatter, per-call OutputSink wiring, GO-batch splitting — with the PG-specific
    /// behaviors the compiler needs:
    ///   * SBN "databases" are PG *schemas* in one physical database, so a `database`
    ///     argument that isn't the connection DB becomes `SET search_path TO <schema>, public`
    ///     (D1). `use &lt;db&gt;` in a batch is rewritten the same way — the only statement-level
    ///     substitution.
    ///   * User SQL passes through UNTOUCHED — no DDL/body translation. PG source is authored
    ///     natively. The only rewrites are the search_path ones above and diagnostic
    ///     set-statements (Sybase-isms) mapped to EXPLAIN (D4).
    ///   * A GO-chunk is split into individual statements (D3) so each runs in its own implicit
    ///     transaction — Sybase/MSSQL autocommit-per-statement parity — using a PG-aware splitter
    ///     that respects quotes, comments, and dollar-quoted bodies.
    /// </summary>
    public class PostgresExecutor : ISqlExecutor
    {
        private readonly ResolvedProfile _profile;
        private static readonly Regex GoRegex = new(@"^\s*go\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex ExitRegex = new(@"^\s*(exit|quit)\s*$", RegexOptions.IgnoreCase);
        private static readonly string? PgSqlInitScript = LoadPgSqlInit();

        // The only statement-level substitutions.
        private static readonly Regex UseRegex = new(@"^\s*use\s+(\w+)\s*$", RegexOptions.IgnoreCase);
        // Sybase-ism diagnostic toggles — PG syntax errors, never forwarded raw (D4).
        private static readonly Regex DiagSetRegex = new(
            @"^\s*set\s+(showplan|noexec|statistics\s+\w+)\s+(on|off)\s*$", RegexOptions.IgnoreCase);
        // Statements EXPLAIN can wrap.
        private static readonly Regex DmlRegex = new(
            @"^\s*(select|insert|update|delete|with)\b", RegexOptions.IgnoreCase);

        // Per-call output sink wired up by ExecuteSql/ExecuteBatch. The connection's
        // Notice handler routes through this so NOTICE/INFO/WARNING messages stream live.
        private Action<string>? _emit;

        // Persistent connection for batch-at-a-time execution (OpenConnection/ExecuteBatch/CloseConnection).
        private NpgsqlConnection? _persistentConn;

        // Session diagnostic state (D4). PG has no session-level showplan/statistics/noexec,
        // so we emulate them by prefixing DML with EXPLAIN variants until turned off. Tracked
        // across batches like ASE keeps its set-options for the life of the connection.
        private bool _showplan;
        private bool _statistics;
        private bool _noexec;

        // set nocount on|off: T-SQL, not PG - tracked here and applied to the row counts printed.
        private static readonly Regex NocountRegex = new(@"^\s*set\s+nocount\s+(on|off)\s*$", RegexOptions.IgnoreCase);
        private bool _nocount;

        // isql-style renderer for the chunk currently executing; notices go through it too
        // (docs/architecture.md, "isql output format").
        private IsqlFormatter? _fmt;

        private static bool IsNumericType(Type t) =>
            t == typeof(int) || t == typeof(long) || t == typeof(short) ||
            t == typeof(decimal) || t == typeof(double) || t == typeof(float) ||
            t == typeof(byte);

        public PostgresExecutor(ResolvedProfile profile)
        {
            _profile = profile;
        }

        private static string? LoadPgSqlInit()
        {
            // Session defaults live in a PGSQLINI.sql — same mechanism MssqlExecutor uses for
            // SQLCMDINI. Resolve from $env:PGSQLINI first (explicit), else a local PGSQLINI.sql
            // sitting next to settings.json / the binaries. A local, versionable settings file —
            // never hard-coded values.
            var path = Environment.GetEnvironmentVariable("PGSQLINI");
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                var settings = ProfileManager.FindSettingsFile();
                var dir = (settings != null ? Path.GetDirectoryName(settings) : null) ?? AppContext.BaseDirectory;
                path = Path.Combine(dir, "PGSQLINI.sql");
            }
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        // Resolve the connection DB and (optional) schema to SET search_path to (D1).
        // If `database` is empty or names the connection DB → plain connect, no search_path.
        // Otherwise `database` names a schema → connect to the profile's admin DB and switch
        // the search_path to it.
        private (string db, string? schema) ResolveTarget(string database)
        {
            var connDb = !string.IsNullOrEmpty(_profile.Database) ? _profile.Database : "postgres";
            if (string.IsNullOrEmpty(database) ||
                string.Equals(database, _profile.Database, StringComparison.OrdinalIgnoreCase))
                return (connDb, null);
            return (_profile.AdminDatabase, database);
        }

        private NpgsqlConnection NewConnection(string database)
        {
            var sb = new NpgsqlConnectionStringBuilder
            {
                Host = _profile.Host,
                Port = _profile.Port,
                Username = _profile.User,
                Password = _profile.Pass,
                Database = database,
                Pooling = false,
                ApplicationName = "ibsCompiler"
            };
            var conn = new NpgsqlConnection(sb.ConnectionString);
            conn.Notice += OnNotice;
            return conn;
        }

        // Runs after every connect (incl. bulk-copy connects, D5). PGSQLINI.sql (optional),
        // then always datestyle ISO/MDY, then the requested schema search_path if any.
        private static void InitSession(NpgsqlConnection conn, string? searchPath)
        {
            if (PgSqlInitScript != null)
                using (var cmd = new NpgsqlCommand(PgSqlInitScript, conn))
                    cmd.ExecuteNonQuery();
            using (var cmd = new NpgsqlCommand("SET datestyle TO 'ISO, MDY'", conn))
                cmd.ExecuteNonQuery();
            if (!string.IsNullOrEmpty(searchPath))
                using (var cmd = new NpgsqlCommand($"SET search_path TO {searchPath}, public", conn))
                    cmd.ExecuteNonQuery();
        }

        private void ResetDiagnostics()
        {
            _showplan = false;
            _statistics = false;
            _noexec = false;
            _nocount = false;
        }

        public ExecReturn ExecuteSql(string sqlText, string database, bool captureOutput = false, string outputFile = "")
        {
            var result = new ExecReturn { Returncode = true, Output = "" };
            var output = new StringBuilder();
            var sink = OutputSink.Build(output, captureOutput, outputFile);
            _emit = sink.Emit;

            try
            {
                var (db, searchPath) = ResolveTarget(database);
                using var conn = NewConnection(db);
                conn.Open();
                InitSession(conn, searchPath);
                ResetDiagnostics();

                int width = IsqlFormatter.ResolveWidth(captureOutput, outputFile);
                foreach (var chunk in SplitBatches(sqlText))
                {
                    if (string.IsNullOrWhiteSpace(chunk)) continue;
                    if (ExitRegex.IsMatch(chunk.Trim())) break;
                    RunChunk(conn, chunk, sink.Emit, width, ref result);
                }
            }
            catch (Exception ex)
            {
                sink.Emit($"ERROR! {ex.Message}");
                result.Returncode = false;
            }
            finally
            {
                _emit = null;
                sink.Dispose();
            }

            result.Output = output.ToString();
            return result;
        }

        public void OpenConnection(string database)
        {
            var (db, searchPath) = ResolveTarget(database);
            _persistentConn = NewConnection(db);
            _persistentConn.Open();
            InitSession(_persistentConn, searchPath);
            ResetDiagnostics();
        }

        public ExecReturn ExecuteBatch(string batch, bool captureOutput = false, string outputFile = "")
        {
            var result = new ExecReturn { Returncode = true, Output = "" };
            var output = new StringBuilder();
            var sink = OutputSink.Build(output, captureOutput, outputFile);
            _emit = sink.Emit;

            try
            {
                if (!string.IsNullOrWhiteSpace(batch) && _persistentConn != null)
                    RunChunk(_persistentConn, batch, sink.Emit,
                        IsqlFormatter.ResolveWidth(captureOutput, outputFile), ref result);
            }
            catch (Exception ex)
            {
                sink.Emit($"ERROR! {ex.Message}");
                result.Returncode = false;
            }
            finally
            {
                _emit = null;
                sink.Dispose();
            }

            result.Output = output.ToString();
            return result;
        }

        public void CloseConnection()
        {
            _persistentConn?.Dispose();
            _persistentConn = null;
        }

        // Execute one GO-chunk as individual statements (D3). A PostgresException aborts the
        // REMAINDER of the current chunk (server batch-abort behavior) but not the whole run —
        // the caller moves on to the next GO-chunk.
        // `result` MUST be by-ref: ExecReturn is a struct (CommandVariables.cs:80), so a
        // by-value parameter would take the failure flag to the grave and every PG error
        // would print but still exit 0.
        private void RunChunk(NpgsqlConnection conn, string chunk, Action<string> emit, int width, ref ExecReturn result)
        {
            var fmt = new IsqlFormatter(emit, width);
            _fmt = fmt;
            try
            {
                int searchFrom = 0;
                foreach (var stmt in SplitStatements(chunk))
                {
                    // The statement's first line within the chunk, which errors are reported from.
                    int at = chunk.IndexOf(stmt, searchFrom, StringComparison.Ordinal);
                    if (at < 0) at = searchFrom;
                    int stmtLine = 1 + chunk.Take(at).Count(ch => ch == '\n');
                    searchFrom = at + stmt.Length;

                    // `use <db>` → schema switch (D1). Only statement-level substitution.
                    var useM = UseRegex.Match(stmt);
                    if (useM.Success)
                    {
                        var useSql = $"SET search_path TO {useM.Groups[1].Value}, public";
                        try
                        {
                            using var uc = new NpgsqlCommand(useSql, conn);
                            uc.ExecuteNonQuery();
                        }
                        catch (PostgresException ex)
                        {
                            EmitPgException(ex, fmt, useSql, stmtLine, 0);
                            result.Returncode = false;
                            return;
                        }
                        continue;
                    }

                    // Diagnostic set-statement — track state, emit nothing (matches Sybase silence).
                    var diagM = DiagSetRegex.Match(stmt);
                    if (diagM.Success)
                    {
                        UpdateDiagnostics(diagM);
                        continue;
                    }
                    var nocountM = NocountRegex.Match(stmt);
                    if (nocountM.Success)
                    {
                        _nocount = nocountM.Groups[1].Value.Equals("on", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    var sql = ApplyDiagPrefix(stmt);
                    try
                    {
                        RunStatement(conn, sql, fmt);
                    }
                    catch (PostgresException ex)
                    {
                        EmitPgException(ex, fmt, sql, stmtLine, sql.Length - stmt.Length);
                        // The failed statement's result set, if one had begun, ends here.
                        fmt.Done(false, 0, isError: true);
                        result.Returncode = false;
                        return; // abort remainder of this GO-chunk
                    }
                }
            }
            finally
            {
                _fmt = null;
            }
        }

        private void UpdateDiagnostics(Match m)
        {
            var mode = m.Groups[1].Value.ToLowerInvariant();
            bool on = m.Groups[2].Value.Equals("on", StringComparison.OrdinalIgnoreCase);
            if (mode.StartsWith("statistics")) _statistics = on;
            else if (mode == "showplan") _showplan = on;
            else if (mode == "noexec") _noexec = on;
        }

        // Map active Sybase diagnostics to a PG EXPLAIN prefix (D4). noexec wins (must NOT
        // execute → plain EXPLAIN); else statistics → EXPLAIN (ANALYZE, BUFFERS); else showplan
        // → EXPLAIN. Only wraps DML.
        private string ApplyDiagPrefix(string stmt)
        {
            if (!_noexec && !_statistics && !_showplan) return stmt;
            if (!DmlRegex.IsMatch(stmt)) return stmt;
            string prefix = _noexec ? "EXPLAIN "
                          : _statistics ? "EXPLAIN (ANALYZE, BUFFERS) "
                          : "EXPLAIN ";
            return prefix + stmt;
        }

        private void OnNotice(object? sender, NpgsqlNoticeEventArgs e)
        {
            var emit = _emit;
            if (emit == null) return;
            var sev = e.Notice.Severity ?? "";
            if (sev.StartsWith("DEBUG", StringComparison.OrdinalIgnoreCase)) return; // NOTICE/INFO/WARNING pass
            // A notice prints like an isql print / informational message: its text, wrapped.
            (_fmt ?? new IsqlFormatter(emit, IsqlFormatter.DefaultWidth)).Message(e.Notice.MessageText);
        }

        public ExecReturn BulkCopy(string table, BcpDirection direction, string dataFile, string formatFile = "", string fieldTerminator = "\t", int batchSize = 0)
        {
            var result = new ExecReturn { Returncode = true, Output = "" };

            try
            {
                if (direction == BcpDirection.IN)
                {
                    BulkCopyIn(table, dataFile, fieldTerminator, batchSize);
                }
                else
                {
                    var rows = BulkCopyOut(table, dataFile, fieldTerminator);
                    result.Output = rows.ToString();
                }
            }
            catch (Exception ex)
            {
                result.Output = $"ERROR! BulkCopy failed: {ex.Message}";
                result.Returncode = false;
                ibs_compiler_common.WriteLine(result.Output);
            }

            return result;
        }

        // Bulk-copy connections always hit the profile's physical DB; the `db..table` form's
        // db part is a *schema*, so it becomes a schema-qualified target, not a separate DB.
        private NpgsqlConnection OpenBulkConnection()
        {
            var db = !string.IsNullOrEmpty(_profile.Database) ? _profile.Database : "postgres";
            var conn = NewConnection(db);
            conn.Open();
            InitSession(conn, null);
            return conn;
        }

        // "db..table" → schema."table"; plain "table" → "table". Identifiers containing '#' or
        // uppercase are quoted (e.g. "w#options").
        private static string ResolveCopyTarget(string table)
        {
            string schema = "";
            string tableName = table;
            if (table.Contains(".."))
            {
                var parts = table.Split(new[] { ".." }, 2, StringSplitOptions.None);
                schema = parts[0];
                tableName = parts[1];
            }
            var target = QuoteIdent(tableName);
            if (!string.IsNullOrEmpty(schema))
                target = QuoteIdent(schema) + "." + target;
            return target;
        }

        private static string QuoteIdent(string ident)
        {
            bool needs = ident.Any(ch => ch == '#' || char.IsUpper(ch)) ||
                         !ident.All(ch => char.IsLetterOrDigit(ch) || ch == '_');
            return needs ? "\"" + ident.Replace("\"", "\"\"") + "\"" : ident;
        }

        private void BulkCopyIn(string table, string dataFile, string fieldTerminator, int batchSize)
        {
            using var conn = OpenBulkConnection();
            var target = ResolveCopyTarget(table);

            // Column types (empty-string → 0 coercion is per-column for numeric types).
            var columnTypes = new List<Type>();
            using (var schemaCmd = new NpgsqlCommand($"SELECT * FROM {target} WHERE 1=0", conn))
            using (var schemaReader = schemaCmd.ExecuteReader())
            {
                for (int i = 0; i < schemaReader.FieldCount; i++)
                    columnTypes.Add(schemaReader.GetFieldType(i));
            }

            var lines = ibs_compiler_common.ReadBulkLines(dataFile);
            if (lines.Length == 0) return;
            // The file's separator is the caller's -t; COPY's wire separator is always a tab,
            // so the two only coincide by default.
            var sep = new[] { fieldTerminator };
            int colCount = columnTypes.Count > 0 ? columnTypes.Count : lines[0].Split(sep, StringSplitOptions.None).Length;

            // Text-format COPY: tab-separated, PG-escaped rows straight to STDIN.
            string CopyLine(string line)
            {
                var cols = line.Split(sep, StringSplitOptions.None);

                // Extra fields merge into the last column (native BCP behavior).
                if (cols.Length > colCount && colCount > 0)
                {
                    var merged = new string[colCount];
                    for (int i = 0; i < colCount - 1; i++)
                        merged[i] = cols[i];
                    merged[colCount - 1] = string.Join(fieldTerminator, cols.Skip(colCount - 1));
                    cols = merged;
                }

                var sb = new StringBuilder();
                for (int i = 0; i < colCount; i++)
                {
                    if (i > 0) sb.Append('\t');
                    string val = i < cols.Length ? cols[i] : "";
                    bool isNum = i < columnTypes.Count && IsNumericType(columnTypes[i]);
                    if (isNum && string.IsNullOrEmpty(val)) val = "0"; // empty numeric → 0
                    // empty string stays empty (not NULL) for text columns
                    sb.Append(EscapeCopyText(val));
                }
                // Explicit \n — the STDIN writer's WriteLine would emit CRLF on Windows,
                // injecting a stray \r into the final column.
                sb.Append('\n');
                return sb.ToString();
            }

            // COPY runs inside whatever transaction the connection has open, so the default is
            // one explicit transaction around one COPY: a row PG rejects takes the whole load
            // down with it and the table is left as it was. -b N is the traditional bcp deal —
            // a transaction and a COPY per N rows, so the chunks before a failure stay.
            // NpgsqlTransaction.Dispose rolls back anything not committed.
            var rows = lines.Where(l => !string.IsNullOrEmpty(l)).ToList();
            int chunk = batchSize > 0 ? batchSize : rows.Count;
            int total = 0;
            for (int start = 0; start < rows.Count; start += chunk)
            {
                using var tran = conn.BeginTransaction();
                using (var writer = conn.BeginTextImport($"COPY {target} FROM STDIN (FORMAT text)"))
                {
                    for (int i = start; i < Math.Min(start + chunk, rows.Count); i++)
                    {
                        writer.Write(CopyLine(rows[i]));
                        total++;
                        if (total % 1000 == 0)
                            ibs_compiler_common.WriteLine($"{total} rows sent to the server.");
                    }
                } // dispose completes the COPY
                tran.Commit();
            }

            ibs_compiler_common.WriteLine("");
            ibs_compiler_common.WriteLine($"{total} rows copied.");
        }

        private int BulkCopyOut(string table, string dataFile, string fieldTerminator)
        {
            using var conn = OpenBulkConnection();
            var target = ResolveCopyTarget(table);

            using var reader = conn.BeginTextExport($"COPY (SELECT * FROM {target}) TO STDOUT (FORMAT text)");
            using var writer = ibs_compiler_common.OpenBulkWriter(dataFile);

            int rowCount = 0;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                // Unescape PG text-format escapes so the file matches the raw delimited
                // form the other executors write.
                writer.WriteLine(UnescapeCopyLine(line, fieldTerminator));
                rowCount++;
                if (rowCount % 1000 == 0)
                    ibs_compiler_common.WriteLine($"{rowCount} rows successfully extracted to {dataFile}");
            }
            return rowCount;
        }

        // PG text-format field escaping: backslash, tab, newline, CR.
        private static string EscapeCopyText(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        // Field separators in COPY output are literal tabs; escaped tabs within data are "\t".
        // Split on the literal tabs first, then unescape each field, then rejoin on the
        // caller's terminator — with a non-tab terminator an unescaped tab is just data.
        private static string UnescapeCopyLine(string line, string fieldTerminator)
        {
            var fields = line.Split('\t');
            for (int i = 0; i < fields.Length; i++)
                fields[i] = UnescapeCopyField(fields[i]);
            return string.Join(fieldTerminator, fields);
        }

        private static string UnescapeCopyField(string f)
        {
            if (f == "\\N") return ""; // NULL sentinel → empty, matching the other executors
            if (f.IndexOf('\\') < 0) return f;
            var sb = new StringBuilder(f.Length);
            for (int i = 0; i < f.Length; i++)
            {
                char c = f[i];
                if (c == '\\' && i + 1 < f.Length)
                {
                    char n = f[i + 1];
                    switch (n)
                    {
                        case 't': sb.Append('\t'); i++; break;
                        case 'n': sb.Append('\n'); i++; break;
                        case 'r': sb.Append('\r'); i++; break;
                        case '\\': sb.Append('\\'); i++; break;
                        default: sb.Append(c); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static string[] SplitBatches(string sqlText)
        {
            return GoRegex.Split(sqlText)
                .Where(b => !string.IsNullOrWhiteSpace(b))
                .ToArray();
        }

        // PG-aware statement splitter (D3). Semicolons terminate statements ONLY outside
        // single/double-quoted strings, --/ * * / comments, and dollar-quoted bodies
        // ($$ / $tag$ ...). A statement containing a dollar-quoted function body stays whole.
        private static List<string> SplitStatements(string chunk)
        {
            var stmts = new List<string>();
            var sb = new StringBuilder();
            int i = 0, n = chunk.Length;

            while (i < n)
            {
                char c = chunk[i];

                // line comment
                if (c == '-' && i + 1 < n && chunk[i + 1] == '-')
                {
                    while (i < n && chunk[i] != '\n') { sb.Append(chunk[i]); i++; }
                    continue;
                }
                // block comment
                if (c == '/' && i + 1 < n && chunk[i + 1] == '*')
                {
                    sb.Append("/*"); i += 2;
                    while (i < n && !(chunk[i] == '*' && i + 1 < n && chunk[i + 1] == '/'))
                    { sb.Append(chunk[i]); i++; }
                    if (i < n) { sb.Append("*/"); i += 2; }
                    continue;
                }
                // single-quoted string ('' escapes a quote)
                if (c == '\'')
                {
                    sb.Append(c); i++;
                    while (i < n)
                    {
                        sb.Append(chunk[i]);
                        if (chunk[i] == '\'')
                        {
                            if (i + 1 < n && chunk[i + 1] == '\'') { sb.Append(chunk[i + 1]); i += 2; continue; }
                            i++; break;
                        }
                        i++;
                    }
                    continue;
                }
                // double-quoted identifier ("" escapes a quote)
                if (c == '"')
                {
                    sb.Append(c); i++;
                    while (i < n)
                    {
                        sb.Append(chunk[i]);
                        if (chunk[i] == '"')
                        {
                            if (i + 1 < n && chunk[i + 1] == '"') { sb.Append(chunk[i + 1]); i += 2; continue; }
                            i++; break;
                        }
                        i++;
                    }
                    continue;
                }
                // dollar-quoted body ($$...$$ or $tag$...$tag$)
                if (c == '$')
                {
                    var tag = ibs_compiler_common.MatchDollarTag(chunk, i);
                    if (tag != null)
                    {
                        sb.Append(tag);
                        int start = i + tag.Length;
                        int end = chunk.IndexOf(tag, start, StringComparison.Ordinal);
                        if (end < 0)
                        {
                            sb.Append(chunk.Substring(start));
                            i = n;
                        }
                        else
                        {
                            sb.Append(chunk.Substring(start, end - start));
                            sb.Append(tag);
                            i = end + tag.Length;
                        }
                        continue;
                    }
                }
                // statement terminator
                if (c == ';')
                {
                    var s = sb.ToString().Trim();
                    if (s.Length > 0) stmts.Add(s);
                    sb.Clear();
                    i++;
                    continue;
                }

                sb.Append(c);
                i++;
            }

            var last = sb.ToString().Trim();
            if (last.Length > 0) stmts.Add(last);
            return stmts;
        }

        // Dollar-tag recognition lives in ibs_compiler_common.MatchDollarTag (shared with the
        // batch-level PgDollarQuoteTracker) so the splitter and tracker agree on body boundaries.

        // True only when the reader's current result set has ≥1 column and EVERY column is the
        // PG `refcursor` type. Mixed columns (refcursor + other) return false — we print the raw
        // result then, rather than guess (matches the SR 52910 contract).
        private static bool ResultIsAllRefcursor(NpgsqlDataReader reader)
        {
            if (reader.FieldCount == 0) return false;
            System.Collections.ObjectModel.ReadOnlyCollection<NpgsqlDbColumn> schema;
            try { schema = reader.GetColumnSchema(); }
            catch { return false; }
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var pt = i < schema.Count ? schema[i].PostgresType : null;
                if (pt == null || !string.Equals(pt.Name, "refcursor", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        // Read the cursor names out of a refcursor-typed reader (all rows, all refcursor-only
        // result sets), preserving order — that's the FETCH order, matching Sybase result-set order.
        private static List<string> CollectCursorNames(NpgsqlDataReader reader)
        {
            var names = new List<string>();
            do
            {
                if (!ResultIsAllRefcursor(reader)) continue;
                while (reader.Read())
                    for (int i = 0; i < reader.FieldCount; i++)
                        if (!reader.IsDBNull(i))
                            names.Add(reader.GetString(i));
            } while (reader.NextResult());
            return names;
        }

        // Execute one statement and render its output (D6: refcursor auto-dereference).
        //
        // SBN procs ported to PG are authored as `returns setof refcursor` — one cursor per
        // Sybase result set. `select * from proc(...)` then yields a result set of cursor
        // *names* (`<unnamed portal 1>`…), and the cursors themselves die at end of the
        // producing statement's transaction. To reproduce Sybase isql's multi-resultset output,
        // when the result set is refcursor-typed we collect the names and `FETCH ALL FROM` each,
        // in order, rendering those sets through the isql formatter — the name result is not
        // printed.
        //
        // Both the producing statement and the FETCHes must run in the SAME transaction (a
        // non-holdable cursor only lives to the end of the transaction that opened it), so the
        // statement is wrapped in an explicit BEGIN/COMMIT. A single statement inside BEGIN/COMMIT
        // is autocommit-equivalent, so this is invisible for the non-refcursor path — same result,
        // same durability, locks released at commit exactly as before. We can't know a statement
        // is refcursor-typed until after it executes, hence the wrapper is unconditional.
        //
        // Everything runs synchronously so notices (raised on the reading thread) interleave with
        // rows exactly as the server sent them.
        private void RunStatement(NpgsqlConnection conn, string sql, IsqlFormatter fmt)
        {
            using var tx = conn.BeginTransaction();

            List<string>? cursorNames = null;
            using (var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 0, Transaction = tx })
            using (var reader = cmd.ExecuteReader())
            {
                if (ResultIsAllRefcursor(reader))
                    cursorNames = CollectCursorNames(reader);   // no output for the name result
                else
                    RenderReader(reader, fmt);
            } // producing reader closed; tx still open → its refcursors stay alive to FETCH

            if (cursorNames != null)
                foreach (var name in cursorNames)
                {
                    using var fcmd = new NpgsqlCommand($"FETCH ALL FROM {QuoteIdent(name)}", conn)
                        { CommandTimeout = 0, Transaction = tx };
                    using var freader = fcmd.ExecuteReader();
                    RenderReader(freader, fmt);
                }

            tx.Commit();
        }

        // Every result set an open reader carries, then the statement's row count the way isql
        // reports it: after a result set, or alone for a statement that changed rows. A statement
        // with no count (DDL, DO, SET) prints nothing.
        private void RenderReader(NpgsqlDataReader reader, IsqlFormatter fmt)
        {
            bool hadResultSet = false;
            do
            {
                if (reader.FieldCount > 0)
                {
                    hadResultSet = true;
                    RenderResultSet(reader, fmt);
                }
            } while (reader.NextResult());

            if (!hadResultSet)
            {
                int n = reader.RecordsAffected;
                fmt.Done(n >= 0 && !_nocount, Math.Max(n, 0));
            }
        }

        // How one PG column displays: its isql width, or -1 when the type has no declared width
        // (text, unconstrained varchar/numeric, bytea, json, arrays ...) and the width comes from
        // the values instead.
        private sealed class PgColumn
        {
            public string Name = "";
            public string Type = "";
            public int Width;
            public bool Right;
            public int Scale = -1;
        }

        private static readonly Regex TypeModifier = new(@"\(\s*(\d+)\s*(?:,\s*(\d+)\s*)?\)");

        private static PgColumn[] PgColumns(NpgsqlDataReader reader)
        {
            System.Collections.ObjectModel.ReadOnlyCollection<NpgsqlDbColumn>? schema = null;
            try { schema = reader.GetColumnSchema(); }
            catch { schema = null; }

            var cols = new PgColumn[reader.FieldCount];
            for (int i = 0; i < cols.Length; i++)
            {
                var c = new PgColumn { Name = reader.GetName(i) ?? "" };
                // PG names an expression column ?column?; isql shows such a column with no heading.
                if (c.Name == "?column?") c.Name = "";
                // Npgsql names the type with its modifier - "character varying(10)", "numeric(10,3)",
                // "timestamp(3) without time zone": the base name picks the mapping, the modifier
                // gives the length / precision and scale.
                var full = (reader.GetDataTypeName(i) ?? "").ToLowerInvariant();
                var mod = TypeModifier.Match(full);
                c.Type = mod.Success ? TypeModifier.Replace(full, "").Trim() : full;
                int? size = schema != null && i < schema.Count ? schema[i].ColumnSize : null;
                int? precision = schema != null && i < schema.Count ? schema[i].NumericPrecision : null;
                int? scale = schema != null && i < schema.Count ? schema[i].NumericScale : null;
                if (mod.Success)
                {
                    int first = int.Parse(mod.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    size ??= first;
                    precision ??= first;
                    if (mod.Groups[2].Success)
                        scale ??= int.Parse(mod.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                    else if (c.Type == "numeric") scale ??= 0;
                }
                c.Right = true;
                switch (c.Type)
                {
                    case "smallint": c.Width = 6; break;
                    case "integer":
                    case "oid": c.Width = 11; break;
                    case "bigint": c.Width = 20; break;
                    case "boolean": c.Width = 1; break;
                    case "real":
                    case "double precision": c.Width = 27; break;
                    case "money": c.Width = 24; break;
                    case "numeric":
                        if (precision is int p && p > 0) { c.Width = p + 3; c.Scale = scale ?? -1; }
                        else c.Width = -1;
                        break;
                    case "timestamp without time zone":
                    case "timestamp with time zone": c.Width = 31; break;
                    case "date": c.Width = 16; break;
                    case "time without time zone":
                    case "time with time zone": c.Width = 15; break;
                    case "uuid": c.Width = 36; c.Right = false; break;
                    case "character":
                    case "character varying":
                        c.Right = false;
                        c.Width = size is int n && n > 0 ? n : -1;
                        break;
                    default:
                        c.Right = false;
                        c.Width = -1;
                        break;
                }
                cols[i] = c;
            }
            return cols;
        }

        // A value as isql prints the equivalent Sybase type.
        private static string FormatValue(PgColumn c, object v)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            switch (c.Type)
            {
                case "money":
                    return IsqlFormatter.Money(Convert.ToDecimal(v, inv));
                case "numeric":
                    return v is decimal d ? IsqlFormatter.Numeric(d, c.Scale) : Convert.ToString(v, inv) ?? "";
                case "real":
                case "double precision":
                    return IsqlFormatter.Float(v is float f ? f : Convert.ToDouble(v, inv));
                case "date":
                    if (v is DateTime dd) return IsqlFormatter.DateValue(dd);
                    if (v is DateOnly donly) return IsqlFormatter.DateValue(donly.ToDateTime(TimeOnly.MinValue));
                    break;
                case "time without time zone":
                case "time with time zone":
                    if (v is TimeSpan ts) return IsqlFormatter.TimeValue(ts.Hours, ts.Minutes);
                    if (v is TimeOnly to) return IsqlFormatter.TimeValue(to.Hour, to.Minute);
                    if (v is DateTimeOffset tz) return IsqlFormatter.TimeValue(tz.Hour, tz.Minute);
                    break;
            }
            return v switch
            {
                bool b => IsqlFormatter.Bit(b),
                byte[] bytes => IsqlFormatter.Hex(bytes),
                // timestamptz arrives as UTC; isql-style output shows the client's local time.
                DateTime dt => IsqlFormatter.DateTimeValue(dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt),
                DateTimeOffset dto => IsqlFormatter.DateTimeValue(dto.LocalDateTime),
                string str => str,
                _ => Convert.ToString(v, inv) ?? ""
            };
        }

        // One result set through the isql formatter. Rows stream as they arrive when every column
        // has a declared width; a column without one is sized to its longest value (capped at
        // isql's 512), so that result set is read whole before it prints.
        private void RenderResultSet(NpgsqlDataReader reader, IsqlFormatter fmt)
        {
            var cols = PgColumns(reader);
            bool sized = cols.All(c => c.Width >= 0);
            int rowCount = 0;

            string?[] ReadRow()
            {
                var values = new string?[cols.Length];
                for (int i = 0; i < cols.Length; i++)
                    values[i] = reader.IsDBNull(i) ? null : FormatValue(cols[i], reader.GetValue(i));
                return values;
            }

            IsqlColumn[] Layout(List<string?[]>? rows) => cols.Select((c, i) =>
            {
                int width = c.Width;
                if (width < 0)
                {
                    width = 1;
                    if (rows != null)
                        foreach (var r in rows) width = Math.Max(width, (r[i] ?? "NULL").Length);
                }
                return IsqlFormatter.Column(c.Name, width, c.Right);
            }).ToArray();

            if (sized)
            {
                fmt.BeginResultSet(Layout(null));
                while (reader.Read())
                {
                    fmt.Row(ReadRow());
                    rowCount++;
                }
            }
            else
            {
                var rows = new List<string?[]>();
                while (reader.Read()) rows.Add(ReadRow());
                fmt.BeginResultSet(Layout(rows));
                foreach (var r in rows) fmt.Row(r);
                rowCount = rows.Count;
            }
            fmt.Done(!_nocount, rowCount);
        }

        // A server error in isql's layout: the SQLSTATE as the message number, ERROR as level 16
        // (FATAL 20, PANIC 21), state 1. The line is the batch line the error points at - the
        // statement's own line plus the line of the error position within it - or, for an error
        // raised inside a PL/pgSQL function, that function and its line, as isql reports a proc.
        private static void EmitPgException(PostgresException ex, IsqlFormatter fmt, string sent, int stmtLine, int prefixLength)
        {
            string? proc = null;
            int line = stmtLine;
            var where = ex.Where ?? "";
            var m = PlpgsqlWhere.Match(where);
            if (m.Success)
            {
                int fnLine = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (m.Groups[1].Value == "inline_code_block")
                    line = stmtLine + fnLine - 1;    // a DO block: its body's line, from the statement
                else
                {
                    proc = m.Groups[1].Value;
                    line = fnLine;
                }
            }
            else if (ex.Position > 0)
            {
                int pos = Math.Min(ex.Position - 1 - prefixLength, sent.Length - prefixLength);
                if (pos > 0) line = stmtLine + sent.Substring(prefixLength, pos).Count(ch => ch == '\n');
            }
            int level = (ex.Severity ?? "").ToUpperInvariant() switch
            {
                "FATAL" => 20,
                "PANIC" => 21,
                _ => 16
            };
            fmt.Error(ex.SqlState, level, 1, null, proc, line, ex.MessageText);
        }

        private static readonly Regex PlpgsqlWhere = new(@"PL/pgSQL function ([^\s(]+)\(.*?\) line (\d+)", RegexOptions.IgnoreCase);

        public void Dispose()
        {
            _persistentConn?.Dispose();
            _persistentConn = null;
        }
    }
}
