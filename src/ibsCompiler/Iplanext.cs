using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace ibsCompiler
{
    /// <summary>
    /// Port of Python iplanext.py.
    /// Extended plan viewer: process info + SQL text + execution plan for a SPID.
    /// Gathers comprehensive debugging info into a single output file.
    /// </summary>
    public static class iplanext_main
    {
        public static int Run(string[] args)
        {
            string? profileName = null;
            string? spidStr = null;
            string? hostOverride = null;
            int portOverride = 0;
            string? userOverride = null;
            string? passOverride = null;
            string? platformOverride = null;
            string? dbOverride = null;

            var positional = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg == "-S" || arg == "--server") { profileName = args[++i]; }
                else if (arg.StartsWith("-S") && arg.Length > 2) { profileName = arg.Substring(2); }
                else if (arg == "-H" || arg == "--host") { hostOverride = args[++i]; }
                else if (arg == "-p" || arg == "--port") { portOverride = int.Parse(args[++i]); }
                else if (arg == "-U" || arg == "--user") { userOverride = args[++i]; }
                else if (arg == "-P" || arg == "--password") { passOverride = args[++i]; }
                else if (arg == "-D" || arg == "--database-override") { dbOverride = args[++i]; }
                else if (arg == "--platform") { platformOverride = args[++i]; }
                else { positional.Add(arg); }
            }

            if (positional.Count >= 1 && profileName == null)
                profileName = positional[0];
            if (positional.Count >= 2)
                spidStr = positional[1];

            if (string.IsNullOrEmpty(profileName) || string.IsNullOrEmpty(spidStr) || !int.TryParse(spidStr, out var spid) || spid <= 0)
            {
                Console.Error.WriteLine("Usage: iplanext <profile> <spid>");
                return 1;
            }

            var profileMgr = new ProfileManager();
            var resolved = ResolveConnection(profileMgr, profileName!, hostOverride, portOverride,
                                             userOverride, passOverride, platformOverride);
            if (resolved == null)
            {
                Console.Error.WriteLine($"Profile '{profileName}' not found.");
                return 1;
            }

            var database = dbOverride ?? "master";
            var outputFile = $"PLANTRACE.{resolved.ProfileName}.{spid}.{Environment.ProcessId}";

            using var executor = SqlExecutorFactory.Create(resolved);
            using var writer = new StreamWriter(outputFile, false);

            writer.WriteLine($"--- PLANTRACE {DateTime.Now} ---");
            writer.WriteLine();

            // Query 1: Process Info
            string sqlProcessInfo;
            if (resolved.ServerType == SQLServerTypes.MSSQL)
            {
                sqlProcessInfo = $@"SELECT spid, status, loginame AS login, hostname, blocked AS blk,
                    cmd AS comm, cpu, physical_io AS io, hostprocess AS host,
                    program_name AS proce
                    FROM master..sysprocesses
                    WHERE spid = {spid}";
            }
            else
            {
                sqlProcessInfo = $@"SELECT spid, status, suser_name(suid) AS login, hostname, blocked AS blk,
                    cmd AS comm, cpu, physical_io AS io, hostprocess AS host,
                    object_name(id, dbid) AS proce
                    FROM master..sysprocesses
                    WHERE spid = {spid}";
            }

            writer.WriteLine("--- Process Info ---");
            var result1 = executor.ExecuteSql(sqlProcessInfo, "master", captureOutput: true);
            writer.WriteLine(result1.Output);
            writer.WriteLine();

            // Query 2: SQL Text
            string sqlText;
            if (resolved.ServerType == SQLServerTypes.MSSQL)
            {
                sqlText = $@"SELECT SUBSTRING(st.text, (r.statement_start_offset/2)+1,
                    ((CASE r.statement_end_offset WHEN -1 THEN DATALENGTH(st.text)
                    ELSE r.statement_end_offset END - r.statement_start_offset)/2)+1) AS statement_text,
                    st.text AS full_text
                    FROM sys.dm_exec_requests r
                    CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) st
                    WHERE r.session_id = {spid}";
            }
            else
            {
                sqlText = $"dbcc traceon(3604)\ngo\ndbcc sqltext({spid})";
            }

            writer.WriteLine("--- SQL Text ---");
            var result2 = executor.ExecuteSql(sqlText, database, captureOutput: true);
            writer.WriteLine(result2.Output);
            writer.WriteLine();

            // Query 3: Execution Plan
            string sqlPlan;
            if (resolved.ServerType == SQLServerTypes.MSSQL)
            {
                sqlPlan = $@"SELECT r.session_id AS spid, r.status, r.command,
                    DB_NAME(r.database_id) AS database_name,
                    r.cpu_time, r.total_elapsed_time,
                    CAST(qp.query_plan AS nvarchar(max)) AS query_plan
                    FROM sys.dm_exec_requests r
                    CROSS APPLY sys.dm_exec_query_plan(r.plan_handle) qp
                    WHERE r.session_id = {spid}";
            }
            else
            {
                sqlPlan = $"sp_showplan {spid}, NULL, NULL, NULL";
            }

            writer.WriteLine("--- Execution Plan ---");
            var result3 = executor.ExecuteSql(sqlPlan, database, captureOutput: true);
            writer.WriteLine(result3.Output);

            writer.Close();

            // Display output
            Console.WriteLine(outputFile);
            Console.WriteLine(File.ReadAllText(outputFile));
            Console.WriteLine(outputFile);

            return 0;
        }

        private static ResolvedProfile? ResolveConnection(ProfileManager mgr, string profileName,
            string? hostOverride, int portOverride, string? userOverride, string? passOverride, string? platformOverride)
        {
            var profile = mgr.ResolveProfile(profileName);
            if (profile == null) return null;

            var p = profile.Value.Profile;
            return new ResolvedProfile
            {
                ProfileName = profile.Value.ProfileName,
                Host = hostOverride ?? p.Host,
                Port = portOverride > 0 ? portOverride : p.EffectivePort,
                User = userOverride ?? p.Username,
                Pass = passOverride ?? p.Password,
                ServerType = platformOverride != null
                    ? (platformOverride.ToUpper() == "MSSQL" ? SQLServerTypes.MSSQL : SQLServerTypes.SYBASE)
                    : p.ServerType,
                Company = p.Company ?? "101",
                Language = p.DefaultLanguage ?? "1",
                IRPath = p.SqlSource ?? "",
                IsProfile = true
            };
        }
    }
}
