using System.Diagnostics;
using ibsCompiler.Configuration;

namespace ibsCompiler
{
    /// <summary>
    /// Interactive setup for shared profiles, three checks in order:
    /// git installed -> a GitHub account signed in -> that account can read the repo.
    /// Each failing check says what to do next and stops. Headless runs never get here;
    /// they print the store's denial, which names the next command.
    /// </summary>
    public static partial class set_profile_main
    {
        /// <summary>True only while the Shared Profiles menu is driving a person.</summary>
        private static bool _guideAccess;

        /// <summary>
        /// Run one store call. On an access denial at the menu, run setup and retry once;
        /// otherwise print the error. Returns true on success.
        /// </summary>
        private static bool Guided(Func<(bool Ok, string Error)> attempt)
        {
            var (ok, error) = attempt();
            if (ok) return true;

            var kind = Store.LastDenial;
            if (!_guideAccess || !CliArgs.IsInteractiveTty() || kind == SharedDenial.None)
            {
                PrintError(error);
                return false;
            }
            if (!SetupShared()) return false;

            (ok, error) = attempt();
            if (!ok) PrintError(error);
            return ok;
        }

        private static void PrintGitMissing()
        {
            PrintWarning("Git is not installed.");
            PrintDim(OperatingSystem.IsWindows()
                ? "  Install Git for Windows from https://git-scm.com/downloads, then open a new window and try again."
                : "  Install git with your package manager (e.g. 'sudo apt install git'), then try again.");
        }

