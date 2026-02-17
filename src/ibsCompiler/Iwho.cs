using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace ibsCompiler
{
    /// <summary>
    /// Port of Python iwho.py.
    /// Shows process list from master..sysprocesses.
    /// Cross-platform replacement for Sybase-only 'z' stored procedure.
    /// </summary>
    public static class iwho_main
    {
        public static int Run(string[] args)
        {
            // Parse arguments
            string? profileName = null;
            string? filter = null;
            string? hostOverride = null;
            int portOverride = 0;
            string? userOverride = null;
            string? passOverride = null;
            string? platformOverride = null;
            int timer = 0;

            var positional = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg == "-S" || arg == "--server") { profileName = args[++i]; }
                else if (arg.StartsWith("-S")) { profileName = arg.Substring(2); }
                else if (arg == "-H" || arg == "--host") { hostOverride = args[++i]; }
                else if (arg == "-p" || arg == "--port") { portOverride = int.Parse(args[++i]); }
                else if (arg == "-U" || arg == "--user") { userOverride = args[++i]; }
                else if (arg == "-P" || arg == "--password") { passOverride = args[++i]; }
                else if (arg == "--platform") { platformOverride = args[++i]; }
                else if (arg == "-t" || arg == "--timer") { timer = int.Parse(args[++i]); }
                else if (arg.StartsWith("-t") && int.TryParse(arg.Substring(2), out var t)) { timer = t; }
                else { positional.Add(arg); }
            }

            // Positional: profile [filter]
            if (positional.Count >= 1 && profileName == null)
                profileName = positional[0];
            if (positional.Count >= 2)
                filter = positional[1];

            if (string.IsNullOrEmpty(profileName))
            {
                Console.Error.WriteLine("Usage: iwho <profile> [filter] [-t seconds]");
                Console.Error.WriteLine("  filter: username, username%, or SPID number");
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

            do
            {
                RunQuery(resolved, filter ?? "");
                if (timer > 0)
                {
                    Thread.Sleep(timer * 1000);
                    Console.WriteLine();
                }
            } while (timer > 0);

            return 0;
        }

        private static void RunQuery(ResolvedProfile profile, string filter)
        {
            string sql;
            if (profile.ServerType == SQLServerTypes.MSSQL)
            {
                sql = @"SELECT spid,
                        RTRIM(substring(status, 1, 5)) AS status,
                        RTRIM(loginame) AS login,
                        RTRIM(substring(hostname, 1, 10)) AS hostname,
                        blocked AS blk,
                        RTRIM(substring(cmd, 1, 4)) AS comm,
                        cpu, physical_io AS io,
                        RTRIM(substring(hostprocess, 1, 5)) AS host,
                        RTRIM(program_name) AS proce
                        FROM master..sysprocesses";
            }
            else
            {
                sql = @"SELECT spid,
                        substring(status, 1, 5) AS status,
                        suser_name(suid) AS login,
                        substring(hostname, 1, 10) AS hostname,
                        blocked AS blk,
                        substring(cmd, 1, 4) AS comm,
                        cpu, physical_io AS io,
                        substring(hostprocess, 1, 5) AS host,
                        object_name(id, dbid) AS proce
                        FROM master..sysprocesses";
            }

            if (!string.IsNullOrEmpty(filter))
            {
                if (int.TryParse(filter, out _))
                    sql += $" WHERE spid = {filter}";
                else if (filter.Contains('%'))
                    sql += $" WHERE {(profile.ServerType == SQLServerTypes.MSSQL ? "loginame" : "suser_name(suid)")} LIKE '{filter}'";
                else
                    sql += $" WHERE {(profile.ServerType == SQLServerTypes.MSSQL ? "loginame" : "suser_name(suid)")} = '{filter}'";
            }

            using var executor = SqlExecutorFactory.Create(profile);
            var result = executor.ExecuteSql(sql, "master", captureOutput: false);
            if (!result.Returncode)
                Console.Error.WriteLine("Error executing query.");
        }

        private static ResolvedProfile? ResolveConnection(ProfileManager mgr, string profileName,
            string? hostOverride, int portOverride, string? userOverride, string? passOverride, string? platformOverride)
        {
            var profile = mgr.ResolveProfile(profileName);
            if (profile == null) return null;

            var p = profile.Value.Profile;
            var resolved = new ResolvedProfile
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
            return resolved;
        }
    }
}
