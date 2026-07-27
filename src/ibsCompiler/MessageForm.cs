using System.Text;

namespace ibsCompiler
{
    /// <summary>
    /// The message add / edit / translate editor: a vertical field form driven exactly
    /// like <see cref="ProfileEditor"/> — every field on screen at once, Up/Down to move,
    /// Enter to edit the focused row **in place** (seeded with its current value), a
    /// numbered action menu with the always-visible <c>Choice:</c> prompt, <c>S</c> to
    /// save and Esc to back out with a discard guard. It is the TUI counterpart of the
    /// SBN GUI's message detail panel, where s#msgno, company, language and the text are
    /// visible together instead of arriving as a chain of one-shot prompts.
    ///
    /// Simpler than the profile editor only in what it does not need: no Test items, no
    /// live applicability, no enum/bool cycling. Read-only rows (a reserved s#msgno, an
    /// edit's key columns) render dim and refuse the edit.
    /// </summary>
    internal static class MessageForm
    {
        /// <summary>Column where a field's value starts: "  " + pointer+space + label(-16) + ": ".</summary>
        private const int ValueCol = 2 + 2 + 16 + 2;

        /// <summary>One row of the form. <see cref="Value"/> carries the default in and the answer out.</summary>
        internal sealed class Field
        {
            public string Label { get; set; } = "";
            public string Value { get; set; } = "";
            /// <summary>Displayed dim and not editable.</summary>
            public bool ReadOnly { get; set; }
            /// <summary>Reject anything that is not an integer.</summary>
            public bool Numeric { get; set; }
            /// <summary>Dim trailing note, e.g. "(reserved)" or "(key)".</summary>
            public string? Note { get; set; }
            /// <summary>Pre-edit value, captured at entry, for the dirty marker and discard guard.</summary>
            internal string Original { get; set; } = "";
        }

