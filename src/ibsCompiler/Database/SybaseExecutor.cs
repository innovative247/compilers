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

        // Persistent connection for batch-at-a-time execution (OpenConnection/ExecuteBatch/CloseConnection)
        private AseConnection? _persistentConn;
        private StringBuilder? _batchOutput;

        public SybaseExecutor(ResolvedProfile profile)
        {
            _profile = profile;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        private string BuildConnectionString(string database, bool includeCharset = true)
        {
            var sb = new StringBuilder();
            sb.Append($"Data Source={_profile.Host}");
            sb.Append($";Port={_profile.Port}");
            sb.Append($";User ID={_profile.User}");
            sb.Append($";Password={_profile.Pass}");
            if (!string.IsNullOrEmpty(database))
                sb.Append($";Database={database}");
            if (includeCharset)
                sb.Append(";Charset=utf8");
            sb.Append(";Pooling=false");
            return sb.ToString();
        }

        public ExecReturn ExecuteSql(string sqlText, string database, bool captureOutput = false, string outputFile = "")
        {
            var result = new ExecReturn { Returncode = true, Output = "" };
            var output = new StringBuilder();

            try
            {
                using var connection = new AseConnection(BuildConnectionString(database));
                connection.InfoMessage += (sender, e) =>
                {
                    foreach (AseError err in e.Errors)
                    {
                        if (err.Severity >= 11) continue; // errors handled by AseException catch
                        var msg = err.Message;
                        if (msg.StartsWith("Changed client character set") ||
                            msg.StartsWith("Changed database context") ||
                            msg.StartsWith("Changed language setting"))
                            continue;
                        output.AppendLine(msg);
                    }
                };
                connection.Open();

                var batches = SplitBatches(sqlText);
                foreach (var batch in batches)
                {
                    if (string.IsNullOrWhiteSpace(batch)) continue;
                    // exit/quit are isql client commands — stop processing
                    if (ExitRegex.IsMatch(batch.Trim())) break;
                    try
                    {
                        using var cmd = new AseCommand(batch, connection);
                        cmd.CommandTimeout = 0;

                        using var reader = cmd.ExecuteReader();
                        do
                        {
                            if (reader.FieldCount > 0)
                            {
                                FormatResultSet(reader, output);
                            }
                        } while (reader.NextResult());
                    }
                    catch (AseException ex)
                    {
                        foreach (AseError err in ex.Errors)
                        {
                            output.AppendLine($"Msg {err.MessageNumber}, Level {err.Severity}, State {err.State}");
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

            if (!captureOutput && !string.IsNullOrWhiteSpace(result.Output))
            {
                var target = !string.IsNullOrEmpty(outputFile) ? outputFile : ibs_compiler_common.DefaultOutFile;
                if (!string.IsNullOrEmpty(target))
                    ibs_compiler_common.WriteLineToDisk(target, result.Output);
                else
                    Console.Write(result.Output);
            }

            return result;
        }

        public void OpenConnection(string database)
        {
            _batchOutput = new StringBuilder();
            _persistentConn = new AseConnection(BuildConnectionString(database));
            _persistentConn.InfoMessage += (sender, e) =>
            {
                foreach (AseError err in e.Errors)
                {
                    if (err.Severity >= 11) continue; // errors handled by AseException catch
                    var msg = err.Message;
                    if (msg.StartsWith("Changed client character set") ||
                        msg.StartsWith("Changed database context") ||
                        msg.StartsWith("Changed language setting"))
                        continue;
                    _batchOutput.AppendLine(msg);
                }
            };
            _persistentConn.Open();
        }

        public ExecReturn ExecuteBatch(string batch, bool captureOutput = false, string outputFile = "")
        {
            var result = new ExecReturn { Returncode = true, Output = "" };
            _batchOutput!.Clear();

            try
            {
                if (!string.IsNullOrWhiteSpace(batch))
                {
                    using var cmd = new AseCommand(batch, _persistentConn);
                    cmd.CommandTimeout = 0;

                    using var reader = cmd.ExecuteReader();
                    do
                    {
                        if (reader.FieldCount > 0)
                            FormatResultSet(reader, _batchOutput);
                    } while (reader.NextResult());
                }
            }
            catch (AseException ex)
            {
                foreach (AseError err in ex.Errors)
                {
                    _batchOutput.AppendLine($"Msg {err.MessageNumber}, Level {err.Severity}, State {err.State}");
                    _batchOutput.AppendLine(err.Message);
                }
                result.Returncode = false;
            }

            result.Output = _batchOutput.ToString();

            if (!captureOutput && !string.IsNullOrWhiteSpace(result.Output))
            {
                var target = !string.IsNullOrEmpty(outputFile) ? outputFile : ibs_compiler_common.DefaultOutFile;
                if (!string.IsNullOrEmpty(target))
                    ibs_compiler_common.WriteLineToDisk(target, result.Output);
                else
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

            var connStr = BuildConnectionString(database, includeCharset: false);
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
                    row[i] = cols[i];
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

        private static void FormatResultSet(IDataReader reader, StringBuilder output)
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
