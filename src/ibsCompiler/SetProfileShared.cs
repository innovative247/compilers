using System.Text.RegularExpressions;
using ibsCompiler.Configuration;

namespace ibsCompiler
{
    /// <summary>
    /// Shared profiles — the replacement for the old Unix <c>$SYBASE/interfaces</c>.
    /// <para>
    /// Publishing and fetching are both one-way COPIES. Publishing does not tie your
    /// local profile to the shared record; fetching does not subscribe you to later
    /// changes. Nothing here ever auto-updates a local profile.
    /// </para>
    /// <para>
    /// Shared records are never merged into <c>settings.json</c> and never take part in
    /// profile or alias resolution — they are only visible through the commands below.
    /// </para>
    /// </summary>
    public static partial class set_profile_main
    {
        private static GitSharedProfileStore? _store;

        private static GitSharedProfileStore Store => _store ??= new GitSharedProfileStore(_settingsPath);

        #region Headless

        /// <summary>
        /// <c>set_profile --share NAME [--note "..."]</c> — publish or update the shared
        /// copy of one of your profiles.
        /// </summary>
        private static int ShareHeadless(string rawName, List<string> args)
        {
            var match = FindProfile(rawName.Trim().ToUpperInvariant());
            if (match == null)
            {
                Console.Error.WriteLine($"ERROR: profile '{rawName}' not found.");
                return 1;
            }
            var (name, profile) = match.Value;
            var note = CliArgs.GetOption(args, "--note") ?? "";

            // --dry-run prints exactly what WOULD be published and touches nothing:
            // "what am I about to share?", answered before you share it. Also how the
            // suite checks the payload without writing to the store.
            if (CliArgs.HasFlag(args, "--dry-run"))
            {
                try
                {
                    var preview = SharedProfileMap.Capture(name, profile, Store.ResolveOwner(), note);
                    Console.WriteLine(SharedProfileMap.SerializeForPublish(preview));
                    PrintDim("  Dry run - nothing was published.");
                    return 0;
                }
                catch (InvalidOperationException ex)
                {
                    PrintError(ex.Message);
                    return 1;
                }
            }

            return PublishProfile(name, profile, note) ? 0 : 1;
        }

        /// <summary>Shared by the headless flag and the interactive menu.</summary>
        private static bool PublishProfile(string name, ProfileData profile, string note)
        {
            if (!Store.TryEnsureReady(out var ready))
            {
                PrintError(ready);
                return false;
            }

            var owner = Store.ResolveOwner();
            var shared = SharedProfileMap.Capture(name, profile, owner, note);

            string json;
            try
            {
                json = SharedProfileMap.SerializeForPublish(shared);
            }
            catch (InvalidOperationException ex)
            {
                // A field that must never be shared reached the payload. Break the
                // publish loudly rather than quietly sanitizing it away.
                PrintError(ex.Message);
                return false;
            }

            var subject = $"{shared.Name}: {shared.Host}:{shared.Port} {shared.Platform} ({owner})";
            if (!Store.TryPublish(shared.Name, json, subject, out var err))
            {
                PrintError(err);
                return false;
            }

            PrintSuccess($"Shared '{shared.Name}' as {owner}.");
            PrintDim("  Password and SQL source were not published.");
            return true;
        }

        /// <summary><c>set_profile --unshare NAME</c> — remove your published copy.</summary>
        private static int UnshareHeadless(string rawName, List<string> args)
        {
            var name = rawName.Trim().ToUpperInvariant();
            if (!Store.TryEnsureReady(out var ready)) { PrintError(ready); return 1; }

            var existing = Store.Get(name);
            if (existing == null)
            {
                Console.Error.WriteLine($"ERROR: '{name}' is not in the shared store.");
                return 1;
            }

            var owner = Store.ResolveOwner();
            if (!string.Equals(existing.Owner, owner, StringComparison.OrdinalIgnoreCase))
            {
                // Allowed — push access is the gate — but never silent.
                PrintWarning($"'{name}' was published by {existing.Owner}, not you.");
                if (!CliArgs.HasFlag(args, "--yes") && !ConfirmYesNo($"Withdraw {existing.Owner}'s shared profile?", defaultYes: false))
                {
                    Console.WriteLine("Cancelled.");
                    return 1;
                }
            }

            if (!Store.TryWithdraw(name, $"Remove {name} ({owner})", out var err))
            {
                PrintError(err);
                return 1;
            }
            PrintSuccess($"'{name}' withdrawn from the shared store.");
            return 0;
        }