        /// <summary>
        /// Render <paramref name="fields"/> and drive the key loop until the user saves or
        /// backs out. <paramref name="validate"/> runs on save and returns an error string
        /// to keep the form open, or null to accept. Returns false when the user cancelled.
        /// </summary>
        public static bool Run(string title, IList<Field> fields, Func<IList<Field>, string?>? validate = null)
        {
            // Never drive a ReadKey loop on a redirected console; callers fall back to
            // sequential prompts. The browser already guards this, so this is belt-and-braces.
            if (Console.IsInputRedirected || Console.IsOutputRedirected) return false;

            foreach (var f in fields) f.Original = f.Value;

            const int MenuRows = 3;  // Save + Back, plus one blank row before the prompt
            const string Footer = "  [Up/Down] move  [Enter] edit  (in an edit: Left/Right/Home/End, Esc cancels)";

            // Layout (top→bottom): blank + title + blank + N field rows + blank + footer +
            // blank + menu rows + prompt row + one spare row so the final newline never
            // scrolls the cached row numbers out from under us.
            if (Console.WindowHeight < fields.Count + MenuRows + 8 || Console.WindowWidth < 40)
                return RunSequential(title, fields, validate);

            int startRow = 0, menuRow0 = 0, promptRow = 0;

            void Scaffold()
            {
                Console.WriteLine();
                var prev = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("  " + title);
                Console.ForegroundColor = prev;
                Console.WriteLine();
                for (int i = 0; i < fields.Count; i++) Console.WriteLine();
                Console.WriteLine();
                Console.WriteLine(Footer);
                Console.WriteLine();
                // Reserve the menu block, the prompt row and one spare row with WriteLine
                // (which scrolls the buffer when written at the bottom, unlike
                // SetCursorPosition), then derive every cached row from where we landed.
                for (int j = 0; j < MenuRows; j++) Console.WriteLine();
                Console.WriteLine();
                Console.WriteLine();
                promptRow = Console.CursorTop - 2;
                menuRow0 = promptRow - MenuRows;
                startRow = menuRow0 - 3 - fields.Count;
            }

            int cursor = FirstEditable(fields);
            if (cursor < 0) cursor = 0;

            string Fit(string s)
            {
                int w = Math.Max(1, Console.WindowWidth - 1);
                return s.Length < w ? s.PadRight(w) : s.Substring(0, w);
            }

            void DrawRow(int idx, bool isCursor)
            {
                var f = fields[idx];
                Console.SetCursorPosition(0, startRow + idx);
                var pointer = isCursor ? ">" : " ";
                var dirty = !f.ReadOnly && f.Value != f.Original ? " *" : "";
                var note = string.IsNullOrEmpty(f.Note) ? "" : $"   {f.Note}";
                // A message can run to 255 bytes — far past one row. Show as much as fits
                // and mark the cut with an ellipsis rather than letting Fit chop it
                // silently; the inline editor scrolls to reveal the rest.
                var shown = f.Value;
                int room = Math.Max(4, Console.WindowWidth - 1 - ValueCol - dirty.Length - note.Length);
                if (shown.Length > room) shown = shown.Substring(0, room - 1) + "…";
                var line = Fit($"  {pointer} {f.Label,-16}: {shown}{dirty}{note}");
                if (f.ReadOnly)
                {
                    var p = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write(line);
                    Console.ForegroundColor = p;
                }
                else Console.Write(line);
            }

            // Numbered actions, matching the browser's 98/99 convention.
            List<(int Num, string Label, string Action)> BuildMenu() => new()
            {
                (1,  "Save", "save"),
                (98, "Back", "back"),
            };

            void RenderMenu()
            {
                var items = BuildMenu();
                for (int j = 0; j < MenuRows; j++)
                {
                    Console.SetCursorPosition(0, menuRow0 + j);
                    Console.Write(Fit(j < items.Count ? $"  {items[j].Num,2}. {items[j].Label}" : ""));
                }
            }

            void Render()
            {
                for (int i = 0; i < fields.Count; i++) DrawRow(i, i == cursor);
                RenderMenu();
                Console.SetCursorPosition(0, startRow + cursor);
            }

            void Message(string text, ConsoleColor color)
            {
                Console.SetCursorPosition(0, promptRow);
                var p = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.Write(Fit("  " + text));
                Console.ForegroundColor = p;
            }

            // Idle state of the prompt line is the bare `Choice: ` label, as everywhere else.
            void ClearMessage()
            {
                ConsoleMenu.DrawChoiceBuffer(promptRow, "Choice", "");
                Console.CursorVisible = false; // the caret belongs on the focused field row
            }

            void ShowMenuBuffer(string buf) => ConsoleMenu.DrawChoiceBuffer(promptRow, "Choice", buf);

            // In-place single-line editor seeded with the current value — ProfileEditor's,
            // extended with a caret and a horizontally scrolling window because a message
            // is up to 255 bytes and never fits one terminal row. The window follows the
            // caret; a leading/trailing '…' marks text scrolled out of view.
            string? InlineEdit(int fieldIdx, string seed)
            {
                var f = fields[fieldIdx];
                var buf = new StringBuilder(seed);
                int row = startRow + fieldIdx;
                int caret = buf.Length;   // start at the end, like a normal prompt
                int off = 0;              // first visible character

                void Draw()
                {
                    int room = Math.Max(8, Console.WindowWidth - 1 - ValueCol);
                    // Keep the caret inside the window, then clamp the window to the text.
                    if (caret < off) off = caret;
                    if (caret > off + room - 1) off = caret - room + 1;
                    if (off > Math.Max(0, buf.Length - room + 1)) off = Math.Max(0, buf.Length - room + 1);
                    if (off < 0) off = 0;

                    var visible = buf.ToString(off, Math.Min(room, buf.Length - off));
                    // Ellipsis markers replace the edge character they stand in for, so the
                    // caret column arithmetic below stays exact.
                    var chars = visible.ToCharArray();
                    if (off > 0 && chars.Length > 0) chars[0] = '…';
                    if (off + visible.Length < buf.Length && chars.Length > 0) chars[chars.Length - 1] = '…';

                    Console.SetCursorPosition(0, row);
                    Console.Write(Fit($"  > {f.Label,-16}: {new string(chars)}"));
                    Console.SetCursorPosition(Math.Min(ValueCol + (caret - off), Console.WindowWidth - 1), row);
                }

                Console.CursorVisible = true;
                Draw();
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    switch (key.Key)
                    {
                        case ConsoleKey.Enter:  Console.CursorVisible = false; return buf.ToString();
                        case ConsoleKey.Escape: Console.CursorVisible = false; return null;
                        case ConsoleKey.LeftArrow:  if (caret > 0) caret--; break;
                        case ConsoleKey.RightArrow: if (caret < buf.Length) caret++; break;
                        case ConsoleKey.Home: caret = 0; break;
                        case ConsoleKey.End:  caret = buf.Length; break;
                        case ConsoleKey.Backspace:
                            if (caret > 0) { buf.Remove(caret - 1, 1); caret--; }
                            break;
                        case ConsoleKey.Delete:
                            if (caret < buf.Length) buf.Remove(caret, 1);
                            break;
                        default:
                            if (!char.IsControl(key.KeyChar)) { buf.Insert(caret, key.KeyChar); caret++; }
                            break;
                    }
                    Draw();
                }
            }

