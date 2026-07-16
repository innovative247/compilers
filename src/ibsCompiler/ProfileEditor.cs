using System.Text;
using System.Text.Json;
using ibsCompiler.Configuration;

namespace ibsCompiler
{
    /// <summary>
    /// Whole-profile interactive TUI editor. Every field renders as a label:value
    /// row and stays visible at all times; Up/Down moves, Enter edits in place,
    /// S saves, T opens the test chooser, Esc cancels (confirming a discard when
    /// dirty). Fields that don't apply to the current profile (Company / SQL Source
    /// under raw mode, Database off PostgreSQL) render a dim <c>---</c> and reject
    /// edits with a one-line note. Headless flags retain full parity — this path is
    /// used only when the console is a real TTY (not redirected); the caller falls
    /// back to the sequential prompt flow otherwise.
    /// </summary>
    internal static class ProfileEditor
    {
        private enum FieldKind { Text, Enum, Bool, Int, Password, Path, AliasList }

        private sealed class Field
        {
            public string Label = "";
            public FieldKind Kind;
            /// Underlying canonical string, used for dirty comparison.
            public Func<ProfileData, string> Raw = _ => "";
            /// What is drawn in the value column.
            public Func<ProfileData, string> Display = _ => "";
            /// Commit a validated text value (Text/Int/Path/AliasList only).
            public Action<ProfileData, string> Set = (_, __) => { };
            /// When present and false, the field is inapplicable: it renders a dim
            /// <c>---</c>, is skipped by validation, and rejects edits with <see cref="DisabledNote"/>.
            public Func<ProfileData, bool>? Applicable;
            /// One-line reason shown when the user tries to edit an inapplicable field.
            public string? DisabledNote;
            /// Returns an error string when the input is invalid, else null.
            public Func<ProfileData, string, string?>? Validate;
            /// Grayed resolved-default hint drawn after the value.
            public Func<ProfileData, string>? Hint;
        }

