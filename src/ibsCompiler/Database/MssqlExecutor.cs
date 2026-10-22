using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using ibsCompiler.Configuration;

namespace ibsCompiler.Database
{
    public class MssqlExecutor : ISqlExecutor
    {
        private readonly ResolvedProfile _profile;
        private static readonly Regex GoRegex = new(@"^\s*go\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex ExitRegex = new(@"^\s*(exit|quit)\s*$", RegexOptions.IgnoreCase);
        private static readonly string? SqlCmdInitScript = LoadSqlCmdInit();

        // Per-call output sink wired up by ExecuteSql/ExecuteBatch. The persistent
        // connection's InfoMessage handler routes through this so PRINT/RAISERROR
        // messages stream as the server emits them.
        private Action<string>? _emit;

        // Persistent connection for batch-at-a-time execution (OpenConnection/ExecuteBatch/CloseConnection)
        private SqlConnection? _persistentConn;

        // isql-style renderer for the batch currently executing. Server messages (InfoMessage)
        // go through it too, so they interleave with result sets as isql's do
        // (docs/architecture.md, "isql output format").
        private IsqlFormatter? _fmt;

        // Whether the batch currently executing raised a server error (severity 11+). Errors reach
        // InfoMessage in server order because the connection fires it for user errors too.
        private bool _batchHadError;
        // An error arrived since the current result set began: the statement failed, so isql
        // prints no heading for it.
        private bool _errorInResultSet;

        // @@servername of the session, which isql's error header names (SqlError.Server holds the
        // client's data source instead).
        private string? _serverName;

        public MssqlExecutor(ResolvedProfile profile)
        {
            _profile = profile;
        }

        private static string? LoadSqlCmdInit()
        {
            // Session defaults live in a SQLCMDINI.sql, exactly like native sqlcmd reads its
            // SQLCMDINI startup script (the mechanism the legacy compiler relied on). Resolve it
            // from $env:SQLCMDINI first (explicit, matches the legacy toolchain), else a local
            // SQLCMDINI.sql sitting next to settings.json/the binaries. A local, versionable
            // settings file — never hard-coded values.
            var path = Environment.GetEnvironmentVariable("SQLCMDINI");
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                var settings = ProfileManager.FindSettingsFile();
                var dir = (settings != null ? Path.GetDirectoryName(settings) : null) ?? AppContext.BaseDirectory;
                path = Path.Combine(dir, "SQLCMDINI.sql");
            }
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        private static void ExecuteInitScript(SqlConnection connection)
        {
            // Session defaults (ARITHABORT, CONCAT_NULL_YIELDS_NULL, ANSI_NULL_DFLT, ...) come from
            // SQLCMDINI.sql via the env var / local file — never hard-coded. Same mechanism native
            // sqlcmd and the legacy compiler used.
            if (SqlCmdInitScript != null)
                using (var cmd = new SqlCommand(SqlCmdInitScript, connection))
                    cmd.ExecuteNonQuery();
            // Honor the target database's QUOTED_IDENTIFIER. Microsoft.Data.SqlClient forces it ON at
            // login regardless of the database default (this is the one option where it differs from
            // native sqlcmd, whose default is OFF). Read the database's configured value and SET the
            // session to match, so the database — configured by the installer — is the source of
            // truth. SBN needs OFF, which install-sbn sets at the model/database level.
            ApplyDatabaseQuotedIdentifier(connection);
        }

        private static void ApplyDatabaseQuotedIdentifier(SqlConnection connection)
        {
            bool? on = null;
            using (var q = new SqlCommand(
                "SELECT is_quoted_identifier_on FROM sys.databases WHERE database_id = DB_ID()", connection))
            {
                var o = q.ExecuteScalar();
                if (o != null && o != DBNull.Value) on = Convert.ToBoolean(o);
            }
            if (on.HasValue)
                using (var s = new SqlCommand("SET QUOTED_IDENTIFIER " + (on.Value ? "ON" : "OFF"), connection))
                    s.ExecuteNonQuery();
        }

        private string BuildConnectionString(string database)
        {
            var sb = new SqlConnectionStringBuilder
            {
                DataSource = $"{_profile.Host},{_profile.Port}",
                UserID = _profile.User,
                Password = _profile.Pass,
                TrustServerCertificate = true,
                Encrypt = false,
                Pooling = false,
                ApplicationName = "ibsCompiler"
            };
            if (!string.IsNullOrEmpty(database))
                sb.InitialCatalog = database;
            return sb.ConnectionString;
        }

        public ExecReturn ExecuteSql(string sqlText, string database, bool captureOutput = false, string outputFile = "")
        {
            var result = new ExecReturn { Returncode = true, Output = "" };
            var output = new StringBuilder();
            var sink = OutputSink.Build(output, captureOutput, outputFile);
            _emit = sink.Emit;

            try
            {
                using var connection = OpenSession(database);

                int width = IsqlFormatter.ResolveWidth(captureOutput, outputFile);
                var batches = SplitBatches(sqlText);
                foreach (var batch in batches)
                {
                    if (string.IsNullOrWhiteSpace(batch)) continue;
                    if (ExitRegex.IsMatch(batch.Trim())) break;
                    if (!RunBatch(connection, batch, sink.Emit, width)) result.Returncode = false;
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
            _persistentConn = OpenSession(database);
        }

        private SqlConnection OpenSession(string database)
        {
            var conn = new SqlConnection(BuildConnectionString(database));
            // User errors (severity 11-19) come through InfoMessage, in order with prints and
            // result sets, instead of as one exception at the end of the batch.
            conn.FireInfoMessageEventOnUserErrors = true;
            conn.InfoMessage += OnInfoMessage;
            try
            {
                conn.Open();
                ExecuteInitScript(conn);
                if (_serverName == null)
                    using (var q = new SqlCommand("select @@servername", conn))
                        _serverName = q.ExecuteScalar() as string ?? "";
            }
            catch
            {
                conn.Dispose();
                throw;
            }
            return conn;
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
                    result.Returncode = RunBatch(_persistentConn, batch, sink.Emit,
                        IsqlFormatter.ResolveWidth(captureOutput, outputFile));
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

        // Runs one batch the way isql does: result sets, row counts (StatementCompleted, one per
        // DONE token that carries a count) and messages (InfoMessage) all reach the isql formatter
        // in the order the server sent them. Returns false when the batch raised a server error.
        private bool RunBatch(SqlConnection connection, string batch, Action<string> emit, int width)
        {
            var fmt = new IsqlFormatter(emit, width);
            _fmt = fmt;
            _batchHadError = false;
            bool counted = false;
            try
            {
                using var cmd = new SqlCommand(batch, connection) { CommandTimeout = 0 };
                // A statement's DONE token with a count. SqlClient cannot tell a statement inside a
                // procedure from a top-level one, so every counted statement prints, as top-level.
                cmd.StatementCompleted += (_, e) =>
                {
                    counted = true;
                    fmt.Done(true, e.RecordCount);
                };
                using var reader = cmd.ExecuteReader();
                bool more = true;
                while (more)
                {
                    if (reader.FieldCount == 0)
                    {
                        more = reader.NextResult();
                        continue;
                    }
                    var cols = Columns(reader);
                    fmt.BeginResultSet(cols.Select(c => c.Col).ToArray());
                    counted = false;
                    _errorInResultSet = false;
                    while (reader.Read())
                    {
                        var values = new string?[cols.Length];
                        for (int i = 0; i < cols.Length; i++)
                            values[i] = reader.IsDBNull(i) ? null : FormatValue(cols[i], reader.GetValue(i));
                        fmt.Row(values);
                    }
                    more = reader.NextResult();
                    // Under set nocount on the result set's DONE carries no count, so nothing
                    // reported its end: close it here (prints the heading of an empty one).
                    if (!counted) fmt.Done(false, 0, isError: _errorInResultSet);
                }
                return !_batchHadError;
            }
            catch (SqlException ex)
            {
                // Severity 20+ (and connection failures) still arrive as an exception.
                foreach (SqlError err in ex.Errors) EmitError(err, fmt);
                return false;
            }
            finally
            {
                _fmt = null;
            }
        }

        private sealed record MssqlColumn(IsqlColumn Col, string Type, int Scale);

        // isql display width and alignment per SQL Server type (docs/architecture.md,
        // "isql output format" - MSSQL mapping).
        private static MssqlColumn[] Columns(SqlDataReader reader)
        {
            var schema = reader.GetSchemaTable();
            var cols = new MssqlColumn[reader.FieldCount];
            for (int i = 0; i < cols.Length; i++)
            {
                var type = (reader.GetDataTypeName(i) ?? "").ToLowerInvariant();
                int size = -1, precision = 0, scale = -1;
                bool isLong = false;
                if (schema != null && i < schema.Rows.Count)
                {
                    var row = schema.Rows[i];
                    size = IntOf(row["ColumnSize"], -1);
                    precision = IntOf(row["NumericPrecision"], 0);
                    scale = IntOf(row["NumericScale"], -1);
                    isLong = row.Table.Columns.Contains("IsLong") && row["IsLong"] is bool l && l;
                }
                int width;
                bool right = true, noChunk = false;
                switch (type)
                {
                    case "tinyint": width = 3; break;
                    case "smallint": width = 6; break;
                    case "int": width = 11; break;
                    case "bigint": width = 20; break;
                    case "bit": width = 1; break;
                    case "real":
                    case "float": width = 27; break;
                    case "money":
                    case "smallmoney": width = 24; break;
                    case "numeric":
                    case "decimal": width = (precision > 0 ? precision : 38) + 3; break;
                    case "datetime":
                    case "smalldatetime":
                    case "datetime2":
                    case "datetimeoffset": width = 31; break;
                    case "date": width = 16; break;
                    case "time": width = 15; break;
                    case "uniqueidentifier": width = 36; right = false; break;
                    case "binary":
                    case "varbinary":
                    case "timestamp":
                    case "rowversion":
                        right = false;
                        if (isLong || size <= 0 || size > IsqlFormatter.MaxColumnWidth) { width = IsqlFormatter.MaxColumnWidth; noChunk = true; }
                        else width = 2 + 2 * size;
                        break;
                    case "image":
                    case "text":
                    case "ntext":
                    case "xml": width = IsqlFormatter.MaxColumnWidth; right = false; noChunk = true; break;
                    default:
                        // char types: their length in characters; (max) types print like text.
                        right = false;
                        if (isLong || size <= 0 || size > 8000) { width = IsqlFormatter.MaxColumnWidth; noChunk = true; }
                        else width = size;
                        break;
                }
                cols[i] = new MssqlColumn(IsqlFormatter.Column(reader.GetName(i) ?? "", width, right, noChunk), type, scale);
            }
            return cols;
        }

        private static int IntOf(object o, int fallback)
        {
            if (o == null || o is DBNull) return fallback;
            try { return Convert.ToInt32(o, System.Globalization.CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        // A value as isql prints the equivalent Sybase type.
        private static string FormatValue(MssqlColumn c, object v)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            switch (c.Type)
            {
                case "money":
                case "smallmoney":
                    return IsqlFormatter.Money(Convert.ToDecimal(v, inv));
                case "numeric":
                case "decimal":
                    return v is decimal d ? IsqlFormatter.Numeric(d, c.Scale) : Convert.ToString(v, inv) ?? "";
                case "real":
                case "float":
                    return IsqlFormatter.Float(v is float f ? f : Convert.ToDouble(v, inv));
                case "date":
                    if (v is DateTime dd) return IsqlFormatter.DateValue(dd);
                    break;
                case "time":
                    if (v is TimeSpan ts) return IsqlFormatter.TimeValue(ts.Hours, ts.Minutes);
                    break;
            }
            return v switch
            {
                bool b => IsqlFormatter.Bit(b),
                byte[] bytes => IsqlFormatter.Hex(bytes),
                DateTime dt => IsqlFormatter.DateTimeValue(dt),
                DateTimeOffset dto => IsqlFormatter.DateTimeValue(dto.DateTime),
                string str => str,
                _ => Convert.ToString(v, inv) ?? ""
            };
        }

        // Messages isql does not print: database, language and character set changes.
        private static bool IsSuppressed(SqlError err) =>
            err.Number == 5701 || err.Number == 5703 || err.Number == 5704 ||
            err.Message.StartsWith("Changed database context") ||
            err.Message.StartsWith("Changed language setting");

        private void OnInfoMessage(object sender, SqlInfoMessageEventArgs e)
        {
            var emit = _emit;
            if (emit == null) return;
            var fmt = _fmt ?? new IsqlFormatter(emit, IsqlFormatter.DefaultWidth);
            foreach (SqlError err in e.Errors)
            {
                if (err.Class >= 11)
                {
                    _batchHadError = true;
                    _errorInResultSet = true;
                    EmitError(err, fmt);
                    continue;
                }
                if (IsSuppressed(err)) continue;
                fmt.Message(err.Message);
            }
        }

        private void EmitError(SqlError err, IsqlFormatter fmt) =>
            fmt.Error(err.Number, err.Class, err.State, string.IsNullOrEmpty(_serverName) ? err.Server : _serverName,
                err.Procedure, err.LineNumber, err.Message);

        public ExecReturn BulkCopy(string table, BcpDirection direction, string dataFile, string formatFile = "", string fieldTerminator = "\t", int batchSize = 0)
        {
            var result = new ExecReturn { Returncode = true, Output = "" };

            try
            {
                if (direction == BcpDirection.IN)
                {
                    BulkCopyIn(table, dataFile, formatFile, fieldTerminator, batchSize);
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

        private void BulkCopyIn(string table, string dataFile, string formatFile, string fieldTerminator, int batchSize)
        {
            // Parse database from table name (e.g., "sbnmaster..w#actions" → db=sbnmaster, table=w#actions)
            string database = "";
            string tableName = table;
            if (table.Contains(".."))
            {
                var parts = table.Split(new[] { ".." }, 2, StringSplitOptions.None);
                database = parts[0];
                tableName = parts[1];
            }

            var connStr = BuildConnectionString(database);
            using var connection = new SqlConnection(connStr);
            connection.Open();
            ExecuteInitScript(connection);

            // Get column schema from database
            var columnTypes = new List<Type>();
            var columnNames = new List<string>();
            using (var schemaCmd = new SqlCommand($"SELECT * FROM {tableName} WHERE 1=0", connection))
            using (var schemaReader = schemaCmd.ExecuteReader())
            {
                var schemaTable = schemaReader.GetSchemaTable();
                if (schemaTable != null)
                {
                    foreach (DataRow schemaRow in schemaTable.Rows)
                    {
                        columnNames.Add(schemaRow["ColumnName"].ToString() ?? $"col{columnTypes.Count}");
                        columnTypes.Add(schemaRow["DataType"] as Type ?? typeof(string));
                    }
                }
            }

            // Read the delimited data file and load into table
            var lines = ibs_compiler_common.ReadBulkLines(dataFile);
            if (lines.Length == 0) return;

            // Build DataTable with correct column types from schema
            var sep = new[] { fieldTerminator };
            var dataTable = new DataTable();
            var firstCols = lines[0].Split(sep, StringSplitOptions.None);
            int colCount = firstCols.Length;
            for (int i = 0; i < colCount; i++)
            {
                var colName = i < columnNames.Count ? columnNames[i] : $"col{i}";
                var colType = i < columnTypes.Count ? columnTypes[i] : typeof(string);
                dataTable.Columns.Add(colName, colType);
            }

            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line)) continue;
                var cols = line.Split(sep, StringSplitOptions.None);

                // If data has more fields than table columns, merge extras into last column
                // (matches native BCP behavior — last column gets the remainder)
                if (cols.Length > colCount && colCount > 0)
                {
                    var merged = new string[colCount];
                    for (int i = 0; i < colCount - 1; i++)
                        merged[i] = cols[i];
                    merged[colCount - 1] = string.Join(fieldTerminator, cols.Skip(colCount - 1));
                    cols = merged;
                }

                var row = dataTable.NewRow();
                for (int i = 0; i < Math.Min(cols.Length, dataTable.Columns.Count); i++)
                {
                    var val = cols[i];
                    var colType = dataTable.Columns[i].DataType;
                    bool isNumeric = colType == typeof(int) || colType == typeof(long) ||
                                     colType == typeof(short) || colType == typeof(byte) ||
                                     colType == typeof(decimal) || colType == typeof(double) ||
                                     colType == typeof(float);

                    if (isNumeric)
                    {
                        if (string.IsNullOrEmpty(val))
                            row[i] = Convert.ChangeType(0, colType);
                        else
                            row[i] = Convert.ChangeType(val, colType);
                    }
                    else
                    {
                        row[i] = val; // empty string stays as empty string
                    }
                }
                dataTable.Rows.Add(row);
            }

            // All-or-nothing by default: the file goes in under one transaction, so a row
            // the server rejects halfway leaves the table exactly as it was. -b N buys the
            // traditional bcp deal instead — UseInternalTransaction makes each batch of N
            // its own commit, and the batches before the failure stay.
            using SqlTransaction? tran = batchSize > 0 ? null : connection.BeginTransaction();
            using var bulkCopy = new SqlBulkCopy(connection,
                batchSize > 0 ? SqlBulkCopyOptions.UseInternalTransaction : SqlBulkCopyOptions.Default,
                tran)
            {
                DestinationTableName = tableName,
                BulkCopyTimeout = 0,
                // Under the external transaction this is only how often rows go over the
                // wire; with -b it is the commit boundary.
                BatchSize = batchSize > 0 ? batchSize : 1000,
                NotifyAfter = 1000
            };

            bulkCopy.SqlRowsCopied += (sender, e) =>
            {
                ibs_compiler_common.WriteLine($"{e.RowsCopied} rows sent to the server.");
            };

            // Map column ordinals for SqlBulkCopy
            for (int i = 0; i < colCount; i++)
                bulkCopy.ColumnMappings.Add(i, i);

            try
            {
                bulkCopy.WriteToServer(dataTable);
                tran?.Commit();
            }
            catch
            {
                tran?.Rollback();
                throw;
            }

            ibs_compiler_common.WriteLine("");
            ibs_compiler_common.WriteLine($"{dataTable.Rows.Count} rows copied.");
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
            using var connection = new SqlConnection(connStr);
            connection.Open();
            ExecuteInitScript(connection);

            using var cmd = new SqlCommand($"SELECT * FROM {tableName}", connection);
            using var reader = cmd.ExecuteReader();
            using var writer = ibs_compiler_common.OpenBulkWriter(dataFile);

            int rowCount = 0;
            while (reader.Read())
            {
                var values = new string[reader.FieldCount];
                for (int i = 0; i < reader.FieldCount; i++)
                    values[i] = reader.IsDBNull(i) ? "" : reader[i].ToString() ?? "";
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
