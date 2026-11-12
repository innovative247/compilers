using System.Text.Json;
using ibsCompiler.Configuration;

namespace ibsCompiler.Database
{
    public enum MssqlSessionSource { Off, SettingsFile, EnvironmentVariable, BesideSettings, Shipped }

    /// <summary>Which session-init script is active, where it came from, and its text.</summary>
    public sealed record MssqlSessionResolution(MssqlSessionSource Source, string? Path, string Script);

    /// <summary>
    /// The one place that decides which session-init script runs on every MSSQL connection.
    /// Order: settings.json says off; the file in settings.json (used when SQLCMDINI could not be
    /// set); the file the SQLCMDINI variable names; with the variable unset, a SQLCMDINI.sql beside
    /// settings.json (the older way to keep one); the shipped script. A path that is missing, is a
    /// folder, is not a valid path or cannot be read never fails a command: one warning, then the
    /// next source, and the copy built into the product ends the chain.
    /// </summary>
    public static class MssqlSessionSettings
    {
        public const string BesideSettingsFileName = "SQLCMDINI.sql";
        private const string ShippedResource = "SQLCMDINI.default.sql";
        private static int _warned;

        /// <summary>The shipped script as a file beside the binaries; every update replaces it.</summary>
        public static string ShippedFilePath => System.IO.Path.Combine(AppContext.BaseDirectory, ShippedResource);

        /// <summary>Folder that holds settings.json and the older beside-settings script; the binaries' folder when there is none.</summary>
        public static string BesideSettingsDirectory(string? settingsPath) =>
            (string.IsNullOrEmpty(settingsPath) ? null : System.IO.Path.GetDirectoryName(settingsPath)) ?? AppContext.BaseDirectory;

        public static string BesideSettingsPath(string? settingsPath) =>
            System.IO.Path.Combine(BesideSettingsDirectory(settingsPath), BesideSettingsFileName);

        public static string ShippedScript()
        {
            using var s = typeof(MssqlSessionSettings).Assembly.GetManifestResourceStream(ShippedResource)
                ?? throw new InvalidOperationException($"embedded resource {ShippedResource} is missing");
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }

        /// <summary>One warning line per process, however many sources are bad.</summary>
        public static void WarnOnce(string message)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0) Console.Error.WriteLine(message);
        }

        private static bool IsPathProblem(Exception ex) =>
            ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;

        public static bool IsShippedFile(string path)
        {
            try
            {
                return string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(ShippedFilePath),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch (Exception ex) when (IsPathProblem(ex)) { return false; }
        }

        /// <summary>
        /// The only place a candidate path is checked and read. False (after one warning, and for a
        /// missing file only when <paramref name="warnIfMissing"/>) for a path that is blank, invalid,
        /// a folder, missing or unreadable.
        /// </summary>
        private static bool TryReadCandidate(string source, string path, bool warnIfMissing, out string text)
        {
            text = "";
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    WarnOnce($"warning: {source} is blank; using the next source.");
                    return false;
                }
                var full = System.IO.Path.GetFullPath(path);
                if (Directory.Exists(full))
                {
                    WarnOnce($"warning: {source} names a folder, not a file: {path}");
                    return false;
                }
                if (!File.Exists(full))
                {
                    if (warnIfMissing) WarnOnce($"warning: {source} names a file that was not found: {path}");
                    return false;
                }
                text = File.ReadAllText(full);
                return true;
            }
            catch (Exception ex) when (IsPathProblem(ex))
            {
                WarnOnce($"warning: {source} could not be read: {path} ({ex.Message})");
                return false;
            }
        }

        public static MssqlSessionResolution Resolve(MssqlSessionConfig? config, string? settingsPath)
        {
            if (string.Equals(config?.Mode, "off", StringComparison.OrdinalIgnoreCase))
                return new(MssqlSessionSource.Off, null, "");

            var file = config?.FilePath;
            if (!string.IsNullOrEmpty(file) && TryReadCandidate("the file in settings.json", file, true, out var fileText))
                return new(MssqlSessionSource.SettingsFile, file, fileText);

            var named = SqlCmdIniVariable.Get();
            if (!string.IsNullOrEmpty(named) && TryReadCandidate("SQLCMDINI", named, true, out var namedText))
                return new(IsShippedFile(named) ? MssqlSessionSource.Shipped : MssqlSessionSource.EnvironmentVariable, named, namedText);

            // Older installs kept their own script beside settings.json; silent when there is none.
            var beside = BesideSettingsPath(settingsPath);
            if (string.IsNullOrEmpty(named) && TryReadCandidate("the file beside settings.json", beside, false, out var besideText))
                return new(MssqlSessionSource.BesideSettings, beside, besideText);

            // The shipped file is what runs, so the path shown to the user is the truth; the copy
            // built into the product covers an install where that file has gone missing or unreadable.
            var shipped = ShippedFilePath;
            if (TryReadCandidate("the shipped file", shipped, false, out var shippedText))
                return new(MssqlSessionSource.Shipped, shipped, shippedText);
            return new(MssqlSessionSource.Shipped, null, ShippedScript());
        }

        /// <summary>Resolve against the settings.json the compilers would load (used by the executor).</summary>
        public static MssqlSessionResolution ResolveCurrent()
        {
            var path = ProfileManager.FindSettingsFile();
            MssqlSessionConfig? config = null;
            try
            {
                var json = path == null ? null : ibs_compiler_common.ReadAllTextResilient(path);
                if (json != null)
                    config = JsonSerializer.Deserialize<SettingsFile>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.MssqlSessionSettings;
            }
            catch (Exception ex) when (ex is JsonException || IsPathProblem(ex)) { /* an unreadable settings.json just means defaults here; the profile loader reports it */ }
            return Resolve(config, path);
        }
    }
}