        /// <summary>
        /// <c>set_profile --shared [--refresh]</c> — list what other developers have
        /// published. This is the only thing that surfaces shared records; they are
        /// never mixed into the normal profile list.
        /// </summary>
        private static int SharedListHeadless(List<string> args)
        {
            var refresh = CliArgs.HasFlag(args, "--refresh");
            if (!EnsureStore(refresh)) return 1;

            var shared = Store.List();
            PrintSharedList(shared);
            return 0;
        }

        /// <summary>
        /// <c>set_profile --fetch NAME [--as LOCAL] ...</c> — copy a shared record into
        /// settings.json. Password and SQL source are resolved here, on arrival.
        /// </summary>
        private static int FetchHeadless(string rawName, List<string> args)
        {
            if (!EnsureStore(CliArgs.HasFlag(args, "--refresh"))) return 1;

            var sharedName = rawName.Trim().ToUpperInvariant();
            var shared = Store.Get(sharedName);
            if (shared == null)
            {
                Console.Error.WriteLine($"ERROR: '{sharedName}' is not in the shared store. Run 'set_profile --shared --refresh' to see what is.");
                return 1;
            }

            var target = (CliArgs.GetOption(args, "--as") ?? shared.Name).Trim().ToUpperInvariant();
            var existing = _settings.Profiles.Keys.FirstOrDefault(k => string.Equals(k, target, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                var nameError = ValidateNewProfileName(target);
                if (nameError != null)
                {
                    Console.Error.WriteLine($"ERROR: {nameError}");
                    return 1;
                }
            }

            // Which fields the developer is willing to take.
            var accepted = ResolveAcceptedFields(shared, existing, args, out var acceptError);
            if (acceptError != null)
            {
                Console.Error.WriteLine($"ERROR: {acceptError}");
                return 1;
            }
            if (accepted == null) { Console.WriteLine("Cancelled."); return 1; }

            return ApplyFetch(shared, target, existing, accepted, args) ? 0 : 1;
        }

        #endregion

        #region Fetch mechanics

        /// <summary>
        /// Decide which shared fields to copy. A brand-new profile takes everything.
        /// Fetching ONTO an existing profile never blind-overwrites: interactively it
        /// prompts per differing field with keep-local as the default, and headlessly it
        /// demands an explicit choice rather than assuming one.
        /// Returns null when the developer cancelled.
        /// </summary>
        private static List<ShareField>? ResolveAcceptedFields(
            SharedProfile shared, string? existingName, List<string> args, out string? error)
        {
            error = null;

            if (existingName == null)
                return SharedProfileMap.Fields.ToList();

            var local = _settings.Profiles[existingName];
            var differing = SharedProfileMap.Fields
                .Where(f => !string.Equals(f.FromShared(shared), f.FromLocal(local), StringComparison.Ordinal))
                .ToList();

            var acceptAll  = CliArgs.HasFlag(args, "--accept-all");
            var acceptNone = CliArgs.HasFlag(args, "--accept-none");
            var acceptList = CliArgs.GetMulti(args, "--accept");

            if (acceptAll && acceptNone)
            {
                error = "--accept-all and --accept-none are mutually exclusive.";
                return null;
            }
            if (acceptAll) return differing;
            if (acceptNone) return new List<ShareField>();

            if (acceptList.Count > 0)
            {
                var chosen = new List<ShareField>();
                foreach (var raw in acceptList)
                {
                    foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var field = SharedProfileMap.Field(part);
                        if (field == null)
                        {
                            error = $"unknown field '{part}' in --accept. Valid: {string.Join(", ", SharedProfileMap.Fields.Select(f => f.Key))}.";
                            return null;
                        }
                        if (!chosen.Contains(field)) chosen.Add(field);
                    }
                }
                return chosen;
            }

            if (!CliArgs.IsInteractiveTty())
            {
                error = $"profile '{existingName}' already exists. Fetching onto it needs an explicit choice: " +
                        "--accept-all, --accept-none, or --accept FIELD[,FIELD...].";
                return null;
            }

            return PromptFieldOverrides(shared, existingName, local, differing);
        }