        private static Field[] BuildFields(
            string profileName, Func<string, IEnumerable<string>, string?>? validateAliases)
        {
            return new[]
            {
                new Field
                {
                    Label = "Aliases", Kind = FieldKind.AliasList,
                    Raw = p => string.Join(",", p.Aliases ?? new List<string>()),
                    Display = p => (p.Aliases == null || p.Aliases.Count == 0)
                        ? "(none)" : string.Join(", ", p.Aliases),
                    Set = (p, v) => p.Aliases = v
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(a => a.ToUpperInvariant()).Distinct().ToList(),
                    // Same routing-safety rule the sequential/headless paths enforce:
                    // a saved alias may not collide with another profile's name/alias.
                    Validate = validateAliases == null ? null : (_, v) =>
                        validateAliases(profileName, v
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(a => a.ToUpperInvariant()).Distinct().ToList()),
                },
                new Field
                {
                    Label = "Raw Mode", Kind = FieldKind.Bool,
                    Raw = p => p.RawMode ? "true" : "false",
                    Display = p => p.RawMode ? "yes" : "no",
                },
                new Field
                {
                    Label = "Platform", Kind = FieldKind.Enum,
                    Raw = p => p.Platform ?? "",
                    // Values are ALWAYS the uppercase canonical token (SYBASE/MSSQL/POSTGRES).
                    Display = p => ibs_compiler_common.CanonicalName(ibs_compiler_common.ParsePlatform(p.Platform)),
                },
                new Field
                {
                    Label = "Database", Kind = FieldKind.Text,
                    Applicable = p => ibs_compiler_common.ParsePlatform(p.Platform) == SQLServerTypes.POSTGRES,
                    DisabledNote = "not applicable unless Platform is POSTGRES",
                    Raw = p => p.Database ?? "",
                    Display = p => string.IsNullOrEmpty(p.Database) ? "(none)" : p.Database,
                    Set = (p, v) => p.Database = v.Trim(),
                },
                new Field
                {
                    Label = "Host", Kind = FieldKind.Text,
                    Raw = p => p.Host ?? "",
                    Display = p => string.IsNullOrEmpty(p.Host) ? "(unset)" : p.Host,
                    Set = (p, v) => p.Host = v.Trim(),
                    Validate = (_, v) => string.IsNullOrWhiteSpace(v) ? "Host is required." : null,
                },
                new Field
                {
                    Label = "Port", Kind = FieldKind.Int,
                    Raw = p => p.Port.ToString(),
                    Display = p => p.Port.ToString(),
                    Set = (p, v) => p.Port = int.Parse(v.Trim()),
                    Validate = (_, v) =>
                    {
                        if (string.IsNullOrWhiteSpace(v)) return null; // empty keeps the resolved default
                        return int.TryParse(v.Trim(), out var n) && n > 0 ? null : "Port must be a positive number.";
                    },
                    Hint = p => $"default {ibs_compiler_common.DefaultPort(ibs_compiler_common.ParsePlatform(p.Platform))}",
                },
                new Field
                {
                    Label = "Username", Kind = FieldKind.Text,
                    Raw = p => p.Username ?? "",
                    Display = p => string.IsNullOrEmpty(p.Username) ? "(unset)" : p.Username,
                    Set = (p, v) => p.Username = v.Trim(),
                    Validate = (_, v) => string.IsNullOrWhiteSpace(v) ? "Username is required." : null,
                },
                new Field
                {
                    Label = "Password", Kind = FieldKind.Password,
                    Raw = p => p.Password ?? "",
                    Display = p => string.IsNullOrEmpty(p.Password) ? "(unset)" : "****",
                },
                new Field
                {
                    Label = "Company", Kind = FieldKind.Text,
                    Applicable = p => !p.RawMode,
                    DisabledNote = "not applicable in raw mode",
                    Raw = p => p.Company ?? "",
                    Display = p => string.IsNullOrEmpty(p.Company) ? "(unset)" : p.Company,
                    Set = (p, v) => p.Company = v.Trim(),
                },
                new Field
                {
                    Label = "Default Language", Kind = FieldKind.Text,
                    Raw = p => p.DefaultLanguage ?? "",
                    Display = p => string.IsNullOrEmpty(p.DefaultLanguage) ? "1" : p.DefaultLanguage,
                    Set = (p, v) => p.DefaultLanguage = string.IsNullOrWhiteSpace(v) ? "1" : v.Trim(),
                },
                new Field
                {
                    Label = "Data Charset", Kind = FieldKind.Text,
                    Raw = p => p.DataCharset ?? "",
                    Display = p => string.IsNullOrEmpty(p.DataCharset) ? "(server default)" : p.DataCharset,
                    Set = (p, v) => p.DataCharset = v.Trim(),
                },
                new Field
                {
                    Label = "SQL Source", Kind = FieldKind.Path,
                    Applicable = p => !p.RawMode,
                    DisabledNote = "not applicable in raw mode",
                    Raw = p => p.SqlSource ?? "",
                    Display = p => string.IsNullOrEmpty(p.SqlSource) ? "(unset)" : p.SqlSource,
                    Set = (p, v) =>
                    {
                        var t = v.Trim();
                        p.SqlSource = (t == "." || t == "./" || t == ".\\") ? Directory.GetCurrentDirectory() : t;
                    },
                },
            };
        }

