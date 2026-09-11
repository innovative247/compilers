using System.Diagnostics;

namespace ibsCompiler.Configuration
{
    /// <summary>
    /// Where shared profiles live. One seam, so the git-backed store below can be
    /// swapped for an HTTPS/API-key backend later without touching the command layer.
    /// </summary>
    public interface ISharedProfileStore
    {
        /// <summary>Human-readable location of the local cache, for diagnostics.</summary>
        string Location { get; }

        /// <summary>UTC time the cache was last refreshed, or null when there is no cache yet.</summary>
        DateTime? CacheTimeUtc { get; }

        /// <summary>Make sure a usable local cache exists, cloning it if this is the first run.</summary>
        bool TryEnsureReady(out string error);

        /// <summary>Pull the latest published records.</summary>
        bool TryRefresh(out string error);

        /// <summary>Everything currently in the cache, name-ordered.</summary>
        IReadOnlyList<SharedProfile> List();

        /// <summary>Publish or update one record. <paramref name="json"/> has already passed the publish guard.</summary>
        bool TryPublish(string name, string json, string commitSubject, out string error);

        /// <summary>Remove one record from the store.</summary>
        bool TryWithdraw(string name, string commitSubject, out string error);

        /// <summary>Identity recorded as OWNER on publish.</summary>
        string ResolveOwner();
    }

    /// <summary>
    /// Shared profiles backed by a private GitHub repo, with the developer's own
    /// GitHub account as the access control: read access lists and fetches, push
    /// access publishes. Nothing here enforces that — an unauthorized push is simply
    /// rejected by GitHub and the rejection is reported verbatim.
    /// <para>
    /// The local clone IS the cache. It is tool-owned and never hand-edited, which is
    /// why a refresh is a hard reset onto origin/main rather than a merge: there is no
    /// local work in it to preserve, and a half-merged cache would be worse than a
    /// stale one.
    /// </para>
    /// </summary>
    public class GitSharedProfileStore : ISharedProfileStore
    {
        public const string RepoUrl = "https://github.com/innovative247/compiler-profiles.git";
        private const string Branch = "main";
        private const string ProfilesDir = "profiles";
        private const int GitTimeoutSeconds = 90;

        private readonly string _cacheDir;

        public GitSharedProfileStore(string? settingsPath = null)
        {
            var baseDir = !string.IsNullOrEmpty(settingsPath)
                ? Path.GetDirectoryName(settingsPath)
                : Path.GetDirectoryName(ProfileManager.FindSettingsFile() ?? "");
            if (string.IsNullOrEmpty(baseDir)) baseDir = AppContext.BaseDirectory;
            _cacheDir = Path.Combine(baseDir!, "shared-profiles");
        }

        public string Location => _cacheDir;

        public DateTime? CacheTimeUtc
        {
            get
            {
                var marker = Path.Combine(_cacheDir, ".git", "FETCH_HEAD");
                if (File.Exists(marker)) return File.GetLastWriteTimeUtc(marker);
                var head = Path.Combine(_cacheDir, ".git", "HEAD");
                if (File.Exists(head)) return File.GetLastWriteTimeUtc(head);
                return null;
            }
        }

        public bool HasCache => Directory.Exists(Path.Combine(_cacheDir, ".git"));

        #region git plumbing

        /// <summary>
        /// Run git and capture both streams. GIT_TERMINAL_PROMPT=0 keeps a missing
        /// credential from parking the CLI on an invisible prompt — a hang is a worse
        /// failure than a clean "authentication failed" for a tool that scripts and
        /// agents drive. A GUI credential helper still works.
        /// </summary>
        private static (int Code, string Out, string Err) Git(string? workDir, params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (!string.IsNullOrEmpty(workDir)) psi.WorkingDirectory = workDir;
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

            try
            {
                using var p = Process.Start(psi);
                if (p == null) return (-1, "", "could not start git");
                var stdout = p.StandardOutput.ReadToEnd();
                var stderr = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(GitTimeoutSeconds * 1000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return (-1, stdout, $"git timed out after {GitTimeoutSeconds}s");
                }
                return (p.ExitCode, stdout, stderr);
            }
            catch (Exception ex)
            {
                return (-1, "", ex.Message);
            }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            foreach (var line in text.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length > 0) return t;
            }
            return "";
        }

        private static bool GitAvailable()
        {
            var (code, _, _) = Git(null, "--version");
            return code == 0;
        }

        #endregion

        public bool TryEnsureReady(out string error)
        {
            error = "";
            if (HasCache) return true;

            if (!GitAvailable())
            {
                error = "git is not installed or not on PATH - shared profiles are unavailable.";
                return false;
            }

            try { Directory.CreateDirectory(Path.GetDirectoryName(_cacheDir)!); } catch { }

            var (code, _, err) = Git(null, "clone", "--depth", "1", "--branch", Branch, RepoUrl, _cacheDir);
            if (code != 0)
            {
                // Leave nothing half-cloned behind, or the next run thinks it has a cache.
                try { if (Directory.Exists(_cacheDir)) Directory.Delete(_cacheDir, recursive: true); } catch { }
                error = $"could not reach the shared profile store: {FirstLine(err)}";
                return false;
            }
            return true;
        }