        /// <summary>
        /// The per-field merge prompt. Default on every row is KEEP LOCAL — you opt in to
        /// each override rather than opting out. Password and SQL source never appear:
        /// they are not shared fields and a merge never touches them.
        /// </summary>
        private static List<ShareField>? PromptFieldOverrides(
            SharedProfile shared, string existingName, ProfileData local, List<ShareField> differing)
        {
            Console.WriteLine();
            PrintSubheader($"Merge shared '{shared.Name}' into your profile '{existingName}'");
            Console.WriteLine();

            var same = SharedProfileMap.Fields.Except(differing).ToList();
            if (same.Count > 0)
                PrintDim($"  Unchanged: {string.Join(", ", same.Select(f => f.Label))}");

            if (differing.Count == 0)
            {
                Console.WriteLine();
                PrintSuccess("Every shared field already matches your profile. Nothing to merge.");
                return new List<ShareField>();
            }

            Console.WriteLine();
            Console.WriteLine($"  {"Field",-12}{"Yours",-28}Shared");
            Console.WriteLine($"  {new string('-', 11),-12}{new string('-', 27),-28}{new string('-', 27)}");
            foreach (var f in differing)
                Console.WriteLine($"  {f.Label,-12}{Ellipsize(f.FromLocal(local), 26),-28}{Ellipsize(f.FromShared(shared), 26)}");

            Console.WriteLine();
            PrintDim("  Answer per field. Enter keeps your value.");
            Console.WriteLine();

            var accepted = new List<ShareField>();
            foreach (var f in differing)
            {
                if (ConfirmYesNo($"  Use the shared {f.Label} ({Ellipsize(f.FromShared(shared), 40)})?", defaultYes: false))
                    accepted.Add(f);
            }

            Console.WriteLine();
            if (accepted.Count == 0) PrintDim("  Keeping every local value.");
            else PrintDim($"  Overriding: {string.Join(", ", accepted.Select(f => f.Label))}");

            return accepted;
        }

        /// <summary>
        /// Write the fetched record into settings.json, then resolve the two things a
        /// shared record deliberately never carries: the password and the SQL source.
        /// </summary>
        private static bool ApplyFetch(
            SharedProfile shared, string target, string? existingName, List<ShareField> accepted, List<string> args)
        {
            var isNew = existingName == null;
            // A fetched profile starts with NO password. ProfileData's own default is the
            // legacy 'ibsibs', which would quietly hand every fetch a password nobody
            // chose and defeat the prompt-on-arrival contract.
            var profile = isNew
                ? new ProfileData { DefaultLanguage = "1", Password = "" }
                : _settings.Profiles[existingName!];

            foreach (var f in accepted)
            {
                if (f.Key == "ALIASES") continue; // handled below, conflicts must be filtered
                f.Apply(profile, shared);
            }

            if (accepted.Any(f => f.Key == "ALIASES"))
                ApplyAliases(shared, target, profile);

            // Provenance only — never a subscription. The listing uses these to say
            // "your copy is older", and nothing ever acts on that automatically.
            profile.SharedFrom = shared.Name;
            profile.SharedUpdated = shared.Updated ?? "";

            if (isNew) _settings.Profiles[target] = profile;

            if (!ResolveSqlSourceOnFetch(profile, target, args, isNew)) return false;
            if (!ResolvePasswordOnFetch(profile, target, args)) return false;

            if (!SaveSettings()) return false;

            PrintSuccess(isNew
                ? $"Fetched '{shared.Name}' into new profile '{target}'."
                : $"Merged shared '{shared.Name}' into '{target}'.");
            DisplayProfile(target, profile);

            if (!profile.RawMode && !string.IsNullOrEmpty(profile.SqlSource) && Directory.Exists(profile.SqlSource))
                ibs_compiler_common.EnsureSymbolicLinks(profile.SqlSource);

            return true;
        }

        /// <summary>
        /// Aliases arrive advisory. One that collides with a profile name or another
        /// profile's alias is dropped and reported — never silently reassigned, because
        /// a stolen alias silently reroutes every command that uses it.
        /// </summary>
        private static void ApplyAliases(SharedProfile shared, string target, ProfileData profile)
        {
            var incoming = (shared.Aliases ?? new List<string>())
                .Select(a => a.Trim().ToUpperInvariant())
                .Where(a => a.Length > 0)
                .ToList();

            var kept = new List<string>();
            foreach (var alias in incoming)
            {
                var conflict = ValidateAliasConflicts(target, new[] { alias });
                if (conflict != null) { PrintWarning($"Alias dropped — {conflict}"); continue; }
                if (ReservedNames.Contains(alias)) { PrintWarning($"Alias '{alias}' dropped — reserved command name."); continue; }
                kept.Add(alias);
            }
            profile.Aliases = kept;
        }