        /// <summary>
        /// The three checks. True when this machine can read the shared store and a local
        /// copy exists; false after telling the person what to do next.
        /// </summary>
        private static bool SetupShared()
        {
            // 1. git
            if (!GitSharedProfileStore.GitAvailable()) { PrintGitMissing(); return false; }

            // 2. GitHub account
            var account = Store.PinnedAccount;
            if (account.Length == 0)
            {
                var chosen = ChooseGitHubAccount(change: false);
                if (chosen == null) return false;
                if (!UseGitHubAccount(chosen)) return false;
                account = chosen;
            }

            // 3. access - asked without any credential window, so the answer is immediate.
            var allowUi = GitSharedProfileStore.AllowCredentialUi;
            var invitationTried = false;
            string? shown = null;
            try
            {
                while (true)
                {
                    if (shown != account)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"GitHub account: {account}");
                        shown = account;
                    }
                    Console.WriteLine("Checking authority...");
                    GitSharedProfileStore.AllowCredentialUi = false;
                    var state = Store.CheckAccess(out var detail);
                    GitSharedProfileStore.AllowCredentialUi = allowUi;

                    switch (state)
                    {
                        case GitSharedProfileStore.AccessState.Ok:
                            PrintSuccess("Authorized.");
                            if (!Store.TryEnsureReady(out var error)) { PrintError(error); return false; }
                            return true;

                        case GitSharedProfileStore.AccessState.GitMissing:
                            PrintGitMissing();
                            return false;

                        case GitSharedProfileStore.AccessState.NotSignedIn:
                            PrintWarning($"{account} is not signed in to GitHub on this machine.");
                            if (!ConfirmYesNo($"Sign in to GitHub as {account}?", defaultYes: true)) return false;
                            if (!SignInToGitHub(account)) return false;
                            continue;

                        case GitSharedProfileStore.AccessState.NoAccess:
                            // An approved request leaves an invitation waiting; offer to accept it here.
                            var invitation = GitSharedProfileStore.InvitationState.None;
                            if (!invitationTried)
                            {
                                invitationTried = true;
                                invitation = GitSharedProfileStore.PendingInvitation(account);
                                if (invitation == GitSharedProfileStore.InvitationState.Invited)
                                {
                                    if (!ConfirmYesNo("Invitation is pending. Do you want to accept?", defaultYes: true)) return false;
                                    if (GitSharedProfileStore.AcceptInvitation(account))
                                    {
                                        PrintSuccess($"{account} invitation accepted.");
                                        continue;
                                    }
                                    PrintWarning("Could not accept the invitation.");
                                }
                            }
                            if (invitation == GitSharedProfileStore.InvitationState.RequestOpen)
                                PrintWarning($"{account} access request pending.");
                            else
                                PrintWarning($"{account} does not have access to shared profiles.");
                            Console.WriteLine();
                            PrintMenu(1, "Send an access request");
                            PrintMenu(2, "Use a different GitHub account");
                            PrintMenu(98, "Back");
                            var pick = ReadMenuChoice("1-2");
                            if (pick == "1") { RequestAccess(account); return false; }
                            if (pick != "2") return false;
                            var other = ChooseGitHubAccount(change: true);
                            if (other == null || !UseGitHubAccount(other)) return false;
                            account = other;
                            invitationTried = false;
                            continue;

                        default:
                            PrintWarning("Could not reach GitHub" + (detail.Length > 0 ? $": {detail}" : "."));
                            return false;
                    }
                }
            }
            finally { GitSharedProfileStore.AllowCredentialUi = allowUi; }
        }

        /// <summary>Shared Profiles menu "Change GitHub account": pick one, then re-run the checks.</summary>
        private static void ChangeGitHubAccount()
        {
            var chosen = ChooseGitHubAccount(change: true);
            if (chosen == null || !UseGitHubAccount(chosen)) return;
            SetupShared();
        }

        /// <summary>Save the account in Compilers settings (only) and point the local copy at it.</summary>
        private static bool UseGitHubAccount(string account)
        {
            if (!SaveSharedSettings(s => s.GitHubAccount = account)) return false;
            Store.ApplyAccountToCache();
            return true;
        }

        /// <summary>
        /// The GitHub account to use. With nothing signed in, offers to sign in. With one
        /// account, uses it (unless changing). With several, or when changing, lists them
        /// plus "sign in with another". Null when cancelled.
        /// </summary>
        private static string? ChooseGitHubAccount(bool change)
        {
            if (!GitSharedProfileStore.CredentialManagerAvailable())
            {
                // No Git Credential Manager (typically Linux): gh is the sign-in.
                var login = GitSharedProfileStore.GhAvailable() ? GitSharedProfileStore.GhLogin() : null;
                if (login != null && !change) return login;
                PrintWarning(login == null ? "Not signed in to GitHub." : $"Signed in to GitHub as {login}.");
                PrintDim("Sign in with 'gh auth login' (GitHub CLI, https://cli.github.com), then try again.");
                return null;
            }

            var accounts = GitSharedProfileStore.CredentialManagerAccountList();
            if (accounts.Count == 0)
            {
                PrintWarning("Not signed in to GitHub.");
                if (!ConfirmYesNo("Sign in to GitHub now?", defaultYes: true)) return null;
                if (!SignInToGitHub(null)) return null;
                accounts = GitSharedProfileStore.CredentialManagerAccountList();
                return accounts.Count == 1 ? accounts[0] : PickCredentialManagerAccount();
            }
            if (accounts.Count == 1 && !change) return accounts[0];
            return PickCredentialManagerAccount();
        }

        /// <summary>
        /// Git Credential Manager's own sign-in, attached to this console: it opens the
        /// browser and waits as long as the person needs.
        /// </summary>
        private static bool SignInToGitHub(string? account)
        {
            Console.WriteLine();
            int code;
            if (OperatingSystem.IsWindows())
            {
                Console.WriteLine("Please complete authentication in your browser...");
                code = GitSharedProfileStore.BrowserSignIn(account);
            }
            else
            {
                // Elsewhere GCM may need its own terminal prompts, so it keeps the console.
                code = account == null
                    ? GitSharedProfileStore.RunInteractive("git", "credential-manager", "github", "login")
                    : GitSharedProfileStore.RunInteractive("git", "credential-manager", "github", "login", "--username", account);
            }
            if (code == 0) return true;
            PrintWarning("GitHub sign-in did not finish.");
            return false;
        }

        /// <summary>Choose one of GCM's accounts, or sign in with another. Null when cancelled.</summary>
        private static string? PickCredentialManagerAccount()
        {
            for (int round = 0; round < 3; round++)
            {
                var accounts = GitSharedProfileStore.CredentialManagerAccountList();
                Console.WriteLine();
                PrintDim("GitHub accounts signed in on this machine:");
                for (int i = 0; i < accounts.Count; i++) PrintMenu(i + 1, accounts[i]);
                var signIn = accounts.Count + 1;
                PrintMenu(signIn, "Sign in with another account");

                var pick = ReadLinePrompt($"\nChoose [1-{signIn}, Enter to cancel]: ");
                if (string.IsNullOrEmpty(pick)) return null;
                if (!int.TryParse(pick, out var idx) || idx < 1 || idx > signIn)
                {
                    PrintWarning("Invalid selection.");
                    return null;
                }
                if (idx <= accounts.Count) return accounts[idx - 1];

                if (!SignInToGitHub(null)) return null;
                var added = GitSharedProfileStore.CredentialManagerAccountList()
                    .Where(a => !accounts.Contains(a, StringComparer.OrdinalIgnoreCase)).ToList();
                if (added.Count == 1) return added[0];
                // Nothing new (signed in again as a listed account): choose from the list.
            }
            return null;
        }

        /// <summary>
        /// Send the request as an issue on the public Compilers repo (the private profiles
        /// repo is invisible to someone without access), which notifies the repo owners -
        /// so it names the GitHub account only. Posted directly as that account; the
        /// prefilled browser page is the fallback.
        /// </summary>
        private static void RequestAccess(string account)
        {
            Console.WriteLine();
            Console.WriteLine("Sending access request...");
            var issue = GitSharedProfileStore.CreateAccessRequest(account);
            if (issue != null)
            {
                PrintSuccess("Access request sent.");
                PrintDim("Open Shared profiles again once accepted.");
                return;
            }

            var url = GitSharedProfileStore.AccessRequestUrl(account);
            PrintWarning("Could not send the request directly.");
            PrintDim("Your browser will open the request page - click 'Create' to send it.");
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch
            {
                PrintDim("Could not open a browser. Open this address instead:");
                PrintDim($"{url}");
            }
            PrintDim("When you are added, GitHub emails you an invitation - accept it, then open Shared profiles again.");
        }

        /// <summary>A numbered-menu answer; Esc or end of input means Back.</summary>
        private static string ReadMenuChoice(string range)
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
                return ReadLinePrompt($"\nChoose [{range}]: ") ?? "98";
            Console.WriteLine();
            var input = ConsoleMenu.ReadDeferredChoice();
            return string.IsNullOrEmpty(input) ? "98" : input;
        }

        /// <summary>The account git uses for GitHub here, for display. Null when none is found.</summary>
        private static string? SignedInAccount()
        {
            if (Store.PinnedAccount.Length > 0) return Store.PinnedAccount;
            var gcm = GitSharedProfileStore.CredentialManagerAvailable()
                ? GitSharedProfileStore.CredentialManagerAccounts() : null;
            if (gcm != null) return gcm;
            return GitSharedProfileStore.GhAvailable() ? GitSharedProfileStore.GhLogin() : null;
        }
    }
}
