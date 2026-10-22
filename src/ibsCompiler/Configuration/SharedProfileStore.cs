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

    /// <summary>The access step a store call was refused at.</summary>
    public enum SharedDenial
    {
        None,
        GitMissing,
        NotSignedIn,
        NoIdentity,
        NoAccess,
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
        public const string RepoUrl = "https://github.com/innovative247/compilers-profiles.git";
        public const string RepoPage = "https://github.com/innovative247/compilers-profiles";

        /// <summary>
        /// What profile sharing needs, in the order a developer sets it up. Every
        /// denial leads with this so the reader learns the whole contract once, then
        /// the one line naming which step this machine is missing.
        /// </summary>
        public static readonly string AccessRequirements = string.Join(Environment.NewLine, new[]
        {
            "Profile sharing requires access to the private shared-profiles repo on GitHub:",
            "1. git installed (https://git-scm.com/downloads).",
            "2. git signed in to GitHub (Git Credential Manager, or 'gh auth login').",
            "3. That GitHub account added to the repo - run set_profile > Shared profiles to request it.",
        });

        public const string IssuesRepo = "innovative247/compilers";

        /// <summary>Which access step the last store call was denied at.</summary>
        public SharedDenial LastDenial { get; private set; } = SharedDenial.None;

        /// <summary>
        /// The denial text: the contract, the step this machine is missing, and the one
        /// command to run next. Also records the step so an interactive caller can walk
        /// the user through it (see LastDenial).
        /// </summary>
        private string Denied(SharedDenial kind, string missing)
        {
            LastDenial = kind;
            var next = kind switch
            {
                SharedDenial.GitMissing => "install git (https://git-scm.com/downloads), then run this command again.",
                SharedDenial.NotSignedIn => "run 'gh auth login' (or 'git credential-manager github login'), then run this command again.",
                SharedDenial.NoIdentity => "run set_profile > Shared profiles to pick your GitHub account, then run this command again.",
                SharedDenial.NoAccess => "ask the repo admins to add your GitHub account - request access at " +
                                         AccessRequestUrl(null) + " - then run this command again once you have been added.",
                _ => "fix the problem above, then run this command again.",
            };
            return AccessRequirements + Environment.NewLine + "Missing here: " + missing +
                   Environment.NewLine + "Next: " + next;
        }

        /// <summary>
        /// Prefilled "new issue" page on the Compilers repo asking the admins for access.
        /// <paramref name="login"/> is the GitHub account when known.
        /// </summary>
        public static string AccessRequestUrl(string? login)
        {
            var (title, body) = AccessRequestText(login);
            return $"https://github.com/{IssuesRepo}/issues/new?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
        }

        /// <summary>
        /// The request is a PUBLIC issue on innovative247/compilers, so it names the
        /// GitHub login only - never an email address.
        /// </summary>
        public static (string Title, string Body) AccessRequestText(string? login)
        {
            var who = string.IsNullOrWhiteSpace(login) ? "my GitHub account" : login!;
            return ($"Access request: compilers-profiles for {who}",
                    $"{who} requests access to innovative247/compilers-profiles for Compilers shared profiles.\n\n" +
                    "Repo owners: reply **approve** to grant access, or **deny** to close this request.\n" +
                    $"Or grant it by hand: {RepoPage}/settings/access");
        }
        private const string Branch = "main";
        private const string ProfilesDir = "profiles";
        private const int GitTimeoutSeconds = 90;

        private readonly string _cacheDir;
        private readonly Func<SharedProfilesSettings?>? _settings;

        /// <summary>
        /// <paramref name="settings"/> supplies the Compilers-scoped publishing identity and
        /// GitHub account, read live so a change in the menu applies to the next call.
        /// </summary>
        public GitSharedProfileStore(string? settingsPath = null, Func<SharedProfilesSettings?>? settings = null)
        {
            _settings = settings;
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

        /// <summary>A real cache has .git\HEAD; a leftover .git folder from a failed delete does not count.</summary>
        public bool HasCache => File.Exists(Path.Combine(_cacheDir, ".git", "HEAD"));

        /// <summary>
        /// True only while a person is at the Shared Profiles menu on a console: then a
        /// credential helper may show its own sign-in UI. Everywhere else it must fail fast.
        /// </summary>
        public static bool AllowCredentialUi { get; set; }

        #region git plumbing

        /// <summary>
        /// Run git and capture both streams. GIT_TERMINAL_PROMPT=0 keeps a missing
        /// credential from parking the CLI on an invisible prompt — a hang is a worse
        /// failure than a clean "authentication failed" for a tool that scripts and
        /// agents drive. A GUI credential helper still works.
        /// </summary>
        private static (int Code, string Out, string Err) Git(string? workDir, params string[] args) =>
            Run("git", workDir, args);

        /// <summary>
        /// The one process runner (git and gh). Both streams are read asynchronously
        /// BEFORE the timed wait, so a child that fills a pipe cannot block past the
        /// timeout; on timeout the whole process tree is killed.
        /// </summary>
        private static (int Code, string Out, string Err) Run(string exe, string? workDir, params string[] args)
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (!string.IsNullOrEmpty(workDir)) psi.WorkingDirectory = workDir;
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            // Git Credential Manager ignores GIT_TERMINAL_PROMPT and pops a GUI; unattended
            // runs must get a clean "not signed in" instead of waiting out the timeout.
            if (!AllowCredentialUi) psi.Environment["GCM_INTERACTIVE"] = "never";
            // Never let git walk up out of the cache into whatever repo contains it: a
            // damaged cache must fail, not run set-url / reset --hard on the parent checkout.
            if (!string.IsNullOrEmpty(workDir))
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(workDir).TrimEnd('\\', '/'));
                if (!string.IsNullOrEmpty(parent)) psi.Environment["GIT_CEILING_DIRECTORIES"] = parent;
            }

            try
            {
                using var p = Process.Start(psi);
                if (p == null) return (-1, "", $"could not start {exe}");
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                // A person at the menu may be signing in to GitHub in the window git opened:
                // no time limit, only Esc in this console stops it.
                if (AllowCredentialUi)
                {
                    if (!WaitForExitOrEscape(p))
                    {
                        try { p.Kill(entireProcessTree: true); } catch { }
                        return (-1, "", "cancelled");
                    }
                    return (p.ExitCode, outTask.Result, errTask.Result);
                }
                if (!p.WaitForExit(GitTimeoutSeconds * 1000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return (-1, "", $"{exe} timed out after {GitTimeoutSeconds}s");
                }
                // The process has exited, so both pipes are closed and these complete.
                return (p.ExitCode, outTask.Result, errTask.Result);
            }
            catch (Exception ex)
            {
                return (-1, "", ex.Message);
            }
        }

        /// <summary>
        /// Wait for the process with no time limit. False when Esc is pressed first. After a
        /// few seconds the person is told how to stop it - by then git is usually waiting
        /// on a GitHub sign-in.
        /// </summary>
        private static bool WaitForExitOrEscape(Process p, bool hint = true)
        {
            var hinted = !hint;
            var started = DateTime.UtcNow;
            while (!p.WaitForExit(200))
            {
                if (!hinted && DateTime.UtcNow - started > TimeSpan.FromSeconds(5))
                {
                    Console.WriteLine("Waiting for GitHub (sign in if a window opened) - press Esc to cancel.");
                    hinted = true;
                }
                try
                {
                    while (Console.KeyAvailable)
                        if (Console.ReadKey(intercept: true).Key == ConsoleKey.Escape) return false;
                }
                catch (InvalidOperationException) { }
            }
            return true;
        }

        /// <summary>URL-scoped credential key, so the pinned account applies to GitHub only.</summary>
        private const string CredentialUserKey = "credential.https://github.com.username";

        /// <summary>The GitHub account pinned for Compilers (settings), or "".</summary>
        public string PinnedAccount => (_settings?.Invoke()?.GitHubAccount ?? "").Trim();

        /// <summary>The Compilers-scoped publishing email (settings), or "".</summary>
        public string SettingsEmail => (_settings?.Invoke()?.Email ?? "").Trim();

        /// <summary>The Compilers-scoped publishing name (settings), or "".</summary>
        public string SettingsName => (_settings?.Invoke()?.Name ?? "").Trim();

        /// <summary>
        /// git for anything that talks to GitHub (clone, fetch, push, ls-remote): when an
        /// account is pinned, a per-command -c selects it. Nothing is written to any
        /// git config by this - the pin lives in Compilers settings and the cache's own
        /// .git/config only.
        /// </summary>
        private (int Code, string Out, string Err) RemoteGit(string? workDir, params string[] args)
        {
            var account = PinnedAccount;
            if (account.Length == 0) return Git(workDir, args);
            var all = new List<string> { "-c", $"{CredentialUserKey}={account}" };
            all.AddRange(args);
            return Git(workDir, all.ToArray());
        }

        /// <summary>-c identity flags for a commit in the cache, when Compilers settings supply them.</summary>
        private List<string> IdentityFlags()
        {
            var flags = new List<string>();
            var email = SettingsEmail;
            var name = SettingsName;
            // Nothing in settings or git config: commit as the GitHub account, with
            // GitHub's own no-reply address for it rather than anything invented.
            if (email.Length == 0 && GitConfig("user.email").Length == 0 && PinnedAccount.Length > 0)
                email = $"{PinnedAccount}@users.noreply.github.com";
            if (name.Length == 0 && GitConfig("user.name").Length == 0)
                name = PinnedAccount.Length > 0 ? PinnedAccount : email.Split('@')[0];
            if (email.Length > 0) { flags.Add("-c"); flags.Add($"user.email={email}"); }
            if (name.Length > 0) { flags.Add("-c"); flags.Add($"user.name={name}"); }
            return flags;
        }

        /// <summary>
        /// Record the pinned account in the cache repo's own config (never --global).
        /// No pin removes the entry. Safe to call when there is no cache.
        /// </summary>
        public void ApplyAccountToCache()
        {
            if (!HasCache) return;
            var account = PinnedAccount;
            if (account.Length > 0) Git(_cacheDir, "config", "--local", CredentialUserKey, account);
            else Git(_cacheDir, "config", "--local", "--unset-all", CredentialUserKey);
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

        public static bool GitAvailable()
        {
            var (code, _, _) = Git(null, "--version");
            return code == 0;
        }

        #endregion

        public bool TryEnsureReady(out string error)
        {
            LastDenial = SharedDenial.None;
            if (!GitAvailable())
            {
                error = Denied(SharedDenial.GitMissing, "git is not installed or not on PATH (step 1).");
                return false;
            }
            if (!TryResolveOwner(out _, out error)) return false;
            if (HasCache) return EnsureOrigin(out error);

            try { Directory.CreateDirectory(Path.GetDirectoryName(_cacheDir)!); } catch { }

            var (code, _, err) = RemoteGit(null, "clone", "--depth", "1", "--branch", Branch, RepoUrl, _cacheDir);
            if (code != 0)
            {
                // Leave nothing half-cloned behind, or the next run thinks it has a cache.
                DeleteCache();
                error = DescribeCloneFailure(ErrorLine(err));
                return false;
            }
            ApplyAccountToCache();
            return true;
        }

        /// <summary>
        /// Turn git's clone stderr into the step the developer is missing. GitHub answers
        /// "not found" for a private repo the caller cannot see, so that line means
        /// "no access", not "wrong URL".
        /// </summary>
        private string DescribeCloneFailure(string line)
        {
            if (IsNotSignedIn(line))
                return Denied(SharedDenial.NotSignedIn, "git is not signed in to GitHub on this machine (step 2).");

            if (IsAccessRefused(line))
                return Denied(SharedDenial.NoAccess, "GitHub refused access - the signed-in account is not a collaborator on the repo, " +
                              "or git is signed in as a different account (step 3).");

            if (line.Contains("Could not resolve host", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("unable to access", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                return $"could not reach GitHub from this machine: {line}";

            return Denied(SharedDenial.None, $"could not clone the shared store: {line}");
        }

        private static bool IsNotSignedIn(string line) =>
            line.Contains("could not read Username", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Cannot prompt", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase);

        private static bool IsAccessRefused(string line) =>
            line.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("403", StringComparison.Ordinal) ||
            line.Contains("Permission", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("denied", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A cache cloned before the repo was renamed still has the old origin, and the
        /// rename has no redirect - the old URL is dead. Point it at the real URL.
        /// A failure here is a local git problem, never an access problem.
        /// </summary>
        private bool EnsureOrigin(out string error)
        {
            error = "";
            var (code, url, _) = Git(_cacheDir, "remote", "get-url", "origin");
            if (code == 0 && string.Equals(url.Trim(), RepoUrl, StringComparison.OrdinalIgnoreCase))
            {
                ApplyAccountToCache();
                return true;
            }
            var (scode, _, serr) = code == 0
                ? Git(_cacheDir, "remote", "set-url", "origin", RepoUrl)
                : Git(_cacheDir, "remote", "add", "origin", RepoUrl);
            if (scode == 0) { ApplyAccountToCache(); return true; }
            LastDenial = SharedDenial.None;
            error = $"could not point the shared-profile cache at the shared repo: {FirstLine(serr)}";
            return false;
        }

        public bool TryRefresh(out string error)
        {
            if (!TryEnsureReady(out error)) return false;

            var (code, _, err) = RemoteGit(_cacheDir, "fetch", "--depth", "1", "origin", Branch);
            if (code != 0)
            {
                var why = ErrorLine(err);
                if (IsNotSignedIn(why))
                    error = Denied(SharedDenial.NotSignedIn, "git is not signed in to GitHub on this machine (step 2).");
                else if (IsAccessRefused(why))
                    error = Denied(SharedDenial.NoAccess, "GitHub refused access - the signed-in account is not a collaborator on the repo, " +
                                   "or git is signed in as a different account (step 3).");
                else
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

            // Compilers settings identity when set (per-command -c, never git config);
            // otherwise whoever this machine commits as. TryResolveOwner is the gate.
            var commitArgs = IdentityFlags();
            commitArgs.AddRange(new[] { "commit", "-m", subject });
            var (ccode, _, cerr) = Git(_cacheDir, commitArgs.ToArray());
            if (ccode != 0) { error = $"git commit failed: {FirstLine(cerr)}"; ResetCache(); return false; }

            var (pcode, _, perr) = RemoteGit(_cacheDir, "push", "origin", Branch);
            if (pcode != 0)
            {
                ResetCache();
                var line = ErrorLine(perr);
                if (IsNotSignedIn(line))
                    error = Denied(SharedDenial.NotSignedIn, "git is not signed in to GitHub on this machine (step 2).");
                else if (line.Contains("403", StringComparison.Ordinal) ||
                         line.Contains("Permission", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("denied", StringComparison.OrdinalIgnoreCase))
                    error = Denied(SharedDenial.NoAccess, "push rejected - your GitHub account can read the shared store but not write to it. " +
                                   $"Ask a repo admin for write access: {line}");
                else
                    error = $"push rejected: {line}";
                return false;
            }

            error = "";
            return true;
        }

        private void ResetCache()
        {
            Git(_cacheDir, "reset", "--hard", $"origin/{Branch}");
        }

        /// <summary>Where the publishing email comes from: "Compilers settings", "git config", or "" when none.</summary>
        public string OwnerSource =>
            PinnedAccount.Length > 0 ? "GitHub account" :
            SettingsEmail.Length > 0 ? "Compilers settings" :
            GitConfig("user.email").Length > 0 ? "git config" : "";

        /// <summary>
        /// Everything here is attributed to the Compilers-scoped publishing email when one
        /// is set, else the git identity this machine commits as, else the GitHub username
        /// the person gave Compilers.
        /// <para>
        /// With none of those the feature is DENIED rather than attributed to something
        /// invented such as the OS user name: an unattributable record in a store
        /// everyone reads is worse than no record.
        /// </para>
        /// </summary>
        public bool TryResolveOwner(out string owner, out string error)
        {
            error = "";

            // The GitHub username Compilers signs in with - what the shared list shows as
            // "shared by" - then a Compilers-settings email, then git's own user.email.
            var email = PinnedAccount;
            if (email.Length == 0) email = SettingsEmail;
            if (email.Length == 0) email = GitConfig("user.email");
            if (!string.IsNullOrWhiteSpace(email))
            {
                owner = email;
                return true;
            }

            owner = "";
            error = Denied(SharedDenial.NoIdentity, "no GitHub account is set for Compilers and git has no user.email (step 2).");
            return false;
        }

        // ---- probes and fixes used by the interactive access walk-through ----

        /// <summary>Run a tool with its output captured. Code -1 when it cannot start.</summary>
        private static (int Code, string Out) Tool(string exe, params string[] args)
        {
            var (code, o, _) = Run(exe, null, args);
            return (code, o);
        }

        /// <summary>Run a tool attached to this console so its own prompts work.</summary>
        public static int RunInteractive(string exe, params string[] args)
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            try
            {
                using var p = Process.Start(psi);
                if (p == null) return -1;
                p.WaitForExit();
                return p.ExitCode;
            }
            catch { return -1; }
        }

        /// <summary>
        /// Git Credential Manager's browser sign-in with its own output hidden (the caller
        /// says what is happening). No time limit; Esc in this console cancels. Exit code,
        /// -1 when cancelled or it could not start.
        /// </summary>
        public static int BrowserSignIn(string? username)
        {
            var psi = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "credential-manager", "github", "login", "--browser" }) psi.ArgumentList.Add(a);
            if (!string.IsNullOrEmpty(username)) { psi.ArgumentList.Add("--username"); psi.ArgumentList.Add(username); }
            try
            {
                using var p = Process.Start(psi);
                if (p == null) return -1;
                _ = p.StandardOutput.ReadToEndAsync();
                _ = p.StandardError.ReadToEndAsync();
                if (!WaitForExitOrEscape(p, hint: false))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return -1;
                }
                return p.ExitCode;
            }
            catch { return -1; }
        }

        public static bool GhAvailable() => Tool("gh", "--version").Code == 0;

        public static bool CredentialManagerAvailable() => Git(null, "credential-manager", "--version").Code == 0;

        /// <summary>The GitHub login gh is signed in as, or null.</summary>
        public static string? GhLogin()
        {
            var (code, o) = Tool("gh", "api", "user", "--jq", ".login");
            var login = o.Trim();
            return code == 0 && login.Length > 0 ? login : null;
        }

        /// <summary>The GitHub account(s) Git Credential Manager holds, or null when it cannot say.</summary>
        public static string? CredentialManagerAccounts()
        {
            var (code, o, _) = Git(null, "credential-manager", "github", "list");
            var accounts = o.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            return code == 0 && accounts.Count > 0 ? string.Join(", ", accounts) : null;
        }

        public static string GlobalGitConfig(string key)
        {
            var (code, v, _) = Git(null, "config", "--global", "--get", key);
            return code == 0 ? v.Trim() : "";
        }

        /// <summary>The GitHub accounts Git Credential Manager holds, in its own order.</summary>
        public static List<string> CredentialManagerAccountList()
        {
            var (code, o, _) = Git(null, "credential-manager", "github", "list");
            if (code != 0) return new List<string>();
            return o.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        }

        public static string GitVersion()
        {
            var (code, o, _) = Git(null, "--version");
            return code == 0 ? o.Trim() : "";
        }

        /// <summary>Outcome of the live access probe.</summary>
        public enum AccessState { Ok, GitMissing, NotSignedIn, NoAccess, Unreachable }

        /// <summary>
        /// <c>git ls-remote</c> against the store with the pinned account, classified with
        /// the same rules as clone/fetch. Read-only; writes nothing anywhere.
        /// </summary>
        public AccessState CheckAccess(out string detail)
        {
            detail = "";
            if (!GitAvailable()) return AccessState.GitMissing;
            var (code, _, err) = RemoteGit(null, "ls-remote", RepoUrl, "HEAD");
            if (code == 0) return AccessState.Ok;
            var line = ErrorLine(err);
            detail = line;
            if (IsNotSignedIn(line)) return AccessState.NotSignedIn;
            if (IsAccessRefused(line)) return AccessState.NoAccess;
            return AccessState.Unreachable;
        }

        /// <summary>
        /// Delete the local cache. Git marks object files read-only, which makes a plain
        /// recursive delete fail on Windows, so clear the attribute first.
        /// </summary>
        public bool DeleteCache()
        {
            try
            {
                if (!Directory.Exists(_cacheDir)) return true;
                foreach (var f in Directory.EnumerateFiles(_cacheDir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
                // HEAD first: whatever else is left behind, it no longer counts as a cache.
                try { File.Delete(Path.Combine(_cacheDir, ".git", "HEAD")); } catch { }
                Directory.Delete(_cacheDir, recursive: true);
                return true;
            }
            catch { return !Directory.Exists(_cacheDir); }
        }

        /// <summary>
        /// Post the access request as an issue on the public Compilers repo, signed in as
        /// <paramref name="login"/> with the token git already holds for that account (Git
        /// Credential Manager or gh). An open request from the same account is returned
        /// instead of posting another. The issue URL, or null when it could not be posted.
        /// </summary>
        public static string? CreateAccessRequest(string login)
        {
            var token = GitHubToken(login);
            if (token == null) return null;
            var (title, body) = AccessRequestText(login);
            try
            {
                using var http = GitHubApi(token);
                var api = $"https://api.github.com/repos/{IssuesRepo}/issues";

                var existing = OpenAccessRequest(http, login);
                if (existing != null) return existing;

                var payload = System.Text.Json.JsonSerializer.Serialize(new { title, body });
                using var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                using var resp = http.PostAsync(api, content).Result;
                if (!resp.IsSuccessStatusCode) return null;
                var created = System.Text.Json.JsonDocument.Parse(resp.Content.ReadAsStringAsync().Result);
                return created.RootElement.GetProperty("html_url").GetString();
            }
            catch { return null; }
        }

        /// <summary>Where an account without access stands.</summary>
        public enum InvitationState { None, Invited, RequestOpen }

        /// <summary>
        /// For an account without access: Invited when an invitation to the profiles repo
        /// is waiting (the request was approved), RequestOpen when its access request is
        /// still open, None otherwise.
        /// </summary>
        public static InvitationState PendingInvitation(string login)
        {
            var token = GitHubToken(login);
            if (token == null) return InvitationState.None;
            try
            {
                using var http = GitHubApi(token);
                if (InvitationId(http) != null) return InvitationState.Invited;
                return OpenAccessRequest(http, login) != null ? InvitationState.RequestOpen : InvitationState.None;
            }
            catch { return InvitationState.None; }
        }

        /// <summary>Accept <paramref name="login"/>'s invitation to the profiles repo. True on success.</summary>
        public static bool AcceptInvitation(string login)
        {
            var token = GitHubToken(login);
            if (token == null) return false;
            try
            {
                using var http = GitHubApi(token);
                var id = InvitationId(http);
                if (id == null) return false;
                using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Patch,
                    $"https://api.github.com/user/repository_invitations/{id}");
                using var resp = http.SendAsync(req).Result;
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        /// <summary>The id of the signed-in account's invitation to the profiles repo, or null.</summary>
        private static long? InvitationId(System.Net.Http.HttpClient http)
        {
            var repo = RepoPage["https://github.com/".Length..];
            var list = http.GetStringAsync("https://api.github.com/user/repository_invitations").Result;
            foreach (var inv in System.Text.Json.JsonDocument.Parse(list).RootElement.EnumerateArray())
                if (string.Equals(inv.GetProperty("repository").GetProperty("full_name").GetString(), repo,
                        StringComparison.OrdinalIgnoreCase))
                    return inv.GetProperty("id").GetInt64();
            return null;
        }

        private static System.Net.Http.HttpClient GitHubApi(string token)
        {
            var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Add("User-Agent", "ibs-compilers");
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
            return http;
        }

        /// <summary>The URL of <paramref name="login"/>'s open access request, or null.</summary>
        private static string? OpenAccessRequest(System.Net.Http.HttpClient http, string login)
        {
            var (title, _) = AccessRequestText(login);
            var open = http.GetStringAsync(
                $"https://api.github.com/repos/{IssuesRepo}/issues?state=open&creator={Uri.EscapeDataString(login)}").Result;
            foreach (var issue in System.Text.Json.JsonDocument.Parse(open).RootElement.EnumerateArray())
                if (issue.GetProperty("title").GetString() == title)
                    return issue.GetProperty("html_url").GetString();
            return null;
        }

        /// <summary>
        /// The GitHub token git would use for <paramref name="login"/>, read from the
        /// credential helper without ever showing a sign-in window; gh's token when there is
        /// no helper answer. Kept in memory only. Null when none.
        /// </summary>
        private static string? GitHubToken(string login)
        {
            try
            {
                var psi = new ProcessStartInfo("git", "credential fill")
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
                psi.Environment["GCM_INTERACTIVE"] = "never";
                using var p = Process.Start(psi);
                if (p != null)
                {
                    p.StandardInput.Write($"protocol=https\nhost=github.com\nusername={login}\n\n");
                    p.StandardInput.Close();
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    _ = p.StandardError.ReadToEndAsync();
                    if (p.WaitForExit(GitTimeoutSeconds * 1000) && p.ExitCode == 0)
                    {
                        var pw = outTask.Result.Split('\n').Select(l => l.Trim())
                            .FirstOrDefault(l => l.StartsWith("password=", StringComparison.Ordinal));
                        if (pw != null && pw.Length > "password=".Length) return pw["password=".Length..];
                    }
                    else { try { p.Kill(entireProcessTree: true); } catch { } }
                }
            }
            catch { }

            if (!GhAvailable()) return null;
            var (code, o) = Tool("gh", "auth", "token", "--user", login);
            var token = o.Trim();
            return code == 0 && token.Length > 0 ? token : null;
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
