namespace ibsCompiler.Database
{
    /// <summary>
    /// Abstraction for SQL execution, replacing F4.8's exec_process(isql/sqlcmd) and exec_bcp(bcp/obcp).
    /// Implementations use managed ADO.NET providers - no native tools required.
    /// </summary>
    public interface ISqlExecutor : IDisposable
    {
        /// <summary>
        /// Execute SQL text that may contain multiple GO-separated batches.
        /// Captures print messages, errors, and optionally result sets.
        /// </summary>
        /// <param name="sqlText">SQL to execute (may contain GO batch separators)</param>
        /// <param name="database">Database to use (USE database)</param>
        /// <param name="captureOutput">If true, capture all output to ExecReturn.Output instead of writing to console/file</param>
        /// <param name="outputFile">If non-empty, write output to this file</param>
        ExecReturn ExecuteSql(string sqlText, string database, bool captureOutput = false, string outputFile = "");

        /// <summary>
        /// Bulk copy data between a file and a database table.
        /// Replaces F4.8's exec_bcp() which launched native bcp/obcp.
        /// </summary>
        ExecReturn BulkCopy(string table, BcpDirection direction, string dataFile, string formatFile = "");
    }

    /// <summary>
    /// Factory to create the correct executor based on server type.
    /// </summary>
    public static class SqlExecutorFactory
    {
        public static ISqlExecutor Create(Configuration.ResolvedProfile profile)
        {
            return profile.ServerType switch
            {
                SQLServerTypes.MSSQL => new MssqlExecutor(profile),
                SQLServerTypes.SYBASE => new SybaseExecutor(profile),
                _ => throw new ArgumentException($"Unknown server type: {profile.ServerType}")
            };
        }
    }
}
