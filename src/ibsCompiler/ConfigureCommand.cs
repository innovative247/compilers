using System.Runtime.InteropServices;
using ibsCompiler.Configuration;

namespace ibsCompiler
{
    /// <summary>
    /// Shows compiler configuration status and offers PATH setup.
    /// Invoked via: any-command configure
    /// </summary>
    public static class ConfigureCommand
    {
        public static void Run()
        {
            var binDir = GetExeDir().TrimEnd(Path.DirectorySeparatorChar);
            var rid = GetRuntimeId();
            var settingsPath = ProfileManager.FindSettingsFile();

            // Fix PATH before displaying status
            var wasInPath = IsInPath(binDir);
            var inPath = wasInPath;
            if (!inPath)
                inPath = AddToPath(binDir);

            // Fix settings.json before displaying status
            if (settingsPath == null)
            {
                var examplePath = Path.Combine(binDir, "settings.json.example");
                var targetPath = Path.Combine(binDir, "settings.json");
                if (File.Exists(examplePath))
                {
                    File.Copy(examplePath, targetPath);
                    settingsPath = targetPath;
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== Compilers Configuration ===");
            Console.WriteLine();
            Console.WriteLine($"  Version:     {VersionInfo.Version}");
            Console.WriteLine($"  Bin dir:     {binDir}");
            Console.WriteLine($"  Platform:    {rid}");
            Console.WriteLine($"  Settings:    {(settingsPath != null ? settingsPath : "NOT FOUND")}");
            Console.WriteLine($"  PATH:        {(inPath ? "OK" : "FAILED — add manually")}");
            Console.WriteLine();
        }

        /// <summary>
        /// Get the directory containing the actual executable (not the temp extraction dir).
        /// </summary>
        private static string GetExeDir()
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                var dir = Path.GetDirectoryName(exePath);
                if (!string.IsNullOrEmpty(dir))
                    return dir;
            }
            return AppContext.BaseDirectory;
        }

        private static string GetRuntimeId()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "win-x64";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux-x64";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "osx-x64";
            return "unknown";
        }

        private static bool IsInPath(string binDir)
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            var sep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
            return pathVar.Split(sep).Any(p =>
                string.Equals(p.TrimEnd(Path.DirectorySeparatorChar), binDir, StringComparison.OrdinalIgnoreCase));
        }

        private static bool AddToPath(string binDir)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return AddToPathWindows(binDir);
                else
                    return AddToPathUnix(binDir);
            }
            catch
            {
                return false;
            }
        }

        private static bool AddToPathWindows(string binDir)
        {
            var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
            if (current.Split(';').Any(p =>
                string.Equals(p.TrimEnd(Path.DirectorySeparatorChar), binDir, StringComparison.OrdinalIgnoreCase)))
                return true; // Already there

            var updated = string.IsNullOrEmpty(current) ? binDir : current + ";" + binDir;
            Environment.SetEnvironmentVariable("PATH", updated, EnvironmentVariableTarget.User);
            return true;
        }

        private static bool AddToPathUnix(string binDir)
        {
            var exportLine = $"export PATH=\"{binDir}:$PATH\"";
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // Try .bashrc first, then .zshrc
            string[] rcFiles = { ".bashrc", ".zshrc" };
            bool added = false;

            foreach (var rcFile in rcFiles)
            {
                var rcPath = Path.Combine(home, rcFile);
                if (File.Exists(rcPath))
                {
                    var content = File.ReadAllText(rcPath);
                    if (!content.Contains(binDir))
                    {
                        File.AppendAllText(rcPath, $"\n# IBS Compilers\n{exportLine}\n");
                        Console.WriteLine($"  Added to ~/{rcFile}");
                        added = true;
                    }
                    else
                    {
                        Console.WriteLine($"  Already in ~/{rcFile}");
                        added = true;
                    }
                }
            }

            // If no rc file exists, create .bashrc
            if (!added)
            {
                var bashrc = Path.Combine(home, ".bashrc");
                File.WriteAllText(bashrc, $"# IBS Compilers\n{exportLine}\n");
                Console.WriteLine("  Created ~/.bashrc with PATH entry");
                added = true;
            }

            return added;
        }
    }
}
