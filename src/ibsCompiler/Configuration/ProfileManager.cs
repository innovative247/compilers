using System.Text.Json;

namespace ibsCompiler.Configuration
{
    /// <summary>
    /// Loads profiles from settings.json and resolves connection parameters.
    /// Replaces F4.8 WindowsVariables (env var based) with profile-based configuration.
    /// Falls back to environment variables when no profile is found.
    /// </summary>
    public class ProfileManager
    {
        private SettingsFile _settings;
        private string? _settingsPath;

        public ProfileManager()
        {
            _settings = new SettingsFile();
            _settingsPath = FindSettingsFile();
            if (_settingsPath != null)
                LoadSettings(_settingsPath);
        }

        public ProfileManager(string settingsPath)
        {
            _settings = new SettingsFile();
            _settingsPath = settingsPath;
            if (File.Exists(settingsPath))
                LoadSettings(settingsPath);
        }

        private void LoadSettings(string path)
        {
            try
            {
                var json = File.ReadAllText(path);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                _settings = JsonSerializer.Deserialize<SettingsFile>(json, options) ?? new SettingsFile();
            }
            catch
            {
                _settings = new SettingsFile();
            }
        }

        /// <summary>
        /// Search for settings.json in standard locations.
        /// </summary>
        public static string? FindSettingsFile()
        {
            // 1. Executable directory — the canonical location (settings.json lives next to the binaries)
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                var exeDir = Path.GetDirectoryName(exePath);
                if (!string.IsNullOrEmpty(exeDir))
                {
                    var path = Path.Combine(exeDir, "settings.json");
                    if (File.Exists(path)) return path;
                }
            }

            // 2. AppContext.BaseDirectory fallback (framework-dependent deployments)
            var basePath = Path.Combine(AppContext.BaseDirectory, "settings.json");
            if (File.Exists(basePath)) return basePath;

            // 3. Current directory
            var cwdPath = Path.Combine(Directory.GetCurrentDirectory(), "settings.json");
            if (File.Exists(cwdPath)) return cwdPath;

            return null;
        }

        /// <summary>
        /// Resolve a server name or alias to a profile. Returns null if no profile found.
        /// </summary>
        public (string ProfileName, ProfileData Profile)? ResolveProfile(string nameOrAlias)
        {
            if (string.IsNullOrEmpty(nameOrAlias) || _settings.Profiles.Count == 0)
                return null;

            var upper = nameOrAlias.ToUpperInvariant();

            // Exact name match (case-insensitive)
            foreach (var kvp in _settings.Profiles)
            {
                if (kvp.Key.ToUpperInvariant() == upper)
                    return (kvp.Key, kvp.Value);
            }

            // Alias match (case-insensitive)
            foreach (var kvp in _settings.Profiles)
            {
                if (kvp.Value.Aliases != null)
                {
                    foreach (var alias in kvp.Value.Aliases)
                    {
                        if (alias.ToUpperInvariant() == upper)
                            return (kvp.Key, kvp.Value);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Build a resolved profile from command-line args.
        /// If -S matches a profile, use its settings. Command-line flags override profile values.
        /// Falls back to environment variables if no profile matches.
        /// </summary>
        public ResolvedProfile Resolve(CommandVariables cmdvars)
        {
            var profile = ResolveProfile(cmdvars.Server);
            if (profile.HasValue)
            {
                var p = profile.Value.Profile;
                return new ResolvedProfile
                {
                    ProfileName = profile.Value.ProfileName,
                    Host = p.Host,
                    Port = p.EffectivePort,
                    User = string.IsNullOrEmpty(cmdvars.User) || cmdvars.User == "sbn0" ? p.Username : cmdvars.User,
                    Pass = string.IsNullOrEmpty(cmdvars.Pass) || cmdvars.Pass == "ibsibs" ? p.Password : cmdvars.Pass,
                    ServerType = cmdvars.ServerType != default ? cmdvars.ServerType : p.ServerType,
                    Company = p.Company ?? "101",
                    Language = p.DefaultLanguage ?? "1",
                    IRPath = p.SqlSource ?? "",
                    RawMode = p.RawMode,
                    IsProfile = true
                };
            }

            // Fallback to environment variables (legacy F4.8 behavior)
            return ResolveFromEnvironment(cmdvars);
        }

        private static ResolvedProfile ResolveFromEnvironment(CommandVariables cmdvars)
        {
            var irPath = Environment.GetEnvironmentVariable("IR") ?? "";
            var company = Environment.GetEnvironmentVariable("CMPY") ?? "101";
            var language = Environment.GetEnvironmentVariable("IBSLANG") ?? "1";
            var dbcmd = Environment.GetEnvironmentVariable("DBCMD") ?? "SYBASE";

            SQLServerTypes serverType;
            if (cmdvars.ServerType != default)
                serverType = cmdvars.ServerType;
            else if (dbcmd.ToUpper() == "MSSQL")
                serverType = SQLServerTypes.MSSQL;
            else
                serverType = SQLServerTypes.SYBASE;

            // When no profile, treat -S as a literal hostname
            var port = serverType == SQLServerTypes.MSSQL ? 1433 : 5000;
            if (!string.IsNullOrEmpty(cmdvars.Port))
            {
                var portStr = cmdvars.Port.TrimStart(':', ',');
                if (int.TryParse(portStr, out var p)) port = p;
            }

            return new ResolvedProfile
            {
                ProfileName = cmdvars.Server,
                Host = cmdvars.Server,
                Port = port,
                User = cmdvars.User,
                Pass = cmdvars.Pass,
                ServerType = serverType,
                Company = company,
                Language = language,
                IRPath = irPath,
                RawMode = false,
                IsProfile = false
            };
        }

        public List<string> ListProfiles()
        {
            return _settings.Profiles.Keys.ToList();
        }
    }

    /// <summary>
    /// Fully resolved connection and environment settings, combining profile data with command-line overrides.
    /// This replaces F4.8's WindowsVariables.
    /// </summary>
    public class ResolvedProfile
    {
        public string ProfileName { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string User { get; set; } = "sbn0";
        public string Pass { get; set; } = "ibsibs";
        public SQLServerTypes ServerType { get; set; } = SQLServerTypes.SYBASE;
        public string Company { get; set; } = "101";
        public string Language { get; set; } = "1";
        public string IRPath { get; set; } = "";
        public bool RawMode { get; set; }
        public bool IsProfile { get; set; }
    }
}
