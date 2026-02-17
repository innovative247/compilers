using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace ibsCompiler.TransferData
{
    /// <summary>
    /// Queries database servers to discover databases, tables, and row counts.
    /// Uses existing ISqlExecutor.ExecuteSql with captureOutput:true.
    /// </summary>
    public class DatabaseDiscovery
    {
        /// <summary>
        /// Test connectivity by running SELECT 1.
        /// </summary>
        public static bool TestConnection(ConnectionConfig conn)
        {
            try
            {
                var profile = BuildProfile(conn);
                using var executor = SqlExecutorFactory.Create(profile);
                var result = executor.ExecuteSql("SELECT 1", "", captureOutput: true);
                return result.Returncode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Get list of databases from server.
        /// </summary>
        public static List<string> GetDatabases(ConnectionConfig conn)
        {
            var sql = conn.ServerType == SQLServerTypes.MSSQL
                ? "SELECT name FROM sys.databases WHERE database_id > 4 ORDER BY name"
                : "SELECT name FROM master..sysdatabases WHERE dbid > 4 ORDER BY name";

            return QueryColumn(conn, sql, "master");
        }

        /// <summary>
        /// Get list of user tables from a specific database.
        /// </summary>
        public static List<string> GetTables(ConnectionConfig conn, string database)
        {
            var sql = conn.ServerType == SQLServerTypes.MSSQL
                ? "SELECT name FROM sys.tables ORDER BY name"
                : "SELECT name FROM sysobjects WHERE type='U' ORDER BY name";

            return QueryColumn(conn, sql, database);
        }

        /// <summary>
        /// Get approximate row count for a table.
        /// </summary>
        public static long GetRowCount(ConnectionConfig conn, string database, string table)
        {
            try
            {
                var sql = $"SELECT COUNT(*) FROM {table}";
                var results = QueryColumn(conn, sql, database);
                if (results.Count > 0 && long.TryParse(results[0].Trim(), out var count))
                    return count;
            }
            catch { }
            return -1;
        }

        private static List<string> QueryColumn(ConnectionConfig conn, string sql, string database)
        {
            try
            {
                var profile = BuildProfile(conn);
                using var executor = SqlExecutorFactory.Create(profile);
                var result = executor.ExecuteSql(sql, database, captureOutput: true);
                if (!result.Returncode || string.IsNullOrEmpty(result.Output))
                    return new List<string>();

                // Parse captured output — each line may have column headers and dashes,
                // then data rows. Skip header lines.
                var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var data = new List<string>();
                bool pastHeader = false;
                foreach (var rawLine in lines)
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    // Skip header separator (dashes)
                    if (line.All(c => c == '-' || c == ' '))
                    {
                        pastHeader = true;
                        continue;
                    }

                    if (!pastHeader)
                    {
                        pastHeader = true; // First line is column header
                        continue;
                    }

                    // Skip rows affected messages
                    if (line.StartsWith("(") && line.Contains("row")) continue;

                    data.Add(line.Trim());
                }
                return data;
            }
            catch
            {
                return new List<string>();
            }
        }

        public static ResolvedProfile BuildProfile(ConnectionConfig conn)
        {
            return new ResolvedProfile
            {
                ProfileName = $"{conn.Host}:{conn.EffectivePort}",
                Host = conn.Host,
                Port = conn.EffectivePort,
                User = conn.Username,
                Pass = conn.Password,
                ServerType = conn.ServerType,
                Company = "101",
                Language = "1",
                IRPath = "",
                RawMode = false,
                IsProfile = false
            };
        }
    }
}
