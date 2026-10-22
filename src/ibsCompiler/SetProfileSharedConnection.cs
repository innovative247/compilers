using ibsCompiler.Configuration;

namespace ibsCompiler
{
    /// <summary>
    /// The GitHub connection status and the settings writer for shared profiles. Everything here is
    /// scoped to Compilers: the account and publishing identity live in settings.json
    /// (<see cref="SharedProfilesSettings"/>) and the cache's own .git/config. Nothing
    /// writes git's global config or signs anyone out of GitHub machine-wide.
    /// </summary>
    public static partial class set_profile_main
    {
        /// <summary>
        /// Change only the "SharedProfiles" node of settings.json. The file is re-read and
        /// every other node (profiles, data_transfer, ...) is written back untouched, so a
        /// stale in-memory snapshot can never revert another process's edits. An all-empty
        /// section is removed. False (after printing why) when the write failed.
        /// </summary>
        private static bool SaveSharedSettings(Action<SharedProfilesSettings> change)
        {
            var section = new SharedProfilesSettings
            {
                Email = _settings.SharedProfiles?.Email ?? "",
                Name = _settings.SharedProfiles?.Name ?? "",
                GitHubAccount = _settings.SharedProfiles?.GitHubAccount ?? "",
            };
            change(section);
            section.Email = section.Email.Trim();
            section.Name = section.Name.Trim();
            section.GitHubAccount = section.GitHubAccount.Trim();
            return WriteSharedSection(section.IsEmpty ? null : section);
        }

        private static bool WriteSharedSection(SharedProfilesSettings? section)
        {
            try
            {
                System.Text.Json.Nodes.JsonNode root = new System.Text.Json.Nodes.JsonObject();
                if (File.Exists(_settingsPath))
                    root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_settingsPath)) ?? root;
                if (root is not System.Text.Json.Nodes.JsonObject obj)
                    throw new InvalidOperationException("settings.json is not a JSON object");

                if (section == null) obj.Remove("SharedProfiles");
                else obj["SharedProfiles"] = System.Text.Json.JsonSerializer.SerializeToNode(section);

                var json = obj.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                if (!ibs_compiler_common.WriteAllTextAtomic(_settingsPath, json))
                    throw new IOException("the file could not be written");
                _settings.SharedProfiles = section;
                return true;
            }
            catch (Exception ex)
            {
                PrintError($"Could not save {_settingsPath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Print the connection status: git, publishing email and its source, GitHub
        /// account, a live access check and the local copy. True when the access check passed.
        /// </summary>
        private static bool PrintConnectionStatus()
        {
            var version = GitSharedProfileStore.GitVersion();
            PrintListField("Git:", version.Length > 0 ? version : "not installed", version.Length > 0 ? ConsoleColor.Gray : ConsoleColor.Yellow);

            var hasOwner = Store.TryResolveOwner(out var owner, out _);
            if (hasOwner)
                PrintListField("Publish as:", $"{owner}  (from {Store.OwnerSource})", ConsoleColor.Green);
            else
                PrintListField("Publish as:", "not set", ConsoleColor.Yellow);

            var pinned = Store.PinnedAccount;
            if (pinned.Length > 0)
                PrintListField("GitHub:", $"{pinned}  (set for Compilers)", ConsoleColor.Green);
            else
                PrintListField("GitHub:", (version.Length > 0 ? SignedInAccount() : null) ?? "no signed-in account found", ConsoleColor.Gray);

            var state = Store.CheckAccess(out var detail);
            var (text, color) = state switch
            {
                GitSharedProfileStore.AccessState.Ok => ("OK - can read the shared store", ConsoleColor.Green),
                GitSharedProfileStore.AccessState.GitMissing => ("git is not installed", ConsoleColor.Yellow),
                GitSharedProfileStore.AccessState.NotSignedIn => ("not signed in to GitHub", ConsoleColor.Yellow),
                GitSharedProfileStore.AccessState.NoAccess => ("no access - this account is not a collaborator on the repo", ConsoleColor.Yellow),
                _ => ("unreachable" + (detail.Length > 0 ? $": {detail}" : ""), ConsoleColor.Yellow),
            };
            PrintListField("Access:", text, color);

            PrintListField("Local copy:", Store.Location, ConsoleColor.Gray);
            var age = Store.CacheTimeUtc;
            PrintListField("Refreshed:", age.HasValue && Store.HasCache
                ? $"{Describe(DateTime.UtcNow - age.Value)} ago"
                : "no local copy yet", ConsoleColor.Gray);

            return state == GitSharedProfileStore.AccessState.Ok;
        }

        /// <summary><c>set_profile --shared-status</c> - the same status, no prompts. Exit 0 when access is OK.</summary>
        private static int SharedStatusHeadless()
        {
            Console.WriteLine();
            PrintSubheader("GitHub connection (shared profiles)");
            Console.WriteLine();
            return PrintConnectionStatus() ? 0 : 1;
        }
    }
}
