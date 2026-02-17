using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;

namespace ibsCompiler
{
    /// <summary>
    /// Daily version check against GitHub Releases + install/update/configure subcommands.
    /// Called at the top of every Program.cs before the real command runs.
    /// </summary>
    public static class VersionCheck
    {
        private const string GitHubOwner = "innovative247";
        private const string GitHubRepo = "compilers";
        private const string ReleasesApiUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";

        /// <summary>
        /// Check for subcommands and daily version updates.
        /// Returns true if the caller should continue running its normal logic.
        /// Returns false if a subcommand was handled (caller should exit with 0).
        /// </summary>
        public static bool CheckForUpdates(string commandName, string[] args)
        {
            // Clean up .old files from previous self-update (Windows)
            try { CleanupOldFiles(); } catch { }

            if (args.Length > 0)
            {
                var sub = args[0].ToLowerInvariant().TrimStart('-');
                switch (sub)
                {
                    case "version":
                    case "v":
                        Console.WriteLine($"{commandName} {VersionInfo.Version}");
                        return false;

                    case "install":
                    case "update":
                        RunSelfUpdate().GetAwaiter().GetResult();
                        return false;

                    case "configure":
                        ConfigureCommand.Run();
                        return false;
                }
            }

            // Daily background check (non-blocking)
            try { DailyCheck(); } catch { /* silently ignore */ }

            return true;
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

        private static string GetStateDir()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, "ibs-compilers");
            }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "ibs-compilers");
        }

        private static string GetStateFile()
        {
            var dir = GetStateDir();
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "version_state.json");
        }

        /// <summary>
        /// Remove .old files left over from a previous Windows self-update.
        /// </summary>
        private static void CleanupOldFiles()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            var exeDir = GetExeDir();
            foreach (var oldFile in Directory.GetFiles(exeDir, "*.old"))
            {
                try { File.Delete(oldFile); } catch { }
            }
        }

        private static void DailyCheck()
        {
            var stateFile = GetStateFile();
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

            // Read existing state
            if (File.Exists(stateFile))
            {
                try
                {
                    var json = JsonNode.Parse(File.ReadAllText(stateFile));
                    var lastCheck = json?["last_check_date"]?.GetValue<string>();
                    if (lastCheck == today) return; // Already checked today
                }
                catch { /* corrupt file, re-check */ }
            }

            // Query GitHub (5s timeout)
            string? latestTag = null;
            try
            {
                using var http = CreateHttpClient(timeout: 5);
                var response = http.GetAsync(ReleasesApiUrl).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode) return;

                var release = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                latestTag = release?["tag_name"]?.GetValue<string>();
            }
            catch { return; } // Network error — silently continue

            if (string.IsNullOrEmpty(latestTag)) return;

            var latestVersion = latestTag.TrimStart('v');
            var currentVersion = VersionInfo.Version;

            // Save state
            var state = new JsonObject
            {
                ["last_check_date"] = today,
                ["latest_version"] = latestVersion,
                ["current_version"] = currentVersion
            };
            try { File.WriteAllText(stateFile, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
            catch { /* ignore write errors */ }

            // Compare versions
            if (IsNewer(latestVersion, currentVersion))
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"  A new version is available: v{latestVersion} (current: v{currentVersion})");
                Console.Error.WriteLine($"  Run any command with 'update' to install: set_profile update");
                Console.Error.WriteLine();
            }
        }

        private static bool IsNewer(string latest, string current)
        {
            if (System.Version.TryParse(latest, out var vLatest) &&
                System.Version.TryParse(current, out var vCurrent))
            {
                return vLatest > vCurrent;
            }
            return false;
        }

        private static async Task RunSelfUpdate()
        {
            Console.WriteLine($"Current version: v{VersionInfo.Version}");
            Console.WriteLine("Checking for updates...");

            using var http = CreateHttpClient(timeout: 30);

            // Get latest release info
            var response = await http.GetAsync(ReleasesApiUrl);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Failed to check for updates (HTTP {(int)response.StatusCode})");
                return;
            }

            var release = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            var tagName = release?["tag_name"]?.GetValue<string>() ?? "";
            var latestVersion = tagName.TrimStart('v');

            if (!IsNewer(latestVersion, VersionInfo.Version))
            {
                Console.WriteLine($"Already up to date (v{VersionInfo.Version}).");
                return;
            }

            Console.WriteLine($"New version available: v{latestVersion}");

            // Determine platform asset name
            var assetName = GetPlatformAssetName();
            if (assetName == null)
            {
                Console.Error.WriteLine("Unsupported platform for auto-update.");
                return;
            }

            // Find asset URL
            string? downloadUrl = null;
            var assets = release?["assets"]?.AsArray();
            if (assets != null)
            {
                foreach (var asset in assets)
                {
                    var name = asset?["name"]?.GetValue<string>();
                    if (name == assetName)
                    {
                        downloadUrl = asset?["browser_download_url"]?.GetValue<string>();
                        break;
                    }
                }
            }

            if (downloadUrl == null)
            {
                Console.Error.WriteLine($"Could not find release asset: {assetName}");
                return;
            }

            // Download
            Console.WriteLine($"Downloading {assetName}...");
            var tempFile = Path.GetTempFileName();
            try
            {
                using var downloadStream = await http.GetStreamAsync(downloadUrl);
                using var fileStream = File.Create(tempFile);
                await downloadStream.CopyToAsync(fileStream);
                fileStream.Close();

                // Install to the directory containing the actual exe
                var installDir = GetExeDir();

                Console.WriteLine($"Installing to {installDir}...");

                if (assetName.EndsWith(".zip"))
                    ExtractZip(tempFile, installDir);
                else
                    ExtractTarGz(tempFile, installDir);

                Console.WriteLine($"Updated to v{latestVersion}. Please re-run your command.");
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        }

        private static string? GetPlatformAssetName()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return "compilers-net8-win-x64.zip";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return "compilers-net8-linux-x64.tar.gz";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return "compilers-net8-osx-x64.tar.gz";
            return null;
        }

        private static void ExtractZip(string zipPath, string destDir)
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var destPath = Path.Combine(destDir, entry.Name);

                // On Windows, rename running exe to .old before overwriting
                if (File.Exists(destPath))
                {
                    var oldPath = destPath + ".old";
                    try { File.Delete(oldPath); } catch { }
                    try { File.Move(destPath, oldPath); } catch { /* in use — skip */ }
                }

                entry.ExtractToFile(destPath, overwrite: true);
            }
        }

        private static void ExtractTarGz(string tarGzPath, string destDir)
        {
            // Use system tar for .tar.gz extraction
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "tar",
                Arguments = $"-xzf \"{tarGzPath}\" -C \"{destDir}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit();
        }

        private static HttpClient CreateHttpClient(int timeout)
        {
            var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(timeout);
            http.DefaultRequestHeaders.Add("User-Agent", $"IBS-Compilers/{VersionInfo.Version}");
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            return http;
        }
    }
}
