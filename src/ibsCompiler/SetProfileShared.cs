using System.Text.RegularExpressions;
using ibsCompiler.Configuration;

namespace ibsCompiler
{
    /// <summary>
    /// Shared profiles — the replacement for the old Unix <c>$SYBASE/interfaces</c>.
    /// <para>
    /// Publishing and syncing are both COPIES. A synced (or published) local profile
    /// remembers which record it came from, so the shared list can say "Update
    /// available" — but nothing here ever updates a local profile until the developer
    /// syncs it.
    /// </para>
    /// <para>
    /// Shared records are never merged into <c>settings.json</c> and never take part in
    /// profile or alias resolution — they are only visible through the commands below.
    /// </para>
    /// </summary>
    public static partial class set_profile_main
    {
        private static GitSharedProfileStore? _store;

        private static GitSharedProfileStore Store => _store ??= new GitSharedProfileStore(_settingsPath, () => _settings.SharedProfiles);

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
                if (!Store.TryResolveOwner(out var previewOwner, out var identityError))
                {
                    PrintError(identityError);
                    return 1;
                }
                try
                {
                    var preview = SharedProfileMap.Capture(name, profile, previewOwner, note);
                    Console.WriteLine(SharedProfileMap.SerializeForPublish(preview));
                    PrintDim("Dry run - nothing was published.");
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
            Console.WriteLine($"Sharing {name}...");
            if (!Guided(() => (Store.TryEnsureReady(out var ready), ready))) return false;

            var owner = "";
            if (!Guided(() => (Store.TryResolveOwner(out owner, out var identityError), identityError))) return false;
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
            if (!Guided(() => (Store.TryPublish(shared.Name, json, subject, out var err), err))) return false;

            // Stamp the local profile with what was just published, or the listing would
            // immediately report your own copy as older than the record you just wrote.
            if (string.Equals(name, shared.Name, StringComparison.OrdinalIgnoreCase))
            {
                profile.SharedFrom = shared.Name;
                profile.SharedUpdated = shared.Updated;
                SaveSettingsQuietly();
            }

            PrintSuccess($"Shared {shared.Name} as {owner}.");
            return true;
        }

        /// <summary>
        /// Persist settings without the "Settings saved to:" line. Used for bookkeeping
        /// writes the developer did not ask for and should not have to read about.
        /// </summary>
        private static void SaveSettingsQuietly()
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(_settings,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                ibs_compiler_common.WriteAllTextAtomic(_settingsPath, json);
            }
            catch { }
        }

        /// <summary><c>set_profile --unshare NAME</c> — remove your published copy.</summary>
        private static int UnshareHeadless(string rawName, List<string> args)
        {
            var name = rawName.Trim().ToUpperInvariant();
            if (!Guided(() => (Store.TryEnsureReady(out var ready), ready))) return 1;

            var existing = Store.Get(name);
            if (existing == null)
            {
                Console.Error.WriteLine($"ERROR: '{name}' is not in the shared store.");
                return 1;
            }

            if (!Store.TryResolveOwner(out var owner, out var identityError))
            {
                PrintError(identityError);
                return 1;
            }
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

            Console.WriteLine($"Removing {name}...");
            if (!Guided(() => (Store.TryWithdraw(name, $"Remove {name} ({owner})", out var err), err))) return 1;
            PrintSuccess($"Removed {name} from shared profiles.");
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
            SharedProfile shared, string? existingName, List<string> args, out string? error,
            bool interactive = false)
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