        /// <summary>
        /// Edits <paramref name="profile"/> in place (caller passes a working copy).
        /// <paramref name="onTest"/> runs a named test (the --what vocabulary) against
        /// the working copy so unsaved edits are what get tested. Returns true when the
        /// user saved, false when cancelled, null when the terminal is too small to host
        /// the widget (caller must fall back to the sequential prompt flow).
        /// </summary>
        public static bool? Edit(string profileName, ProfileData profile, bool isCreate,
            Action<ProfileData, string>? onTest,
            Func<string, IEnumerable<string>, string?>? validateAliases = null)
        {
            // Belt-and-suspenders: never drive a ReadKey loop on a redirected console.
            // The caller already routes those to the sequential/headless paths.
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
                return false;

            var fields = BuildFields(profileName, validateAliases);

            // Layout needs: 1 blank + title + 1 blank + N field rows + 1 blank +
            // footer + message row = fields.Length + 5 rows, minimum. Width must also
            // clear the widest fixed-column cursor moves (Discard prompt lands at 25).
            if (Console.WindowHeight < fields.Length + 5 || Console.WindowWidth < 40)
            {
                Console.WriteLine();
                Console.WriteLine("  Terminal too small for the profile editor — using sequential prompts.");
                return null;
            }
            var snapshot = JsonSerializer.Deserialize<ProfileData>(JsonSerializer.Serialize(profile))!;

            var title = (isCreate ? "Create Profile: " : "Edit Profile: ") + profileName;
            const string footer = "  [Up/Down] move  [Enter] edit  [S] save  [T] test  [Esc] cancel";

            int startRow = 0;

            void Scaffold()
            {
                Console.WriteLine();
                var prev = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("  " + title);
                Console.ForegroundColor = prev;
                Console.WriteLine();
                for (int i = 0; i < fields.Length; i++) Console.WriteLine();
                Console.WriteLine();
                Console.Write(footer);
                // Reserve the message row below the footer explicitly: WriteLine (unlike
                // SetCursorPosition) auto-scrolls the buffer when it is written at the
                // bottom of the window, so this row is guaranteed to exist afterward.
                Console.WriteLine();
                int messageRow = Console.CursorTop;
                startRow = messageRow - fields.Length - 2;
            }

            bool IsApplicable(int fieldIdx)
                => fields[fieldIdx].Applicable == null || fields[fieldIdx].Applicable!(profile);

            int cursor = 0; // field index — every field is navigable, even disabled ones.

            void DrawRow(int fieldIdx, bool isCursor)
            {
                var f = fields[fieldIdx];
                bool applicable = IsApplicable(fieldIdx);
                Console.SetCursorPosition(0, startRow + fieldIdx);
                var pointer = isCursor ? ">" : " ";
                string line;
                if (!applicable)
                {
                    // Inapplicable field: always visible, but shown as a dim placeholder
                    // and non-editable. Keeps the layout stable instead of blanking rows.
                    line = $"  {pointer} {f.Label,-16}: ---";
                }
                else
                {
                    var dirty = f.Raw(profile) != f.Raw(snapshot) ? " *" : "";
                    var hint = f.Hint != null ? $"  ({f.Hint(profile)})" : "";
                    line = $"  {pointer} {f.Label,-16}: {f.Display(profile)}{dirty}{hint}";
                }
                if (line.Length < Console.WindowWidth - 1) line = line.PadRight(Console.WindowWidth - 1);
                else line = line.Substring(0, Console.WindowWidth - 1);
                if (!applicable)
                {
                    var prev = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write(line);
                    Console.ForegroundColor = prev;
                }
                else
                {
                    Console.Write(line);
                }
            }

            void Render()
            {
                if (cursor >= fields.Length) cursor = fields.Length - 1;
                if (cursor < 0) cursor = 0;
                for (int i = 0; i < fields.Length; i++)
                    DrawRow(i, i == cursor);
                Console.SetCursorPosition(0, startRow + cursor);
            }

            // A transient message line just below the footer.
            void Message(string text, ConsoleColor color)
            {
                Console.SetCursorPosition(0, startRow + fields.Length + 2);
                var prev = Console.ForegroundColor;
                Console.ForegroundColor = color;
                var line = "  " + text;
                if (line.Length < Console.WindowWidth - 1) line = line.PadRight(Console.WindowWidth - 1);
                Console.Write(line);
                Console.ForegroundColor = prev;
            }

            void ClearMessage()
            {
                Console.SetCursorPosition(0, startRow + fields.Length + 2);
                Console.Write(new string(' ', Console.WindowWidth - 1));
            }

            // In-place single-line editor seeded with the current value.
            string? InlineEdit(int fieldIdx, string seed)
            {
                var f = fields[fieldIdx];
                var buf = new StringBuilder(seed);
                int row = startRow + fieldIdx;
                int labelCol = 2 + 2 + 16 + 2; // "  " + pointer+space + label(-16) + ": "

                void Draw()
                {
                    Console.SetCursorPosition(0, row);
                    var text = $"  > {f.Label,-16}: {buf}";
                    if (text.Length < Console.WindowWidth - 1) text = text.PadRight(Console.WindowWidth - 1);
                    else text = text.Substring(0, Console.WindowWidth - 1);
                    Console.Write(text);
                    Console.SetCursorPosition(Math.Min(labelCol + buf.Length, Console.WindowWidth - 1), row);
                }

                Console.CursorVisible = true;
                Draw();
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Enter) { Console.CursorVisible = false; return buf.ToString(); }
                    if (key.Key == ConsoleKey.Escape) { Console.CursorVisible = false; return null; }
                    if (key.Key == ConsoleKey.Backspace)
                    {
                        if (buf.Length > 0) buf.Remove(buf.Length - 1, 1);
                    }
                    else if (!char.IsControl(key.KeyChar))
                    {
                        buf.Append(key.KeyChar);
                    }
                    Draw();
                }
            }

