using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ibsCompiler
{
    /// <summary>
    /// One IDE the Compilers can integrate with. To add an IDE: write one class that
    /// implements this and add one entry to <see cref="IdeRegistry.All"/>. The set_profile
    /// "IDE Integration" menu and the <c>--install-&lt;stem&gt;</c> / <c>--remove-&lt;stem&gt;</c>
    /// headless flags are generated from the registry; nothing else needs editing.
    /// </summary>
    internal interface IIdeIntegration
    {
        /// <summary>Display name, e.g. "VS Code".</summary>
        string Name { get; }

        /// <summary>Headless flag stem, e.g. "vscode" gives --install-vscode / --remove-vscode.</summary>
        string FlagStem { get; }

        /// <summary>True when the IDE is detected (File.Exists checks only; never spawns a process).</summary>
        bool IsAvailable { get; }

        /// <summary>Installed extension version, or null when not installed (or not queryable).</summary>
        string? GetInstalledVersion();

        /// <summary>
        /// Fast installed-version probe: reads the IDE's own install state from disk and never
        /// runs the IDE's CLI. Null when not installed or unknown; never throws.
        /// </summary>
        string? ProbeInstalledVersion();

        /// <summary>Installs or updates the integration and verifies it. Returns 0 on success.</summary>
        int Install();

        /// <summary>Removes the integration and verifies it. Returns 0 on success (including "not installed").</summary>
        int Remove();
    }

    internal static class IdeRegistry
    {
        /// <summary>Every IDE integration, in menu order.</summary>
        public static readonly IReadOnlyList<IIdeIntegration> All = new IIdeIntegration[]
        {
            new VsCodeIntegration(),
        };

        public static string InstallFlag(IIdeIntegration ide) => "--install-" + ide.FlagStem;
        public static string RemoveFlag(IIdeIntegration ide) => "--remove-" + ide.FlagStem;

        /// <summary>All headless flags (install then remove, per IDE, in registry order).</summary>
        public static string[] HeadlessFlags { get; } =
            All.SelectMany(i => new[] { InstallFlag(i), RemoveFlag(i) }).ToArray();
    }

    internal sealed class VsCodeIntegration : IIdeIntegration
    {
        private const string ExtensionId = "innovative247.compilers";

        public string Name => "VS Code";
        public string FlagStem => "vscode";
        public bool IsAvailable => FindCli() != null;

        public string? GetInstalledVersion()
        {
            var code = FindCli();
            if (code == null) return null;
            var r = RunCode(code, new[] { "--list-extensions", "--show-versions" }, 60_000);
            return r.Ok ? ParseVersion(r.Output) : null;
        }

        public string? ProbeInstalledVersion()
        {
            try
            {
                string? best = null;
                foreach (var dir in ExtensionDirs())
                {
                    var v = ProbeDir(dir);
                    if (v != null && (best == null || CompareVersions(v, best) > 0)) best = v;
                }
                return best;
            }
            catch { return null; }
        }

        private static IEnumerable<string> ExtensionDirs()
        {
            var env = Environment.GetEnvironmentVariable("VSCODE_EXTENSIONS");
            if (!string.IsNullOrWhiteSpace(env)) { yield return env; yield break; }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) yield break;
            yield return Path.Combine(home, ".vscode", "extensions");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                yield return Path.Combine(home, ".vscode-server", "extensions"); // WSL remote
        }

        /// <summary>extensions.json is authoritative (old version folders linger until VS Code restarts); folder names only when it is absent.</summary>
        private static string? ProbeDir(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return null;
                var manifest = Path.Combine(dir, "extensions.json");
                if (File.Exists(manifest))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
                    if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
                    string? best = null;
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        if (!e.TryGetProperty("identifier", out var id) || !id.TryGetProperty("id", out var idv)) continue;
                        if (!string.Equals(idv.GetString(), ExtensionId, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!e.TryGetProperty("version", out var ver)) continue;
                        var v = ver.GetString();
                        if (!string.IsNullOrWhiteSpace(v) && (best == null || CompareVersions(v, best) > 0)) best = v;
                    }
                    return best;
                }
                string? folderBest = null;
                var prefix = ExtensionId + "-";
                foreach (var d in Directory.EnumerateDirectories(dir, prefix + "*"))
                {
                    var v = Path.GetFileName(d).Substring(prefix.Length);
                    if (folderBest == null || CompareVersions(v, folderBest) > 0) folderBest = v;
                }
                return folderBest;
            }
            catch { return null; }
        }

        private static int CompareVersions(string a, string b) =>
            Version.TryParse(a, out var va) && Version.TryParse(b, out var vb)
                ? va.CompareTo(vb)
                : string.CompareOrdinal(a, b);

        public int Install()
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var vsix = Path.Combine(exeDir, "compilers.vsix");
            if (!File.Exists(vsix))
            {
                set_profile_main.PrintError($"compilers.vsix not found beside the program ({exeDir}). Reinstall or update the Compilers.");
                return 1;
            }

            var code = FindCli();
            if (code == null) { PrintNotFound(); return 1; }

            var before = ListVersion(code, out _);

            var install = RunCode(code, new[] { "--install-extension", vsix, "--force" }, 120_000);
            if (install.Output.Length > 0) Console.WriteLine(install.Output);
            if (!install.Ok)
            {
                set_profile_main.PrintError(install.Failure ?? $"VS Code could not install the extension (exit {install.ExitCode}).");
                return 1;
            }

            var after = ListVersion(code, out var listError);
            var expected = VersionInfo.Version;
            if (after != expected)
            {
                var found = listError ?? (after == null ? $"{ExtensionId} is not listed" : $"found {ExtensionId}@{after}");
                set_profile_main.PrintError($"VS Code reported success but the extension is not at the expected version. Expected {ExtensionId}@{expected}; {found}.");
                return 1;
            }

            Console.WriteLine();
            if (before == null)
                set_profile_main.PrintSuccess($"VS Code extension {expected} installed.");
            else if (before == expected)
                set_profile_main.PrintSuccess($"VS Code extension {expected} reinstalled (was already {before}).");
            else
                set_profile_main.PrintSuccess($"VS Code extension {expected} updated from {before}.");
            Console.WriteLine("In VS Code: F5 runs the file or selection; pick profile/database from the status bar.");
            return 0;
        }

        public int Remove()
        {
            var code = FindCli();
            if (code == null) { PrintNotFound(); return 1; }

            var before = ListVersion(code, out var listError);
            if (listError != null) { set_profile_main.PrintError(listError); return 1; }
            if (before == null)
            {
                Console.WriteLine("VS Code extension is not installed; nothing to remove.");
                return 0;
            }

            var remove = RunCode(code, new[] { "--uninstall-extension", ExtensionId }, 120_000);
            if (remove.Output.Length > 0) Console.WriteLine(remove.Output);
            if (!remove.Ok)
            {
                set_profile_main.PrintError(remove.Failure ?? $"VS Code could not remove the extension (exit {remove.ExitCode}).");
                return 1;
            }

            var after = ListVersion(code, out listError);
            if (listError != null) { set_profile_main.PrintError(listError); return 1; }
            if (after != null)
            {
                set_profile_main.PrintError($"VS Code reported success but {ExtensionId}@{after} is still installed.");
                return 1;
            }

            Console.WriteLine();
            set_profile_main.PrintSuccess($"VS Code extension {before} removed.");
            return 0;
        }

        private static void PrintNotFound() =>
            set_profile_main.PrintError("VS Code not found: no 'code' command on PATH or in the usual install folders.");

        /// <summary>Installed version of our extension, or null. <paramref name="error"/> is set when the query itself failed.</summary>
        private static string? ListVersion(string code, out string? error)
        {
            var r = RunCode(code, new[] { "--list-extensions", "--show-versions" }, 60_000);
            if (!r.Ok)
            {
                error = r.Failure ?? $"Could not list VS Code extensions (exit {r.ExitCode}). {r.Output}".Trim();
                return null;
            }
            error = null;
            return ParseVersion(r.Output);
        }

        private static string? ParseVersion(string listing)
        {
            var prefix = ExtensionId + "@";
            foreach (var raw in listing.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(prefix.Length);
            }
            return null;
        }

        private readonly record struct CodeResult(bool Ok, int ExitCode, string Output, string? Failure);

        /// <summary>
        /// Runs the VS Code CLI and captures stdout+stderr. Node's deprecation warnings
        /// (DEP0169 etc.) come from VS Code's own CLI, so they are switched off in the child's
        /// environment rather than by filtering text: real errors still show.
        /// </summary>
        private static CodeResult RunCode(string code, string[] args, int timeoutMs)
        {
            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.Environment["NODE_NO_WARNINGS"] = "1";
            var nodeOptions = Environment.GetEnvironmentVariable("NODE_OPTIONS");
            psi.Environment["NODE_OPTIONS"] = string.IsNullOrWhiteSpace(nodeOptions)
                ? "--no-deprecation" : nodeOptions + " --no-deprecation";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // code.cmd is a batch file: it needs cmd.exe. The outer quotes are cmd's /c rule.
                psi.FileName = "cmd.exe";
                psi.Arguments = $"/c \"\"{code}\" {string.Join(" ", args.Select(a => $"\"{a}\""))}\"";
            }
            else
            {
                psi.FileName = code;
                foreach (var a in args) psi.ArgumentList.Add(a);
            }

            try
            {
                using var proc = Process.Start(psi);
                if (proc == null) return new CodeResult(false, -1, "", $"Could not start {code}.");
                var stdout = proc.StandardOutput.ReadToEndAsync();
                var stderr = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(timeoutMs))
                {
                    try { proc.Kill(true); } catch { }
                    return new CodeResult(false, -1, "", $"VS Code did not finish within {timeoutMs / 1000} seconds.");
                }
                var output = (stdout.Result + stderr.Result).Trim();
                return new CodeResult(proc.ExitCode == 0, proc.ExitCode, output, null);
            }
            catch (Exception ex)
            {
                return new CodeResult(false, -1, "", $"Could not run VS Code: {ex.Message}");
            }
        }

        /// <summary>Finds the VS Code command-line launcher, or null when VS Code is not installed.</summary>
        private static string? FindCli()
        {
            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var launcher = isWindows ? "code.cmd" : "code";

            // Test-only seam (not user-facing): when set, ONLY these directories are searched.
            var seam = Environment.GetEnvironmentVariable("COMPILERS_IDE_PROBE_PATHS");
            var searchPath = string.IsNullOrEmpty(seam) ? Environment.GetEnvironmentVariable("PATH") ?? "" : seam;
            foreach (var dir in searchPath.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    var candidate = Path.Combine(dir.Trim().Trim('"'), launcher);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }

            if (isWindows && string.IsNullOrEmpty(seam))
            {
                var roots = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                };
                foreach (var root in roots)
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    var candidate = Path.Combine(root, "Microsoft VS Code", "bin", "code.cmd");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Background cache of each integration's fast version probe. Nothing here blocks the
    /// menu except the bounded waits the callers ask for.
    /// </summary>
    internal static class IdeStatus
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<string, Task<IdeProbe>> Probes = new();

        /// <summary>One integration's background probe result.</summary>
        internal readonly record struct IdeProbe(bool Detected, string? Version);

        /// <summary>Starts one background probe per registered integration (idempotent).</summary>
        public static void Start()
        {
            lock (Gate)
                foreach (var ide in IdeRegistry.All)
                {
                    var i = ide;
                    if (!Probes.ContainsKey(i.FlagStem)) Probes[i.FlagStem] = Task.Run(() => Safe(i));
                }
        }

        /// <summary>Re-probes one integration (after Add / Remove).</summary>
        public static void Refresh(IIdeIntegration ide)
        {
            lock (Gate) Probes[ide.FlagStem] = Task.Run(() => Safe(ide));
        }

        private static IdeProbe Safe(IIdeIntegration ide)
        {
            try { return ide.IsAvailable ? new IdeProbe(true, ide.ProbeInstalledVersion()) : default; }
            catch { return default; }
        }

        /// <summary>Waits up to <paramref name="ms"/> for every probe to finish.</summary>
        public static void WaitAll(int ms)
        {
            Task[] tasks;
            lock (Gate) tasks = Probes.Values.ToArray();
            try { Task.WaitAll(tasks, ms); } catch { }
        }

        /// <summary>Probe result if finished (waiting up to <paramref name="waitMs"/>), else "not detected".</summary>
        public static IdeProbe Get(IIdeIntegration ide, int waitMs = 0)
        {
            Start();
            Task<IdeProbe> t;
            lock (Gate) t = Probes[ide.FlagStem];
            try { if (!t.IsCompleted && waitMs > 0) t.Wait(waitMs); } catch { }
            return t.IsCompletedSuccessfully ? t.Result : default;
        }

        /// <summary>True when the probe returned a version that is older than this build (compared as versions).</summary>
        public static bool IsOutOfDate(string? installed) =>
            installed != null
            && Version.TryParse(installed, out var have)
            && Version.TryParse(VersionInfo.Version, out var want)
            && have < want;

        /// <summary>True when any finished probe shows an out-of-date integration (never waits).</summary>
        public static bool AnyOutOfDate() =>
            IdeRegistry.All.Any(ide => IsOutOfDate(Get(ide).Version));
    }

    public static partial class set_profile_main
    {
        /// <summary>
        /// The "IDE Integration" menu, generated from <see cref="IdeRegistry"/>: two numbered
        /// items per IDE (Add, Remove), then 98 Back and 99 Exit. An invalid choice stays here.
        /// </summary>
        private static void IdeIntegrationMenu()
        {
            while (true)
            {
                Console.WriteLine();
                PrintSubheader("IDE Integration");
                Console.WriteLine();

                var ides = IdeRegistry.All;
                var actions = new Dictionary<string, Func<int>>();
                var n = 1;
                foreach (var ide in ides)
                {
                    var probe = IdeStatus.Get(ide, 200);
                    if (!probe.Detected) continue; // only offer IDEs that are installed here
                    var installed = probe.Version;
                    PrintMenu(n, AddLabel(ide, installed), IdeStatus.IsOutOfDate(installed) ? ConsoleColor.Yellow : null);
                    actions[n.ToString()] = ide.Install;
                    n++;
                    PrintMenu(n, $"Remove from {ide.Name}");
                    actions[n.ToString()] = ide.Remove;
                    n++;
                }
                if (n == 1)
                    PrintDim($"No supported IDE detected ({string.Join(", ", ides.Select(i => i.Name))}).");
                PrintMenu(98, "Back");
                PrintMenu(99, "Exit");

                // Interactive TTY -> deferred 'Choice:' entry; redirected -> plain ReadLine.
                string? choice;
                if (Console.IsInputRedirected || Console.IsOutputRedirected)
                {
                    ConsoleMenu.WritePrompt(n == 1 ? "\nChoose [98-99]: " : $"\nChoose [1-{n - 1}]: ");
                    choice = Console.ReadLine()?.Trim();
                    if (choice == null) return; // end of piped input
                }
                else
                {
                    Console.WriteLine();
                    choice = ConsoleMenu.ReadDeferredChoice();
                    if (string.IsNullOrEmpty(choice)) return; // Esc -> Back
                }

                if (choice == "98") return;
                if (choice == "99") Environment.Exit(0);
                if (actions.TryGetValue(choice, out var run))
                {
                    Console.WriteLine();
                    run();
                    foreach (var ide in ides) IdeStatus.Refresh(ide);
                }
                else
                {
                    Console.WriteLine("Invalid choice.");
                }
            }
        }

        private static string AddLabel(IIdeIntegration ide, string? installed)
        {
            var label = $"Add to {ide.Name}";
            if (installed == null) return label;
            var current = VersionInfo.Version;
            if (installed == current) return $"{label} (up to date)";
            return IdeStatus.IsOutOfDate(installed)
                ? $"{label} (installed {installed}, update to {current})"
                : $"{label} (installed {installed})";
        }
    }
}