        /// <summary>
        /// SQL source is per-developer and never travels with a shared record, so it is
        /// resolved here and verified against THIS machine. A path that is merely
        /// inherited from another profile is still checked before it is accepted.
        /// </summary>
        private static bool ResolveSqlSourceOnFetch(ProfileData profile, string target, List<string> args, bool isNew)
        {
            if (profile.RawMode) return true;

            var flag = CliArgs.GetOption(args, "--sql-source");
            if (!string.IsNullOrEmpty(flag))
            {
                profile.SqlSource = flag.Trim();
                if (!Directory.Exists(profile.SqlSource))
                    PrintWarning($"SQL source does not exist on this machine: {profile.SqlSource}");
                return true;
            }

            // Merging into an existing profile leaves its own SQL source alone — it is
            // not a shared field and a merge must not touch it.
            if (!isNew && !string.IsNullOrEmpty(profile.SqlSource))
            {
                if (!Directory.Exists(profile.SqlSource))
                    PrintWarning($"Existing SQL source is missing: {profile.SqlSource}");
                return true;
            }

            var suggestion = SuggestSqlSource();

            if (!CliArgs.IsInteractiveTty())
            {
                if (!string.IsNullOrEmpty(suggestion) && Directory.Exists(suggestion))
                {
                    profile.SqlSource = suggestion;
                    PrintDim($"  SQL source taken from your other profiles: {suggestion}");
                    return true;
                }
                PrintWarning($"No SQL source set for '{target}'. Pass --sql-source PATH, or set it later with 'set_profile --edit {target} --sql-source PATH'.");
                return true;
            }

            Console.WriteLine();
            PrintDim("  A shared profile never carries a SQL source — it is different on every machine.");
            while (true)
            {
                var prompt = string.IsNullOrEmpty(suggestion)
                    ? "  SQL source directory: "
                    : $"  SQL source directory [{suggestion}]: ";
                Console.Write(prompt);
                var entered = Console.ReadLine()?.Trim() ?? "";
                if (entered.Length == 0) entered = suggestion ?? "";

                if (entered.Length == 0)
                {
                    PrintWarning("  Skipped — set it later with 'set_profile --edit " + target + " --sql-source PATH'.");
                    return true;
                }
                if (entered is "." or "./" or ".\\") entered = Directory.GetCurrentDirectory();

                if (Directory.Exists(entered))
                {
                    profile.SqlSource = entered;
                    PrintSuccess($"  Using: {entered}");
                    return true;
                }
                PrintWarning($"  Path does not exist on this machine: {entered}");
                if (ConfirmYesNo("  Use it anyway?", defaultYes: false))
                {
                    profile.SqlSource = entered;
                    return true;
                }
            }
        }

        /// <summary>The SQL source most of your existing profiles already point at.</summary>
        private static string? SuggestSqlSource()
        {
            return _settings.Profiles.Values
                .Select(p => p.SqlSource)
                .Where(s => !string.IsNullOrEmpty(s) && Directory.Exists(s))
                .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();
        }

        /// <summary>
        /// The password never arrives with a shared record. Ask for it now, offer to
        /// verify it against the server before saving, and accept "skip" — a profile
        /// with no password simply prompts on first connect.
        /// </summary>
        private static bool ResolvePasswordOnFetch(ProfileData profile, string target, List<string> args)
        {
            var flag = CliArgs.GetOption(args, "--password");
            if (flag != null) { profile.Password = flag; return true; }
            if (CliArgs.HasFlag(args, "--no-password")) { profile.Password = ""; return true; }

            if (!CliArgs.IsInteractiveTty())
            {
                if (string.IsNullOrEmpty(profile.Password))
                    PrintDim($"  No password stored for '{target}' — you will be prompted on first use.");
                return true;
            }

            Console.WriteLine();
            PrintDim("  Passwords are never shared. Enter yours for this server, or press Enter to be");
            PrintDim("  prompted the first time you use the profile.");
            Console.Write("  Password: ");
            var entered = ReadPassword();
            Console.WriteLine();
            if (string.IsNullOrEmpty(entered))
            {
                profile.Password = "";
                PrintDim("  Skipped — you will be prompted on first use.");
                return true;
            }

            profile.Password = entered;
            if (ConfirmYesNo("  Test the connection now?", defaultYes: true))
                TestConnection(profile, allowCredentialRetry: false);
            return true;
        }

        #endregion

        #region Listing

