using System.Collections.Generic;
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

        // isql measures, pads and cuts in bytes of the session charset and writes the server's
        // bytes as they are. So the formatter is fed every server string as "byte text" - one
        // character per byte of its session-charset encoding (Latin-1 maps bytes to characters
        // one to one) - and the lines it emits go out as those bytes (OutputSink.EmitRaw).
        // _sessionEnc is the current call's session charset; null until the connection is open.
        private static readonly Encoding ByteText = Encoding.Latin1;
        private Encoding? _sessionEnc;

        private string? B(string? s) =>
            s == null || _sessionEnc == null ? s : ByteText.GetString(_sessionEnc.GetBytes(s));

        // Wires the sink to the session's charset and returns the formatter's line emitter.
        private Action<string> UseSessionCharset(OutputSink sink, Encoding? enc)
        {
            _sessionEnc = enc;
            sink.Encoding = enc;
            _emit = enc == null ? sink.Emit : line => sink.EmitRaw(ByteText.GetBytes(line));
            return _emit;
        }

        // Severe errors already written for the batch currently executing, and whether any
        // were seen at all. Reset per batch by BeginBatch.
        private readonly HashSet<string> _batchErrorKeys = new();
        private bool _batchHadError;

        private void BeginBatch()
        {
            _batchErrorKeys.Clear();
            _batchHadError = false;
        }

        // Persistent connection for batch-at-a-time execution (OpenConnection/ExecuteBatch/CloseConnection)
        private AseConnection? _persistentConn;

        // isql-style renderer for the batch currently executing. Server messages (InfoMessage)
        // go through it too, so they wrap and interleave with result sets exactly as isql's do.
        private IsqlFormatter? _fmt;

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
        private const string ServerCharsetSql =
            "select cs.name from master.dbo.syscharsets cs, master.dbo.syscurconfigs cur " +
            "where cur.config = 131 and cs.id = cur.value";

        private string DetectServerCharset()
        {
            if (_serverCharsetName != null) return _serverCharsetName;
            try
            {
                using var conn = new AseConnection(BuildConnectionString(""));
                conn.Open();
                using var cmd = new AseCommand(ServerCharsetSql, conn);
                _serverCharsetName = (cmd.ExecuteScalar()?.ToString() ?? "cp850").Trim();
            }
            catch { _serverCharsetName = "cp850"; }
            return _serverCharsetName;
        }

        // The server charset read over an open session; null when it cannot be read or has no
        // .NET encoding (batch text then goes as it is).
        private static Encoding? ReadServerEncoding(AseConnection connection)
        {
            try
            {
                // Read through DONE, as RunBatch does: a streaming reader left early breaks the session.
                string? name = null;
                using var cmd = new AseCommand(ServerCharsetSql, connection);
                cmd.ResultObserver = ev => { if (ev.Kind == AseResultEventKind.Row && ev.Values.Length > 0) name ??= ev.Values[0]?.ToString(); };
                cmd.ExecuteNonQuery();
                return string.IsNullOrWhiteSpace(name) ? null : CharsetToEncoding(name);
            }
            catch (AseException) { return null; }
        }

        // A character the server charset cannot hold goes as its UTF-8 bytes, as legacy isql -J sent it
        // (docs/architecture.md § Charset); commentsOnly on a utf8 session, where literals go as they are.
        private static string KeepUnconvertible(string batch, Encoding? serverEnc, bool commentsOnly)
        {
            if (serverEnc == null || serverEnc.CodePage == 65001) return batch;
            StringBuilder? sb = null;
            int depth = 0;      // block-comment nesting
            bool line = false;  // inside a -- comment
            char quote = '\0';  // inside a '...' or "..." literal
            for (int i = 0; i < batch.Length; i++)
            {
                char c = batch[i];
                if (c >= 0x80)
                {
                    int n = char.IsHighSurrogate(c) && i + 1 < batch.Length && char.IsLowSurrogate(batch[i + 1]) ? 2 : 1;
                    var unit = batch.Substring(i, n);
                    var bytes = (!commentsOnly || line || depth > 0) && serverEnc.GetString(serverEnc.GetBytes(unit)) != unit
                        ? UnconvertibleBytes(unit, serverEnc) : null;
                    if (bytes != null)
                    {
                        sb ??= new StringBuilder(batch, 0, i, batch.Length + 16);
                        sb.Append(serverEnc.GetString(bytes));
                    }
                    else sb?.Append(unit);
                    i += n - 1;
                    continue;
                }
                sb?.Append(c);
                char next = i + 1 < batch.Length ? batch[i + 1] : '\0';
                bool pair = false;
                if (quote != '\0') { if (c == quote) { if (next == quote) pair = true; else quote = '\0'; } }
                else if (line) { if (c == '\n') line = false; }
                else if (c == '/' && next == '*') { depth++; pair = true; }
                else if (depth > 0) { if (c == '*' && next == '/') { depth--; pair = true; } }
                else if (c == '-' && next == '-') { line = true; pair = true; }
                else if (c == '\'' || c == '"') quote = c;
                if (pair) { sb?.Append(next); i++; }
            }
            return sb?.ToString() ?? batch;
        }

        // The UTF-8 bytes to send for an unconvertible character, or null to send it as it is (the
        // server then refuses it): U+FFFD and lone surrogates are decode damage, not the source's
        // character, and a charset that cannot carry the bytes unchanged would swap in others.
        private static byte[]? UnconvertibleBytes(string unit, Encoding serverEnc)
        {
            if (unit[0] == '\uFFFD' || (unit.Length == 1 && char.IsSurrogate(unit[0]))) return null;
            var bytes = Encoding.UTF8.GetBytes(unit);
            return serverEnc.GetBytes(serverEnc.GetString(bytes)).AsSpan().SequenceEqual(bytes) ? bytes : null;
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
        // streaming: the driver hands each row over as it arrives instead of holding a result set
        // until it is complete (fork option, MOD 08.96.12856) - for SQL whose output is watched live.
        // charset: the client charset to ask for; empty lets the server use its own.
        private string BuildConnectionString(string database, bool streaming = false, string charset = "")
        {
            var sb = new StringBuilder();
            sb.Append($"Data Source={_profile.Host}");
            sb.Append($";Port={_profile.Port}");
            sb.Append($";User ID={_profile.User}");
            sb.Append($";Password={_profile.Pass}");
            if (!string.IsNullOrEmpty(database))
                sb.Append($";Database={database}");
            sb.Append(";Pooling=false");
            if (streaming) sb.Append(";StreamingResults=true");
            if (!string.IsNullOrEmpty(charset)) sb.Append($";Charset={charset}");
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
                using var connection = new AseConnection(BuildConnectionString(database, streaming: true));
                connection.InfoMessage += OnInfoMessage;
                connection.Open();
                var emit = UseSessionCharset(sink, connection.CharsetEncoding);
                FlushMessages(connection);

                int width = IsqlFormatter.ResolveWidth(captureOutput, outputFile);
                var batches = SplitBatches(sqlText);
                foreach (var batch in batches)
                {
                    if (string.IsNullOrWhiteSpace(batch)) continue;
                    if (ExitRegex.IsMatch(batch.Trim())) break;
                    if (!RunBatch(connection, batch, emit, width, utf8: false)) result.Returncode = false;
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
                _sessionEnc = null;
                sink.Dispose();
            }

            result.Output = output.ToString();
            return result;
        }

        // The persistent (runsql) session is utf8 - see OpenConnection - and the server's own
        // charset behind it (KeepUnconvertible).
        private bool _persistentUtf8;
        private Encoding? _persistentServerEnc;

        public void OpenConnection(string database)
        {
            // Legacy runsql ran isql -Jutf8 ($BCPJ), so its session used the utf8 client charset:
            // the server converts char data to UTF-8 and sizes char columns for it (3 bytes a
            // character), which is what runsql output shows. (Legacy isqlline passed -J and so
            // used the server's own charset, as ExecuteSql does.) A server that refuses utf8
            // gets its own charset instead.
            try
            {
                _persistentConn = OpenPersistent(database, "utf8");
                _persistentUtf8 = true;
                _persistentServerEnc = ReadServerEncoding(_persistentConn);
            }
            catch (AseException ex) when (!ex.Message.Contains("Login failed", StringComparison.OrdinalIgnoreCase))
            {
                // Only a charset refusal retries: a bad login or a timeout would just fail twice.
                _persistentConn = OpenPersistent(database, "");
                _persistentUtf8 = false;
            }
            FlushMessages(_persistentConn);
        }

        private AseConnection OpenPersistent(string database, string charset)
        {
            var conn = new AseConnection(BuildConnectionString(database, streaming: true, charset: charset));
            conn.InfoMessage += OnInfoMessage;
            try
            {
                conn.Open();
            }
            catch
            {
                conn.Dispose();
                throw;
            }
            return conn;
        }

        // ASE holds print/raiserror output in the session's network buffer until it fills or
        // the batch ends, so a long-running proc's progress arrives in bursts. flushmessage
        // makes the server send each message as it is raised - output stays real time.
        private static void FlushMessages(AseConnection connection)
        {
            try
            {
                using var cmd = new AseCommand("set flushmessage on", connection);
                cmd.ExecuteNonQuery();
            }
            catch (AseException) { } // a server without the option still runs, just buffered
        }

        public ExecReturn ExecuteBatch(string batch, bool captureOutput = false, string outputFile = "")
        {
            var result = new ExecReturn { Returncode = true, Output = "" };
            var output = new StringBuilder();
            var sink = OutputSink.Build(output, captureOutput, outputFile);
            _emit = sink.Emit;

            var emit = _persistentConn != null ? UseSessionCharset(sink, _persistentConn.CharsetEncoding) : sink.Emit;
            try
            {
                if (!string.IsNullOrWhiteSpace(batch) && _persistentConn != null)
                    result.Returncode = RunBatch(_persistentConn, batch, emit,
                        IsqlFormatter.ResolveWidth(captureOutput, outputFile), _persistentUtf8);
            }
            finally
            {
                _emit = null;
                _sessionEnc = null;
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

        // Runs one batch the way isql does: every result token goes to the isql formatter in
        // server order as it arrives (row formats, rows, done counts, return statuses), and the
        // InfoMessage handler feeds it the batch's messages and errors on the same thread, so
        // the two interleave exactly as the server sent them. Returns false when the batch
        // raised a server error.
        // utf8: the session's client charset is utf8 (changes how unichar data displays).
        private bool RunBatch(AseConnection connection, string batch, Action<string> emit, int width, bool utf8)
        {
            BeginBatch();
            var fmt = new IsqlFormatter(emit, width) { Utf8ByteText = _sessionEnc?.CodePage == 65001 };
            _fmt = fmt;
            try
            {
                // A utf8 session's server charset is the persistent one; otherwise the session's.
                using var cmd = new AseCommand(
                    KeepUnconvertible(batch, utf8 ? _persistentServerEnc : _sessionEnc, commentsOnly: utf8), connection);
                cmd.CommandTimeout = 0;
                AseResultColumn[] meta = Array.Empty<AseResultColumn>();
                // A throw inside the observer would leave the connection mid-command (broken for
                // the rest of the script), so the first one is kept and reported after the batch.
                Exception? observerFault = null;
                cmd.ResultObserver = ev =>
                {
                    if (observerFault != null) return;
                    try { Observe(ev); }
                    catch (Exception ex) { observerFault = ex; }
                };
                void Observe(AseResultEvent ev)
                {
                    switch (ev.Kind)
                    {
                        case AseResultEventKind.RowFormat:
                            meta = ev.Columns;
                            fmt.BeginResultSet(meta.Select(c => ToIsqlColumn(c, utf8)).ToArray());
                            break;
                        case AseResultEventKind.Row:
                            var values = new string?[meta.Length];
                            for (int i = 0; i < meta.Length && i < ev.Values.Length; i++)
                                values[i] = B(FormatValue(meta[i], ev.Values[i], utf8));
                            fmt.Row(values);
                            break;
                        case AseResultEventKind.Done:
                            fmt.Done(ev.HasCount, ev.Count, ev.IsError, ev.InProc, ev.IsProc);
                            break;
                        case AseResultEventKind.ReturnStatus:
                            fmt.ReturnStatus(ev.ReturnStatus);
                            break;
                        case AseResultEventKind.ComputeRow:
                            var cvalues = new string?[ev.Columns.Length];
                            for (int i = 0; i < ev.Columns.Length && i < ev.Values.Length; i++)
                                cvalues[i] = B(FormatValue(ev.Columns[i], ev.Values[i], utf8));
                            fmt.ComputeRow(ev.Columns.Select(c => ToIsqlColumn(c, utf8)).ToArray(), cvalues);
                            break;
                    }
                }
                cmd.ExecuteNonQuery();
                if (observerFault != null)
                {
                    emit($"ERROR! {observerFault.Message}");
                    return false;
                }
                return !_batchHadError;
            }
            catch (AseException ex)
            {
                EmitAseException(ex, fmt);
                return false;
            }
            finally
            {
                _fmt = null;
            }
        }

        // isql display width and alignment per server type (docs/architecture.md, "isql output format").
        private IsqlColumn ToIsqlColumn(AseResultColumn c, bool utf8)
        {
            int len = c.Length;
            int width;
            bool right = true, noChunk = false;
            switch (c.DataTypeName)
            {
                case "tinyint": width = 3; break;
                case "smallint": width = 6; break;
                case "int": width = 11; break;
                case "bigint": width = 20; break;
                case "unsigned smallint": width = 5; break;
                case "unsigned int": width = 10; break;
                case "unsigned bigint": width = 20; break;
                case "bit": width = 1; break;
                case "real":
                case "float": width = 27; break;
                case "money":
                case "smallmoney": width = 24; break;
                case "numeric":
                case "decimal": width = (c.Precision > 0 ? c.Precision : 38) + 3; break;
                case "datetime":
                case "smalldatetime": width = 31; break;
                case "date": width = 16; break;
                case "time": width = 15; break;
                case "unichar":
                case "univarchar":
                    // UTF-16 data: as UTF-8 text in a utf8 session (3 bytes a character), else
                    // isql shows its bytes in hex.
                    width = utf8 ? Math.Max(len, 0) / 2 * 3 : 2 + 2 * Math.Max(len, 0);
                    right = false;
                    break;
                case "binary":
                case "varbinary": width = 2 + 2 * Math.Max(len, 0); right = false; break;
                case "image":
                case "text":
                case "unitext": width = IsqlFormatter.MaxColumnWidth; right = false; noChunk = true; break;
                default: width = len >= 0 ? len : IsqlFormatter.MaxColumnWidth; right = false; break;
            }
            // isql heads a column with its label whenever the server sent one, even a blank one.
            var name = !string.IsNullOrEmpty(c.Label) ? c.Label : c.ColumnName ?? "";
            return IsqlFormatter.Column(B(name) ?? "", width, right, noChunk);
        }

        // A value as isql prints it; null for NULL.
        private string? FormatValue(AseResultColumn c, object? v, bool utf8)
        {
            if (v == null || v is DBNull) return null;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            switch (c.DataTypeName)
            {
                case "money":
                case "smallmoney":
                    return IsqlFormatter.Money(Convert.ToDecimal(v, inv));
                case "numeric":
                case "decimal":
                    return v is decimal d && c.Scale >= 0 ? IsqlFormatter.Numeric(d, c.Scale) : Convert.ToString(v, inv);
                case "real":
                case "float":
                    return IsqlFormatter.Float(v is float f ? f : Convert.ToDouble(v, inv));
                case "bit":
                    return v is bool b ? IsqlFormatter.Bit(b) : Convert.ToString(v, inv);
                case "date":
                    if (v is DateTime dd) return IsqlFormatter.DateValue(dd);
                    break;
                case "time":
                    if (v is TimeSpan ts) return IsqlFormatter.TimeValue(ts.Hours, ts.Minutes);
                    if (v is DateTime tt) return IsqlFormatter.TimeValue(tt.Hour, tt.Minute);
                    break;
                case "unichar":
                case "univarchar":
                    // isql shows UTF-16 data as its bytes when the client charset cannot hold it.
                    if (v is string u && !utf8) return IsqlFormatter.Hex(Encoding.Unicode.GetBytes(u));
                    break;
            }
            return v switch
            {
                byte[] bytes => IsqlFormatter.Hex(bytes),
                DateTime dt => IsqlFormatter.DateTimeValue(dt),
                _ => Convert.ToString(v, inv)
            };
        }

        // Messages isql does not print: database, language and character set changes.
        private static bool IsSuppressed(AseError err) =>
            err.MessageNumber == 5701 || err.MessageNumber == 5703 || err.MessageNumber == 5704 ||
            err.Message.StartsWith("Changed client character set") ||
            err.Message.StartsWith("Changed database context") ||
            err.Message.StartsWith("Changed language setting");

        private void OnInfoMessage(object sender, AseInfoMessageEventArgs e)
        {
            var emit = _emit;
            if (emit == null) return;
            var fmt = _fmt ?? new IsqlFormatter(emit, IsqlFormatter.DefaultWidth);
            foreach (AseError err in e.Errors)
            {
                // Severe (>10) errors arrive here as well as in the AseException that
                // ExecuteNonQuery throws at the end of the batch. Emit them here, in server order,
                // and record them so the exception's copy is not printed twice.
                if (err.Severity >= 11)
                {
                    _batchErrorKeys.Add(ErrorKey(err));
                    _batchHadError = true;
                    EmitAseError(err, fmt);
                    continue;
                }
                if (IsSuppressed(err)) continue;
                fmt.Message(B(err.Message) ?? "");
            }
        }

        // Identity of a server error, used to suppress the duplicate when the same error is
        // reported both via InfoMessage and via the AseException thrown at the end of the batch.
        private static string ErrorKey(AseError err) =>
            $"{err.MessageNumber}|{err.State}|{err.LineNum}|{err.Message}";

        private void EmitAseException(AseException ex, IsqlFormatter fmt)
        {
            foreach (AseError err in ex.Errors)
            {
                // Informational messages and errors already emitted via InfoMessage are not repeated.
                if (err.Severity < 11 || !_batchErrorKeys.Add(ErrorKey(err))) continue;
                EmitAseError(err, fmt);
            }
        }

        private void EmitAseError(AseError err, IsqlFormatter fmt) =>
            fmt.Error(err.MessageNumber, err.Severity, err.State, B(err.ServerName), B(err.ProcName), err.LineNum, B(err.Message) ?? "");

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

        // Server's configured charset, for callers that need to report what the data
        // is being adhered to. Cached; the reinterpretation pair uses the same value.
        public string ServerCharset => DetectServerCharset();

        private void BulkCopyIn(string table, string dataFile, string fieldTerminator, int batchSize)
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

            // Read the delimited data file and load into DataTable (all string columns)
            // Server handles type conversion during BCP insert
            var lines = ibs_compiler_common.ReadBulkLines(dataFile);
            if (lines.Length == 0) return;

            var sep = new[] { fieldTerminator };
            var dataTable = new DataTable();
            var firstCols = lines[0].Split(sep, StringSplitOptions.None);
            int colCount = firstCols.Length;
            for (int i = 0; i < colCount; i++)
                dataTable.Columns.Add($"col{i}", typeof(string));

            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line)) continue;
                var cols = line.Split(sep, StringSplitOptions.None);

                // If data has more fields than table columns, merge extras into last column
                if (cols.Length > colCount && colCount > 0)
                {
                    var merged = new string[colCount];
                    for (int i = 0; i < colCount - 1; i++)
                        merged[i] = cols[i];
                    merged[colCount - 1] = string.Join(fieldTerminator, cols.Skip(colCount - 1));
                    cols = merged;
                }

                var row = dataTable.NewRow();
                for (int i = 0; i < Math.Min(cols.Length, colCount); i++)
                    row[i] = ToServer(cols[i]);
                dataTable.Rows.Add(row);
            }

            // No transaction here, ever: ASE answers a bulk insert inside one with
            // "BULK INSERT command not allowed within multi-statement transaction." (the
            // AseBulkCopy(connection, transaction) overload exists but the server refuses it,
            // surfacing as "Connection entered broken state"). The bulk BATCH is ASE's own
            // unit of atomicity instead — a row the server rejects discards the whole batch
            // in flight — so the default is one batch for the file, and -b N is N-row batches
            // where the batches before a failure stay. That is what native bcp does too.
            if (batchSize > 0)
            {
                int done = 0;
                while (done < dataTable.Rows.Count)
                {
                    var slice = dataTable.Clone();
                    for (int i = done; i < Math.Min(done + batchSize, dataTable.Rows.Count); i++)
                        slice.ImportRow(dataTable.Rows[i]);
                    WriteBatch(connection, tableName, slice, progress: false);
                    done += slice.Rows.Count;
                    ibs_compiler_common.WriteLine($"{done} rows sent to the server.");
                }
            }
            else
            {
                WriteBatch(connection, tableName, dataTable, progress: true);
            }

            ibs_compiler_common.WriteLine("");
            ibs_compiler_common.WriteLine($"{dataTable.Rows.Count} rows copied.");
        }

        private static void WriteBatch(AseConnection connection, string tableName, DataTable rows, bool progress)
        {
            using var bulkCopy = new AseBulkCopy(connection)
            {
                DestinationTableName = tableName,
                // 0 = send these rows as a single bulk batch, which is also the rollback unit.
                BatchSize = 0,
                NotifyAfter = 1000
            };
            if (progress)
                bulkCopy.AseRowsCopied += (sender, e) =>
                    ibs_compiler_common.WriteLine($"{e.RowsCopied} rows sent to the server.");
            bulkCopy.WriteToServer(rows);
        }

        private int BulkCopyOut(string table, string dataFile, string fieldTerminator)
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
            using var writer = ibs_compiler_common.OpenBulkWriter(dataFile);

            int rowCount = 0;
            while (reader.Read())
            {
                var values = new string[reader.FieldCount];
                for (int i = 0; i < reader.FieldCount; i++)
                    values[i] = FromServer(reader.IsDBNull(i) ? "" : reader[i].ToString() ?? "");
                writer.WriteLine(string.Join(fieldTerminator, values));
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

        public void Dispose()
        {
            _persistentConn?.Dispose();
            _persistentConn = null;
        }
    }
}