            // Stash Company/SqlSource when Raw Mode blanks them so toggling Raw back
            // off round-trips to the original values instead of losing them.
            string? stashedCompany = null;
            string? stashedSqlSource = null;
            void ApplyRawSideEffects()
            {
                if (profile.RawMode)
                {
                    stashedCompany = profile.Company;
                    stashedSqlSource = profile.SqlSource;
                    profile.Company = "0";
                    profile.SqlSource = "";
                }
                else
                {
                    profile.Company = stashedCompany ?? snapshot.Company;
                    profile.SqlSource = stashedSqlSource ?? snapshot.SqlSource;
                }
            }

            // Suspend the TUI, let scrolling test output print, wait for a key, then
            // re-scaffold and redraw the editor intact. Shared by the [T] chooser.
            void RunTestSuspended(string kind)
            {
                Console.CursorVisible = true;
                // Land on the message row (guaranteed to exist — see Scaffold), then
                // WriteLine from there so the buffer scrolls as needed instead of
                // SetCursorPosition-ing to a row that may not exist yet.
                Console.SetCursorPosition(0, startRow + fields.Length + 2);
                Console.WriteLine();
                Console.WriteLine();
                onTest!(profile, kind);
                Console.WriteLine();
                Console.Write("  Press any key to return to the editor...");
                Console.ReadKey(intercept: true);
                Console.CursorVisible = false;
                Scaffold();
                Render();
            }

