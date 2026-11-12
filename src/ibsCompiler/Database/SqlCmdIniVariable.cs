using System.Runtime.InteropServices;

namespace ibsCompiler.Database
{
    /// <summary>
    /// The SQLCMDINI variable, which names the MSSQL session-settings script (the mechanism native
    /// sqlcmd and the legacy compilers used). The process value wins; otherwise the value saved for
    /// the user: the Windows user environment, or an export line in the shell rc files elsewhere.
    /// </summary>
    public static class SqlCmdIniVariable
    {
        private const string Name = "SQLCMDINI";
        private const string ExportPrefix = "export " + Name + "=";
        private static readonly string[] RcFiles = { ".bashrc", ".zshrc" };
        // Test-only seam (not user-facing): when it names a file, the saved value lives in that file
        // (one line, the path; empty or missing = unset) instead of the user environment or rc files.
        private const string StoreSeam = "COMPILERS_SQLCMDINI_STORE";

        private static string? Store
        {
            get
            {
                var s = Environment.GetEnvironmentVariable(StoreSeam);
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
        }

        public static string? Get()
        {
            var process = Environment.GetEnvironmentVariable(Name);
            if (!string.IsNullOrEmpty(process)) return process;

            string? saved = null;
            if (Store is { } store)
            {
                try { saved = File.Exists(store) ? File.ReadAllText(store).Trim() : null; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try { saved = Environment.GetEnvironmentVariable(Name, EnvironmentVariableTarget.User); }
                catch (System.Security.SecurityException) { }
            }
            else
                saved = ReadFromRcFiles();
            return string.IsNullOrEmpty(saved) ? null : saved;
        }

        /// <summary>Save for the user and set it for this process. Other programs see it once restarted. False with the reason when it could not be saved.</summary>
        public static bool Set(string path, out string error)
        {
            error = "";
            try
            {
                if (Store is { } store)
                {
                    if (!ibs_compiler_common.WriteAllTextAtomic(store, path))
                        throw new IOException($"could not write {store}");
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    Environment.SetEnvironmentVariable(Name, path, EnvironmentVariableTarget.User);
                else
                    SaveToRcFiles(path);
                Environment.SetEnvironmentVariable(Name, path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string RcPath(string rcFile) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), rcFile);

        /// <summary>Only an unindented line is ours; an indented one sits inside something else in the rc file.</summary>
        private static bool IsExportLine(string line) => line.StartsWith(ExportPrefix, StringComparison.Ordinal);

        /// <summary>The last of our export lines in ~/.bashrc then ~/.zshrc, unquoted (single-quoted as written here, or double-quoted).</summary>
        private static string? ReadFromRcFiles()
        {
            string? found = null;
            foreach (var rcFile in RcFiles)
            {
                try
                {
                    var rcPath = RcPath(rcFile);
                    if (!File.Exists(rcPath)) continue;
                    foreach (var line in File.ReadAllLines(rcPath))
                        if (IsExportLine(line)) found = Unquote(line.Substring(ExportPrefix.Length).Trim());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return found;
        }

        private static string Unquote(string value)
        {
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
                return value.Substring(1, value.Length - 2).Replace("'\\''", "'");
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                return value.Substring(1, value.Length - 2);
            return value;
        }

        /// <summary>Same rc files and "# IBS Compilers" marker as ConfigureCommand's PATH entry; one export line, replaced in place.</summary>
        private static void SaveToRcFiles(string path)
        {
            var exportLine = $"{ExportPrefix}'{path.Replace("'", "'\\''")}'";
            var wrote = false;
            foreach (var rcFile in RcFiles)
            {
                var rcPath = RcPath(rcFile);
                if (!File.Exists(rcPath)) continue;
                var lines = File.ReadAllLines(rcPath).Where(l => !IsExportLine(l)).ToList();
                var marker = lines.FindIndex(l => l.Trim() == "# IBS Compilers");
                if (marker >= 0) lines.Insert(marker + 1, exportLine);
                else { lines.Add(""); lines.Add("# IBS Compilers"); lines.Add(exportLine); }
                File.WriteAllText(rcPath, string.Join("\n", lines) + "\n");
                wrote = true;
            }
            // No rc file yet: create .bashrc, as the PATH entry does.
            if (!wrote)
                File.WriteAllText(RcPath(".bashrc"), $"# IBS Compilers\n{exportLine}\n");
        }
    }
}