        public bool TryRefresh(out string error)
        {
            if (!TryEnsureReady(out error)) return false;

            var (code, _, err) = Git(_cacheDir, "fetch", "--depth", "1", "origin", Branch);
            if (code != 0)
            {
                error = $"could not refresh the shared profile store: {FirstLine(err)}";
                return false;
            }

            // Tool-owned cache, no local work to preserve - see the class remarks.
            var (rcode, _, rerr) = Git(_cacheDir, "reset", "--hard", $"origin/{Branch}");
            if (rcode != 0)
            {
                error = $"could not update the local cache: {FirstLine(rerr)}";
                return false;
            }
            error = "";
            return true;
        }

        public IReadOnlyList<SharedProfile> List()
        {
            var dir = Path.Combine(_cacheDir, ProfilesDir);
            if (!Directory.Exists(dir)) return Array.Empty<SharedProfile>();

            var result = new List<SharedProfile>();
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                var json = ibs_compiler_common.ReadAllTextResilient(file);
                if (json == null) continue;
                var shared = SharedProfileMap.Deserialize(json);
                if (shared == null) continue;
                if (string.IsNullOrEmpty(shared.Name))
                    shared.Name = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
                result.Add(shared);
            }
            return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public SharedProfile? Get(string name)
        {
            return List().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private string PathFor(string name) =>
            Path.Combine(_cacheDir, ProfilesDir, name.ToUpperInvariant() + ".json");

        public bool TryPublish(string name, string json, string commitSubject, out string error)
        {
            if (!TryRefresh(out error)) return false;

            var relative = $"{ProfilesDir}/{name.ToUpperInvariant()}.json";
            try
            {
                Directory.CreateDirectory(Path.Combine(_cacheDir, ProfilesDir));
                // LF, no BOM - the store pins eol=lf so a CRLF write would show every
                // publish as a whole-file diff.
                File.WriteAllText(PathFor(name), json.Replace("\r\n", "\n") + "\n", new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                error = $"could not stage the shared profile: {ex.Message}";
                return false;
            }

            return Commit(relative, commitSubject, out error);
        }

        public bool TryWithdraw(string name, string commitSubject, out string error)
        {
            if (!TryRefresh(out error)) return false;

            var path = PathFor(name);
            if (!File.Exists(path))
            {
                error = $"'{name.ToUpperInvariant()}' is not in the shared store.";
                return false;
            }
            try { File.Delete(path); }
            catch (Exception ex) { error = $"could not remove the shared profile: {ex.Message}"; return false; }

            return Commit($"{ProfilesDir}/{name.ToUpperInvariant()}.json", commitSubject, out error);
        }

        /// <summary>
        /// Stage one path, commit it, push it. A rejected push (no write access) leaves
        /// the cache clean by resetting back onto origin, so the next read is not served
        /// from a local-only commit that nobody else can see.
        /// </summary>
        private bool Commit(string relativePath, string subject, out string error)
        {
            var (acode, _, aerr) = Git(_cacheDir, "add", "--", relativePath);
            if (acode != 0) { error = $"git add failed: {FirstLine(aerr)}"; return false; }

            var (scode, sout, _) = Git(_cacheDir, "status", "--porcelain");
            if (scode == 0 && string.IsNullOrWhiteSpace(sout))
            {
                error = "";
                return true; // nothing changed - already published exactly this
            }

            var who = ResolveOwner();
            var (ccode, _, cerr) = Git(_cacheDir,
                "-c", $"user.name={who}",
                "-c", $"user.email={who}@users.noreply.github.com",
                "commit", "-m", subject);
            if (ccode != 0) { error = $"git commit failed: {FirstLine(cerr)}"; ResetCache(); return false; }

            var (pcode, _, perr) = Git(_cacheDir, "push", "origin", Branch);
            if (pcode != 0)
            {
                ResetCache();
                error = $"push rejected - your GitHub account may not have write access to the shared store: {FirstLine(perr)}";
                return false;
            }

            error = "";
            return true;
        }

        private void ResetCache()
        {
            Git(_cacheDir, "reset", "--hard", $"origin/{Branch}");
        }

        private string? _owner;

        /// <summary>
        /// What the developer chose to be credited as, from settings.json. Set by the
        /// command layer, which is the side that reads settings.
        /// </summary>
        public string OwnerOverride { get; set; } = "";

        /// <summary>
        /// Who to record as OWNER. An explicit choice wins; otherwise the GitHub login,
        /// since that is the account the store is gated on. Falls back to the git
        /// identity and then the OS user, so a publish never fails just because gh is
        /// missing.
        /// </summary>
        public string ResolveOwner()
        {
            if (!string.IsNullOrWhiteSpace(OwnerOverride)) return OwnerOverride.Trim();
            if (_owner != null) return _owner;

            var login = GhLogin();
            if (!string.IsNullOrEmpty(login)) return _owner = login;

            var (code, name, _) = Git(null, "config", "--get", "user.name");
            if (code == 0 && !string.IsNullOrWhiteSpace(name)) return _owner = name.Trim();

            return _owner = Environment.UserName;
        }

        private static string? GhLogin()
        {
            try
            {
                var psi = new ProcessStartInfo("gh")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("api");
                psi.ArgumentList.Add("user");
                psi.ArgumentList.Add("--jq");
                psi.ArgumentList.Add(".login");
                using var p = Process.Start(psi);
                if (p == null) return null;
                var outp = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                if (!p.WaitForExit(15000)) { try { p.Kill(true); } catch { } return null; }
                if (p.ExitCode != 0) return null;
                var login = outp.Trim();
                return string.IsNullOrEmpty(login) ? null : login;
            }
            catch { return null; }
        }
    }
}
