using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using AdoNetCore.AseClient;
using ibsCompiler.Configuration;

namespace ibsCompiler.Database
{
    public class SybaseExecutor : ISqlExecutor
    {
        private readonly ResolvedProfile _profile;
        private static readonly Regex GoRegex = new(@"^\s*go\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex ExitRegex = new(@"^\s*(exit|quit)\s*$", RegexOptions.IgnoreCase);

        // Per-call output sink wired up by ExecuteSql/ExecuteBatch. The persistent
        // connection's InfoMessage handler routes through this so PRINT/RAISERROR
        // messages stream as the server emits them.
        private Action<string>? _emit;

        // True once the session has a diagnostic mode active (set showplan/statistics/
        // noexec ON). ASE keeps these for the life of the connection; tracked across
        // batches by UpdateDiagnostics.
        private bool _diagnosticsActive;

        // Whether to skip GetSchemaTable() for the batch currently streaming. GetSchemaTable
        // forces AseClient to flush the server's plan tokens for an internal metadata pass —
        // with showplan on that dumps ~25 plan blocks the user never ran. We skip it only
        // when diagnostics were ALREADY active before this batch: then the batch's own
        // statement plan flushes naturally during execution and the metadata noise is gone.
        // When the SAME batch both enables showplan and runs the query, skipping would lose
        // the user's plan too (it only surfaces via the flush), so that case is left as-is.
        private bool _skipSchemaForCurrentBatch;

        // Persistent connection for batch-at-a-time execution (OpenConnection/ExecuteBatch/CloseConnection)
        private AseConnection? _persistentConn;

        // Width cap for streamed result-set columns (mirrors isql -w300).
        private const int MaxColumnWidth = 256;

        // Minimum display width per .NET type. The schema's ColumnSize reports byte
        // size for fixed-length types (datetime=8, int=4, etc.), which is too small
        // for the rendered string. Returns 0 for variable-length types (string, byte[])
        // — they rely on ColumnSize directly.
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

        public SybaseExecutor(ResolvedProfile profile)
        {
            _profile = profile;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        // DATA_CHARSET reinterpretation. AseClient decodes/encodes char/varchar using the
        // server's declared charset (e.g. cp850). When the data is actually stored in a
        // different single-byte code page (e.g. Windows-1252 entered by ANSI Windows
        // clients), the server's lens turns it to mojibake (0xE9 'é' -> 'Ú'). Because the
        // client charset == server charset there is NO server-side conversion: the bytes
        // round-trip faithfully and the server encoding is a lossless bijection. So we can
        // recover the true text in .NET by re-encoding to the server charset (back to the
        // raw bytes) and decoding as the real data charset — and inversely on write.
        private bool _csInit;
        private bool _reinterpret;
        private Encoding? _serverEnc;     // charset AseClient used (server default)
        private Encoding? _dataEnc;       // actual data code page (profile DATA_CHARSET)
        private string? _serverCharsetName;

        private void EnsureCharsets()
        {
            if (_csInit) return;
            _csInit = true;
            if (string.IsNullOrWhiteSpace(_profile.DataCharset)) return;
            _dataEnc = CharsetToEncoding(_profile.DataCharset);
            _serverEnc = CharsetToEncoding(DetectServerCharset());
            // Only valid when the server charset is single-byte (GetBytes is a lossless
            // bijection). A UTF-8 server already yields correct Unicode — never reinterpret.
            _reinterpret = _dataEnc != null && _serverEnc != null
                && _serverEnc.CodePage != 65001
                && _dataEnc.CodePage != _serverEnc.CodePage;
        }

        private static Encoding? CharsetToEncoding(string name)
        {
            switch (name.Trim().ToLowerInvariant())
            {
                case "cp850": case "850": return SafeEnc(850);
                case "cp1252": case "1252": case "windows-1252": return SafeEnc(1252);
                case "iso_1": case "iso-8859-1": case "latin1": return SafeEnc(28591);
                case "cp1250": case "1250": return SafeEnc(1250);
                case "cp1251": case "1251": return SafeEnc(1251);
                case "cp1253": case "1253": return SafeEnc(1253);
                case "utf8": case "utf-8": return SafeEnc(65001);
                default: return int.TryParse(name.Trim(), out var n) ? SafeEnc(n) : null;
            }
        }

        private static Encoding? SafeEnc(int cp)
        {
            try { return Encoding.GetEncoding(cp); } catch { return null; }
        }

        // Server's declared default charset, via syscharsets (config 131 = default charset id).
        private string DetectServerCharset()
        {
            if (_serverCharsetName != null) return _serverCharsetName;
            try
            {
                using var conn = new AseConnection(BuildConnectionString(""));
                conn.Open();
                using var cmd = new AseCommand(
                    "select cs.name from master.dbo.syscharsets cs, master.dbo.syscurconfigs cur " +
                    "where cur.config = 131 and cs.id = cur.value", conn);
                _serverCharsetName = (cmd.ExecuteScalar()?.ToString() ?? "cp850").Trim();
            }
            catch { _serverCharsetName = "cp850"; }
            return _serverCharsetName;
        }

        // Raw bytes (re-encode to server charset) reinterpreted as the real data charset.
        private string FromServer(string s)
        {
            EnsureCharsets();
            return _reinterpret ? _dataEnc!.GetString(_serverEnc!.GetBytes(s)) : s;
        }

        // True text encoded to the real data charset, expressed as the server-charset
        // string AseClient will faithfully store as those bytes.
        private string ToServer(string s)
        {
            EnsureCharsets();
            return _reinterpret ? _serverEnc!.GetString(_dataEnc!.GetBytes(s)) : s;
        }

        // CHARSET POLICY — do NOT hardcode a charset (e.g. "utf8") in the connection string.
        // The Sybase TDS protocol negotiates charset automatically: when the client omits it,
        // the server responds with its configured charset via a TDS_ENV_CHARSET token, and
        // EnvChangeTokenHandler + CodePagesEncodingProvider resolve the correct .NET Encoding.
        // Hardcoding utf8 causes connection failures on servers running other codepages (e.g.
        // cp850) because the server rejects the mismatch outright. Letting the server decide
        // is the only universal approach — it works for utf8, cp850, iso_1, and any other
        // charset the server is configured with.
        private string BuildConnectionString(string database)
        {
            var sb = new StringBuilder();
            sb.Append($"Data Source={_profile.Host}");
            sb.Append($";Port={_profile.Port}");
            sb.Append($";User ID={_profile.User}");
            sb.Append($";Password={_profile.Pass}");
            if (!string.IsNullOrEmpty(database))
                sb.Append($";Database={database}");
            sb.Append(";Pooling=false");
            return sb.ToString();
        }

        public ExecReturn ExecuteSql(string sqlText, string database, bool captureOutput = false, string outputFile = "")
        {
            var result = new ExecReturn { Returncode = true, Output = "" };
            var output = new StringBuilder();
            var sink = OutputSink.Build(output, captureOutput, outputFile);
            _emit = sink.Emit;

            try
            {
                using var connection = new AseConnection(BuildConnectionString(database));
                connection.InfoMessage += OnInfoMessage;
                connection.Open();
                _diagnosticsActive = false; // fresh session starts with diagnostics off

                var batches = SplitBatches(sqlText);
                foreach (var batch in batches)
                {
                    if (string.IsNullOrWhiteSpace(batch)) continue;
                    if (ExitRegex.IsMatch(batch.Trim())) break;
                    _skipSchemaForCurrentBatch = _diagnosticsActive; // state BEFORE this batch
                    UpdateDiagnostics(batch);
                    try
                    {
                        using var cmd = new AseCommand(batch, connection);
                        cmd.CommandTimeout = 0;

                        // Use the SBNAlgen streaming pattern verbatim: ExecuteReaderAsync
                        // returning a DbDataReader, then ReadAsync/NextResultAsync on the
                        // base type so the provider can surface server tokens incrementally.
                        StreamCommandAsync(cmd, sink.Emit).GetAwaiter().GetResult();
                    }
                    catch (AseException ex)
                    {
                        EmitAseException(ex, sink.Emit);
                        result.Returncode = false;
                    }
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
            _persistentConn = new AseConnection(BuildConnectionString(database));
            _persistentConn.InfoMessage += OnInfoMessage;
            _persistentConn.Open();
            _diagnosticsActive = false; // fresh session starts with diagnostics off
        }

        public ExecReturn ExecuteBatch(string batch, bool captureOutput = false, string outputFile = "")
        {
            var result = new ExecReturn { Returncode = true, Output = "" };
            var output = new StringBuilder();
            var sink = OutputSink.Build(output, captureOutput, outputFile);
            _emit = sink.Emit;

            try
            {
                if (!string.IsNullOrWhiteSpace(batch))
                {
                    _skipSchemaForCurrentBatch = _diagnosticsActive; // state BEFORE this batch
                    UpdateDiagnostics(batch);
                    using var cmd = new AseCommand(batch, _persistentConn);
                    cmd.CommandTimeout = 0;

                    StreamCommandAsync(cmd, sink.Emit).GetAwaiter().GetResult();
                }
            }
            catch (AseException ex)
            {
                EmitAseException(ex, sink.Emit);
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

        // Track whether the session has a plan/diagnostic mode turned on, by scanning the
        // SQL we're about to run. ASE keeps these settings for the life of the connection,
        // so the flag persists across batches until explicitly turned off. Last directive
        // in the text wins (handles "set showplan on ... set showplan off" in one batch).
        private static readonly Regex DiagOnRegex = new(
            @"\bset\s+(showplan|noexec|statistics\s+\w+)\s+on\b", RegexOptions.IgnoreCase);
        private static readonly Regex DiagOffRegex = new(
            @"\bset\s+(showplan|noexec|statistics\s+\w+)\s+off\b", RegexOptions.IgnoreCase);

        private void UpdateDiagnostics(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return;
            int lastOn = -1, lastOff = -1;
            var mOn = DiagOnRegex.Matches(sql);
            if (mOn.Count > 0) lastOn = mOn[mOn.Count - 1].Index;
            var mOff = DiagOffRegex.Matches(sql);
            if (mOff.Count > 0) lastOff = mOff[mOff.Count - 1].Index;
            if (lastOn >= 0 && lastOn > lastOff) _diagnosticsActive = true;
            else if (lastOff >= 0 && lastOff > lastOn) _diagnosticsActive = false;
        }

        private void OnInfoMessage(object sender, AseInfoMessageEventArgs e)
        {
            var emit = _emit;
            if (emit == null) return;
            foreach (AseError err in e.Errors)
            {
                if (err.Severity >= 11) continue; // errors handled by AseException catch
                var msg = err.Message;
                if (msg.StartsWith("Changed client character set") ||
                    msg.StartsWith("Changed database context") ||
                    msg.StartsWith("Changed language setting"))
                    continue;
                emit(msg);
            }
        }

        private static void EmitAseException(AseException ex, Action<string> emit)
        {
            foreach (AseError err in ex.Errors)
            {
                emit($"Msg {err.MessageNumber}, Level {err.Severity}, State {err.State}");
                emit(err.Message);
            }
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

        private void BulkCopyIn(string table, string dataFile)
        {
            string database = "";
            string tableName = table;
            if (table.Contains(".."))
            {
                var parts = table.Split(new[] { ".." }, 2, StringSplitOptions.None);
                database = parts[0];
                tableName = parts[1];
            }

            var connStr = BuildConnectionString(database);
            using var connection = new AseConnection(connStr);
            connection.Open();

            using var bulkCopy = new AseBulkCopy(connection)
            {
                DestinationTableName = tableName,
                BatchSize = 0,
                NotifyAfter = 1000
            };

            bulkCopy.AseRowsCopied += (sender, e) =>
            {
                ibs_compiler_common.WriteLine($"{e.RowsCopied} rows sent to the server.");
            };

            // Read tab-delimited data file and load into DataTable (all string columns)
            // Server handles type conversion during BCP insert
            var lines = File.ReadAllLines(dataFile);
            if (lines.Length == 0) return;

            var dataTable = new DataTable();
            var firstCols = lines[0].Split('\t');
            int colCount = firstCols.Length;
            for (int i = 0; i < colCount; i++)
                dataTable.Columns.Add($"col{i}", typeof(string));

            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line)) continue;
                var cols = line.Split('\t');

                // If data has more fields than table columns, merge extras into last column
                if (cols.Length > colCount && colCount > 0)
                {
                    var merged = new string[colCount];
                    for (int i = 0; i < colCount - 1; i++)
                        merged[i] = cols[i];
                    merged[colCount - 1] = string.Join("\t", cols.Skip(colCount - 1));
                    cols = merged;
                }

                var row = dataTable.NewRow();
                for (int i = 0; i < Math.Min(cols.Length, colCount); i++)
                    row[i] = ToServer(cols[i]);
                dataTable.Rows.Add(row);
            }

            bulkCopy.WriteToServer(dataTable);

            ibs_compiler_common.WriteLine("");
            ibs_compiler_common.WriteLine($"{dataTable.Rows.Count} rows copied.");
        }

        private int BulkCopyOut(string table, string dataFile)
        {
            string database = "";
            string tableName = table;
            if (table.Contains(".."))
            {
                var parts = table.Split(new[] { ".." }, 2, StringSplitOptions.None);
                database = parts[0];
                tableName = parts[1];
            }

            var connStr = BuildConnectionString(database);
            using var connection = new AseConnection(connStr);
            connection.Open();

            using var cmd = new AseCommand($"SELECT * FROM {tableName}", connection);
            using var reader = cmd.ExecuteReader();
            using var writer = ibs_compiler_common.OpenSourceWriter(dataFile);

            int rowCount = 0;
            while (reader.Read())
            {
                var values = new string[reader.FieldCount];
                for (int i = 0; i < reader.FieldCount; i++)
                    values[i] = FromServer(reader.IsDBNull(i) ? "" : reader[i].ToString() ?? "");
                writer.WriteLine(string.Join("\t", values));
                rowCount++;
                if (rowCount % 1000 == 0)
                    ibs_compiler_common.WriteLine($"{rowCount} rows successfully extracted to {dataFile}");
            }
            return rowCount;
        }

        private static string[] SplitBatches(string sqlText)
        {
            return GoRegex.Split(sqlText)
                .Where(b => !string.IsNullOrWhiteSpace(b))
                .ToArray();
        }

        private async System.Threading.Tasks.Task StreamCommandAsync(AseCommand cmd, Action<string> emit)
        {
            // SequentialAccess is required for streaming. Without it, the AseClient
            // runs the entire token loop synchronously and only returns the reader
            // after the whole batch is buffered. With it, the client spawns a background
            // pump (InternalConnection.cs:487) and StreamingDataReaderTokenHandler
            // surfaces each result-set as soon as DoneInProc arrives.
            //
            // Note on streaming granularity: Sybase ASE coalesces small (<TDS packet)
            // result-sets into one TCP packet, so a proc emitting tiny rows in a loop
            // will not stream per-row — the server flushes when the packet fills or
            // the proc completes. Real workloads (ma_algen-style queue rows) typically
            // exceed the packet threshold and stream as expected.
            using var raw = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess);
            using var reader = (System.Data.Common.DbDataReader)raw;
            do
            {
                if (reader.FieldCount > 0)
                    await StreamOneResultSetAsync(reader, emit);
            } while (await reader.NextResultAsync());
        }

        private async System.Threading.Tasks.Task StreamOneResultSetAsync(System.Data.Common.DbDataReader reader, Action<string> emit)
        {
            int colCount = reader.FieldCount;
            // GetSchemaTable() forces AseClient to flush the server's plan tokens for an
            // internal metadata pass. With `set showplan on` (or statistics/noexec) active
            // that dumps ~25 plan blocks the user never ran, burying their own statement's
            // plan. While diagnostics are active we skip it and fall back to type-based
            // column widths (no data truncation — strings still cap at MaxColumnWidth);
            // exact declared widths are only needed for ordinary result grids, not when
            // the user is reading a query plan.
            System.Data.DataTable? schema = _skipSchemaForCurrentBatch ? null : reader.GetSchemaTable();
            var widths = new int[colCount];
            var names = new string[colCount];
            var isNumeric = new bool[colCount];

            for (int i = 0; i < colCount; i++)
            {
                names[i] = reader.GetName(i);
                var t = reader.GetFieldType(i);
                isNumeric[i] = t == typeof(int) || t == typeof(long) || t == typeof(short) ||
                               t == typeof(decimal) || t == typeof(double) || t == typeof(float) ||
                               t == typeof(byte);

                // Default when no schema (diagnostics mode / lookup failure): use the
                // type's known render width for fixed types, else the variable-width cap
                // so long strings (e.g. sysname) are never truncated.
                int colSize = schema == null
                    ? (MinDisplayWidthForType(t) > 0 ? MinDisplayWidthForType(t) : MaxColumnWidth)
                    : 30;
                if (schema != null && i < schema.Rows.Count)
                {
                    var raw = schema.Rows[i]["ColumnSize"];
                    if (raw != DBNull.Value)
                    {
                        try
                        {
                            int cs = Convert.ToInt32(raw);
                            if (cs > 0) colSize = Math.Min(cs, MaxColumnWidth);
                        }
                        catch { }
                    }
                }
                // ColumnSize reports BYTE size for fixed-length types (datetime=8, int=4)
                // — too small for the printed form. Use the type's render width when larger.
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
