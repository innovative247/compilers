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

        /// <summary>
        /// The identity every shared-profile action is attributed to. False when this
        /// machine has no git identity configured, which denies the whole feature.
        /// </summary>
        bool TryResolveOwner(out string owner, out string error);
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
        public const string RepoPage = "https://github.com/innovative247/compiler-profiles";

        /// <summary>
        /// What profile sharing needs, in the order a developer sets it up. Every
        /// denial leads with this so the reader learns the whole contract once, then
        /// the one line naming which step this machine is missing.
        /// </summary>
        public static readonly string AccessRequirements = string.Join(Environment.NewLine, new[]
        {
            "Profile sharing requires access to the private GitHub repo " + RepoPage,
            "  1. A GitHub account with access to that repo",
            "     (a repo admin grants it at " + RepoPage + "/settings/access).",
            "  2. git installed and signed in to GitHub over HTTPS (Git Credential Manager, or 'gh auth login').",
            "  3. A git identity on this machine:",
            "       git config --global user.email \"you@innovative247.com\"",
            "       git config --global user.name  \"Your Name\"",
        });

        private static string Denied(string missing) =>
            AccessRequirements + Environment.NewLine + "Missing here: " + missing;
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

        /// <summary>
        /// The line that says why git failed. git prints progress ("Cloning into ...")
        /// on stderr before the failure, so the first line is usually not the reason;
        /// prefer fatal:/error:, then remote:, then the last thing it said.
        /// </summary>
        private static string ErrorLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            return lines.FirstOrDefault(l => l.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase) ||
                                             l.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                ?? lines.FirstOrDefault(l => l.StartsWith("remote:", StringComparison.OrdinalIgnoreCase))
                ?? lines.LastOrDefault()
                ?? "";
        }

        private static bool GitAvailable()
        {
            var (code, _, _) = Git(null, "--version");
            return code == 0;
        }

        #endregion

        public bool TryEnsureReady(out string error)
        {
            if (!TryResolveOwner(out _, out error)) return false;
            if (HasCache) return true;

            if (!GitAvailable())
            {
                error = Denied("git is not installed or not on PATH (step 2).");
                return false;
            }

            try { Directory.CreateDirectory(Path.GetDirectoryName(_cacheDir)!); } catch { }

            var (code, _, err) = Git(null, "clone", "--depth", "1", "--branch", Branch, RepoUrl, _cacheDir);
            if (code != 0)
            {
                // Leave nothing half-cloned behind, or the next run thinks it has a cache.
                try { if (Directory.Exists(_cacheDir)) Directory.Delete(_cacheDir, recursive: true); } catch { }
                error = DescribeCloneFailure(ErrorLine(err));
                return false;
            }
            return true;
        }

        /// <summary>
        /// Turn git's clone stderr into the step the developer is missing. GitHub answers
        /// "not found" for a private repo the caller cannot see, so that line means
        /// "no access", not "wrong URL".
        /// </summary>
        private static string DescribeCloneFailure(string line)
        {
            if (line.Contains("could not read Username", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase))
                return Denied("git is not signed in to GitHub on this machine (step 2).");

            if (line.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("403", StringComparison.Ordinal) ||
                line.Contains("Permission", StringComparison.OrdinalIgnoreCase))
                return Denied("GitHub refused access - the signed-in account is not a collaborator on the repo, " +
                              "or git is signed in as a different account (step 1).");

            if (line.Contains("Could not resolve host", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("unable to access", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                return $"could not reach GitHub from this machine: {line}";

            return Denied($"could not clone the shared store: {line}");
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

            // No identity flags: commit as whoever this machine commits as. Nothing
            // reaches here without one - TryResolveOwner is the gate.
            var (ccode, _, cerr) = Git(_cacheDir, "commit", "-m", subject);
            if (ccode != 0) { error = $"git commit failed: {FirstLine(cerr)}"; ResetCache(); return false; }

            var (pcode, _, perr) = Git(_cacheDir, "push", "origin", Branch);
            if (pcode != 0)
            {
                ResetCache();
                var line = ErrorLine(perr);
                error = line.Contains("403", StringComparison.Ordinal) ||
                        line.Contains("Permission", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("denied", StringComparison.OrdinalIgnoreCase)
                    ? "push rejected - your GitHub account can read the shared store but not write to it. " +
                      $"A repo admin grants write access at {RepoPage}/settings/access: {line}"
                    : $"push rejected: {line}";
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
        /// Everything here is attributed to the git identity this machine commits as -
        /// the name colleagues already see on every other commit, and on a work machine
        /// the work address.
        /// <para>
        /// With no identity configured the feature is DENIED rather than attributed to
        /// something invented. A GitHub handle, or worse the OS user name, is not who
        /// the developer is anywhere else in the estate, and an unattributable record in
        /// a store everyone reads is worse than no record.
        /// </para>
        /// </summary>
        public bool TryResolveOwner(out string owner, out string error)
        {
            error = "";
            if (_owner != null) { owner = _owner; return true; }

            var email = GitConfig("user.email");
            if (!string.IsNullOrWhiteSpace(email))
            {
                owner = _owner = email;
                return true;
            }

            owner = "";
            error = Denied("no git identity is configured on this machine (step 3).");
            return false;
        }

        private string GitConfig(string key)
        {
            // Read it where the publish will happen, so the answer is the one that will
            // actually land in the commit.
            var (code, value, _) = Git(HasCache ? _cacheDir : null, "config", "--get", key);
            return code == 0 ? value.Trim() : "";
        }

    }
}
