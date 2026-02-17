using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace ibsCompiler
{
    /// <summary>
    /// Port of Python iplan.py.
    /// Shows execution plan for a running SPID.
    /// Cross-platform replacement for Unix shell script.
    /// </summary>
    public static class iplan_main
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
                Console.Error.WriteLine("Usage: iplan <profile> <spid>");
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

            string sql;
            if (resolved.ServerType == SQLServerTypes.MSSQL)
            {
                sql = $@"SELECT r.session_id AS spid, r.status, r.command,
                        DB_NAME(r.database_id) AS database_name,
                        r.cpu_time, r.total_elapsed_time,
                        CAST(qp.query_plan AS nvarchar(max)) AS query_plan
                        FROM sys.dm_exec_requests r
                        CROSS APPLY sys.dm_exec_query_plan(r.plan_handle) qp
                        WHERE r.session_id = {spid}";
            }
            else
            {
                sql = $"sp_showplan {spid}, NULL, NULL, NULL";
            }

            using var executor = SqlExecutorFactory.Create(resolved);
            var result = executor.ExecuteSql(sql, database, captureOutput: false);

            if (string.IsNullOrWhiteSpace(result.Output) || !result.Returncode)
            {
                Console.WriteLine($"There is no active server process for the specified spid value '{spid}'.");
                Console.WriteLine("Possibly the user connection has terminated.");
            }

            return result.Returncode ? 0 : 1;
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
