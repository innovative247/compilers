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

        // Persistent connection for batch-at-a-time execution (OpenConnection/ExecuteBatch/CloseConnection)
        private SqlConnection? _persistentConn;
        private StringBuilder? _batchOutput;

        public MssqlExecutor(ResolvedProfile profile)
        {
            _profile = profile;
        }

        private static string? LoadSqlCmdInit()
        {
            var path = Environment.GetEnvironmentVariable("SQLCMDINI");
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return File.ReadAllText(path);
            return null;
        }

        private static void ExecuteInitScript(SqlConnection connection)
        {
            if (SqlCmdInitScript == null) return;
            using var cmd = new SqlCommand(SqlCmdInitScript, connection);
            cmd.ExecuteNonQuery();
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

            try
            {
                using var connection = new SqlConnection(BuildConnectionString(database));
                connection.InfoMessage += (sender, e) =>
                {
                    foreach (SqlError err in e.Errors)
                    {
                        if (err.Class >= 11) continue; // errors handled by SqlException catch
                        var msg = err.Message;
                        if (msg.StartsWith("Changed database context")) continue;
                        output.AppendLine(msg);
                    }
                };
                connection.Open();
                ExecuteInitScript(connection);

                // Split on GO batch separator
                var batches = SplitBatches(sqlText);
                foreach (var batch in batches)
                {
                    if (string.IsNullOrWhiteSpace(batch)) continue;
                    // exit/quit are isql client commands — stop processing
                    if (ExitRegex.IsMatch(batch.Trim())) break;
                    try
                    {
                        using var cmd = new SqlCommand(batch, connection);
                        cmd.CommandTimeout = 0; // no timeout for long-running scripts

                        using var reader = cmd.ExecuteReader();
                        do
                        {
                            if (reader.FieldCount > 0)
                            {
                                // Format result set as text output (similar to sqlcmd)
                                FormatResultSet(reader, output);
                            }
                        } while (reader.NextResult());
                    }
                    catch (SqlException ex)
                    {
                        foreach (SqlError err in ex.Errors)
                        {
                            var msg = $"Msg {err.Number}, Level {err.Class}, State {err.State}";
                            if (err.Procedure != null && err.Procedure.Length > 0)
                                msg += $", Procedure {err.Procedure}";
                            if (err.LineNumber > 0)
                                msg += $", Line {err.LineNumber}";
                            output.AppendLine(msg);
                            output.AppendLine(err.Message);
                        }
                        result.Returncode = false;
                    }
                }
            }
            catch (Exception ex)
            {
                output.AppendLine($"ERROR! {ex.Message}");
                result.Returncode = false;
            }

            result.Output = output.ToString();

            if (!captureOutput)
            {
                var target = !string.IsNullOrEmpty(outputFile) ? outputFile : ibs_compiler_common.DefaultOutFile;
                if (!string.IsNullOrEmpty(target))
                    ibs_compiler_common.WriteLineToDisk(target, result.Output);
                else if (!string.IsNullOrEmpty(result.Output))
                    Console.Write(result.Output);
            }

            return result;
        }

        public void OpenConnection(string database)
        {
            _batchOutput = new StringBuilder();
            _persistentConn = new SqlConnection(BuildConnectionString(database));
            _persistentConn.InfoMessage += (sender, e) =>
            {
                foreach (SqlError err in e.Errors)
                {
                    if (err.Class >= 11) continue; // errors handled by SqlException catch
                    var msg = err.Message;
                    if (msg.StartsWith("Changed database context")) continue;
                    _batchOutput.AppendLine(msg);
                }
            };
            _persistentConn.Open();
            ExecuteInitScript(_persistentConn);
        }

        public ExecReturn ExecuteBatch(string batch, bool captureOutput = false, string outputFile = "")
        {
            var result = new ExecReturn { Returncode = true, Output = "" };
            _batchOutput!.Clear();

            try
            {
                if (!string.IsNullOrWhiteSpace(batch))
                {
                    using var cmd = new SqlCommand(batch, _persistentConn);
                    cmd.CommandTimeout = 0;

                    using var reader = cmd.ExecuteReader();
                    do
                    {
                        if (reader.FieldCount > 0)
                            FormatResultSet(reader, _batchOutput);
                    } while (reader.NextResult());
                }
            }
            catch (SqlException ex)
            {
                foreach (SqlError err in ex.Errors)
                {
                    var msg = $"Msg {err.Number}, Level {err.Class}, State {err.State}";
                    if (err.Procedure != null && err.Procedure.Length > 0)
                        msg += $", Procedure {err.Procedure}";
                    if (err.LineNumber > 0)
                        msg += $", Line {err.LineNumber}";
                    _batchOutput.AppendLine(msg);
                    _batchOutput.AppendLine(err.Message);
                }
                result.Returncode = false;
            }

            result.Output = _batchOutput.ToString();

            if (!captureOutput)
            {
                var target = !string.IsNullOrEmpty(outputFile) ? outputFile : ibs_compiler_common.DefaultOutFile;
                if (!string.IsNullOrEmpty(target))
                    ibs_compiler_common.WriteLineToDisk(target, result.Output);
                else if (!string.IsNullOrEmpty(result.Output))
                    Console.Write(result.Output);
            }

            return result;
        }

        public void CloseConnection()
        {
            _persistentConn?.Dispose();
            _persistentConn = null;
            _batchOutput = null;
        }

        public ExecReturn BulkCopy(string table, BcpDirection direction, string dataFile, string formatFile = "")
        {
            var result = new ExecReturn { Returncode = true, Output = "" };

            try
            {
                if (direction == BcpDirection.IN)
                {
                    BulkCopyIn(table, dataFile, formatFile);
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

        private void BulkCopyIn(string table, string dataFile, string formatFile)
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

            using var bulkCopy = new SqlBulkCopy(connection)
            {
                DestinationTableName = tableName,
                BulkCopyTimeout = 0,
                BatchSize = 1000,
                NotifyAfter = 1000
            };

            bulkCopy.SqlRowsCopied += (sender, e) =>
            {
                ibs_compiler_common.WriteLine($"{e.RowsCopied} rows sent to the server.");
            };

            // Read tab-delimited data file and load into table
            var lines = File.ReadAllLines(dataFile);
            if (lines.Length == 0) return;

            // Build DataTable with correct column types from schema
            var dataTable = new DataTable();
            var firstCols = lines[0].Split('\t');
            int colCount = firstCols.Length;
            for (int i = 0; i < colCount; i++)
            {
                var colName = i < columnNames.Count ? columnNames[i] : $"col{i}";
                var colType = i < columnTypes.Count ? columnTypes[i] : typeof(string);
                dataTable.Columns.Add(colName, colType);
            }

            // Map column ordinals for SqlBulkCopy
            for (int i = 0; i < colCount; i++)
                bulkCopy.ColumnMappings.Add(i, i);

            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line)) continue;
                var cols = line.Split('\t');

                // If data has more fields than table columns, merge extras into last column
                // (matches native BCP behavior — last column gets the remainder)
                if (cols.Length > colCount && colCount > 0)
                {
                    var merged = new string[colCount];
                    for (int i = 0; i < colCount - 1; i++)
                        merged[i] = cols[i];
                    merged[colCount - 1] = string.Join("\t", cols.Skip(colCount - 1));
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
            using var connection = new SqlConnection(connStr);
            connection.Open();
            ExecuteInitScript(connection);

            using var cmd = new SqlCommand($"SELECT * FROM {tableName}", connection);
            using var reader = cmd.ExecuteReader();
            using var writer = new StreamWriter(dataFile, false);

            int rowCount = 0;
            while (reader.Read())
            {
                var values = new string[reader.FieldCount];
                for (int i = 0; i < reader.FieldCount; i++)
                    values[i] = reader.IsDBNull(i) ? "" : reader[i].ToString() ?? "";
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

        private static void FormatResultSet(SqlDataReader reader, StringBuilder output)
        {
            var colCount = reader.FieldCount;
            var colWidths = new int[colCount];
            var colNames = new string[colCount];
            var colIsNumeric = new bool[colCount];
            for (int i = 0; i < colCount; i++)
            {
                colNames[i] = reader.GetName(i);
                colWidths[i] = Math.Max(colNames[i].Length, 10);
                var t = reader.GetFieldType(i);
                colIsNumeric[i] = t == typeof(int) || t == typeof(long) || t == typeof(short) ||
                                  t == typeof(decimal) || t == typeof(double) || t == typeof(float) ||
                                  t == typeof(byte);
            }

            // Read all rows to determine column widths
            var rows = new List<string[]>();
            while (reader.Read())
            {
                var row = new string[colCount];
                for (int i = 0; i < colCount; i++)
                {
                    row[i] = reader.IsDBNull(i) ? "NULL" : (reader[i].ToString() ?? "").TrimEnd();
                    colWidths[i] = Math.Max(colWidths[i], row[i].Length);
                }
                rows.Add(row);
            }

            if (rows.Count == 0) return;

            // Column headers
            for (int i = 0; i < colCount; i++)
            {
                if (i > 0) output.Append(' ');
                output.Append(colIsNumeric[i]
                    ? colNames[i].PadLeft(colWidths[i])
                    : colNames[i].PadRight(colWidths[i]));
            }
            output.AppendLine();

            // Separator
            for (int i = 0; i < colCount; i++)
            {
                if (i > 0) output.Append(' ');
                output.Append(new string('-', colWidths[i]));
            }
            output.AppendLine();

            // Data rows
            foreach (var row in rows)
            {
                for (int i = 0; i < colCount; i++)
                {
                    if (i > 0) output.Append(' ');
                    output.Append(colIsNumeric[i]
                        ? row[i].PadLeft(colWidths[i])
                        : row[i].PadRight(colWidths[i]));
                }
                output.AppendLine();
            }

            var rowWord = rows.Count == 1 ? "row" : "rows";
            output.AppendLine($"({rows.Count} {rowWord} affected)");
            output.AppendLine();
        }

        public void Dispose()
        {
            _persistentConn?.Dispose();
            _persistentConn = null;
        }
    }
}
