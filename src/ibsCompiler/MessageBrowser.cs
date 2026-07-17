using System.Text;
using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace ibsCompiler
{
    /// <summary>
    /// Interactive, file-first message browser for <c>set_messages</c>. It is the
    /// TTY front-end over the pure-file engine in <see cref="MessageFileEditor"/>:
    /// pick a live message type, browse its groups (SBN-GUI-style table), then add /
    /// find / edit / delete individual messages — all as byte-faithful edits to the
    /// flat <c>css.&lt;type&gt;_msg</c> / <c>css.&lt;type&gt;_msgrp</c> files. No database
    /// round-trip except the explicit "Install messages to &lt;profile&gt;" action, which
    /// runs the legacy <see cref="compile_msg_main.Run"/> import and is blocked on GONZO.
    ///
    /// TTY only. Every reachable code path uses <c>Console.ReadKey</c> or absolute-row
    /// rendering, so the entry point refuses to run on a redirected console and points
    /// the caller at the headless flags instead. Green success / red error follow the
    /// same convention as the set_profile hub.
    /// </summary>
    internal static class MessageBrowser
    {
        private enum Nav { Back, Exit }

        // ---- color helpers (match SetProfile's PrintSuccess / PrintError) ----
        private static void Green(string s)
        {
            var p = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(s);
            Console.ForegroundColor = p;
        }

        private static void Red(string s)
        {
            var p = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(s);
            Console.ForegroundColor = p;
        }

        private static void Cyan(string s)
        {
            var p = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(s);
            Console.ForegroundColor = p;
        }

        private static void Dim(string s)
        {
            var p = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(s);
            Console.ForegroundColor = p;
        }

        /// <summary>Truncate-or-pad to an exact visible width (never wraps the console line).</summary>
        private static string Fit(string s, int w)
        {
            s ??= "";
            if (w <= 0) return "";
            if (s.Length == w) return s;
            return s.Length < w ? s.PadRight(w) : s.Substring(0, w);
        }

        /// <summary>
        /// Entry point from <see cref="InteractiveMenus.RunSetMessages"/>. Validates the
        /// profile, then runs the type → group → action screens until the user exits.
        /// Returns a process exit code (0 normal, 1 on a validation failure).
        /// </summary>
        public static int Run(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor)
        {
            // TTY guard. RunSetMessages dispatches every headless flag before reaching
            // us, so a redirected console here is a genuine "interactive without a
            // terminal" misuse — point at the headless surface and bail.
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                Console.Error.WriteLine(
                    "interactive browser requires a terminal; use the headless flags "
                    + "(--add/--find/--edit-msg/--delete-msg/--new-group/--import/--export)");
                return 1;
            }

            // ---- profile validation ----
            if (profile.RawMode)
            {
                Red("ERROR: raw-mode profiles have no message files to browse "
                    + "(set Raw Mode to 'No' on the profile to use this command).");
                return 1;
            }

            string setupDir;
            try { setupDir = ibs_compiler_common.GetPath_Setup(profile); }
            catch { setupDir = ""; }
            if (string.IsNullOrEmpty(setupDir) || !Directory.Exists(setupDir))
            {
                Red($"ERROR: message source directory not found for this profile: "
                    + (string.IsNullOrEmpty(setupDir) ? "(unresolved)" : setupDir));
                return 1;
            }

            var types = MessageFileEditor.ListLiveTypes(profile);
            if (types.Count == 0)
            {
                Red($"ERROR: no live message groups (css.*_msgrp) under {setupDir}.");
                return 1;
            }

            var profileName = profile.IsProfile ? profile.ProfileName : cmdvars.ServerNameOnly;
            bool isGonzo = profileName.Equals("GONZO", StringComparison.OrdinalIgnoreCase)
                        || profileName.Equals("G", StringComparison.OrdinalIgnoreCase);

            try
            {
                Console.CursorVisible = true;
                while (true)
                {
                    // ---- type screen ----
                    types = MessageFileEditor.ListLiveTypes(profile);
                    Console.WriteLine();
                    Cyan($"  Messages — {profileName}");
                    Dim($"  Source: {setupDir}");
                    Console.WriteLine();
                    for (int i = 0; i < types.Count; i++)
                        Console.WriteLine($"   {i + 1}. {types[i].Label} Messages (css.{types[i].Type}_msgrp)");
                    Console.WriteLine("  99. Exit");
                    Console.WriteLine();

                    // Exit is the obvious default — preserves the old blank-Enter-exits shortcut.
                    var choice = ConsoleMenu.ReadDeferredChoice(defaultChoice: "99");
                    if (string.IsNullOrEmpty(choice) || choice == "99") return 0;
                    if (!int.TryParse(choice, out var n) || n < 1 || n > types.Count)
                    {
                        Red($"  No type {choice}.");
                        continue;
                    }

                    var nav = GroupScreen(cmdvars, profile, executor, types[n - 1], profileName, isGonzo, setupDir);
                    if (nav == Nav.Exit) return 0;
                    // Nav.Back → re-loop to the type screen.
                }
            }
            finally { Console.CursorVisible = true; }
        }

        // ================================================================
        // Group screen — SBN-GUI-style table (GROUP / START# / ROWS / DESC)
        // ================================================================
        private static Nav GroupScreen(
            CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor,
            MessageFileEditor.LiveType lt, string profileName, bool isGonzo, string setupDir)
        {
            while (true)
            {
                var groups = MessageFileEditor.ListGroups(profile, lt.Type)
                    .OrderBy(g => g.MinMsg).ThenBy(g => g.Group, StringComparer.Ordinal).ToList();

                // Small-terminal fallback: plain (non-scrolling) numbered list.
                bool tooSmall = Console.WindowHeight < 10 || Console.WindowWidth < 40;
                int selected;
                string extra;
                if (tooSmall)
                    (selected, extra) = GroupPickFallback(lt, groups, setupDir);
                else
                    (selected, extra) = GroupPickScrolling(lt, groups, setupDir);

                if (extra == "exit") return Nav.Exit;
                if (extra == "back") return Nav.Back;
                if (extra == "create")
                {
                    CreateGroupFlow(profile, lt);
                    continue;
                }
                if (extra == "install")
                {
                    InstallToProfile(cmdvars, profile, executor, profileName, isGonzo);
                    continue;
                }
                if (selected >= 0 && selected < groups.Count)
                {
                    var nav = GroupActions(profile, lt, groups[selected], setupDir);
                    if (nav == Nav.Exit) return Nav.Exit;
                    // Nav.Back → refresh the group list.
                }
            }
        }

        /// <summary>
        /// Scrolling group picker. Up/Down move a highlighted row (scroll clamp mirrors
        /// InteractiveCheckbox); digits build a deferred Choice buffer committed on Enter
        /// (row number, 98 Back, 99 Exit); C creates a group, I installs to the profile.
        /// Returns (groupIndex, "") on a row select, else (-1, action-token).
        /// </summary>
        private static (int, string) GroupPickScrolling(MessageFileEditor.LiveType lt, List<MessageFileEditor.MsgGroup> groups, string setupDir)
        {
            int cursor = 0, scroll = 0;
            var buf = new StringBuilder();

            int w = Math.Max(40, Console.WindowWidth) - 1;
            // Column layout: num(4) group(8) start(8) rows(7) desc(rest)
            int descCol = Math.Max(10, w - (2 + 4 + 8 + 8 + 7));

            Console.WriteLine();
            Cyan($"  {lt.Label} message groups  ({groups.Count})");
            Dim($"  Source: {setupDir}");
            Console.WriteLine();
            Console.WriteLine("  " + Fit($"{"",4}{"GROUP",-8}{"START#",-8}{"ROWS",-7}DESCRIPTION", w - 2));
            int headerRow = Console.CursorTop; // first data row lands here
            int visibleRows = Math.Max(3, Console.WindowHeight - (headerRow - Console.CursorTop) - 8);
            visibleRows = Math.Min(visibleRows, Math.Max(1, groups.Count));

            // Reserve the window + footer rows so the buffer scrolls if we are near the bottom.
            for (int i = 0; i < visibleRows; i++) Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("  [Up/Down] move  [Enter] open  C new group  I install to profile  98 Back  99 Exit");
            int footerRow = Console.CursorTop;
            int startRow = footerRow - 2 - visibleRows;
            int promptRow = footerRow; // deferred Choice buffer + messages land here

            void RenderWindow()
            {
                for (int i = 0; i < visibleRows; i++)
                {
                    Console.SetCursorPosition(0, startRow + i);
                    int idx = scroll + i;
                    string line;
                    if (idx >= groups.Count) line = "";
                    else
                    {
                        var g = groups[idx];
                        var ptr = idx == cursor ? ">" : " ";
                        line = $"  {ptr} {idx + 1,-2}{g.Group,-8}{g.MinMsg,-8}{g.RowCount,-7}{g.Description}";
                    }
                    Console.Write(Fit(line, w));
                }
                Console.SetCursorPosition(0, startRow + (cursor - scroll));
            }

            // Always-visible idle state: the bare `Choice: ` label rather than a blank
            // row. No single group number is an obvious default (plain Enter without a
            // typed digit already opens the highlighted cursor row), so this never
            // passes a defaultChoice.
            void ClearPrompt()
            {
                ConsoleMenu.DrawChoiceBuffer(promptRow, "Choice", "");
                Console.CursorVisible = false;
            }

            if (groups.Count == 0)
            {
                Console.SetCursorPosition(0, startRow);
                Dim("  (no groups yet — press C to create one)");
            }
            else RenderWindow();

            try
            {
                Console.CursorVisible = false;
                ClearPrompt(); // render the idle Choice: label before the first keystroke
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);

                    // Digits build a deferred numbered choice.
                    if (char.IsDigit(key.KeyChar))
                    {
                        buf.Append(key.KeyChar);
                        ConsoleMenu.DrawChoiceBuffer(promptRow, "Choice", buf.ToString());
                        continue;
                    }
                    if (buf.Length > 0 && key.Key != ConsoleKey.Enter
                        && key.Key != ConsoleKey.Backspace && key.Key != ConsoleKey.Escape)
                    {
                        buf.Clear();
                        Console.CursorVisible = false;
                        ClearPrompt();
                    }

                    switch (key.Key)
                    {
                        case ConsoleKey.UpArrow:
                            if (cursor > 0) { cursor--; if (cursor < scroll) scroll = cursor; RenderWindow(); }
                            break;
                        case ConsoleKey.DownArrow:
                            if (cursor < groups.Count - 1)
                            {
                                cursor++;
                                if (cursor >= scroll + visibleRows) scroll = cursor - visibleRows + 1;
                                RenderWindow();
                            }
                            break;
                        case ConsoleKey.Backspace:
                            if (buf.Length > 0)
                            {
                                buf.Length--;
                                if (buf.Length == 0) { Console.CursorVisible = false; ClearPrompt(); }
                                else ConsoleMenu.DrawChoiceBuffer(promptRow, "Choice", buf.ToString());
                            }
                            break;
                        case ConsoleKey.Enter:
                            if (buf.Length > 0)
                            {
                                var c = buf.ToString(); buf.Clear();
                                Console.CursorVisible = false; ClearPrompt();
                                if (c == "99") { EndPicker(footerRow); return (-1, "exit"); }
                                if (c == "98") { EndPicker(footerRow); return (-1, "back"); }
                                if (int.TryParse(c, out var num) && num >= 1 && num <= groups.Count)
                                { EndPicker(footerRow); return (num - 1, ""); }
                                Console.SetCursorPosition(0, promptRow);
                                Red(Fit($"  No group {c}.", w));
                                break;
                            }
                            if (groups.Count > 0) { EndPicker(footerRow); return (cursor, ""); }
                            break;
                        case ConsoleKey.C:
                            EndPicker(footerRow); return (-1, "create");
                        case ConsoleKey.I:
                            EndPicker(footerRow); return (-1, "install");
                        case ConsoleKey.Escape:
                        case ConsoleKey.Q:
                            EndPicker(footerRow); return (-1, "back");
                    }
                }
            }
            finally { Console.CursorVisible = true; }
        }

        private static void EndPicker(int footerRow)
        {
            Console.CursorVisible = true;
            try { Console.SetCursorPosition(0, footerRow); Console.WriteLine(); }
            catch { }
        }

        /// <summary>Plain fallback for a terminal too small to host the scrolling table.</summary>
        private static (int, string) GroupPickFallback(MessageFileEditor.LiveType lt, List<MessageFileEditor.MsgGroup> groups, string setupDir)
        {
            Console.WriteLine();
            Cyan($"  {lt.Label} message groups  ({groups.Count})");
            Dim($"  Source: {setupDir}");
            Console.WriteLine($"  {"",4}{"GROUP",-8}{"START#",-8}{"ROWS",-7}DESCRIPTION");
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                Console.WriteLine($"  {i + 1,-4}{g.Group,-8}{g.MinMsg,-8}{g.RowCount,-7}{g.Description}");
            }
            Console.WriteLine("   C. new group   I. install to profile   98. Back   99. Exit");
            Console.WriteLine();
            // Back is the safe, non-destructive default.
            var choice = ConsoleMenu.ReadDeferredChoice(allowText: true, defaultChoice: "98");
            if (string.IsNullOrEmpty(choice)) return (-1, "back");
            var up = choice.Trim().ToUpperInvariant();
            if (up == "C") return (-1, "create");
            if (up == "I") return (-1, "install");
            if (up == "99") return (-1, "exit");
            if (up == "98") return (-1, "back");
            if (int.TryParse(choice, out var num) && num >= 1 && num <= groups.Count) return (num - 1, "");
            Red($"  No group {choice}.");
            return (-1, "again");
        }

        // ================================================================
        // Group actions menu
        // ================================================================
        private static Nav GroupActions(ResolvedProfile profile, MessageFileEditor.LiveType lt, MessageFileEditor.MsgGroup group, string setupDir)
        {
            while (true)
            {
                Console.WriteLine();
                Cyan($"  {lt.Label} / {group.Group} — {group.Description}");
                Dim($"  Source: {setupDir}");
                Console.WriteLine($"  start #{group.MinMsg}   {group.RowCount} message(s)");
                Console.WriteLine();
                Console.WriteLine("   1. Add new message");
                Console.WriteLine("   2. Find existing message");
                Console.WriteLine($"   3. Open css.{lt.Type}_msg in editor");
                Console.WriteLine("  98. Back");
                Console.WriteLine("  99. Exit");
                Console.WriteLine();

                // Back is the safe, non-destructive default.
                var choice = ConsoleMenu.ReadDeferredChoice(defaultChoice: "98");
                if (string.IsNullOrEmpty(choice) || choice == "98") return Nav.Back;
                switch (choice)
                {
                    case "1":
                        AddMessageFlow(profile, lt, group.Group);
                        break;
                    case "2":
                        var nav = FindScreen(profile, lt);
                        if (nav == Nav.Exit) return Nav.Exit;
                        break;
                    case "3":
                        InteractiveMenus.LaunchEditor(lt.MsgPath);
                        // The file may have changed under us — nothing cached here, the
                        // next ListGroups/LoadFile re-reads from disk.
                        Green($"  Reloaded css.{lt.Type}_msg.");
                        break;
                    case "99":
                        return Nav.Exit;
                    default:
                        Red($"  No action {choice}.");
                        break;
                }
            }
        }

        // ================================================================
        // Add message (type/group fixed; dry-run preview → confirm → write)
        // ================================================================
        private static void AddMessageFlow(ResolvedProfile profile, MessageFileEditor.LiveType lt, string group)
        {
            Console.WriteLine();
            Cyan($"  Add message to {lt.Label} / {group}");
            Console.Write("  Message text: ");
            var text = Console.ReadLine() ?? "";
            if (text.Length == 0) { Red("  Cancelled (empty text)."); return; }

            Console.Write("  Language [1]: ");
            var langStr = (Console.ReadLine() ?? "").Trim();
            int lang = 1;
            if (langStr.Length > 0 && !int.TryParse(langStr, out lang)) { Red("  Language must be an integer."); return; }

            Console.Write("  Company [0]: ");
            var cmpyStr = (Console.ReadLine() ?? "").Trim();
            int cmpy = 0;
            if (cmpyStr.Length > 0 && !int.TryParse(cmpyStr, out cmpy)) { Red("  Company must be an integer."); return; }

            Console.Write($"  Update flag [Enter = {(lt.Type == "gui" ? "X" : "space")}]: ");
            var updStr = Console.ReadLine() ?? "";
            char? updFlg = null;
            if (updStr.Length == 1) updFlg = updStr[0];
            else if (updStr.Length > 1) { Red("  Update flag must be a single character."); return; }

            var preview = MessageFileEditor.AddMessage(profile, lt.Type, group, text, lang, cmpy, updFlg, dryRun: true);
            if (!preview.Success) { Red($"  ERROR: {preview.Error}"); return; }
            if (preview.Warning != null) { var p = Console.ForegroundColor; Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"  WARNING: {preview.Warning}"); Console.ForegroundColor = p; }

            Console.WriteLine();
            Console.WriteLine($"  Reserved MSGNO {preview.Msgno}");
            Console.WriteLine("  Row: " + preview.Row.Replace("\t", " | "));
            Console.WriteLine();
            Console.Write("  Write this message? (y/N): ");
            var ans = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (ans != "y" && ans != "yes") { Console.WriteLine("  Cancelled."); return; }

            var result = MessageFileEditor.AddMessage(profile, lt.Type, group, text, lang, cmpy, updFlg, dryRun: false);
            if (!result.Success) { Red($"  ERROR: {result.Error}"); return; }
            Green($"  MSGNO {result.Msgno} saved.");
        }

        // ================================================================
        // Create new group
        // ================================================================
        private static void CreateGroupFlow(ResolvedProfile profile, MessageFileEditor.LiveType lt)
        {
            Console.WriteLine();
            Cyan($"  Create {lt.Label} group");
            Console.Write("  Group (<=6 chars): ");
            var group = (Console.ReadLine() ?? "").Trim();
            if (group.Length == 0) { Red("  Cancelled (empty group)."); return; }

            Console.Write("  Start # [0]: ");
            var startStr = (Console.ReadLine() ?? "").Trim();
            int start = 0;
            if (startStr.Length > 0 && !int.TryParse(startStr, out start)) { Red("  Start must be an integer."); return; }

            Console.Write("  Description: ");
            var desc = (Console.ReadLine() ?? "").Trim();

            var result = MessageFileEditor.AddGroup(profile, lt.Type, group, start, desc, dryRun: false);
            if (!result.Success) { Red($"  ERROR: {result.Error}"); return; }
            Green($"  Group {result.Group} created (start #{result.Start}).");
        }

        // ================================================================
        // Install messages to the profile (legacy compile_msg import; GONZO blocked)
        // ================================================================
        private static void InstallToProfile(
            CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor,
            string profileName, bool isGonzo)
        {
            Console.WriteLine();
            if (isGonzo)
            {
                Red($"  Install is not allowed against {profileName} "
                    + "(GONZO is the canonical message source; export-only).");
                return;
            }

            Console.WriteLine($"  Install compiles the local message files into {profileName}'s database.");
            Console.Write("  Proceed? (y/N): ");
            var ans = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (ans != "y" && ans != "yes") { Console.WriteLine("  Cancelled."); return; }

            Console.WriteLine();
            Console.WriteLine("  Compiling messages...");
            compile_msg_main.Run(cmdvars, profile, executor);
            Green("  Install complete.");
            Console.Write("  Press any key to continue...");
            Console.ReadKey(intercept: true);
            Console.WriteLine();
        }

        // ================================================================
        // Find — incremental search over the whole type file
        // ================================================================
        private static Nav FindScreen(ResolvedProfile profile, MessageFileEditor.LiveType lt)
        {
            var file = MessageFileEditor.LoadFile(profile, lt.Type);
            int? cmpy = null, lang = null;
            var filter = new StringBuilder();

            // Small-terminal fallback: a single-shot prompt search.
            if (Console.WindowHeight < 12 || Console.WindowWidth < 40)
                return FindFallback(profile, lt, file);

            int w = Math.Max(40, Console.WindowWidth) - 1;
            var results = MessageFileEditor.FindMessages(file, lt.Type, "", cmpy, lang);
            int sel = 0, scroll = 0;

            // Scaffold: title + blank + header + blank + window + blank + footer.
            Console.WriteLine();
            Cyan($"  Find in {lt.Label} messages");
            Console.WriteLine();
            Console.WriteLine(); // header row (filled by RenderHeader)
            Console.WriteLine();
            int headerRow = Console.CursorTop - 2;
            int visibleRows = Math.Max(3, Console.WindowHeight - 10);
            for (int i = 0; i < visibleRows; i++) Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("  [Up/Down] select  [Enter] open  [Tab] cmpy/lang refine  [Backspace] trim  [Esc] back");
            int footerRow = Console.CursorTop;
            int startRow = footerRow - 2 - visibleRows;

            void RenderHeader()
            {
                Console.SetCursorPosition(0, headerRow);
                var chips = "";
                if (cmpy.HasValue) chips += $"  [cmpy={cmpy}]";
                if (lang.HasValue) chips += $"  [lang={lang}]";
                Console.Write(Fit($"  Filter: {filter}_" + chips, w));
                Console.SetCursorPosition(0, headerRow + 1);
                Console.Write(Fit($"  showing {results.Count} of {file.Rows.Count}", w));
            }

            void RenderWindow()
            {
                for (int i = 0; i < visibleRows; i++)
                {
                    Console.SetCursorPosition(0, startRow + i);
                    int idx = scroll + i;
                    string line;
                    if (idx >= results.Count) line = "";
                    else
                    {
                        var r = results[idx];
                        var ptr = idx == sel ? ">" : " ";
                        line = $"  {ptr} {r.Msgno,-7}{r.Cmpy}/{r.Lang,-5}{r.Text}";
                    }
                    Console.Write(Fit(line, w));
                }
            }

            void Refilter()
            {
                results = MessageFileEditor.FindMessages(file, lt.Type, filter.ToString(), cmpy, lang);
                sel = 0; scroll = 0;
            }

            void RenderAll() { RenderHeader(); RenderWindow(); }

            try
            {
                Console.CursorVisible = false;
                RenderAll();
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    switch (key.Key)
                    {
                        case ConsoleKey.Escape:
                            EndPicker(footerRow);
                            return Nav.Back;
                        case ConsoleKey.UpArrow:
                            if (sel > 0) { sel--; if (sel < scroll) scroll = sel; RenderWindow(); }
                            break;
                        case ConsoleKey.DownArrow:
                            if (sel < results.Count - 1)
                            {
                                sel++;
                                if (sel >= scroll + visibleRows) scroll = sel - visibleRows + 1;
                                RenderWindow();
                            }
                            break;
                        case ConsoleKey.Backspace:
                            if (filter.Length > 0) { filter.Length--; Refilter(); RenderAll(); }
                            break;
                        case ConsoleKey.Tab:
                            (cmpy, lang) = RefineCmpyLang(footerRow, cmpy, lang);
                            Refilter();
                            // The refine prompt scribbled over the footer — redraw scaffold labels.
                            Console.SetCursorPosition(0, footerRow - 1);
                            Console.Write(Fit("  [Up/Down] select  [Enter] open  [Tab] cmpy/lang refine  [Backspace] trim  [Esc] back", w));
                            RenderAll();
                            break;
                        case ConsoleKey.Enter:
                            if (results.Count > 0)
                            {
                                var chosen = results[sel];
                                Console.CursorVisible = true;
                                Console.SetCursorPosition(0, footerRow);
                                Console.WriteLine();
                                var changed = DetailScreen(profile, lt, chosen);
                                if (changed) file = MessageFileEditor.LoadFile(profile, lt.Type);
                                Refilter();
                                // Re-scaffold the whole screen after the leaf detail view.
                                Console.WriteLine();
                                Cyan($"  Find in {lt.Label} messages");
                                Console.WriteLine();
                                Console.WriteLine();
                                Console.WriteLine();
                                headerRow = Console.CursorTop - 2;
                                for (int i = 0; i < visibleRows; i++) Console.WriteLine();
                                Console.WriteLine();
                                Console.WriteLine("  [Up/Down] select  [Enter] open  [Tab] cmpy/lang refine  [Backspace] trim  [Esc] back");
                                footerRow = Console.CursorTop;
                                startRow = footerRow - 2 - visibleRows;
                                Console.CursorVisible = false;
                                RenderAll();
                            }
                            break;
                        default:
                            if (!char.IsControl(key.KeyChar) && key.KeyChar != '\0')
                            {
                                filter.Append(key.KeyChar);
                                Refilter();
                                RenderAll();
                            }
                            break;
                    }
                }
            }
            finally { Console.CursorVisible = true; }
        }

        /// <summary>Prompt for cmpy/lang refinement; Enter on either keeps/clears that filter.</summary>
        private static (int?, int?) RefineCmpyLang(int promptRow, int? cmpy, int? lang)
        {
            Console.CursorVisible = true;
            Console.SetCursorPosition(0, promptRow);
            Console.Write(new string(' ', Math.Max(1, Console.WindowWidth - 1)));
            Console.SetCursorPosition(0, promptRow);
            Console.Write($"  cmpy [{(cmpy?.ToString() ?? "any")}] (blank=any): ");
            var cs = (Console.ReadLine() ?? "").Trim();
            int? newCmpy = cmpy;
            if (cs.Length == 0) newCmpy = null;
            else if (int.TryParse(cs, out var c)) newCmpy = c;

            Console.SetCursorPosition(0, promptRow);
            Console.Write(new string(' ', Math.Max(1, Console.WindowWidth - 1)));
            Console.SetCursorPosition(0, promptRow);
            Console.Write($"  lang [{(lang?.ToString() ?? "any")}] (blank=any): ");
            var ls = (Console.ReadLine() ?? "").Trim();
            int? newLang = lang;
            if (ls.Length == 0) newLang = null;
            else if (int.TryParse(ls, out var l)) newLang = l;

            Console.CursorVisible = false;
            return (newCmpy, newLang);
        }

        /// <summary>Single-shot search fallback for a terminal too small for the live view.</summary>
        private static Nav FindFallback(ResolvedProfile profile, MessageFileEditor.LiveType lt, MessageFileEditor.MsgFile file)
        {
            Console.WriteLine();
            Cyan($"  Find in {lt.Label} messages (compact)");
            Console.Write("  Search term (blank = all): ");
            var term = Console.ReadLine() ?? "";
            var results = MessageFileEditor.FindMessages(file, lt.Type, term);
            if (results.Count == 0) { Console.WriteLine("  No matches."); return Nav.Back; }
            for (int i = 0; i < Math.Min(results.Count, 30); i++)
            {
                var r = results[i];
                Console.WriteLine($"  {i + 1,-3}{r.Msgno,-7}{r.Cmpy}/{r.Lang}  {r.Text}");
            }
            Console.WriteLine($"  showing {Math.Min(results.Count, 30)} of {results.Count}");
            Console.Write("  Open # (blank = back): ");
            var pick = (Console.ReadLine() ?? "").Trim();
            if (int.TryParse(pick, out var idx) && idx >= 1 && idx <= results.Count)
            {
                DetailScreen(profile, lt, results[idx - 1]);
            }
            return Nav.Back;
        }

        // ================================================================
        // Detail screen — full row + Edit / Delete
        // Returns true when the underlying file changed (caller reloads).
        // ================================================================
        private static bool DetailScreen(ResolvedProfile profile, MessageFileEditor.LiveType lt, MessageFileEditor.MsgRow row)
        {
            bool changed = false;
            while (true)
            {
                Console.WriteLine();
                Cyan($"  {lt.Label} message {row.Msgno}");
                Console.WriteLine($"    msgno : {row.Msgno}");
                Console.WriteLine($"    cmpy  : {row.Cmpy}");
                Console.WriteLine($"    lang  : {row.Lang}");
                Console.WriteLine($"    group : {row.Group}");
                Console.WriteLine($"    flag  : '{row.UpdFlg}'");
                Console.WriteLine($"    text  : {row.Text}");
                Console.WriteLine();
                Console.WriteLine("   1. Edit");
                Console.WriteLine("   2. Delete");
                Console.WriteLine("  98. Back");
                Console.WriteLine();

                // Back is the safe default — Delete (a destructive action) is never defaulted.
                var choice = ConsoleMenu.ReadDeferredChoice(defaultChoice: "98");
                if (string.IsNullOrEmpty(choice) || choice == "98") return changed;
                if (choice == "1")
                {
                    Console.Write("  New text (Enter keeps current): ");
                    var nt = Console.ReadLine();
                    string? newText = string.IsNullOrEmpty(nt) ? null : nt;

                    Console.Write("  New update flag (Enter keeps): ");
                    var nf = Console.ReadLine() ?? "";
                    char? newFlg = null;
                    if (nf.Length == 1) newFlg = nf[0];
                    else if (nf.Length > 1) { Red("  Update flag must be a single character."); continue; }

                    if (newText == null && newFlg == null) { Console.WriteLine("  Nothing changed."); continue; }

                    var res = MessageFileEditor.UpdateMessage(
                        profile, lt.Type, row.Msgno, row.Cmpy, row.Lang,
                        newText, newFlg, dryRun: false, lineIndex: row.LineIndex);
                    if (!res.Success) { Red($"  ERROR: {res.Error}"); continue; }
                    Green($"  EDITED {res.Msgno}");
                    changed = true;
                    return changed; // the row's LineIndex/text are now stale — bounce to the refreshed list.
                }
                else if (choice == "2")
                {
                    Console.Write($"  Type 'delete' to remove message {row.Msgno}: ");
                    var conf = (Console.ReadLine() ?? "").Trim();
                    if (!conf.Equals("delete", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("  Cancelled."); continue; }

                    var res = MessageFileEditor.DeleteMessage(
                        profile, lt.Type, row.Msgno, row.Cmpy, row.Lang,
                        dryRun: false, lineIndex: row.LineIndex);
                    if (!res.Success) { Red($"  ERROR: {res.Error}"); continue; }
                    Green($"  DELETED {res.Msgno}");
                    changed = true;
                    return changed;
                }
                else Red($"  No action {choice}.");
            }
        }
    }
}
