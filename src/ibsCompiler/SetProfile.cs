using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace ibsCompiler
{
    /// <summary>
    /// Port of Python set_profile.py.
    /// Interactive profile management wizard for settings.json.
    /// Create, edit, view, test, copy, and delete profiles.
    /// </summary>
    public static class set_profile_main
    {
        private static SettingsFile _settings = new();
        private static string _settingsPath = "";

        public static int Run(string[] args)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

            LoadSettings();
            MainMenu();
            return 0;
        }

        #region Icons
        private static class Icons
        {
            private static readonly bool _unicode = CheckUnicode();

            public static string GEAR     => _unicode ? "⚙"  : "[*]";
            public static string DATABASE => _unicode ? "🗄"  : "[DB]";
            public static string ARROW    => _unicode ? "🛢"  : "->";
            public static string BULLET   => _unicode ? "🔑" : "*";
            public static string FOLDER   => _unicode ? "📁" : "[D]";
            public static string WARNING  => _unicode ? "⚠"  : "[!]";
            public static string SUCCESS  => _unicode ? "✓"  : "[OK]";
            public static string ERROR    => _unicode ? "✗"  : "[X]";

            private static bool CheckUnicode()
            {
                try
                {
                    var enc = Console.OutputEncoding;
                    if (enc.CodePage == 65001) return true;
                    if (enc.EncodingName.Contains("Unicode", StringComparison.OrdinalIgnoreCase)) return true;
                    if (enc.EncodingName.Contains("UTF", StringComparison.OrdinalIgnoreCase)) return true;
                    if (Environment.GetEnvironmentVariable("WT_SESSION") != null) return true;
                    if (Environment.GetEnvironmentVariable("TERM_PROGRAM") == "vscode") return true;
                    if (Environment.GetEnvironmentVariable("MSYSTEM") != null) return true;
                    return false;
                }
                catch { return false; }
            }
        }
        #endregion

        #region Console Helpers
        private static void PrintHeader(string text, int width = 70)
        {
            var line = new string('=', width);
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(line);
            Console.WriteLine($"  {text}");
            Console.WriteLine(line);
            Console.ForegroundColor = prev;
        }

        private static void PrintSubheader(string text, int width = 70)
        {
            var line = new string('-', width);
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(line);
            Console.WriteLine($"  {text}");
            Console.WriteLine(line);
            Console.ForegroundColor = prev;
        }

        private static void PrintStep(int stepNum, string text)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"STEP {stepNum}: ");
            Console.ForegroundColor = prev;
            Console.WriteLine(text);
        }

        private static void PrintSuccess(string text)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"{Icons.SUCCESS} {text}");
            Console.ForegroundColor = prev;
        }

        private static void PrintError(string text)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"{Icons.ERROR} {text}");
            Console.ForegroundColor = prev;
        }

        private static void PrintWarning(string text)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"{Icons.WARNING} {text}");
            Console.ForegroundColor = prev;
        }

        private static void PrintDim(string text)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(text);
            Console.ForegroundColor = prev;
        }

        private static void PrintMenu(int num, string text)
        {
            var pad = num >= 10 ? " " : "  ";
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"{pad}{num}.");
            Console.ForegroundColor = prev;
            Console.WriteLine($" {text}");
        }

        private static void PrintField(string icon, string label, string value, ConsoleColor valueColor = ConsoleColor.White)
        {
            var prev = Console.ForegroundColor;
            Console.Write($"  {label,-13}");
            Console.ForegroundColor = valueColor;
            Console.WriteLine(value);
            Console.ForegroundColor = prev;
        }

        private static void WriteColor(string text, ConsoleColor color)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.Write(text);
            Console.ForegroundColor = prev;
        }

        private static void WriteBright(string text)
        {
            WriteColor(text, ConsoleColor.White);
        }
        #endregion

        #region Settings I/O
        private static void LoadSettings()
        {
            _settingsPath = ProfileManager.FindSettingsFile() ?? "";
            if (!string.IsNullOrEmpty(_settingsPath) && File.Exists(_settingsPath))
            {
                try
                {
                    var json = File.ReadAllText(_settingsPath);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    _settings = JsonSerializer.Deserialize<SettingsFile>(json, options) ?? new SettingsFile();
                    PrintSuccess($"Loaded settings from: {_settingsPath}");
                }
                catch (JsonException ex)
                {
                    PrintError($"Error reading settings.json: {ex.Message}");
                    _settings = new SettingsFile();
                }
            }
            else
            {
                _settings = new SettingsFile();
                if (string.IsNullOrEmpty(_settingsPath))
                {
                    var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
                    if (string.IsNullOrEmpty(exeDir)) exeDir = AppContext.BaseDirectory;
                    _settingsPath = Path.Combine(exeDir, "settings.json");
                }
                Console.WriteLine($"Creating new settings file: {_settingsPath}");
            }
        }

        private static bool SaveSettings()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_settings, options);
                File.WriteAllText(_settingsPath, json);
                PrintSuccess($"Settings saved to: {_settingsPath}");
                return true;
            }
            catch (Exception ex)
            {
                PrintError($"Error saving settings: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Main Menu
        private static void MainMenu()
        {
            PrintHeader("Profile Setup Wizard");
            Console.WriteLine();
            PrintDim("  Profiles store database connection settings used by runsql, runcreate,");
            PrintDim("  and other compiler commands. Each profile points to a SQL source directory.");

            while (true)
            {
                Console.WriteLine();
                WriteBright("Main Menu");
                Console.Write(" ");
                var prev = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"({_settings.Profiles.Count} profiles configured)");
                Console.ForegroundColor = prev;
                Console.WriteLine();
                PrintMenu(1, "New profile");
                PrintMenu(2, "Existing profile");
                PrintMenu(3, "Add to IDE");
                PrintMenu(4, "Open settings.json");
                PrintMenu(99, "Exit");

                Console.Write("\nChoose [1-4]: ");
                var input = Console.ReadLine()?.Trim();

                switch (input)
                {
                    case "1": CreateProfile(); break;
                    case "2": ExistingProfileMenu(); break;
                    case "3": AddToIdeMenu(); break;
                    case "4": OpenSettingsJson(); break;
                    case "99":
                        Console.WriteLine("\nExiting profile setup wizard.");
                        var line = new string('=', 70);
                        Console.WriteLine(line);
                        PrintSuccess("Profile configuration complete!");
                        Console.WriteLine(line);
                        Console.WriteLine($"\nYour profiles are saved in: {_settingsPath}");
                        return;
                    default: Console.WriteLine("Invalid selection."); break;
                }
            }
        }
        #endregion

        #region List Profiles
        private static void ListProfiles()
        {
            if (_settings.Profiles.Count == 0)
            {
                PrintWarning("No profiles configured yet.");
                Console.WriteLine();
                Console.Write("  Run ");
                WriteBright("set_profile");
                Console.WriteLine(" and select option 1 to create your first profile.");
                return;
            }
            Console.WriteLine();
            PrintSubheader($"Configured Profiles ({_settings.Profiles.Count})");
            foreach (var kvp in _settings.Profiles)
                DisplayProfile(kvp.Key, kvp.Value);
            Console.WriteLine();
        }

        private static void DisplayProfile(string name, ProfileData profile)
        {
            Console.WriteLine();
            WriteBright(name);
            if (profile.Aliases?.Count > 0)
            {
                var joined = string.Join(", ", profile.Aliases);
                Console.Write(" ");
                var prev = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"(aliases: {joined})");
                Console.ForegroundColor = prev;
            }
            Console.WriteLine();

            if (!profile.RawMode)
                PrintField(Icons.GEAR, "Company:", profile.Company ?? "unknown");
            PrintField(Icons.DATABASE, "Platform:", profile.Platform ?? "unknown", ConsoleColor.Cyan);
            PrintField(Icons.ARROW, "Server:", $"{profile.Host}:{profile.EffectivePort}", ConsoleColor.Green);
            PrintField(Icons.BULLET, "Username:", profile.Username ?? "unknown");

            if (!profile.RawMode)
            {
                var sqlSource = profile.SqlSource ?? "unknown";
                var prev2 = Console.ForegroundColor;
                Console.Write($"  {"SQL Source:",-13}");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(sqlSource);
                Console.ForegroundColor = prev2;
            }

            if (profile.RawMode)
                PrintField(Icons.WARNING, "Raw Mode:", "Yes", ConsoleColor.Yellow);
        }
        #endregion

        #region Create Profile
        private static void CreateProfile()
        {
            Console.WriteLine();
            PrintSubheader("Create New Profile");
            Console.WriteLine();
            PrintDim("  A profile stores all the connection settings needed to compile SQL");
            PrintDim("  to a specific database server. Follow the prompts below.");
            Console.WriteLine();

            var profile = new ProfileData();

            // 1. Profile Name
            PrintStep(1, "Profile Name");
            PrintDim("  Use a short, memorable name like GONZO, PROD, or SRM.");
            string name;
            while (true)
            {
                Console.Write("  Profile name: ");
                name = Console.ReadLine()?.Trim().ToUpper() ?? "";
                if (string.IsNullOrEmpty(name) || !Regex.IsMatch(name, @"^[A-Z0-9_]+$"))
                {
                    PrintWarning("Invalid name. Use alphanumeric characters and underscores only.");
                    continue;
                }
                if (_settings.Profiles.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)))
                {
                    PrintError($"Profile '{name}' already exists.");
                    continue;
                }
                break;
            }

            // 2. Aliases
            Console.WriteLine();
            PrintStep(2, "Aliases (Optional)");
            PrintDim("  Aliases allow shortcuts for this profile (e.g., 'G' for 'GONZO').");
            profile.Aliases = PromptAliases(name, new List<string>());

            // 3. Raw Mode (drives what else is asked)
            Console.WriteLine();
            PrintStep(3, "Raw Mode");
            PrintDim("  Raw mode skips SBN-specific preprocessing (options files, changelog).");
            PrintDim("  Use this for projects without the CSS/Setup/ directory structure.");
            Console.Write("  Enable raw mode? [y/N]: ");
            var raw = Console.ReadLine()?.Trim().ToUpper();
            profile.RawMode = raw == "Y";

            // 4. Platform
            Console.WriteLine();
            PrintStep(4, "Database Platform");
            PrintDim("  Select the type of database server you are connecting to.");
            Console.WriteLine();
            PrintMenu(1, "Sybase ASE");
            PrintMenu(2, "Microsoft SQL Server");
            while (true)
            {
                Console.Write("\n  Choose [1-2]: ");
                var platform = Console.ReadLine()?.Trim();
                if (platform == "1") { profile.Platform = "SYBASE"; break; }
                if (platform == "2") { profile.Platform = "MSSQL"; break; }
                Console.WriteLine("  Invalid choice. Please enter 1 or 2.");
            }

            // 5. Host + Port
            Console.WriteLine();
            PrintStep(5, "Server Connection");
            PrintDim("  Enter the hostname or IP address of your database server (do not include port).");
            string host;
            while (true)
            {
                Console.Write("  Hostname or IP: ");
                host = Console.ReadLine()?.Trim() ?? "";
                if (!string.IsNullOrEmpty(host)) break;
                PrintWarning("Server hostname is required.");
            }
            profile.Host = host;

            var defaultPort = profile.Platform == "MSSQL" ? "1433" : "5000";
            while (true)
            {
                Console.Write($"  Port [{defaultPort}]: ");
                var port = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(port))
                {
                    profile.Port = int.Parse(defaultPort);
                    break;
                }
                if (int.TryParse(port, out var p))
                {
                    profile.Port = p;
                    break;
                }
                PrintWarning("Port must be a number.");
            }

            // 6. Credentials
            Console.WriteLine();
            PrintStep(6, "Database Credentials");
            PrintDim("  Enter the username and password for the database server.");
            string username;
            while (true)
            {
                Console.Write("  Username: ");
                username = Console.ReadLine()?.Trim() ?? "";
                if (!string.IsNullOrEmpty(username)) break;
                PrintWarning("Username is required.");
            }
            profile.Username = username;

            string password;
            while (true)
            {
                Console.Write("  Password: ");
                password = ReadPassword();
                Console.WriteLine();
                if (!string.IsNullOrEmpty(password)) break;
                PrintWarning("Password is required.");
            }
            profile.Password = password;

            // 7+. Raw vs normal: different remaining fields
            profile.DefaultLanguage = "1";
            if (profile.RawMode)
            {
                profile.Company = "0";
                profile.SqlSource = "";

                Console.WriteLine();
                PrintStep(7, "Default Database");
                PrintDim("  Default database used when -D is not specified on the command line.");
                Console.Write("  Default database: ");
                profile.Database = Console.ReadLine()?.Trim() ?? "";
            }
            else
            {
                Console.WriteLine();
                PrintStep(7, "Company Number");
                PrintDim("  The COMPANY number identifies your organization in the database.");
                Console.Write("  Company [101]: ");
                var company = Console.ReadLine()?.Trim();
                profile.Company = string.IsNullOrEmpty(company) ? "101" : company;

                Console.WriteLine();
                PrintStep(8, "SQL Source Directory");
                PrintDim("  The directory containing your SQL source files (CSS/, IBS/ folders).");
                var cwd = Directory.GetCurrentDirectory();
                PrintDim($"  Enter '.' to use current directory: {cwd}");
                while (true)
                {
                    Console.Write("\n  Path: ");
                    var sqlSource = Console.ReadLine()?.Trim();
                    if (string.IsNullOrEmpty(sqlSource))
                    {
                        PrintWarning("SQL source path is required.");
                        continue;
                    }
                    if (sqlSource == "." || sqlSource == "./" || sqlSource == ".\\")
                    {
                        sqlSource = cwd;
                        PrintSuccess($"Using: {sqlSource}");
                    }
                    else if (Directory.Exists(sqlSource))
                    {
                        PrintSuccess($"Using: {sqlSource}");
                    }
                    else
                    {
                        PrintWarning($"Path does not exist: {sqlSource}");
                        Console.Write("  Use anyway? [y/n]: ");
                        var useAnyway = Console.ReadLine()?.Trim().ToLower();
                        if (useAnyway != "y") continue;
                    }
                    profile.SqlSource = sqlSource;
                    break;
                }
            }

            // Save
            _settings.Profiles[name] = profile;
            if (SaveSettings())
            {
                Console.WriteLine();
                PrintSuccess($"Profile '{name}' created!");
                DisplayProfile(name, profile);

                // Test connection?
                Console.Write("\nTest connection now? (Y/n): ");
                var test = Console.ReadLine()?.Trim().ToUpper();
                if (test != "N")
                    TestConnection(profile);
            }
        }
        #endregion

        #region Existing Profile Menu
        private static void ExistingProfileMenu()
        {
            if (_settings.Profiles.Count == 0)
            {
                PrintWarning("No profiles configured yet.");
                return;
            }

            ListProfiles();

            Console.Write("Enter profile name or alias (leave blank to cancel): ");
            var input = Console.ReadLine()?.Trim().ToUpper();
            if (string.IsNullOrEmpty(input)) return;

            var match = FindProfile(input);
            if (match == null)
            {
                PrintError($"Profile '{input}' not found.");
                return;
            }

            var (profileName, profile) = match.Value;

            while (true)
            {
                Console.WriteLine();
                WriteBright($"Profile: {profileName}");
                Console.WriteLine();
                Console.WriteLine();
                PrintMenu(1, "View");
                PrintMenu(2, "Test");
                PrintMenu(3, "Edit");
                PrintMenu(4, "Copy");
                PrintMenu(5, "Delete");
                PrintMenu(98, "Back");
                PrintMenu(99, "Exit");

                Console.Write("\nChoose [1-5]: ");
                var choice = Console.ReadLine()?.Trim();

                switch (choice)
                {
                    case "1":
                        DisplayProfile(profileName, profile);
                        break;
                    case "2":
                        TestProfileMenu(profileName, profile);
                        break;
                    case "3":
                        EditProfile(profileName, profile);
                        break;
                    case "4":
                        CopyProfile(profileName, profile);
                        break;
                    case "5":
                        if (DeleteProfile(profileName)) return;
                        break;
                    case "98": return;
                    case "99": Environment.Exit(0); break;
                    default: Console.WriteLine("Invalid selection."); break;
                }
            }
        }
        #endregion

        #region Edit Profile
        private static void EditProfile(string name, ProfileData profile)
        {
            Console.WriteLine();
            PrintSubheader($"Edit Profile: {name}");
            PrintDim("  Press Enter to keep current value.");
            Console.WriteLine();

            // Raw Mode first — drives what else is shown
            PrintDim("  Raw mode skips SBN-specific preprocessing (options files, symlinks, changelog).");
            var currentRaw = profile.RawMode ? "y" : "n";
            Console.Write($"  Raw mode (y/N) [{currentRaw}]: ");
            var val = Console.ReadLine()?.Trim().ToLower();
            if (val == "y") profile.RawMode = true;
            else if (val == "n") profile.RawMode = false;

            Console.Write($"  Aliases [{string.Join(", ", profile.Aliases ?? new List<string>())}] (enter 'clear' to remove): ");
            var aliasInput = Console.ReadLine()?.Trim();
            if (aliasInput?.ToLower() == "clear")
                profile.Aliases = new List<string>();
            else if (!string.IsNullOrEmpty(aliasInput))
                profile.Aliases = PromptAliases(name, profile.Aliases ?? new List<string>(), aliasInput);

            Console.Write($"  Platform [{profile.Platform}] (1=Sybase, 2=MSSQL): ");
            val = Console.ReadLine()?.Trim();
            if (val == "1") profile.Platform = "SYBASE";
            else if (val == "2") profile.Platform = "MSSQL";

            Console.Write($"  Host [{profile.Host}] (do not include port): ");
            val = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(val)) profile.Host = val;

            Console.Write($"  Port [{profile.EffectivePort}]: ");
            val = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(val) && int.TryParse(val, out var port)) profile.Port = port;

            Console.Write($"  Username [{profile.Username}]: ");
            val = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(val)) profile.Username = val;

            Console.Write("  Password [****]: ");
            val = ReadPassword();
            Console.WriteLine();
            if (!string.IsNullOrEmpty(val)) profile.Password = val;

            if (profile.RawMode)
            {
                profile.Company = "0";
                profile.SqlSource = "";

                Console.WriteLine();
                PrintDim("  Default database for raw mode (used when -D not specified).");
                Console.Write($"  Database [{profile.Database}]: ");
                val = Console.ReadLine()?.Trim();
                if (!string.IsNullOrEmpty(val)) profile.Database = val;
            }
            else
            {
                Console.Write($"  Company [{profile.Company}]: ");
                val = Console.ReadLine()?.Trim();
                if (!string.IsNullOrEmpty(val)) profile.Company = val;

                Console.Write($"  SQL Source [{profile.SqlSource}]: ");
                PrintDim($"    (Enter '.' for current directory: {Directory.GetCurrentDirectory()})");
                Console.Write("    New value: ");
                val = Console.ReadLine()?.Trim();
                if (val == "." || val == "./" || val == ".\\")
                {
                    profile.SqlSource = Directory.GetCurrentDirectory();
                    Console.WriteLine($"    Using: {profile.SqlSource}");
                }
                else if (!string.IsNullOrEmpty(val))
                    profile.SqlSource = val;

                profile.Database = "";
            }

            if (SaveSettings())
            {
                PrintSuccess($"Profile '{name}' updated.");
                DisplayProfile(name, profile);
            }
        }
        #endregion

        #region Copy Profile
        private static void CopyProfile(string sourceName, ProfileData sourceProfile)
        {
            Console.Write("New profile name: ");
            var newName = Console.ReadLine()?.Trim().ToUpper();
            if (string.IsNullOrEmpty(newName) || !Regex.IsMatch(newName, @"^[A-Z0-9_]+$"))
            {
                Console.WriteLine("Invalid name.");
                return;
            }
            if (_settings.Profiles.Keys.Any(k => string.Equals(k, newName, StringComparison.OrdinalIgnoreCase)))
            {
                PrintError($"Profile '{newName}' already exists.");
                return;
            }

            var json = JsonSerializer.Serialize(sourceProfile);
            var newProfile = JsonSerializer.Deserialize<ProfileData>(json)!;
            newProfile.Aliases = new List<string>();

            _settings.Profiles[newName] = newProfile;
            if (SaveSettings())
            {
                PrintSuccess($"Profile '{sourceName}' copied to '{newName}'.");
                Console.Write("Edit the new profile? (Y/n): ");
                var edit = Console.ReadLine()?.Trim().ToUpper();
                if (edit != "N")
                    EditProfile(newName, newProfile);
            }
        }
        #endregion

        #region Delete Profile
        private static bool DeleteProfile(string name)
        {
            Console.WriteLine();
            DisplayProfile(name, _settings.Profiles[name]);
            Console.WriteLine();
            Console.Write("Type 'delete' to confirm: ");
            var confirm = Console.ReadLine()?.Trim().ToLower();
            if (confirm != "delete")
            {
                Console.WriteLine("Cancelled.");
                return false;
            }
            _settings.Profiles.Remove(name);
            if (SaveSettings())
                PrintSuccess($"Profile '{name}' deleted.");
            return true;
        }
        #endregion

        #region Test Profile
        private static void TestProfileMenu(string name, ProfileData profile)
        {
            while (true)
            {
                Console.WriteLine();
                WriteBright($"Test: {name}");
                Console.WriteLine();
                if (profile.RawMode)
                    PrintDim("  (RAW MODE - preprocessing tests not available)");
                Console.WriteLine();
                PrintMenu(1, "Test SQL Source path");
                PrintMenu(2, "Test connection");
                if (!profile.RawMode)
                {
                    PrintMenu(3, "Test options");
                    PrintMenu(4, "Test table locations");
                    PrintMenu(5, "Test changelog");
                    PrintMenu(6, "Test symbolic links");
                }
                PrintMenu(98, "Back");
                PrintMenu(99, "Exit");

                var maxChoice = profile.RawMode ? 2 : 6;
                Console.Write($"\nChoose [1-{maxChoice}]: ");
                var choice = Console.ReadLine()?.Trim();

                switch (choice)
                {
                    case "1":
                        TestSqlSource(profile);
                        break;
                    case "2":
                        TestConnection(profile);
                        break;
                    case "3" when !profile.RawMode:
                        TestOptions(name, profile);
                        break;
                    case "4" when !profile.RawMode:
                        TestTableLocations(profile);
                        break;
                    case "5" when !profile.RawMode:
                        TestChangelog(name, profile);
                        break;
                    case "6" when !profile.RawMode:
                        TestSymbolicLinks(profile);
                        break;
                    case "98": return;
                    case "99": Environment.Exit(0); break;
                    default: Console.WriteLine("Invalid selection."); break;
                }
            }
        }

        private static void TestSqlSource(ProfileData profile)
        {
            if (string.IsNullOrEmpty(profile.SqlSource))
            {
                PrintError("SQL Source is not set.");
                return;
            }
            if (Directory.Exists(profile.SqlSource))
            {
                PrintSuccess($"SQL Source exists: {profile.SqlSource}");
                var cssSetup = Path.Combine(profile.SqlSource, "css", "setup");
                if (Directory.Exists(cssSetup))
                    PrintSuccess($"  css/setup directory found: {cssSetup}");
                else
                    PrintError($"  css/setup directory NOT found: {cssSetup}");
            }
            else
            {
                PrintError($"SQL Source NOT found: {profile.SqlSource}");
            }
        }

        private static void TestConnection(ProfileData profile)
        {
            Console.Write("Testing connection... ");
            var resolved = new ResolvedProfile
            {
                Host = profile.Host,
                Port = profile.EffectivePort,
                User = profile.Username,
                Pass = profile.Password,
                ServerType = profile.ServerType,
                Company = profile.Company ?? "101",
                Language = profile.DefaultLanguage ?? "1",
                IRPath = profile.SqlSource ?? ""
            };

            try
            {
                using var executor = SqlExecutorFactory.Create(resolved);
                var result = executor.ExecuteSql("SELECT 1", "master", captureOutput: true);
                if (result.Returncode)
                    PrintSuccess("Connection successful!");
                else
                {
                    PrintError("Connection failed.");
                    if (!string.IsNullOrEmpty(result.Output))
                        Console.WriteLine(result.Output);
                }
            }
            catch (Exception ex)
            {
                PrintError($"Connection failed: {ex.Message}");
            }
        }

        private static void TestOptions(string profileName, ProfileData profile)
        {
            Console.WriteLine("\nTesting options files...");

            if (string.IsNullOrEmpty(profile.SqlSource))
            {
                PrintError("SQL Source is not set.");
                return;
            }

            var cssSetup = Path.Combine(profile.SqlSource, "CSS", "Setup");
            if (!Directory.Exists(cssSetup))
            {
                PrintError($"CSS/Setup directory NOT found: {cssSetup}");
                return;
            }

            var company = profile.Company ?? "101";

            // options.def — defaults
            var optDef = Path.Combine(cssSetup, "options.def");
            if (File.Exists(optDef))
                PrintSuccess($"  options.def found ({CountLines(optDef)} lines)");
            else
                PrintDim("  options.def not found (optional)");

            // options.{company} — company-specific (required)
            var optCompany = Path.Combine(cssSetup, $"options.{company}");
            if (File.Exists(optCompany))
                PrintSuccess($"  options.{company} found ({CountLines(optCompany)} lines)");
            else
                PrintError($"  options.{company} NOT found — this file is required");

            // options.{company}.{profile} — profile-specific
            var optProfile = Path.Combine(cssSetup, $"options.{company}.{profileName}");
            if (File.Exists(optProfile))
                PrintSuccess($"  options.{company}.{profileName} found ({CountLines(optProfile)} lines)");
            else
                PrintDim($"  options.{company}.{profileName} not found (optional)");
        }

        private static int CountLines(string filePath)
        {
            try { return File.ReadAllLines(filePath).Length; }
            catch { return 0; }
        }

        private static void TestTableLocations(ProfileData profile)
        {
            var tblLoc = Path.Combine(profile.SqlSource ?? "", "css", "setup", "table_locations");
            if (File.Exists(tblLoc))
                PrintSuccess($"Table locations file found: {tblLoc}");
            else
                PrintError($"Table locations file NOT found: {tblLoc}");
        }

        private static void TestChangelog(string profileName, ProfileData profile)
        {
            Console.WriteLine("\nTesting changelog...");

            var resolved = new ResolvedProfile
            {
                ProfileName = profileName,
                Host = profile.Host,
                Port = profile.EffectivePort,
                User = profile.Username,
                Pass = profile.Password,
                ServerType = profile.ServerType,
                Company = profile.Company ?? "101",
                Language = profile.DefaultLanguage ?? "1",
                IRPath = profile.SqlSource ?? "",
                IsProfile = true
            };

            // Build CommandVariables for Options resolution
            var cmdvars = new CommandVariables
            {
                User = profile.Username,
                Pass = profile.Password,
                ServerType = profile.ServerType,
                Database = $"{profile.Company}pr",
                Command = "TEST"
            };
            cmdvars.Server = $"{profile.Host}:{profile.EffectivePort}";

            // Resolve placeholders via Options
            var myOptions = new Options(cmdvars, resolved, true);
            if (!myOptions.GenerateOptionFiles())
            {
                PrintError("Could not load options files. Ensure SQL Source and CSS/Setup are configured.");
                return;
            }

            var dbpro = myOptions.ReplaceOptions("&dbpro&");
            if (dbpro == "&dbpro&")
            {
                PrintError("Could not resolve &dbpro& placeholder.");
                return;
            }

            try
            {
                using var executor = SqlExecutorFactory.Create(resolved);

                // Step 1: Check if gclog12 is enabled
                Console.Write("  Checking gclog12 option... ");
                var optionsTable = myOptions.ReplaceOptions("&options&");
                var query = $"SELECT act_flg FROM {optionsTable} WHERE id = 'gclog12'";
                var result = executor.ExecuteSql(query, dbpro, captureOutput: true);

                if (!result.Returncode || string.IsNullOrEmpty(result.Output) ||
                    !result.Output.Contains("+"))
                {
                    PrintError("gclog12 option is off.");
                    return;
                }

                PrintSuccess("gclog12 is on.");

                // Step 2: Check ba_gen_chg_log_new exists
                Console.Write("  Checking ba_gen_chg_log_new... ");
                var spCheck = executor.ExecuteSql(
                    "SELECT 1 FROM sysobjects WHERE name = 'ba_gen_chg_log_new' AND type = 'P'",
                    dbpro, captureOutput: true);

                if (!spCheck.Returncode || string.IsNullOrEmpty(spCheck.Output) ||
                    !spCheck.Output.Contains("1"))
                {
                    PrintError($"ba_gen_chg_log_new stored procedure not found in {dbpro}.");
                    return;
                }

                PrintSuccess("ba_gen_chg_log_new exists.");

                // Step 3: Insert test changelog entry
                var osUser = Environment.UserName;
                var exePath = Environment.ProcessPath ?? "TEST";
                var description = $"User {osUser}: set_profile test".Replace("'", "''");
                var cmdStr = $"{exePath}".Replace("'", "''");

                Console.Write("  Inserting test entry... ");
                var insertSql = $"EXEC ba_gen_chg_log_new '', '{description}', 'TEST', '', '{cmdStr}', '', 'X'";
                var insertResult = executor.ExecuteSql(insertSql, dbpro, captureOutput: true);

                if (!insertResult.Returncode)
                {
                    PrintError("Failed to insert test changelog entry.");
                    return;
                }

                PrintSuccess("Test entry inserted.");

                // Step 4: Show last 5 changelog entries
                var chgLogTable = myOptions.ReplaceOptions("&ba_gen_chg_log&");
                if (chgLogTable != "&ba_gen_chg_log&")
                {
                    Console.WriteLine();
                    PrintDim("  Recent changelog entries:");
                    PrintDim("  " + new string('-', 66));
                    var recentQuery = $"SELECT TOP 5 dateadd(ss, tm, '800101') AS 'server time', descr FROM {chgLogTable} ORDER BY tm DESC";
                    var recentResult = executor.ExecuteSql(recentQuery, dbpro, captureOutput: true);
                    if (recentResult.Returncode && !string.IsNullOrEmpty(recentResult.Output))
                    {
                        foreach (var line in recentResult.Output.Split('\n'))
                        {
                            if (!string.IsNullOrWhiteSpace(line))
                                Console.WriteLine($"  {line}");
                        }
                    }
                    else
                    {
                        PrintDim("  Could not retrieve changelog entries.");
                    }
                }
            }
            catch (Exception ex)
            {
                PrintError($"Changelog test failed: {ex.Message}");
            }
        }

        private static void TestSymbolicLinks(ProfileData profile)
        {
            Console.WriteLine("\nTesting symbolic links...");

            if (string.IsNullOrEmpty(profile.SqlSource))
            {
                PrintError("SQL Source is not set.");
                return;
            }

            if (!Directory.Exists(profile.SqlSource))
            {
                PrintError($"SQL Source directory does not exist: {profile.SqlSource}");
                return;
            }

            var cssDir = Path.Combine(profile.SqlSource, "css");
            if (!Directory.Exists(cssDir))
            {
                PrintError($"css directory not found: {cssDir}");
                return;
            }

            // Check css/setup directory
            var setupDir = Path.Combine(profile.SqlSource, "css", "setup");
            if (Directory.Exists(setupDir))
            {
                PrintSuccess($"css/setup directory exists: {setupDir}");
                var entries = Directory.GetFileSystemEntries(setupDir);
                Console.WriteLine($"  Contains {entries.Length} entries");
            }
            else
            {
                PrintWarning($"css/setup directory not found: {setupDir}");
            }

            // Check for common CSS directory links
            var linkPaths = new[]
            {
                "css/ibs",
                "css/css",
            };

            int existing = 0, missing = 0;
            Console.WriteLine();
            foreach (var linkRel in linkPaths)
            {
                var linkPath = Path.Combine(profile.SqlSource, linkRel);
                if (Directory.Exists(linkPath) || File.Exists(linkPath))
                {
                    Console.Write("  ");
                    PrintSuccess($"{linkRel} exists");
                    existing++;
                }
                else
                {
                    Console.Write("  ");
                    PrintWarning($"{linkRel} missing");
                    missing++;
                }
            }

            Console.WriteLine();
            if (missing == 0)
                PrintSuccess("All expected CSS link paths exist!");
            else
                PrintWarning($"{missing} CSS link paths are missing. Run set_profile in Python to create symbolic links.");
        }
        #endregion

        #region Add to IDE
        private static void AddToIdeMenu()
        {
            Console.WriteLine();
            PrintSubheader("Add to IDE");
            Console.WriteLine();
            PrintMenu(1, "VSCode");
            PrintMenu(98, "Back");
            PrintMenu(99, "Exit");

            Console.Write("\nChoose [1]: ");
            var choice = Console.ReadLine()?.Trim();

            switch (choice)
            {
                case "1": AddToVscode(); break;
                case "98": return;
                case "99": Environment.Exit(0); break;
                default: Console.WriteLine("Invalid choice."); break;
            }
        }

        private static void AddToVscode()
        {
            Console.WriteLine();
            PrintSubheader("Add to VSCode");
            Console.WriteLine();
            PrintDim("  This adds runsql build tasks to your global VSCode settings.");
            PrintDim("  Press Ctrl+Shift+B in VSCode to compile the current SQL file.");
            Console.WriteLine();

            var profileNames = _settings.Profiles.Keys.ToList();
            if (profileNames.Count == 0)
            {
                PrintWarning("No profiles configured. Create a profile first.");
                return;
            }

            WriteBright("  Profiles: ");
            Console.WriteLine(string.Join(", ", profileNames));
            Console.WriteLine();

            // Ask for databases
            WriteBright("  Enter databases (comma-separated), or press Enter to prompt each time:");
            Console.WriteLine();
            Console.Write("  > ");
            var dbInput = Console.ReadLine()?.Trim() ?? "";

            var usePromptMode = string.IsNullOrEmpty(dbInput);
            var databases = new List<string>();
            if (!usePromptMode)
            {
                databases = dbInput.Split(',').Select(d => d.Trim()).Where(d => !string.IsNullOrEmpty(d)).ToList();
                if (databases.Count == 0) usePromptMode = true;
            }

            // Ask which profile is default
            string defaultProfile;
            if (profileNames.Count > 1)
            {
                Console.WriteLine();
                WriteBright("  Which profile should be the default (Ctrl+Shift+B)?");
                Console.WriteLine();
                Console.WriteLine();
                for (int i = 0; i < profileNames.Count; i++)
                    PrintMenu(i + 1, profileNames[i]);

                Console.WriteLine();
                while (true)
                {
                    Console.Write($"  Choose [1-{profileNames.Count}]: ");
                    var choice = Console.ReadLine()?.Trim();
                    if (int.TryParse(choice, out var idx) && idx >= 1 && idx <= profileNames.Count)
                    {
                        defaultProfile = profileNames[idx - 1];
                        break;
                    }
                    Console.WriteLine($"  Please enter 1-{profileNames.Count}.");
                }
            }
            else
            {
                defaultProfile = profileNames[0];
            }

            // Default database
            string? defaultDb = null;
            if (!usePromptMode && databases.Count > 1)
            {
                Console.WriteLine();
                WriteBright($"  Which database should be the default for {defaultProfile}?");
                Console.WriteLine();
                Console.WriteLine();
                for (int i = 0; i < databases.Count; i++)
                    PrintMenu(i + 1, databases[i]);

                Console.WriteLine();
                while (true)
                {
                    Console.Write($"  Choose [1-{databases.Count}]: ");
                    var choice = Console.ReadLine()?.Trim();
                    if (int.TryParse(choice, out var idx) && idx >= 1 && idx <= databases.Count)
                    {
                        defaultDb = databases[idx - 1];
                        break;
                    }
                    Console.WriteLine($"  Please enter 1-{databases.Count}.");
                }
            }
            else if (!usePromptMode && databases.Count == 1)
            {
                defaultDb = databases[0];
            }

            // Show target path
            Console.WriteLine();
            var vscodeDir = GetVscodeUserFolder();
            if (string.IsNullOrEmpty(vscodeDir))
            {
                PrintError("Could not determine VSCode User folder.");
                return;
            }
            var tasksPath = Path.Combine(vscodeDir, "tasks.json");
            Console.Write("  Will write to: ");
            WriteColor(tasksPath, ConsoleColor.Yellow);
            Console.WriteLine();
            Console.WriteLine();

            Console.Write("  Proceed? [Y/n]: ");
            var confirm = Console.ReadLine()?.Trim().ToLower();
            if (confirm == "n") { Console.WriteLine("  Cancelled."); return; }

            // Generate tasks
            Dictionary<string, object> tasksContent;
            if (usePromptMode)
                tasksContent = GenerateVscodeTasksWithPrompt(profileNames, defaultProfile);
            else
                tasksContent = GenerateVscodeTasksAllProfiles(profileNames, databases, defaultProfile, defaultDb);

            // Handle existing file
            if (File.Exists(tasksPath))
            {
                Console.WriteLine();
                PrintWarning("tasks.json already exists.");
                Console.WriteLine();
                WriteColor("  [O]", ConsoleColor.Cyan); Console.WriteLine("verwrite - Replace with new runsql tasks");
                WriteColor("  [M]", ConsoleColor.Cyan); Console.WriteLine("erge     - Keep existing tasks, add/replace runsql tasks");
                WriteColor("  [C]", ConsoleColor.Cyan); Console.WriteLine("ancel    - Abort");
                Console.WriteLine();
                while (true)
                {
                    Console.Write("  Choose [O/M/C]: ");
                    var fc = Console.ReadLine()?.Trim().ToUpper();
                    if (fc == "C") return;
                    if (fc == "O") break;
                    if (fc == "M")
                    {
                        try
                        {
                            var existingJson = File.ReadAllText(tasksPath);
                            using var doc = JsonDocument.Parse(existingJson);
                            if (doc.RootElement.TryGetProperty("tasks", out var existingTasks))
                            {
                                var nonRunsql = new List<JsonElement>();
                                foreach (var task in existingTasks.EnumerateArray())
                                {
                                    var label = task.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                                    if (!label.StartsWith("runsql"))
                                        nonRunsql.Add(task);
                                }
                                Console.WriteLine($"  Keeping {nonRunsql.Count} existing non-runsql tasks.");
                            }
                        }
                        catch
                        {
                            PrintWarning("Could not read existing file, will overwrite.");
                        }
                        break;
                    }
                    Console.WriteLine("  Please enter O, M, or C.");
                }
            }

            // Write
            try
            {
                Directory.CreateDirectory(vscodeDir);
                var json = JsonSerializer.Serialize(tasksContent, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tasksPath, json);

                Console.WriteLine();
                if (usePromptMode)
                {
                    PrintSuccess($"Added {profileNames.Count} tasks to VSCode (will prompt for database):");
                    foreach (var pn in profileNames)
                    {
                        Console.Write($"  {Icons.ARROW} runsql {pn}");
                        if (pn == defaultProfile)
                            WriteColor(" [default - Ctrl+Shift+B]", ConsoleColor.Green);
                        Console.WriteLine();
                    }
                }
                else
                {
                    var taskCount = profileNames.Count * databases.Count;
                    PrintSuccess($"Added {taskCount} tasks to VSCode:");
                    foreach (var pn in profileNames)
                    {
                        foreach (var db in databases)
                        {
                            Console.Write($"  {Icons.ARROW} runsql {pn} (");
                            WriteColor(db, ConsoleColor.Magenta);
                            Console.Write(")");
                            if (pn == defaultProfile && db == defaultDb)
                                WriteColor(" [default - Ctrl+Shift+B]", ConsoleColor.Green);
                            Console.WriteLine();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PrintError($"Failed to write tasks.json: {ex.Message}");
            }
        }

        private static string? GetVscodeUserFolder()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrEmpty(appdata))
                    return Path.Combine(appdata, "Code", "User");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "Code", "User");
            }
            else
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config", "Code", "User");
            }
            return null;
        }

        private static readonly Dictionary<string, object> VscodeProblemMatcher = new()
        {
            ["owner"] = "runsql",
            ["fileLocation"] = new[] { "absolute" },
            ["pattern"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["regexp"] = @"^Msg\s+(\d+)\s+\(severity\s+(\d+),\s+state\s+\d+\).*Line\s+(\d+):",
                    ["line"] = 3,
                    ["code"] = 1
                },
                new Dictionary<string, object>
                {
                    ["regexp"] = @"^\s*""?(.+?)""?\s*$",
                    ["message"] = 1,
                    ["loop"] = true
                }
            }
        };

        private static Dictionary<string, object> GenerateVscodeTasksAllProfiles(
            List<string> profileNames, List<string> databases, string defaultProfile, string? defaultDb)
        {
            var tasks = new List<Dictionary<string, object>>();
            foreach (var pn in profileNames)
            {
                foreach (var db in databases)
                {
                    var task = new Dictionary<string, object>
                    {
                        ["label"] = $"runsql {pn} ({db})",
                        ["type"] = "shell",
                        ["command"] = "runsql",
                        ["args"] = new[] { "${file}", db, pn },
                        ["presentation"] = new Dictionary<string, object>
                        {
                            ["reveal"] = "always",
                            ["panel"] = "shared",
                            ["clear"] = true
                        },
                        ["problemMatcher"] = VscodeProblemMatcher
                    };
                    if (pn == defaultProfile && db == defaultDb)
                        task["group"] = new Dictionary<string, object> { ["kind"] = "build", ["isDefault"] = true };
                    else
                        task["group"] = "build";
                    tasks.Add(task);
                }
            }
            return new Dictionary<string, object>
            {
                ["version"] = "2.0.0",
                ["tasks"] = tasks
            };
        }

        private static Dictionary<string, object> GenerateVscodeTasksWithPrompt(
            List<string> profileNames, string defaultProfile)
        {
            var tasks = new List<Dictionary<string, object>>();
            foreach (var pn in profileNames)
            {
                var task = new Dictionary<string, object>
                {
                    ["label"] = $"runsql {pn}",
                    ["type"] = "shell",
                    ["command"] = "runsql",
                    ["args"] = new[] { "${file}", "${input:database}", pn },
                    ["presentation"] = new Dictionary<string, object>
                    {
                        ["reveal"] = "always",
                        ["panel"] = "shared",
                        ["clear"] = true
                    },
                    ["problemMatcher"] = VscodeProblemMatcher
                };
                if (pn == defaultProfile)
                    task["group"] = new Dictionary<string, object> { ["kind"] = "build", ["isDefault"] = true };
                else
                    task["group"] = "build";
                tasks.Add(task);
            }
            return new Dictionary<string, object>
            {
                ["version"] = "2.0.0",
                ["tasks"] = tasks,
                ["inputs"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["id"] = "database",
                        ["type"] = "promptString",
                        ["description"] = "Enter database name"
                    }
                }
            };
        }
        #endregion

        #region Open Settings
        private static void OpenSettingsJson()
        {
            if (string.IsNullOrEmpty(_settingsPath) || !File.Exists(_settingsPath))
            {
                PrintError($"Settings file not found: {_settingsPath}");
                return;
            }

            InteractiveMenus.LaunchEditor(_settingsPath);
        }
        #endregion

        #region Helpers
        private static (string Name, ProfileData Profile)? FindProfile(string nameOrAlias)
        {
            var upper = nameOrAlias.ToUpperInvariant();
            foreach (var kvp in _settings.Profiles)
            {
                if (kvp.Key.ToUpperInvariant() == upper)
                    return (kvp.Key, kvp.Value);
            }
            foreach (var kvp in _settings.Profiles)
            {
                if (kvp.Value.Aliases?.Any(a => a.ToUpperInvariant() == upper) == true)
                    return (kvp.Key, kvp.Value);
            }
            return null;
        }

        private static List<string> PromptAliases(string profileName, List<string> current, string? input = null)
        {
            if (input == null)
            {
                Console.Write("  Aliases (comma-separated, optional): ");
                input = Console.ReadLine()?.Trim() ?? "";
            }
            if (string.IsNullOrEmpty(input)) return current;

            var aliases = input.Split(',').Select(a => a.Trim().ToUpper()).Where(a => !string.IsNullOrEmpty(a)).ToList();

            // Validate no conflicts
            foreach (var alias in aliases)
            {
                foreach (var kvp in _settings.Profiles)
                {
                    if (kvp.Key == profileName) continue;
                    if (kvp.Key.ToUpperInvariant() == alias)
                    {
                        PrintError($"Alias '{alias}' conflicts with profile name '{kvp.Key}'.");
                        return current;
                    }
                    if (kvp.Value.Aliases?.Any(a => a.ToUpperInvariant() == alias) == true)
                    {
                        PrintError($"Alias '{alias}' is already used by profile '{kvp.Key}'.");
                        return current;
                    }
                }
            }
            return aliases;
        }

        private static string ReadPassword()
        {
            var password = new System.Text.StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (password.Length > 0)
                    {
                        password.Remove(password.Length - 1, 1);
                        Console.Write("\b \b");
                    }
                }
                else
                {
                    password.Append(key.KeyChar);
                    Console.Write('*');
                }
            }
            return password.ToString();
        }
        #endregion
    }
}