            bool TrySave()
            {
                var error = validate?.Invoke(fields);
                if (error == null) return true;
                Render();
                Message(error + "  (press a key)", ConsoleColor.Red);
                Console.ReadKey(intercept: true);
                ClearMessage();
                Render();
                return false;
            }

            // Discard-guard shared by Back and Esc.
            bool ConfirmDiscardIfDirty()
            {
                if (!fields.Any(f => !f.ReadOnly && f.Value != f.Original)) return true;
                const string q = "Discard changes? (y/N) ";
                Message(q, ConsoleColor.Yellow);
                Console.SetCursorPosition(Math.Min(2 + q.Length, Console.WindowWidth - 1), promptRow);
                Console.CursorVisible = true;
                var ans = Console.ReadKey(intercept: true);
                Console.CursorVisible = false;
                if (ans.Key == ConsoleKey.Y) return true;
                ClearMessage();
                Render();
                return false;
            }

            try
            {
                Console.CursorVisible = false;
                Scaffold();
                ClearMessage();
                Render();

                var menuBuf = new StringBuilder();

                while (true)
                {
                    var key = Console.ReadKey(intercept: true);

                    // Digits build the menu-choice buffer, echoed on the prompt line.
                    if (char.IsDigit(key.KeyChar))
                    {
                        menuBuf.Append(key.KeyChar);
                        ShowMenuBuffer(menuBuf.ToString());
                        continue;
                    }

                    // Any other key abandons an in-progress choice and falls through.
                    if (menuBuf.Length > 0 &&
                        key.Key != ConsoleKey.Enter &&
                        key.Key != ConsoleKey.Backspace &&
                        key.Key != ConsoleKey.Escape)
                    {
                        menuBuf.Clear();
                        ClearMessage();
                        Render();
                    }

                    switch (key.Key)
                    {
                        case ConsoleKey.UpArrow:
                            if (cursor > 0) cursor--;
                            Render();
                            break;

                        case ConsoleKey.DownArrow:
                            if (cursor < fields.Count - 1) cursor++;
                            Render();
                            break;

                        case ConsoleKey.Backspace:
                            if (menuBuf.Length > 0)
                            {
                                menuBuf.Length--;
                                if (menuBuf.Length == 0) { ClearMessage(); Render(); }
                                else ShowMenuBuffer(menuBuf.ToString());
                            }
                            break;

                        case ConsoleKey.Enter:
                            // Committing a menu choice takes precedence over field edit.
                            if (menuBuf.Length > 0)
                            {
                                var choice = menuBuf.ToString();
                                menuBuf.Clear();
                                ClearMessage();
                                var hit = BuildMenu().FirstOrDefault(it => it.Num.ToString() == choice);
                                if (hit.Action == null)
                                {
                                    Message($"No menu item {choice}.", ConsoleColor.Yellow);
                                    Console.ReadKey(intercept: true);
                                    ClearMessage();
                                    Render();
                                    break;
                                }
                                if (hit.Action == "save")
                                {
                                    if (TrySave()) return true;
                                    break;
                                }
                                if (ConfirmDiscardIfDirty()) return false;   // back
                                break;
                            }

                            // Empty buffer → edit the focused field in place.
                            ClearMessage();
                            {
                                var f = fields[cursor];
                                if (f.ReadOnly)
                                {
                                    Message(f.Note != null ? $"{f.Label} is read-only {f.Note}" : $"{f.Label} is read-only", ConsoleColor.DarkGray);
                                    break;
                                }
                                while (true)
                                {
                                    var input = InlineEdit(cursor, f.Value);
                                    if (input == null) break;   // Esc = cancel this edit
                                    if (f.Numeric && !int.TryParse(input.Trim(), out _))
                                    {
                                        Message($"{f.Label} must be an integer.", ConsoleColor.Yellow);
                                        continue;
                                    }
                                    f.Value = f.Numeric ? input.Trim() : input;
                                    break;
                                }
                                Render();
                            }
                            break;

                        case ConsoleKey.S:
                            // Save accelerator (the numbered item is the documented surface).
                            if (TrySave()) return true;
                            break;

                        case ConsoleKey.Escape:
                            // Esc clears an in-progress choice; otherwise it is Back.
                            if (menuBuf.Length > 0)
                            {
                                menuBuf.Clear();
                                ClearMessage();
                                Render();
                                break;
                            }
                            if (ConfirmDiscardIfDirty()) return false;
                            break;
                    }
                }
            }
            finally
            {
                Console.CursorVisible = true;
                // Park the cursor below the widget so subsequent output is clean. Land on
                // the prompt row (guaranteed to exist) and WriteLine from there.
                try
                {
                    Console.SetCursorPosition(0, promptRow);
                    Console.WriteLine();
                }
                catch { }
            }
        }