        private static bool EnsureStore(bool refresh)
        {
            if (refresh || !Store.HasCache)
            {
                if (!Store.TryRefresh(out var err)) { PrintError(err); return false; }
                return true;
            }
            if (!Store.TryEnsureReady(out var ready)) { PrintError(ready); return false; }
            return true;
        }

        private static void PrintSharedList(IReadOnlyList<SharedProfile> shared)
        {
            Console.WriteLine();
            PrintSubheader($"Shared Profiles ({shared.Count})");

            var age = Store.CacheTimeUtc;
            if (age.HasValue)
                PrintDim($"  Cache refreshed {Describe(DateTime.UtcNow - age.Value)} ago — 'set_profile --shared --refresh' to update.");

            if (shared.Count == 0)
            {
                Console.WriteLine();
                PrintWarning("Nothing has been shared yet.");
                PrintDim("  Publish one of yours with 'set_profile --share <NAME>'.");
                return;
            }

            Console.WriteLine();
            for (int i = 0; i < shared.Count; i++)
            {
                var s = shared[i];
                Console.Write($"  {i + 1,2}. ");
                WriteBright(s.Name);
                if (s.Aliases?.Count > 0) PrintInline($"  (aliases: {string.Join(", ", s.Aliases)})");
                Console.WriteLine();

                PrintListField("Platform:", s.Platform, ConsoleColor.Cyan);
                PrintListField("Server:", $"{s.Host}:{s.Port}", ConsoleColor.Green);
                PrintListField("Username:", s.Username);
                if (!string.IsNullOrEmpty(s.Database)) PrintListField("Database:", s.Database);
                PrintListField("Shared by:", string.IsNullOrEmpty(s.Owner) ? "unknown" : s.Owner);
                if (!string.IsNullOrEmpty(s.Note)) PrintListField("Note:", s.Note);

                var status = LocalCopyStatus(s);
                if (status != null) PrintListField("Local copy:", status, ConsoleColor.Yellow);

                Console.WriteLine();
            }

            PrintDim("  Fetch one with 'set_profile --fetch <NAME>'. Fetching is a copy — it never");
            PrintDim("  follows later changes, and your local edits never reach the shared record.");
        }

        /// <summary>
        /// Provenance readout, not a sync prompt. Says whether you hold a copy and
        /// whether the shared record has moved on since you took it.
        /// </summary>
        private static string? LocalCopyStatus(SharedProfile shared)
        {
            var mine = _settings.Profiles
                .FirstOrDefault(kvp => string.Equals(kvp.Value.SharedFrom, shared.Name, StringComparison.OrdinalIgnoreCase));
            if (mine.Key == null)
            {
                return _settings.Profiles.Keys.Any(k => string.Equals(k, shared.Name, StringComparison.OrdinalIgnoreCase))
                    ? $"you have a local '{shared.Name}' (not fetched from here)"
                    : null;
            }

            var stale = !string.IsNullOrEmpty(shared.Updated)
                        && !string.Equals(mine.Value.SharedUpdated, shared.Updated, StringComparison.Ordinal);
            return stale
                ? $"{mine.Key} — yours is older than the shared copy"
                : $"{mine.Key} — up to date";
        }

        private static string Describe(TimeSpan span)
        {
            if (span.TotalMinutes < 1) return "less than a minute";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} minute{((int)span.TotalMinutes == 1 ? "" : "s")}";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours} hour{((int)span.TotalHours == 1 ? "" : "s")}";
            return $"{(int)span.TotalDays} day{((int)span.TotalDays == 1 ? "" : "s")}";
        }

        private static string Ellipsize(string value, int width)
        {
            value ??= "";
            if (value.Length == 0) return "(none)";
            return value.Length <= width ? value : value.Substring(0, width - 1) + "…";
        }

