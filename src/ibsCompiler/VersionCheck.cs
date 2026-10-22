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
        private const string DocsUrl = "https://support.innovative247.com/help/Developer-Tools/Compilers";

        /// <summary>
        /// Check for subcommands and daily version updates.
        /// Returns true if the caller should continue running its normal logic.
        /// Returns false if a subcommand was handled (caller should exit with 0).
        /// </summary>
        public static bool CheckForUpdates(string commandName, string[] args, string? usage = null)
        {
            // Clean up .old files from previous self-update (Windows)
            try { CleanupOldFiles(); } catch { }

            if (args.Length > 0)
            {
                var sub = args[0].ToLowerInvariant().TrimStart('-', '/');
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

                    case "help":
                    case "h":
                    case "?":
                        ShowHelp(commandName, usage);
                        return false;
                }
            }

            // Daily background check (non-blocking)
            try { DailyCheck(); } catch { /* silently ignore */ }

            return true;
        }

        private static void ShowHelp(string commandName, string? usage)
        {
            Console.WriteLine($"{commandName} {VersionInfo.Version}");
            if (usage != null)
            {
                Console.WriteLine();
                Console.WriteLine(usage);
            }
            Console.WriteLine();
            Console.WriteLine("Subcommands:");
            Console.WriteLine("  help        Show this help");
            Console.WriteLine("  version     Show version");
            Console.WriteLine("  update      Download and install latest version");
            Console.WriteLine("  configure   Show configuration status, add to PATH");
            Console.WriteLine();
            Console.WriteLine($"Documentation: {DocsUrl}");
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

            // Compare versions — prompt to update if newer
            if (IsNewer(latestVersion, currentVersion))
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"  A new version is available: v{latestVersion} (current: v{currentVersion})");
                // Only prompt on a real TTY. Bare Enter defaults to YES, so a
                // redirected stdin (suite runs, piped scripts, !! escapes) must never
                // reach the prompt — a null/stray ReadLine would self-update binaries
                // out from under whatever is driving the process.
                if (Console.IsInputRedirected)
                {
                    Console.Error.WriteLine("  Run any command with 'update' to install.");
                }
                else
                {
                    Console.Error.Write("  Update now? [Y/n]: ");
                    var answer = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";
                    if (answer == "" || answer.StartsWith("y"))
                    {
                        RunSelfUpdate().GetAwaiter().GetResult();
                    }
                    else
                    {
                        Console.Error.WriteLine("  Run any command with 'update' to install later.");
                    }
                }
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

            using var http = CreateHttpClient(timeout: 5);

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

            // Download (own client: the API client's 5 s timeout would kill a 37 MB body)
            Console.WriteLine($"Downloading {assetName}...");
            var tempFile = Path.GetTempFileName();
            try
            {
                using var dlHttp = CreateHttpClient(timeout: 1);
                // Finite wait for the response headers; the body gets a per-read stall timeout below.
                dlHttp.Timeout = TimeSpan.FromSeconds(100);
                using var dlResponse = await dlHttp.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                dlResponse.EnsureSuccessStatusCode();
                var total = dlResponse.Content.Headers.ContentLength;
                using (var downloadStream = await dlResponse.Content.ReadAsStreamAsync())
                using (var fileStream = File.Create(tempFile))
                {
                    var buffer = new byte[81920];
                    long done = 0, lastReported = 0;
                    var redirected = Console.IsOutputRedirected;
                    int read;
                    while (true)
                    {
                        // A fresh 60 s allowance before every read: a stalled connection
                        // fails clearly instead of hanging the update forever.
                        using (var stall = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                            read = await downloadStream.ReadAsync(buffer.AsMemory(), stall.Token);
                        if (read <= 0) break;
                        await fileStream.WriteAsync(buffer, 0, read);
                        done += read;
                        // Redirected: a plain line every 10 MB; console: redraw in place every 256 KB
                        var step = redirected ? 10L * 1024 * 1024 : 256L * 1024;
                        if (done - lastReported < step) continue;
                        lastReported = done;
                        var msg = total.HasValue
                            ? $"  {done / 1048576.0:F1} / {total.Value / 1048576.0:F1} MB ({done * 100 / total.Value}%)"
                            : $"  {done / 1048576.0:F1} MB";
                        if (redirected) Console.WriteLine(msg);
                        else Console.Write("\r" + msg + "   ");
                    }
                    if (!redirected) Console.Write("\r" + new string(' ', 60) + "\r");
                    Console.WriteLine($"  Downloaded {done / 1048576.0:F1} MB.");
                }

                // Install to the directory containing the actual exe
                var installDir = GetExeDir();

                Console.WriteLine($"Extracting to {installDir}...");

                if (assetName.EndsWith(".zip"))
                    ExtractZip(tempFile, installDir);
                else
                {
                    if (!ExtractTarGz(tempFile, installDir)) return;
                    SetExecutable(installDir);
                }

                var prev = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✓ Updated to v{latestVersion}.");
                Console.ForegroundColor = prev;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("Download failed: no response within 100 s, or no data received for 60 s. " +
                                        "Check your connection and run the update again.");
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
            var count = archive.Entries.Count;
            var index = 0;
            var redirected = Console.IsOutputRedirected;
            foreach (var entry in archive.Entries)
            {
                index++;
                if (redirected) { if (index % 25 == 0) Console.WriteLine($"  {index} / {count} entries"); }
                else Console.Write($"\r  {index} / {count} entries ({index * 100 / count}%)   ");
                if (string.IsNullOrEmpty(entry.Name)) continue;
                if (entry.Name == "settings.json") continue; // Never overwrite user credentials

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
            if (!redirected) Console.Write("\r" + new string(' ', 60) + "\r");
            Console.WriteLine($"  Extracted {count} entries.");
        }

        private static bool ExtractTarGz(string tarGzPath, string destDir)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "tar",
                Arguments = $"-xzf \"{tarGzPath}\" --exclude=./settings.json -C \"{destDir}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
            {
                Console.Error.WriteLine("Extract failed: could not start tar.");
                return false;
            }
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                Console.Error.WriteLine($"Extract failed (tar exit {proc.ExitCode}): {stderr.Result.Trim()}");
                return false;
            }
            Console.WriteLine("  Extracted.");
            return true;
        }

        private static void SetExecutable(string dir)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "find",
                Arguments = $"\"{dir}\" -maxdepth 1 -type f ! -name \"*.json\" ! -name \"*.example\" -exec chmod +x {{}} +",
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
