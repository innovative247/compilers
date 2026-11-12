using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace ibsCompiler
{
    /// <summary>
    /// set_profile main-menu item 6 and its headless flags: what MSSQL session settings
    /// (ARITHABORT, CONCAT_NULL_YIELDS_NULL, ...) every MSSQL connection applies. The decision
    /// itself lives in <see cref="MssqlSessionSettings"/>; the script is chosen by pointing the
    /// SQLCMDINI variable (<see cref="SqlCmdIniVariable"/>) at it. settings.json keeps only the
    /// off switch and, where the variable cannot be set, a file to use instead.
    /// </summary>
    public static partial class set_profile_main
    {
        /// <summary>What to do when SQLCMDINI cannot be set: ask (menu), fail (headless), fail and hint at --use-settings-file, or skip the variable.</summary>
        private enum VariableFallback { Ask, Fail, FailWithHint, UseSettingsFile }

        private static MssqlSessionResolution ResolveMssqlSession() =>
            MssqlSessionSettings.Resolve(_settings.MssqlSessionSettings, _settingsPath);

        private static string ConfiguredSessionFile => _settings.MssqlSessionSettings?.FilePath ?? "";

        /// <summary>Plain-words line for the active source.</summary>
        private static string DescribeMssqlSession(MssqlSessionResolution r) => r.Source switch
        {
            MssqlSessionSource.Off => "Off: no session settings are applied",
            MssqlSessionSource.SettingsFile => $"Using: {r.Path} (set in settings.json because SQLCMDINI could not be set)",
            MssqlSessionSource.EnvironmentVariable => $"Using: {r.Path}",
            MssqlSessionSource.BesideSettings => $"Using: {r.Path} (found beside settings.json)",
            _ => r.Path != null ? $"Using: shipped defaults {r.Path}" : "Using: shipped defaults (built in)",
        };

        private static string WithNotFound(string value) => File.Exists(value) ? value : $"{value} (not found)";

        /// <summary>Where the path lives, the variable, any settings.json file, the active source, then the script. Shared by the menu and <c>--mssql-settings show</c>.</summary>
        private static MssqlSessionResolution PrintMssqlSession()
        {
            var r = ResolveMssqlSession();
            Console.WriteLine("The script path is set in the environment variable SQLCMDINI.");
            var named = SqlCmdIniVariable.Get();
            Console.WriteLine(string.IsNullOrEmpty(named) ? "SQLCMDINI is not set" : $"SQLCMDINI = {WithNotFound(named)}");
            if (ConfiguredSessionFile.Length > 0)
                Console.WriteLine($"settings.json file = {WithNotFound(ConfiguredSessionFile)}");
            Console.WriteLine(DescribeMssqlSession(r));
            if (r.Source != MssqlSessionSource.Off)
            {
                Console.WriteLine();
                Console.WriteLine(r.Script.TrimEnd());
            }
            return r;
        }

        /// <summary>Replace (or, with null, remove) only the "MssqlSessionSettings" node of settings.json.</summary>
        private static bool WriteMssqlSection(MssqlSessionConfig? section)
        {
            if (section == null && _settings.MssqlSessionSettings == null) return true;
            if (!WriteTopLevelSection("MssqlSessionSettings", section)) return false;
            _settings.MssqlSessionSettings = section;
            return true;
        }

        /// <summary>Turn off / on only moves MODE; a file kept in settings.json stays.</summary>
        private static bool SetMssqlSessionOff(bool off)
        {
            var file = _settings.MssqlSessionSettings?.FilePath;
            if (off) return WriteMssqlSection(new MssqlSessionConfig { Mode = "off", FilePath = file });
            return WriteMssqlSection(string.IsNullOrEmpty(file) ? null : new MssqlSessionConfig { FilePath = file });
        }

        /// <summary>Use this file from settings.json. Only FILE is written, so an earlier off is cleared too.</summary>
        private static bool StoreSettingsFile(string path)
        {
            if (!WriteMssqlSection(new MssqlSessionConfig { FilePath = path })) return false;
            Console.WriteLine($"Using {path} from settings.json.");
            return true;
        }

        /// <summary>End of input means No: a run with nobody left to answer must not take the default action.</summary>
        private static bool ConfirmDefaultYes(string question)
        {
            var answer = ReadLinePrompt($"{question} [Y/n]: ");
            if (answer == null) return false;
            return answer.Length == 0 || answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Set SQLCMDINI and say so; off and the settings.json file are not touched.</summary>
        private static bool TrySetVariable(string path, out string error)
        {
            if (!SqlCmdIniVariable.Set(path, out error)) return false;
            Console.WriteLine($"SQLCMDINI = {path}");
            Console.WriteLine("Open a new terminal for other programs to pick up the change.");
            return true;
        }

        /// <summary>
        /// Point SQLCMDINI at a script. On success the last choice wins: the settings.json file and
        /// off are removed. When it cannot be set, <paramref name="how"/> decides: offer the path in
        /// settings.json instead, or print the step to do by hand (stderr, so a headless run fails with it).
        /// </summary>
        private static bool PointSessionAt(string path, VariableFallback how)
        {
            if (how == VariableFallback.UseSettingsFile) return StoreSettingsFile(path);
            if (TrySetVariable(path, out var error)) return WriteMssqlSection(null);
            Console.Error.WriteLine($"ERROR: could not set SQLCMDINI: {error}");
            if (how == VariableFallback.Ask && ConfirmDefaultYes("SQLCMDINI could not be set. Use this file from set_profile instead?"))
                return StoreSettingsFile(path);
            Console.Error.WriteLine(OperatingSystem.IsWindows()
                ? $"Set it yourself in PowerShell: [Environment]::SetEnvironmentVariable(\"SQLCMDINI\", \"{path}\", \"User\")"
                : $"Add this line to your shell profile: export SQLCMDINI=\"{path}\"");
            if (how == VariableFallback.FailWithHint)
                Console.Error.WriteLine("Or rerun with --use-settings-file to use the path from settings.json instead.");
            return false;
        }

        /// <summary>Point the variable back at the shipped file and clear off.</summary>
        private static bool RevertMssqlSession(VariableFallback how)
        {
            if (!PointSessionAt(MssqlSessionSettings.ShippedFilePath, how)) return false;
            PrintSuccess("Reverted to the shipped MSSQL session settings.");
            return true;
        }

        private static void MssqlSessionMenu()
        {
            while (true)
            {
                Console.WriteLine();
                PrintSubheader("MSSQL session settings");
                Console.WriteLine();
                var current = PrintMssqlSession();
                Console.WriteLine();

                PrintMenu(1, "Modify");
                PrintMenu(2, "Set the path");
                PrintMenu(3, current.Source == MssqlSessionSource.Off ? "Turn on" : "Turn off");
                PrintMenu(4, "Revert to shipped defaults");
                PrintMenu(98, "Back");
                PrintMenu(99, "Exit");

                // Interactive TTY -> deferred 'Choice:' entry; redirected -> plain ReadLine.
                string? choice;
                if (Console.IsInputRedirected || Console.IsOutputRedirected)
                {
                    ConsoleMenu.WritePrompt("\nChoose [1-4]: ");
                    choice = Console.ReadLine()?.Trim();
                    if (choice == null) return; // end of piped input
                }
                else
                {
                    Console.WriteLine();
                    choice = ConsoleMenu.ReadDeferredChoice();
                    if (string.IsNullOrEmpty(choice)) return; // Esc -> Back
                }

                switch (choice)
                {
                    case "1": ModifyMssqlSession(); break;
                    case "2": SetMssqlSessionPath(); break;
                    case "3":
                        if (SetMssqlSessionOff(current.Source != MssqlSessionSource.Off))
                            PrintSuccess(current.Source == MssqlSessionSource.Off ? "MSSQL session settings turned on." : "MSSQL session settings turned off.");
                        break;
                    case "4":
                        if (ConfirmYesNo($"Revert to the shipped defaults?\nThis points SQLCMDINI at {MssqlSessionSettings.ShippedFilePath}.", defaultYes: false))
                            RevertMssqlSession(VariableFallback.Ask);
                        break;
                    case "98": return;
                    case "99": Environment.Exit(0); break;
                    default: Console.WriteLine("Invalid choice."); break;
                }
            }
        }

        /// <summary>
        /// Open the file in use in the editor: the file in settings.json, else the one SQLCMDINI names
        /// (set to the shipped file first when it is not set), else one found beside settings.json.
        /// Nothing is copied. A file that does not exist is offered for creation from the shipped settings.
        /// </summary>
        private static void ModifyMssqlSession()
        {
            var file = ConfiguredSessionFile.Length > 0 ? ConfiguredSessionFile : SqlCmdIniVariable.Get();
            if (string.IsNullOrEmpty(file))
            {
                var beside = MssqlSessionSettings.BesideSettingsPath(_settingsPath);
                if (File.Exists(beside)) file = beside;
                else
                {
                    // Not set yet: the shipped file is what runs, so that is what gets opened. Off stays as it is.
                    file = MssqlSessionSettings.ShippedFilePath;
                    if (!TrySetVariable(file, out var setError)) Console.Error.WriteLine($"ERROR: could not set SQLCMDINI: {setError}");
                }
            }
            if (!File.Exists(file))
            {
                if (!ConfirmDefaultYes($"{file} does not exist. Create it from the shipped settings?")) return;
                try
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(file));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    if (!ibs_compiler_common.WriteAllTextAtomic(file, MssqlSessionSettings.ShippedScript()))
                        throw new IOException("the file could not be written");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    PrintError($"Could not create {file}: {ex.Message}");
                    return;
                }
            }
            if (MssqlSessionSettings.IsShippedFile(file))
                Console.WriteLine("An update replaces this file. To keep your changes, save them to your own file and choose \"Set the path\".");
            InteractiveMenus.LaunchEditor(file);
        }

        /// <summary>
        /// A typed line where Esc goes back: Esc on an empty line returns null, Esc on a
        /// part-typed line clears it. Console.ReadLine cannot see Esc, so the keys are read here.
        /// </summary>
        private static string? ReadPathOrBack(string prompt)
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected) return ReadLinePrompt(prompt);
            try { while (Console.KeyAvailable) Console.ReadKey(intercept: true); } catch { }
            ConsoleMenu.WritePrompt(prompt);
            var buf = new System.Text.StringBuilder();
            void Erase(int n) { for (var i = 0; i < n; i++) Console.Write("\b \b"); }
            var caret = OperatingSystem.IsWindows() && Console.CursorVisible;
            try
            {
                Console.CursorVisible = true;
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    switch (key.Key)
                    {
                        case ConsoleKey.Enter:
                            Console.WriteLine();
                            return buf.ToString();
                        case ConsoleKey.Escape:
                            if (buf.Length == 0) { Console.WriteLine(); return null; }
                            Erase(buf.Length);
                            buf.Clear();
                            break;
                        case ConsoleKey.Backspace:
                            if (buf.Length > 0) { buf.Length--; Erase(1); }
                            break;
                        default:
                            if (!char.IsControl(key.KeyChar)) { buf.Append(key.KeyChar); Console.Write(key.KeyChar); }
                            break;
                    }
                }
            }
            finally { Console.CursorVisible = caret; }
        }

        /// <summary>Set the path: the file must exist (else an error and nothing changes), then SQLCMDINI is pointed at it.</summary>
        private static bool UseMssqlSessionFile(string path, VariableFallback how)
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                Console.Error.WriteLine($"ERROR: not a valid path: {path}");
                return false;
            }
            if (!File.Exists(full))
            {
                Console.Error.WriteLine($"ERROR: file not found: {full}");
                return false;
            }
            return PointSessionAt(full, how);
        }

        private static void SetMssqlSessionPath()
        {
            var entered = ReadPathOrBack("Path to the SQL file (Esc or empty to go back): ")?.Trim().Trim('"');
            if (!string.IsNullOrEmpty(entered)) UseMssqlSessionFile(entered, VariableFallback.Ask);
        }

        /// <summary>
        /// Headless equivalents: <c>--mssql-settings show|on|off|revert</c> and
        /// <c>--mssql-settings-file PATH [--use-settings-file]</c>. A variable that cannot be set
        /// exits non-zero with the manual step on stderr.
        /// </summary>
        private static int MssqlSettingsHeadless(string? action, string? file, bool useSettingsFile)
        {
            if (file != null)
            {
                if (string.IsNullOrWhiteSpace(file))
                {
                    Console.Error.WriteLine("ERROR: --mssql-settings-file requires a path.");
                    return 1;
                }
                return UseMssqlSessionFile(file.Trim().Trim('"'),
                    useSettingsFile ? VariableFallback.UseSettingsFile : VariableFallback.FailWithHint) ? 0 : 1;
            }

            switch ((action ?? "").Trim().ToLowerInvariant())
            {
                case "show":
                    PrintMssqlSession();
                    return 0;
                case "on":
                    if (!SetMssqlSessionOff(false)) return 1;
                    PrintSuccess("MSSQL session settings turned on.");
                    Console.WriteLine(DescribeMssqlSession(ResolveMssqlSession()));
                    return 0;
                case "off":
                    if (!SetMssqlSessionOff(true)) return 1;
                    PrintSuccess("MSSQL session settings turned off.");
                    return 0;
                case "revert":
                    return RevertMssqlSession(VariableFallback.Fail) ? 0 : 1;
                default:
                    Console.Error.WriteLine("ERROR: --mssql-settings takes show, on, off or revert.");
                    return 1;
            }
        }
    }
}