        private static void PrintInline(string text)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(text);
            Console.ForegroundColor = prev;
        }

        /// <summary>
        /// Yes/no that works on a TTY and on redirected stdin alike (the suite drives
        /// these paths with piped input).
        /// </summary>
        private static bool ConfirmYesNo(string question, bool defaultYes)
        {
            Console.Write($"{question} [{(defaultYes ? "Y/n" : "y/N")}]: ");
            var answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (answer.Length == 0) return defaultYes;
            return answer is "y" or "yes";
        }

        #endregion

        #region Interactive menu

        /// <summary>
        /// Main-menu option 5. Shared records are reachable from here and nowhere else.
        /// </summary>
        private static void SharedProfilesMenu()
        {
            while (true)
            {
                Console.WriteLine();
                WriteBright("Shared Profiles");
                Console.WriteLine();
                PrintDim("  Connection details other developers have published. Passwords and SQL");
                PrintDim("  source paths are never shared.");
                Console.WriteLine();
                PrintMenu(1, "List shared profiles");
                PrintMenu(2, "Refresh from the shared store");
                PrintMenu(3, "Fetch a shared profile");
                PrintMenu(4, "Share one of my profiles");
                PrintMenu(5, "Withdraw a shared profile");
                PrintMenu(99, "Back");

                Console.Write("\nChoose [1-5]: ");
                var input = Console.ReadLine()?.Trim();

                switch (input)
                {
                    case "1":
                        if (EnsureStore(refresh: false)) PrintSharedList(Store.List());
                        break;
                    case "2":
                        if (EnsureStore(refresh: true)) { PrintSuccess("Refreshed."); PrintSharedList(Store.List()); }
                        break;
                    case "3": FetchInteractive(); break;
                    case "4": ShareInteractive(); break;
                    case "5": WithdrawInteractive(); break;
                    case "99": return;
                    default: Console.WriteLine("Invalid selection."); break;
                }
            }
        }

        private static void FetchInteractive()
        {
            if (!EnsureStore(refresh: false)) return;
            var shared = Store.List();
            if (shared.Count == 0) { PrintWarning("Nothing has been shared yet."); return; }

            PrintSharedList(shared);
            Console.Write($"\nFetch which profile [1-{shared.Count}, Enter to cancel]: ");
            var pick = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(pick)) return;
            if (!int.TryParse(pick, out var idx) || idx < 1 || idx > shared.Count)
            {
                PrintWarning("Invalid selection.");
                return;
            }

            var chosen = shared[idx - 1];
            var target = chosen.Name;

            var existing = _settings.Profiles.Keys.FirstOrDefault(k => string.Equals(k, target, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                PrintWarning($"You already have a profile called '{existing}'.");
                Console.Write($"  Fetch into a different name instead [Enter to merge into {existing}]: ");
                var alt = (Console.ReadLine() ?? "").Trim().ToUpperInvariant();
                if (alt.Length > 0)
                {
                    var err = ValidateNewProfileName(alt);
                    if (err != null) { PrintError(err); return; }
                    target = alt;
                    existing = null;
                }
            }

            var args = new List<string>();
            var accepted = ResolveAcceptedFields(chosen, existing, args, out var acceptError);
            if (acceptError != null) { PrintError(acceptError); return; }
            if (accepted == null) { Console.WriteLine("Cancelled."); return; }

            ApplyFetch(chosen, target, existing, accepted, args);
        }

        private static void ShareInteractive()
        {
            var names = ListProfiles();
            if (names.Count == 0) return;

            Console.Write($"\nShare which profile [1-{names.Count}, Enter to cancel]: ");
            var pick = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(pick)) return;
            if (!int.TryParse(pick, out var idx) || idx < 1 || idx > names.Count)
            {
                PrintWarning("Invalid selection.");
                return;
            }

            var name = names[idx - 1];
            var profile = _settings.Profiles[name];

            Console.WriteLine();
            PrintDim("  These fields will be published:");
            foreach (var f in SharedProfileMap.Fields)
                PrintDim($"    {f.Label,-12}{Ellipsize(f.FromLocal(profile), 40)}");
            PrintDim("  Password and SQL source will NOT be published.");
            Console.WriteLine();

            Console.Write("  Note (optional, e.g. 'lab box, rebuilt nightly'): ");
            var note = (Console.ReadLine() ?? "").Trim();

            if (!ConfirmYesNo($"  Publish '{name}' to the shared store?", defaultYes: true))
            {
                Console.WriteLine("Cancelled.");
                return;
            }

            PublishProfile(name, profile, note);
        }

        private static void WithdrawInteractive()
        {
            if (!EnsureStore(refresh: false)) return;
            var shared = Store.List();
            if (shared.Count == 0) { PrintWarning("Nothing has been shared yet."); return; }

            PrintSharedList(shared);
            Console.Write($"\nWithdraw which profile [1-{shared.Count}, Enter to cancel]: ");
            var pick = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(pick)) return;
            if (!int.TryParse(pick, out var idx) || idx < 1 || idx > shared.Count)
            {
                PrintWarning("Invalid selection.");
                return;
            }

            UnshareHeadless(shared[idx - 1].Name, new List<string>());
        }

        #endregion
    }
}
