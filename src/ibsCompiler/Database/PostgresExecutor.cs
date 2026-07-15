using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using Npgsql.Schema;
using ibsCompiler.Configuration;

namespace ibsCompiler.Database
{
    /// <summary>
    /// PostgreSQL executor (Npgsql). Mirrors MssqlExecutor's shape — streaming result
    /// sets, per-call OutputSink wiring, GO-batch splitting — with the PG-specific
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

        // Width cap for streamed result-set columns (mirrors isql -w300).
        private const int MaxColumnWidth = 256;

        // Minimum display width per .NET type. ColumnSize reports byte size for fixed-length
        // types, which is too small for the rendered string. Returns 0 for variable-length
        // types (string, byte[]) — they rely on ColumnSize directly.
        private static int MinDisplayWidthForType(Type t)
        {
            if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return 26;
            if (t == typeof(Guid)) return 36;
            if (t == typeof(bool)) return 5;
            if (t == typeof(long) || t == typeof(decimal) || t == typeof(double)) return 24;
            if (t == typeof(int)) return 11;
            if (t == typeof(short)) return 6;
            if (t == typeof(byte)) return 3;
            if (t == typeof(float)) return 14;
            if (t == typeof(TimeSpan)) return 16;
            return 0;
        }

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

                foreach (var chunk in SplitBatches(sqlText))
                {
                    if (string.IsNullOrWhiteSpace(chunk)) continue;
                    if (ExitRegex.IsMatch(chunk.Trim())) break;
                    RunChunk(conn, chunk, sink.Emit, result);
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
                    RunChunk(_persistentConn, batch, sink.Emit, result);
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
        private void RunChunk(NpgsqlConnection conn, string chunk, Action<string> emit, ExecReturn result)
        {
            foreach (var stmt in SplitStatements(chunk))
            {
                // `use <db>` → schema switch (D1). Only statement-level substitution.
                var useM = UseRegex.Match(stmt);
                if (useM.Success)
                {
                    try
                    {
                        using var uc = new NpgsqlCommand($"SET search_path TO {useM.Groups[1].Value}, public", conn);
                        uc.ExecuteNonQuery();
                    }
                    catch (PostgresException ex)
                    {
                        EmitPgException(ex, emit);
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

                var sql = ApplyDiagPrefix(stmt);
                try
                {
                    using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 0 };
                    StreamCommandAsync(cmd, emit).GetAwaiter().GetResult();
                }
                catch (PostgresException ex)
                {
                    EmitPgException(ex, emit);
                    result.Returncode = false;
                    return; // abort remainder of this GO-chunk
                }
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
            emit(e.Notice.MessageText);
        }

        // Mirror EmitSqlException's shape as closely as PG fields allow: a header line then the
        // message text, plus Where/Line when the server supplied them.
        private static void EmitPgException(PostgresException ex, Action<string> emit)
        {
            var header = $"Msg {ex.SqlState}, Severity {ex.Severity}";
            if (!string.IsNullOrEmpty(ex.Line)) header += $", Line {ex.Line}";
            emit(header);
            emit(ex.MessageText);
            if (!string.IsNullOrEmpty(ex.Where)) emit(ex.Where);
        }

        public ExecReturn BulkCopy(string table, BcpDirection direction, string dataFile, string formatFile = "")
        {
            var result = new ExecReturn { Returncode = true, Output = "" };

            try
            {
                if (direction == BcpDirection.IN)
                {
                    BulkCopyIn(table, dataFile);
                }
                else
                {
                    var rows = BulkCopyOut(table, dataFile);
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

        private void BulkCopyIn(string table, string dataFile)
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

            var lines = File.ReadAllLines(dataFile);
            if (lines.Length == 0) return;
            int colCount = columnTypes.Count > 0 ? columnTypes.Count : lines[0].Split('\t').Length;

            int total = 0;
            // Text-format COPY: write tab-separated, PG-escaped rows straight to STDIN.
            using (var writer = conn.BeginTextImport($"COPY {target} FROM STDIN (FORMAT text)"))
            {
                foreach (var line in lines)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    var cols = line.Split('\t');

                    // Extra fields merge into the last column (native BCP behavior).
                    if (cols.Length > colCount && colCount > 0)
                    {
                        var merged = new string[colCount];
                        for (int i = 0; i < colCount - 1; i++)
                            merged[i] = cols[i];
                        merged[colCount - 1] = string.Join("\t", cols.Skip(colCount - 1));
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
                    writer.Write(sb.ToString());

                    total++;
                    if (total % 1000 == 0)
                        ibs_compiler_common.WriteLine($"{total} rows sent to the server.");
                }
            } // dispose completes the COPY

            ibs_compiler_common.WriteLine("");
            ibs_compiler_common.WriteLine($"{total} rows copied.");
        }

        private int BulkCopyOut(string table, string dataFile)
        {
            using var conn = OpenBulkConnection();
            var target = ResolveCopyTarget(table);

            using var reader = conn.BeginTextExport($"COPY (SELECT * FROM {target}) TO STDOUT (FORMAT text)");
            using var writer = ibs_compiler_common.OpenSourceWriter(dataFile);

            int rowCount = 0;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                // Unescape PG text-format escapes so the file matches the raw tab-delimited
                // form the other executors write.
                writer.WriteLine(UnescapeCopyLine(line));
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
        // Split on the literal tabs first, then unescape each field, then rejoin.
        private static string UnescapeCopyLine(string line)
        {
            var fields = line.Split('\t');
            for (int i = 0; i < fields.Length; i++)
                fields[i] = UnescapeCopyField(fields[i]);
            return string.Join("\t", fields);
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
                    var tag = MatchDollarTag(chunk, i);
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

        // At s[i] == '$', return the dollar-quote tag ("$$", "$fn$", ...) or null. A digit
        // immediately after '$' means a positional parameter ($1), not a tag.
        private static string? MatchDollarTag(string s, int i)
        {
            int j = i + 1;
            if (j < s.Length && char.IsDigit(s[j])) return null;
            while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
            if (j < s.Length && s[j] == '$') return s.Substring(i, j - i + 1);
            return null;
        }

        private static async System.Threading.Tasks.Task StreamCommandAsync(NpgsqlCommand cmd, Action<string> emit)
        {
            using var reader = await cmd.ExecuteReaderAsync();
            bool hadResultSet = false;
            do
            {
                if (reader.FieldCount > 0)
                {
                    hadResultSet = true;
                    await StreamOneResultSetAsync(reader, emit);
                }
            } while (await reader.NextResultAsync());

            if (!hadResultSet)
            {
                int n = reader.RecordsAffected;
                if (n < 0) n = 0;
                var rowWord = n == 1 ? "row" : "rows";
                emit($"({n} {rowWord} affected)");
                emit("");
            }
        }

        private static async System.Threading.Tasks.Task StreamOneResultSetAsync(NpgsqlDataReader reader, Action<string> emit)
        {
            int colCount = reader.FieldCount;

            // PG fix (1): never GetSchemaTable() — use GetColumnSchema() guarded by try/catch,
            // falling back to type-based widths on any failure (Sybase no-schema pattern).
            System.Collections.ObjectModel.ReadOnlyCollection<NpgsqlDbColumn>? schema = null;
            try { schema = reader.GetColumnSchema(); }
            catch { schema = null; }

            var widths = new int[colCount];
            var names = new string[colCount];
            var isNumeric = new bool[colCount];

            for (int i = 0; i < colCount; i++)
            {
                names[i] = reader.GetName(i);
                var t = reader.GetFieldType(i);
                isNumeric[i] = IsNumericType(t);

                int colSize;
                if (schema != null && i < schema.Count)
                {
                    var cs = schema[i].ColumnSize;
                    // PG fix (2): unbounded text/varchar (ColumnSize null or <= 0) → MaxColumnWidth,
                    // NOT the 30 default — silent truncation of text columns is a known trap.
                    colSize = (cs.HasValue && cs.Value > 0) ? Math.Min(cs.Value, MaxColumnWidth) : MaxColumnWidth;
                }
                else
                {
                    // No schema: type-based render width, else the variable-width cap.
                    colSize = MinDisplayWidthForType(t) > 0 ? MinDisplayWidthForType(t) : MaxColumnWidth;
                }

                widths[i] = Math.Max(Math.Max(Math.Max(colSize, names[i].Length), 10), MinDisplayWidthForType(t));
            }

            var line = new StringBuilder();

            // Header
            for (int i = 0; i < colCount; i++)
            {
                if (i > 0) line.Append(' ');
                line.Append(isNumeric[i] ? names[i].PadLeft(widths[i]) : names[i].PadRight(widths[i]));
            }
            emit(line.ToString());

            // Separator
            line.Clear();
            for (int i = 0; i < colCount; i++)
            {
                if (i > 0) line.Append(' ');
                line.Append(new string('-', widths[i]));
            }
            emit(line.ToString());

            // Stream rows as the server delivers them.
            int rowCount = 0;
            while (await reader.ReadAsync())
            {
                line.Clear();
                for (int i = 0; i < colCount; i++)
                {
                    string val = reader.IsDBNull(i) ? "NULL" : (reader[i].ToString() ?? "").TrimEnd();
                    if (val.Length > widths[i]) val = val.Substring(0, widths[i]);
                    if (i > 0) line.Append(' ');
                    line.Append(isNumeric[i] ? val.PadLeft(widths[i]) : val.PadRight(widths[i]));
                }
                emit(line.ToString());
                rowCount++;
            }

            var rowWord = rowCount == 1 ? "row" : "rows";
            emit($"({rowCount} {rowWord} affected)");
            emit("");
        }

        public void Dispose()
        {
            _persistentConn?.Dispose();
            _persistentConn = null;
        }
    }
}