            // The menu knows it is driving a human; the flag path has to work out whether
            // anyone is there to answer. Asking the console was wrong for the menu case.
            if (!interactive && !CliArgs.IsInteractiveTty())
            {
                error = $"profile '{existingName}' already exists. Downloading onto it needs an explicit choice: " +
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
                PrintDim($"Unchanged: {string.Join(", ", same.Select(f => f.Label))}");

            if (differing.Count == 0)
            {
                Console.WriteLine();
                PrintSuccess("Every shared field already matches your profile. Nothing to merge.");
                return new List<ShareField>();
            }

            Console.WriteLine();
            Console.WriteLine($"{"Field",-12}{"Yours",-28}Shared");
            Console.WriteLine($"{new string('-', 11),-12}{new string('-', 27),-28}{new string('-', 27)}");
            foreach (var f in differing)
                Console.WriteLine($"{f.Label,-12}{Ellipsize(f.FromLocal(local), 26),-28}{Ellipsize(f.FromShared(shared), 26)}");

            Console.WriteLine();
            PrintDim("Answer per field. Enter keeps your value.");
            Console.WriteLine();

            var accepted = new List<ShareField>();
            foreach (var f in differing)
            {
                if (ConfirmYesNo($"Use the shared {f.Label} ({Ellipsize(f.FromShared(shared), 40)})?", defaultYes: false))
                    accepted.Add(f);
            }

            Console.WriteLine();
            if (accepted.Count == 0) PrintDim("Keeping every local value.");
            else PrintDim($"Overriding: {string.Join(", ", accepted.Select(f => f.Label))}");

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

            if (isNew)
                PrintSuccess($"Downloaded '{shared.Name}' into new profile '{target}'.");
            else if (accepted.Count == 0)
                PrintSuccess($"'{target}' left unchanged - no shared value was taken.");
            else
                PrintSuccess($"Merged into '{target}': {string.Join(", ", accepted.Select(f => f.Label))}.");
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
                    PrintDim($"SQL source taken from your other profiles: {suggestion}");
                    return true;
                }
                PrintWarning($"No SQL source set for '{target}'. Pass --sql-source PATH, or set it later with 'set_profile --edit {target} --sql-source PATH'.");
                return true;
            }

            Console.WriteLine();
            PrintDim("A shared profile never carries a SQL source — it is different on every machine.");
            while (true)
            {
                var prompt = string.IsNullOrEmpty(suggestion)
                    ? "  SQL source directory: "
                    : $"  SQL source directory [{suggestion}]: ";
                var entered = ReadLinePrompt(prompt);
                if (entered == null) return true;   // end of input - leave it unset

                if (entered.Length == 0) entered = suggestion ?? "";

                if (entered.Length == 0)
                {
                    PrintWarning("Skipped — set it later with 'set_profile --edit " + target + " --sql-source PATH'.");
                    return true;
                }
                if (entered is "." or "./" or ".\\") entered = Directory.GetCurrentDirectory();

                if (Directory.Exists(entered))
                {
                    profile.SqlSource = entered;
                    PrintSuccess($"Using: {entered}");
                    return true;
                }
                PrintWarning($"Path does not exist on this machine: {entered}");
                if (ConfirmYesNo("Use it anyway?", defaultYes: false))
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
                    PrintDim($"No password stored for '{target}' — you will be prompted on first use.");
                return true;
            }

            Console.WriteLine();
            PrintDim("Passwords are never shared. Enter yours for this server, or press Enter to be");
            PrintDim("prompted the first time you use the profile.");
            ConsoleMenu.WritePrompt("Password: ");
            var entered = ReadPassword();
            Console.WriteLine();
            if (string.IsNullOrEmpty(entered))
            {
                profile.Password = "";
                PrintDim("Skipped — you will be prompted on first use.");
                return true;
            }

            profile.Password = entered;
            if (ConfirmYesNo("Test the connection now?", defaultYes: true))
                TestConnection(profile);
            return true;
        }

        #endregion

        #region Listing

        private static bool EnsureStore(bool refresh)
        {
            if (refresh || !Store.HasCache)
                return Guided(() => (Store.TryRefresh(out var err), err));
            return Guided(() => (Store.TryEnsureReady(out var ready), ready));
        }

        private static void PrintSharedList(IReadOnlyList<SharedProfile> shared)
        {
            Console.WriteLine();
            PrintSubheader($"Shared Profiles ({shared.Count})");

            var age = Store.CacheTimeUtc;
            if (age.HasValue)
                PrintDim($"Cache refreshed {Describe(DateTime.UtcNow - age.Value)} ago — 'set_profile --shared --refresh' to update.");

            if (shared.Count == 0)
            {
                Console.WriteLine();
                PrintWarning("Nothing has been shared yet.");
                PrintDim("Publish one of yours with 'set_profile --share <NAME>'.");
                return;
            }

            var me = Store.TryResolveOwner(out var owner, out _) ? owner : null;
            Console.WriteLine();
            for (int i = 0; i < shared.Count; i++)
            {
                var s = shared[i];
                var row = ClassifyShared(s, me);
                Console.Write($"{i + 1,2}. ");
                WriteBright(s.Name);
                if (s.Aliases?.Count > 0) PrintInline($"  (aliases: {string.Join(", ", s.Aliases)})");
                PrintInline($"  shared by {(string.IsNullOrEmpty(s.Owner) ? "unknown" : s.Owner)}");
                Console.WriteLine();

                PrintListField("Platform:", s.Platform, ConsoleColor.Cyan);
                PrintListField("Server:", $"{s.Host}:{s.Port}", ConsoleColor.Green);
                PrintListField("Username:", s.Username);
                if (!string.IsNullOrEmpty(s.Database)) PrintListField("Database:", s.Database);
                PrintListField("Shared by:", string.IsNullOrEmpty(s.Owner) ? "unknown" : s.Owner);
                if (!string.IsNullOrEmpty(s.Note)) PrintListField("Note:", s.Note);

                if (row.LocalName != null) PrintListField("Local:", row.LocalName);
                PrintListField("Status:", StatusLabel(row), StatusColor(row.State));

                Console.WriteLine();
            }
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
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write(text);
            Console.ForegroundColor = prev;
        }

        /// <summary>
        /// Yes/no that works on a TTY and on redirected stdin alike (the suite drives
        /// these paths with piped input).
        /// </summary>
        /// <summary>
        /// Read a line after discarding anything already sitting in the key buffer.
        /// The main menu is a ReadKey loop and these prompts are ReadLine, so a
        /// keystroke left over from the menu would otherwise answer the next prompt
        /// before the user has seen it.
        /// </summary>
        /// <summary>
        /// Returns null at end of input - a piped run that has run out of answers must
        /// back out, not spin forever re-asking a question nobody is left to answer.
        /// </summary>
        private static string? ReadLinePrompt(string prompt)
        {
            if (!Console.IsInputRedirected)
            {
                try { while (Console.KeyAvailable) Console.ReadKey(intercept: true); } catch { }
            }
            ConsoleMenu.WritePrompt(prompt);
            var line = Console.ReadLine();
            return line?.Trim();
        }

        private static bool ConfirmYesNo(string question, bool defaultYes)
        {
            var answer = (ReadLinePrompt($"{question} [{(defaultYes ? "Y/n" : "y/N")}]: ") ?? "").ToLowerInvariant();
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
            // Only the menu walks a person through a denial; the headless flags just
            // report it (and the next command to run).
            _guideAccess = true;
            GitSharedProfileStore.AllowCredentialUi = CliArgs.IsInteractiveTty();
            try { SharedProfilesMenuLoop(); }
            finally { _guideAccess = false; GitSharedProfileStore.AllowCredentialUi = false; }
        }

        /// <summary>
        /// The numbered menu under the list. Each action works on the marked rows, or the
        /// highlighted one when nothing is marked. The sequential fallback prints the same
        /// menu and reads the same numbers.
        /// </summary>
        private static readonly (string Num, string Label)[] SharedMenu =
        {
            ("1", "Sync - share, push, pull or download, whatever each row needs"),
            ("2", "Unlink - keep a local copy that no longer syncs"),
            ("3", "Remove - withdraw your shared profile, keep it locally"),
            ("4", "Change GitHub account"),
            ("98", "Back"),
        };

        /// <summary>One line above the menu: what each row colour means.</summary>
        private static readonly (string Text, ConsoleColor Color)[] SharedLegend =
        {
            ("Cyan: local", ConsoleColor.Cyan),
            ("   ", ConsoleColor.Gray),
            ("White: shared, not on this machine", ConsoleColor.White),
            ("   ", ConsoleColor.Gray),
            ("Yellow: out of sync", ConsoleColor.Yellow),
        };

        /// <summary>
        /// One screen: every local profile and every shared record in one list, coloured
        /// by where it lives, and a numbered menu under it. Refreshed from the repo on
        /// entry; every action comes back to the list re-read, with the filter kept.
        /// </summary>
        private static void SharedProfilesMenuLoop()
        {
            // Set up before the list: git -> GitHub account -> access, each step printed as
            // it runs. A redirected console cannot be walked through it and only gets the reason.
            bool LoadTty()
            {
                Console.WriteLine();
                WriteBright("Shared profiles");
                Console.WriteLine();
                return SetupShared();
            }

            if (CliArgs.IsInteractiveTty())
            {
                if (!LoadTty()) return;
            }
            else while (true)
            {
                var hasOwner = Store.TryResolveOwner(out _, out var denied);
                if (hasOwner && Store.HasCache && GitSharedProfileStore.GitAvailable()) break;
                if (CliArgs.IsInteractiveTty())
                {
                    if (!SetupShared()) return;
                    continue;
                }
                if (!hasOwner)
                {
                    PrintWarning(denied);
                    return;
                }
                break;
            }

            Console.WriteLine();
            Console.WriteLine("Loading profiles...");
            if (!EnsureStore(refresh: true)) return;

            var tty = !(Console.IsInputRedirected || Console.IsOutputRedirected);
            var account = SignedInAccount() ?? "unknown";
            string? cursorKey = null;
            var filter = "";

            while (true)
            {
                var rows = BuildMergedRows();

                // The picker needs a TTY, a window that holds it, and at least one row.
                if (!tty || rows.Count == 0)
                {
                    if (!SharedSequential(rows)) return;
                    account = SignedInAccount() ?? "unknown";
                    continue;
                }

                var title = $"Profiles ({rows.Count})   GitHub account: {account}";
                var start = Math.Max(0, rows.FindIndex(r => r.Key == cursorKey));
                var result = ProfilePicker.PickWithMenu(title, BuildMergedPickerItems(rows), SharedMenu, start,
                    out var tooSmall, filter, SharedLegend);
                if (tooSmall)
                {
                    // Window cannot hold the picker — the numbered listing still works.
                    tty = false;
                    continue;
                }
                if (result == null || result.Choice == "98") return;   // Esc / Back - to the main menu
                filter = result.Filter;
                if (result.Cursor >= 0) cursorKey = rows[result.Cursor].Key;

                RunSharedChoice(result.Choice, result.Picked.Select(i => rows[i]).ToList());
                if (result.Choice == "4") account = SignedInAccount() ?? "unknown";
                PauseForList();

                // Back to the list re-read from the repo, with the same progress lines as on entry.
                Console.Clear();
                if (!LoadTty()) return;
                Console.WriteLine();
                Console.WriteLine("Loading profiles...");
                if (!EnsureStore(refresh: true)) return;
            }
        }

        /// <summary>
        /// Menu numbers 1-4, shared by the full-screen list and the sequential fallback.
        /// One result line per profile; a failed settings save stops the batch there.
        /// </summary>
        private static void RunSharedChoice(string choice, List<MergedRow> targets)
        {
            if (choice == "4") { ChangeGitHubAccount(); return; }

            Func<MergedRow, bool>? act = choice switch
            {
                "1" => SyncMerged,
                "2" => UnlinkMerged,
                "3" => RemoveMerged,
                _ => null,
            };
            if (act == null) return;

            Console.WriteLine();
            if (targets.Count == 0)
            {
                Console.WriteLine("Nothing selected.");
                return;
            }

            // Two local copies linked to one record are two rows but one record to withdraw.
            var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in targets)
            {
                if (choice == "3" && row.Record != null && !removed.Add(row.Record.Name)) continue;
                if (!act(row)) return;
            }
        }

        /// <summary>
        /// The redirected-console (and too-small window) form of the list: print it and
        /// the same numbered menu, read one choice, then ask which rows. Returns false
        /// when the person backs out or input ends.
        /// </summary>
        private static bool SharedSequential(List<MergedRow> rows)
        {
            PrintMergedList(rows);
            Console.WriteLine();
            foreach (var (text, color) in SharedLegend) WriteColor(text, color);
            Console.WriteLine();
            Console.WriteLine();
            foreach (var (num, label) in SharedMenu) PrintMenu(int.Parse(num), label);
            var input = ReadLinePrompt("\nChoice: ");
            if (input == null || input == "98") return false;   // end of input / Back
            if (input.Length == 0) return true;                  // empty choice does nothing

            switch (input)
            {
                case "4":
                    RunSharedChoice(input, new List<MergedRow>());
                    return true;
                case "1":
                case "2":
                case "3":
                {
                    if (rows.Count == 0) { PrintWarning("There are no profiles."); return true; }
                    var pick = ReadLinePrompt($"Which [1-{rows.Count}, several separated by spaces, Enter to cancel]: ");
                    if (string.IsNullOrEmpty(pick)) return true;
                    var picked = new List<MergedRow>();
                    foreach (var part in pick.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!int.TryParse(part, out var idx) || idx < 1 || idx > rows.Count)
                        {
                            PrintWarning("Invalid selection.");
                            return true;
                        }
                        if (!picked.Contains(rows[idx - 1])) picked.Add(rows[idx - 1]);
                    }
                    RunSharedChoice(input, picked);
                    return true;
                }
                default:
                    Console.WriteLine("Invalid selection.");
                    return true;
            }
        }

        /// <summary>Hold the action's output on screen until the list redraws over it.</summary>
        private static void PauseForList()
        {
            Console.WriteLine();
            ConsoleMenu.WritePrompt("Press any key to return to the list...");
            try { while (Console.KeyAvailable) Console.ReadKey(intercept: true); } catch { }
            Console.ReadKey(intercept: true);
            Console.WriteLine();
        }

        #endregion

        #region Merged list

        /// <summary>
        /// One row of the merged list: a local profile (cyan), or a shared record with no
        /// local copy linked to it (white). A linked local profile stands for its record,
        /// so a record appears once while linked and twice (local + shared) once unlinked.
        /// </summary>
        private sealed class MergedRow
        {
            /// <summary>The local profile; null for a shared record not on this machine.</summary>
            public string? LocalName;
            /// <summary>The record a local row is linked to, or the record of a shared row.</summary>
            public SharedProfile? Record;
            /// <summary>The record's owner is this machine's identity.</summary>
            public bool Mine;
            /// <summary>Linked, and the repo copy was published after this copy last synced.</summary>
            public bool RepoNewer;
            /// <summary>Your own linked record, edited here and not yet pushed.</summary>
            public bool LocalChanged;

            public bool IsLocal => LocalName != null;
            public bool Linked => LocalName != null && Record != null;
            public bool OutOfSync => RepoNewer || LocalChanged;
            public string Name => LocalName ?? Record!.Name;
            public string Key => LocalName != null ? "L:" + LocalName : "R:" + Record!.Name;
        }

        /// <summary>
        /// Every local profile, then every record no local profile is linked to, sorted
        /// by name with the local row first so a same-name pair sits together.
        /// </summary>
        private static List<MergedRow> BuildMergedRows()
        {
            var me = Store.TryResolveOwner(out var owner, out _) ? owner : null;
            var records = Store.List();
            bool IsMine(SharedProfile s) => me != null && string.Equals(s.Owner, me, StringComparison.OrdinalIgnoreCase);

            var rows = new List<MergedRow>();
            var linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, profile) in _settings.Profiles)
            {
                var row = new MergedRow { LocalName = name };
                // A link to a record that is gone (withdrawn) is no link at all.
                var record = string.IsNullOrEmpty(profile.SharedFrom)
                    ? null
                    : records.FirstOrDefault(s => string.Equals(s.Name, profile.SharedFrom, StringComparison.OrdinalIgnoreCase));
                if (record != null)
                {
                    row.Record = record;
                    row.Mine = IsMine(record);
                    row.RepoNewer = IsStale(profile, record);
                    // Someone else's copy differs only if they published again (RepoNewer);
                    // editing it here unlinks it. Your own can carry unpushed edits.
                    row.LocalChanged = row.Mine && DiffersFromRecord(profile, record);
                    linked.Add(record.Name);
                }
                rows.Add(row);
            }
            foreach (var s in records)
            {
                if (linked.Contains(s.Name)) continue;
                rows.Add(new MergedRow { Record = s, Mine = IsMine(s) });
            }

            return rows
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.IsLocal ? 0 : 1)
                .ToList();
        }

        private static ConsoleColor RowColor(MergedRow row) =>
            row.OutOfSync ? ConsoleColor.Yellow : row.IsLocal ? ConsoleColor.Cyan : ConsoleColor.White;

        private static string RowStatus(MergedRow row) =>
            !row.IsLocal ? "Shared"
            : row.Record == null ? "Local"
            : row.RepoNewer ? "Repo newer"
            : row.LocalChanged ? "Changed here"
            : row.Mine ? "Mine"
            : "Linked";

        private static string RowTag(MergedRow row)
        {
            if (row.Record == null) return "";
            var by = row.Mine ? "you" : string.IsNullOrEmpty(row.Record.Owner) ? "unknown" : row.Record.Owner;
            return row.IsLocal && !string.Equals(row.LocalName, row.Record.Name, StringComparison.OrdinalIgnoreCase)
                ? $"linked to {row.Record.Name}, shared by {by}"
                : $"shared by {by}";
        }

        /// <summary>
        /// The detail block: every shared field (the local value on a local row, the
        /// record's on a shared row). On a yellow row each field that differs from the
        /// repo is yellow and carries the repo value too. Passwords are not shared
        /// fields, so they never appear.
        /// </summary>
        private static List<(string Label, string Value, ConsoleColor Color)> MergedDetails(MergedRow row)
        {
            var details = new List<(string Label, string Value, ConsoleColor Color)>();
            var local = row.IsLocal ? _settings.Profiles[row.LocalName!] : null;
            var record = row.Record;

            foreach (var f in SharedProfileMap.Fields)
            {
                var mine = local != null ? f.FromLocal(local) : f.FromShared(record!);
                if (row.OutOfSync && record != null && local != null)
                {
                    var theirs = f.FromShared(record);
                    if (!string.Equals(mine, theirs, StringComparison.Ordinal))
                    {
                        details.Add((f.Label, $"{Ellipsize(mine, 30)} -> repo: {Ellipsize(theirs, 30)}", ConsoleColor.Yellow));
                        continue;
                    }
                }
                if (mine.Length == 0) continue;
                details.Add((f.Label, mine, ConsoleColor.Gray));
            }

            if (local != null && !local.RawMode && !string.IsNullOrEmpty(local.SqlSource))
                details.Add(("SQL Source", local.SqlSource, ConsoleColor.Gray));
            if (record != null)
            {
                details.Add(("Shared by", string.IsNullOrEmpty(record.Owner) ? "unknown" : record.Owner, ConsoleColor.Gray));
                if (!string.IsNullOrEmpty(record.Note)) details.Add(("Note", record.Note, ConsoleColor.Gray));
            }
            return details;
        }

        private static List<PickerItem> BuildMergedPickerItems(List<MergedRow> rows)
        {
            var items = new List<PickerItem>(rows.Count);
            foreach (var row in rows)
            {
                var local = row.IsLocal ? _settings.Profiles[row.LocalName!] : null;
                var s = row.Record;
                var aliases = local?.Aliases ?? s?.Aliases ?? new List<string>();
                var color = RowColor(row);
                var status = RowStatus(row);
                var haystack = string.Join(" ", new[]
                {
                    row.Name,
                    string.Join(" ", aliases),
                    local?.Platform ?? s?.Platform ?? "",
                    local?.Host ?? s?.Host ?? "",
                    (local?.Port ?? s?.Port ?? 0).ToString(),
                    local?.Username ?? s?.Username ?? "",
                    local?.Database ?? s?.Database ?? "",
                    s?.Owner ?? "",
                    s?.Note ?? "",
                    status,
                }).ToLowerInvariant();

                items.Add(new PickerItem
                {
                    Name = row.Name,
                    NameColor = color,
                    Aliases = aliases,
                    Tag = RowTag(row),
                    Status = status,
                    StatusColor = color,
                    Details = MergedDetails(row),
                    SearchText = haystack,
                });
            }
            return items;
        }

        /// <summary>The sequential form of the list: one numbered line per row, in its colour.</summary>
        private static void PrintMergedList(List<MergedRow> rows)
        {
            Console.WriteLine();
            PrintSubheader($"Profiles ({rows.Count})");
            if (rows.Count == 0)
            {
                Console.WriteLine("No local or shared profiles yet.");
                return;
            }
            var width = rows.Max(r => RowStatus(r).Length) + 2;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var color = RowColor(row);
                Console.Write($"{i + 1,2}. ");
                WriteColor(RowStatus(row).PadRight(width), color);
                WriteColor(row.Name, color);
                var tag = RowTag(row);
                if (tag.Length > 0) Console.Write($"  {tag}");
                Console.WriteLine();
            }
        }

        /// <summary>
        /// The local profile a shared record would land on: same name, or (when not
        /// itself a copy of another record) an alias equal to the record's name.
        /// </summary>
        private static string? SameNameLocal(SharedProfile s) =>
            _settings.Profiles.Keys.FirstOrDefault(k => string.Equals(k, s.Name, StringComparison.OrdinalIgnoreCase))
            ?? _settings.Profiles
                .Where(kvp => string.IsNullOrEmpty(kvp.Value.SharedFrom)
                              && kvp.Value.Aliases?.Any(a => string.Equals(a, s.Name, StringComparison.OrdinalIgnoreCase)) == true)
                .Select(kvp => kvp.Key)
                .FirstOrDefault();

        /// <summary>
        /// Sync does what the row implies: an unshared local profile is shared, your own
        /// linked one is pushed, a linked one the repo has updated is pulled, and a shared
        /// record not on this machine is downloaded. Returns false only when settings
        /// could not be saved - the batch stops there.
        /// </summary>
        private static bool SyncMerged(MergedRow row)
        {
            if (row.IsLocal)
            {
                var name = row.LocalName!;
                var profile = _settings.Profiles[name];
                if (row.Record == null) return ShareLocal(name, profile);
                if (row.LocalChanged && !row.RepoNewer)
                {
                    // Published under the record's name; PublishProfile restamps the link.
                    PublishProfile(row.Record.Name, profile, row.Record.Note ?? "");
                    return true;
                }
                Console.WriteLine($"Syncing {name}...");
                if (row.RepoNewer)
                {
                    if (!ApplyAndSave(row.Record, name)) return false;
                    PrintSuccess($"Synced {name} from {OwnerOf(row.Record)}.");
                    return true;
                }
                PrintSuccess($"{name} is already up to date.");
                return true;
            }

            var s = row.Record!;
            Console.WriteLine($"Syncing {s.Name}...");
            var existing = SameNameLocal(s);
            if (existing == null) return SyncNew(s);
            if (!ConfirmYesNo($"Replace your {existing} with the shared {s.Name} and link?", defaultYes: false))
            {
                PrintWarning($"{existing} skipped.");
                return true;
            }
            if (!ApplyAndSave(s, existing)) return false;
            PrintSuccess($"{existing} replaced with the shared {s.Name} and linked.");
            return true;
        }

        /// <summary>
        /// Share a local profile that is not linked to any record. A record of the same
        /// name that is someone else's is never overwritten; your own asks first.
        /// </summary>
        private static bool ShareLocal(string name, ProfileData profile)
        {
            var existing = Store.Get(name);
            var note = "";
            if (existing != null)
            {
                var me = Store.TryResolveOwner(out var owner, out _) ? owner : null;
                if (me == null || !string.Equals(existing.Owner, me, StringComparison.OrdinalIgnoreCase))
                {
                    var by = string.IsNullOrEmpty(existing.Owner) ? "someone else" : existing.Owner;
                    PrintWarning($"{name} skipped - {by} already shares a profile named {name}. Rename yours, or sync the shared {name} to take theirs.");
                    return true;
                }
                if (!ConfirmYesNo($"Replace your shared {name} with this one and link?", defaultYes: false))
                {
                    PrintWarning($"{name} skipped.");
                    return true;
                }
                note = existing.Note ?? "";
            }
            PublishProfile(name, profile, note);
            return true;
        }

        /// <summary>
        /// Unlink: a linked copy of someone else's record becomes a plain local profile
        /// (the record then shows as its own row); a shared record not on this machine is
        /// downloaded as a local copy that never syncs. Your own record is Remove's job.
        /// </summary>
        private static bool UnlinkMerged(MergedRow row)
        {
            if (row.IsLocal)
            {
                var name = row.LocalName!;
                if (row.Record == null)
                {
                    PrintWarning($"{name} is not linked to a shared profile.");
                    return true;
                }
                if (row.Mine)
                {
                    PrintWarning($"{name} is your own shared profile - use Remove to withdraw it and keep it locally.");
                    return true;
                }
                var profile = _settings.Profiles[name];
                var (from, updated) = (profile.SharedFrom, profile.SharedUpdated);
                Unlink(profile);
                if (!SaveSynced())
                {
                    profile.SharedFrom = from;
                    profile.SharedUpdated = updated;
                    return false;
                }
                PrintSuccess($"{name} unlinked - it is now a local copy that no longer syncs.");
                return true;
            }

            var s = row.Record!;
            var target = s.Name.ToUpperInvariant();
            var nameError = ValidateNewProfileName(target);
            while (nameError != null)
            {
                Console.WriteLine($"{target}: {nameError}");
                var entered = ReadLinePrompt($"Local name for the copy of {s.Name} [Enter to skip]: ");
                if (string.IsNullOrEmpty(entered))
                {
                    PrintWarning($"{s.Name} skipped.");
                    return true;
                }
                target = entered.ToUpperInvariant();
                nameError = ValidateNewProfileName(target);
            }
            return SyncNew(s, target, link: false);
        }

        /// <summary>
        /// Remove: withdraw your own record from the repo. Every local copy linked to it
        /// stays, unlinked. Anyone else's record gets one line.
        /// </summary>
        private static bool RemoveMerged(MergedRow row)
        {
            if (row.Record == null)
            {
                PrintWarning($"{row.Name} is not shared - nothing to remove.");
                return true;
            }
            var name = row.Record.Name;
            if (!row.Mine)
            {
                var by = string.IsNullOrEmpty(row.Record.Owner) ? "someone else" : row.Record.Owner;
                PrintWarning($"{name} is shared by {by} - you can only remove your own.");
                return true;
            }
            if (!ConfirmYesNo($"Remove {name} from shared profiles?", defaultYes: false))
            {
                PrintWarning($"{name} kept.");
                return true;
            }
            if (UnshareHeadless(name, new List<string>()) != 0) return true;

            // The record is gone, so nothing local can sync with it any more.
            var linked = _settings.Profiles.Values
                .Where(p => string.Equals(p.SharedFrom, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var p in linked) Unlink(p);
            return linked.Count == 0 || SaveSynced();
        }

        #endregion

        #region Sync

        // SyncState / SharedRow / ClassifyShared describe a record against this machine
        // for the headless '--shared' listing; the menu uses the merged list above.
        private enum SyncState { New, UpdateAvailable, NameClash, UpToDate, LocalChanges }

        /// <summary>A shared record and where it stands against this machine's profiles.</summary>
        private sealed class SharedRow
        {
            public SharedProfile Record = null!;
            public SyncState State;
            /// <summary>The record's owner is this machine's identity.</summary>
            public bool Mine;
            /// <summary>The local profile linked to the record, or the one clashing with it.</summary>
            public string? LocalName;
            /// <summary>Every local profile linked to the record (usually one).</summary>
            public List<string> Linked = new();
            /// <summary>Some linked copy - not necessarily <see cref="LocalName"/> - is older than the record.</summary>
            public bool AnyStale;
        }

        /// <summary>Any shared field of the local profile differs from the record.</summary>
        private static bool DiffersFromRecord(ProfileData local, SharedProfile s) =>
            SharedProfileMap.Fields.Any(f => !string.Equals(f.FromLocal(local), f.FromShared(s), StringComparison.Ordinal));

        private static bool IsStale(ProfileData local, SharedProfile s) =>
            !string.IsNullOrEmpty(s.Updated) && !string.Equals(local.SharedUpdated, s.Updated, StringComparison.Ordinal);

        private static ProfileData CloneProfile(ProfileData p) =>
            System.Text.Json.JsonSerializer.Deserialize<ProfileData>(System.Text.Json.JsonSerializer.Serialize(p))!;

        /// <summary>
        /// Linked (SharedFrom names the record) wins over a name match: a linked copy is
        /// up to date or has an update waiting. Unlinked, a local profile with the
        /// record's name - or an alias equal to it - is a clash; otherwise it is new.
        /// </summary>
        private static SharedRow ClassifyShared(SharedProfile s, string? me)
        {
            var row = new SharedRow
            {
                Record = s,
                Mine = me != null && string.Equals(s.Owner, me, StringComparison.OrdinalIgnoreCase),
            };

            row.Linked = _settings.Profiles
                .Where(kvp => string.Equals(kvp.Value.SharedFrom, s.Name, StringComparison.OrdinalIgnoreCase))
                .Select(kvp => kvp.Key)
                .ToList();
            if (row.Linked.Count > 0)
            {
                // Several copies can be linked; the row's state follows the one named
                // like the record, and AnyStale catches the others.
                row.LocalName = row.Linked.FirstOrDefault(k => string.Equals(k, s.Name, StringComparison.OrdinalIgnoreCase))
                                ?? row.Linked[0];
                // Two-way for your own record: the repo copy being newer wins (pull);
                // otherwise local edits to shared fields are what Sync would publish.
                var primary = _settings.Profiles[row.LocalName];
                row.State = IsStale(primary, s) ? SyncState.UpdateAvailable
                    : row.Mine && DiffersFromRecord(primary, s) ? SyncState.LocalChanges
                    : SyncState.UpToDate;
                row.AnyStale = row.Linked.Any(k => IsStale(_settings.Profiles[k], s));
                return row;
            }

            // An alias match only counts against a profile that is not already a copy of
            // some other record - replacing that would overwrite the wrong profile.
            row.LocalName = _settings.Profiles.Keys.FirstOrDefault(k => string.Equals(k, s.Name, StringComparison.OrdinalIgnoreCase))
                ?? _settings.Profiles
                    .Where(kvp => string.IsNullOrEmpty(kvp.Value.SharedFrom)
                                  && kvp.Value.Aliases?.Any(a => string.Equals(a, s.Name, StringComparison.OrdinalIgnoreCase)) == true)
                    .Select(kvp => kvp.Key)
                    .FirstOrDefault();
            row.State = row.LocalName != null ? SyncState.NameClash : SyncState.New;
            return row;
        }

        private static string StatusLabel(SharedRow row)
        {
            var label = row.State switch
            {
                SyncState.New => "New",
                SyncState.UpdateAvailable => "Update available",
                SyncState.NameClash => "Name clash",
                SyncState.LocalChanges => "Local changes",   // always yours; Sync will share them
                _ => "Up to date",
            };
            if (!row.Mine || row.State == SyncState.LocalChanges) return label;
            return row.State == SyncState.UpToDate ? "Mine" : "Mine, " + label.ToLowerInvariant();
        }

        private static ConsoleColor StatusColor(SyncState state) => state switch
        {
            SyncState.New => ConsoleColor.Green,
            SyncState.UpdateAvailable or SyncState.NameClash or SyncState.LocalChanges => ConsoleColor.Yellow,
            _ => ConsoleColor.Gray,
        };

        /// <summary>Shared fields over one local profile, saved; a failed save puts the profile back as it was.</summary>
        private static bool ApplyAndSave(SharedProfile s, string local)
        {
            var backup = CloneProfile(_settings.Profiles[local]);
            ApplySharedOver(s, local, _settings.Profiles[local]);
            if (SaveSynced()) return true;
            _settings.Profiles[local] = backup;
            return false;
        }

        /// <summary>
        /// Every shared field over the local profile, provenance restamped. Password and
        /// SQL source are not shared fields, so the local ones are always kept.
        /// </summary>
        private static void ApplySharedOver(SharedProfile shared, string localName, ProfileData profile)
        {
            foreach (var f in SharedProfileMap.Fields)
            {
                if (f.Key == "ALIASES") continue; // conflicts must be filtered
                f.Apply(profile, shared);
            }
            ApplyAliases(shared, localName, profile);
            profile.SharedFrom = shared.Name;
            profile.SharedUpdated = shared.Updated ?? "";
        }

        /// <summary>
        /// A record with no local counterpart becomes a new profile of the same name (or
        /// <paramref name="asName"/>), then asks for the two things a shared record never
        /// carries: SQL source and password. <paramref name="link"/> false = a local copy
        /// that never syncs (Unlink on a shared row).
        /// </summary>
        private static string OwnerOf(SharedProfile shared)
            => string.IsNullOrEmpty(shared.Owner) ? "shared profiles" : shared.Owner;

        private static bool SyncNew(SharedProfile shared, string? asName = null, bool link = true)
        {
            var name = (asName ?? shared.Name).ToUpperInvariant();
            var nameError = ValidateNewProfileName(name);
            if (nameError != null)
            {
                PrintWarning($"{name} skipped - {nameError}");
                return true;
            }

            Console.WriteLine();
            WriteBright(name);
            Console.WriteLine();

            // ProfileData's own default password is the legacy 'ibsibs' - a synced profile
            // starts with none and asks.
            var profile = new ProfileData { DefaultLanguage = "1", Password = "" };
            ApplySharedOver(shared, name, profile);
            if (!link) Unlink(profile);
            _settings.Profiles[name] = profile;

            var none = new List<string>();
            ResolveSqlSourceOnFetch(profile, name, none, isNew: true);
            ResolvePasswordOnFetch(profile, name, none);

            if (!SaveSynced())
            {
                _settings.Profiles.Remove(name);
                return false;
            }
            PrintSuccess(link ? $"Synced {name} from {OwnerOf(shared)}." : $"{name} added as a local copy that does not sync.");

            if (!profile.RawMode && !string.IsNullOrEmpty(profile.SqlSource) && Directory.Exists(profile.SqlSource))
                ibs_compiler_common.EnsureSymbolicLinks(profile.SqlSource);
            return true;
        }

        private static bool SaveSynced()
        {
            if (SaveSettings(quiet: true)) return true;
            PrintError($"Could not save settings: {_lastSaveError ?? "unknown error"}");
            return false;
        }

        /// <summary>
        /// Before a local profile is edited: when it is a synced copy of someone else's
        /// record (a record missing from the local copy of the store counts as someone
        /// else's), warn that saving a change cuts it loose. Returns true when it warned.
        /// Your own record warns nothing - you are the one who would re-share it.
        /// </summary>
        private static bool WarnIfSharedFromOther(string name, ProfileData profile)
        {
            var notice = SharedFromOtherNotice(name, profile);
            if (notice == null) return false;
            PrintWarning(notice);
            return true;
        }

        /// <summary>
        /// The warning text for <see cref="WarnIfSharedFromOther"/>, or null when the
        /// profile is not linked to someone else's record. The full-screen editor shows
        /// it on its own screen rather than above it, where it would scroll away.
        /// </summary>
        private static string? SharedFromOtherNotice(string name, ProfileData profile)
        {
            if (!IsSharedFromOther(profile, out var from)) return null;
            return $"{name} is a shared profile from {from}. Saving changes makes it a local copy that no longer syncs.";
        }

        /// <summary>Linked to a record whose owner is not this machine's identity (or that is gone from the local copy).</summary>
        private static bool IsSharedFromOther(ProfileData profile, out string from)
        {
            from = "";
            if (string.IsNullOrEmpty(profile.SharedFrom)) return false;

            SharedProfile? record = null;
            try { if (Store.HasCache) record = Store.Get(profile.SharedFrom); } catch { }
            var me = Store.TryResolveOwner(out var owner, out _) ? owner : null;
            if (record != null && me != null && string.Equals(record.Owner, me, StringComparison.OrdinalIgnoreCase))
                return false;

            from = string.IsNullOrEmpty(record?.Owner) ? "another developer" : record!.Owner;
            return true;
        }

        /// <summary>
        /// Whether a save changes anything the shared record carries. Password and SQL
        /// source are this machine's own and a sync always keeps them, so changing only
        /// those does not cut a profile loose.
        /// </summary>
        private static bool SharedFieldsDiffer(ProfileData a, ProfileData b) =>
            SharedProfileMap.Fields.Any(f => !string.Equals(f.FromLocal(a), f.FromLocal(b), StringComparison.Ordinal));

        /// <summary>Drop the link to a shared record: from here on it is a plain local profile.</summary>
        private static void Unlink(ProfileData profile)
        {
            profile.SharedFrom = "";
            profile.SharedUpdated = "";
        }

        #endregion
    }
}