            try
            {
                Console.CursorVisible = false;
                Scaffold();
                Render();

                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    var f = fields[cursor];

                    switch (key.Key)
                    {
                        case ConsoleKey.UpArrow:
                            if (cursor > 0) cursor--;
                            Render();
                            break;

                        case ConsoleKey.DownArrow:
                            if (cursor < fields.Length - 1) cursor++;
                            Render();
                            break;

                        case ConsoleKey.Enter:
                            ClearMessage();
                            if (!IsApplicable(cursor))
                            {
                                Message(f.DisabledNote ?? "not applicable", ConsoleColor.DarkGray);
                                break;
                            }
                            if (f.Kind == FieldKind.Bool)
                            {
                                profile.RawMode = !profile.RawMode;
                                ApplyRawSideEffects();
                                Render();
                            }
                            else if (f.Kind == FieldKind.Enum)
                            {
                                var menu = ibs_compiler_common.PlatformMenu;
                                var cur = ibs_compiler_common.ParsePlatform(profile.Platform);
                                int idx = Array.IndexOf(menu, cur);
                                var next = menu[(idx + 1) % menu.Length];
                                profile.Platform = ibs_compiler_common.CanonicalName(next);
                                Render();
                            }
                            else if (f.Kind == FieldKind.Password)
                            {
                                Console.SetCursorPosition(0, startRow + cursor);
                                Console.Write(new string(' ', Console.WindowWidth - 1));
                                Console.SetCursorPosition(0, startRow + cursor);
                                Console.Write($"  > {f.Label,-16}: ");
                                Console.CursorVisible = true;
                                var pw = set_profile_main.ReadPassword();
                                Console.CursorVisible = false;
                                if (!string.IsNullOrEmpty(pw)) profile.Password = pw;
                                Render();
                            }
                            else
                            {
                                // Text / Int / Path / AliasList — inline seeded edit.
                                while (true)
                                {
                                    var seed = f.Raw(profile);
                                    var input = InlineEdit(cursor, seed);
                                    if (input == null) break; // Esc = cancel this edit
                                    var err = f.Validate?.Invoke(profile, input);
                                    if (err != null) { Message(err, ConsoleColor.Yellow); continue; }
                                    // Path: warn (not block) on missing directory.
                                    if (f.Kind == FieldKind.Path)
                                    {
                                        var t = input.Trim();
                                        var resolved = (t == "." || t == "./" || t == ".\\")
                                            ? Directory.GetCurrentDirectory() : t;
                                        if (!string.IsNullOrEmpty(resolved) && !Directory.Exists(resolved))
                                            Message($"Warning: directory does not exist: {resolved}", ConsoleColor.Yellow);
                                        else ClearMessage();
                                    }
                                    else if (f.Kind == FieldKind.Int && string.IsNullOrWhiteSpace(input))
                                    {
                                        // Empty port keeps the resolved default — leave Port as-is.
                                        break;
                                    }
                                    f.Set(profile, input);
                                    break;
                                }
                                Render();
                            }
                            break;

                        case ConsoleKey.S:
                        {
                            string? firstError = null;
                            for (int idx = 0; idx < fields.Length; idx++)
                            {
                                var vf = fields[idx];
                                if (!IsApplicable(idx) || vf.Validate == null) continue;
                                var err = vf.Validate(profile, vf.Raw(profile));
                                if (err != null) { firstError = $"{vf.Label}: {err}"; cursor = idx; break; }
                            }
                            if (firstError != null)
                            {
                                Render();
                                Message(firstError + "  (press a key)", ConsoleColor.Red);
                                Console.ReadKey(intercept: true);
                                ClearMessage();
                                Render();
                                break;
                            }
                            return true;
                        }

                        case ConsoleKey.T:
                            if (onTest != null)
                            {
                                // Raw profiles skip SBN-specific preprocessing (options,
                                // table locations, changelog, symlinks) — same rule as the
                                // headless --test path — so only Connection + Path apply.
                                // Full profiles get the whole legacy test set plus [A]ll.
                                string prompt = profile.RawMode
                                    ? "Test: [C]onnection [P]ath   [Esc] cancel"
                                    : "Test: [C]onnection [P]ath [O]ptions [L]ocations [G]changelog [S]ymlinks [A]ll   [Esc] cancel";
                                Message(prompt, ConsoleColor.Cyan);
                                var choice = Console.ReadKey(intercept: true);
                                string? kind = char.ToUpperInvariant(choice.KeyChar) switch
                                {
                                    'C' => "connection",
                                    'P' => "sql-source",
                                    'O' => profile.RawMode ? null : "options",
                                    'L' => profile.RawMode ? null : "table-locations",
                                    'G' => profile.RawMode ? null : "changelog",
                                    'S' => profile.RawMode ? null : "symlinks",
                                    'A' => profile.RawMode ? null : "all",
                                    _ => null,
                                };
                                ClearMessage();
                                if (kind == null) { Render(); break; }
                                RunTestSuspended(kind);
                            }
                            break;

                        case ConsoleKey.Escape:
                        case ConsoleKey.Q:
                        {
                            bool dirty = fields.Any(x => x.Raw(profile) != x.Raw(snapshot));
                            if (!dirty) return false;
                            Message("Discard changes? (y/N) ", ConsoleColor.Yellow);
                            Console.SetCursorPosition(
                                Math.Min(2 + "Discard changes? (y/N) ".Length, Console.WindowWidth - 1),
                                startRow + fields.Length + 2);
                            Console.CursorVisible = true;
                            var ans = Console.ReadKey(intercept: true);
                            Console.CursorVisible = false;
                            if (ans.Key == ConsoleKey.Y) return false;
                            ClearMessage();
                            Render();
                            break;
                        }
                    }
                }
            }
            finally
            {
                Console.CursorVisible = true;
                // Park the cursor below the whole widget so subsequent output is clean.
                // Land on the message row (guaranteed to exist), then WriteLine from
                // there so the buffer scrolls as needed instead of risking a
                // SetCursorPosition to a row that was never written.
                try
                {
                    Console.SetCursorPosition(0, startRow + fields.Length + 2);
                    Console.WriteLine();
                    Console.WriteLine();
                }
                catch { }
            }
        }
    }
}