        /// <summary>
        /// Small-terminal fallback: the same fields as a top-to-bottom prompt run, each
        /// showing its default in brackets. No cursor movement, but nothing is lost.
        /// </summary>
        private static bool RunSequential(string title, IList<Field> fields, Func<IList<Field>, string?>? validate)
        {
            while (true)
            {
                Console.WriteLine();
                Console.WriteLine("  " + title);
                foreach (var f in fields)
                {
                    if (f.ReadOnly)
                    {
                        Console.WriteLine($"    {f.Label,-16}: {f.Value}{(string.IsNullOrEmpty(f.Note) ? "" : "   " + f.Note)}");
                        continue;
                    }
                    Console.Write($"    {f.Label} [{f.Value}]: ");
                    var entered = Console.ReadLine() ?? "";
                    if (entered.Length == 0) continue;
                    if (f.Numeric && !int.TryParse(entered.Trim(), out _))
                    {
                        Console.WriteLine($"    {f.Label} must be an integer.");
                        return false;
                    }
                    f.Value = f.Numeric ? entered.Trim() : entered;
                }
                var error = validate?.Invoke(fields);
                if (error == null) return true;
                Console.WriteLine("    " + error);
                Console.Write("    Try again? (y/N): ");
                var again = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
                if (again != "y" && again != "yes") return false;
            }
        }

        /// <summary>Value of the field with this label, or "" — sugar for the flow call sites.</summary>
        public static string Get(IList<Field> fields, string label)
            => fields.FirstOrDefault(f => f.Label == label)?.Value ?? "";

        /// <summary>Integer value of a numeric field, falling back to <paramref name="fallback"/>.</summary>
        public static int GetInt(IList<Field> fields, string label, int fallback)
            => int.TryParse(Get(fields, label).Trim(), out var v) ? v : fallback;

        private static int FirstEditable(IList<Field> fields)
        {
            for (int i = 0; i < fields.Count; i++) if (!fields[i].ReadOnly) return i;
            return -1;
        }
    }
}
