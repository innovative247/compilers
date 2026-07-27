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
    /// **Nothing is ever truncated.** A message runs to 255 bytes, so its field wraps
    /// across as many screen rows as it needs — the whole value is on screen at all
    /// times, while editing and while navigating. The wrap is presentation only: the
    /// value stays a single line and no CR/LF can enter it (a newline keystroke commits
    /// the edit, and control characters are never inserted), so what lands in
    /// <c>css.&lt;type&gt;_msg</c> is always one physical line.
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
            /// <summary>
            /// Wrap this field's value across as many rows as it needs instead of keeping it
            /// on one. The stored value is still a single line — this is display only.
            /// </summary>
            public bool Wrap { get; set; }
            /// <summary>UTF-8 byte ceiling enforced while typing (0 = no limit). The message column is 255.</summary>
            public int MaxBytes { get; set; }
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

            // Characters that fit on one row after the label column. Fixed for the life of
            // the form so the reserved height and the caret arithmetic always agree.
            int room = Math.Max(8, Console.WindowWidth - 1 - ValueCol);

            // Rows reserved per field. A wrapping field reserves what its ceiling needs
            // (MaxBytes is an upper bound on characters), never what it currently holds —
            // so typing can never change the layout under the cursor.
            int RowsFor(Field f)
            {
                if (!f.Wrap) return 1;
                int cap = Math.Max(f.MaxBytes, f.Value.Length);
                if (cap <= 0) cap = 1;
                return Math.Max(1, (cap + room - 1) / room);
            }
            var heights = fields.Select(RowsFor).ToArray();
            int totalFieldRows = heights.Sum();
            var offsets = new int[fields.Count];       // row offset of each field within the block
            for (int i = 1; i < fields.Count; i++) offsets[i] = offsets[i - 1] + heights[i - 1];

            // Layout (top→bottom): blank + title + blank + field rows + blank + footer +
            // blank + menu rows + prompt row + one spare row so the final newline never
            // scrolls the cached row numbers out from under us.
            if (Console.WindowHeight < totalFieldRows + MenuRows + 8 || Console.WindowWidth < 40)
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
                for (int i = 0; i < totalFieldRows; i++) Console.WriteLine();
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
                startRow = menuRow0 - 3 - totalFieldRows;
            }

            int cursor = FirstEditable(fields);
            if (cursor < 0) cursor = 0;

            string Fit(string s)
            {
                int w = Math.Max(1, Console.WindowWidth - 1);
                return s.Length < w ? s.PadRight(w) : s.Substring(0, w);
            }

            // Every row of one field: the label row carries the first chunk of the value,
            // continuation rows are indented to the value column. Suffix (dirty marker +
            // note) rides the LAST chunk so it never lands mid-text.
            void DrawField(int idx, bool isCursor, string? overrideValue = null)
            {
                var f = fields[idx];
                var value = overrideValue ?? f.Value;
                var suffix = (!f.ReadOnly && value != f.Original ? " *" : "")
                           + (string.IsNullOrEmpty(f.Note) ? "" : $"   {f.Note}");

                var chunks = Chunk(value, room);
                var pad = new string(' ', ValueCol);
                for (int r = 0; r < heights[idx]; r++)
                {
                    var chunk = r < chunks.Count ? chunks[r] : "";
                    var tail = r == chunks.Count - 1 ? suffix : "";
                    var text = r == 0
                        ? $"  {(isCursor ? ">" : " ")} {f.Label,-16}: {chunk}{tail}"
                        : $"{pad}{chunk}{tail}";
                    Console.SetCursorPosition(0, startRow + offsets[idx] + r);
                    var line = Fit(text);
                    if (f.ReadOnly)
                    {
                        var p = Console.ForegroundColor;
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write(line);
                        Console.ForegroundColor = p;
                    }
                    else Console.Write(line);
                }
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
                for (int i = 0; i < fields.Count; i++) DrawField(i, i == cursor);
                RenderMenu();
                Console.SetCursorPosition(0, startRow + offsets[cursor]);
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

            // In-place editor seeded with the current value — ProfileEditor's, plus a caret
            // and wrapped rendering so a 255-byte message is fully visible and fully
            // reachable. Returns null when the edit was abandoned with Esc.
            string? InlineEdit(int fieldIdx, string seed)
            {
                var f = fields[fieldIdx];
                var buf = new StringBuilder(seed);
                int caret = buf.Length;   // start at the end, like a normal prompt

                void Draw()
                {
                    DrawField(fieldIdx, isCursor: true, overrideValue: buf.ToString());
                    Console.SetCursorPosition(
                        Math.Min(ValueCol + caret % room, Console.WindowWidth - 1),
                        startRow + offsets[fieldIdx] + Math.Min(caret / room, heights[fieldIdx] - 1));
                }

                Console.CursorVisible = true;
                Draw();
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    switch (key.Key)
                    {
                        case ConsoleKey.Enter:
                            // A newline arriving mid-paste would otherwise commit here and
                            // spray the rest of the pasted text at the navigation loop.
                            // Anything already queued belongs to this edit — drop it.
                            while (Console.KeyAvailable) Console.ReadKey(intercept: true);
                            Console.CursorVisible = false;
                            return buf.ToString();
                        case ConsoleKey.Escape: Console.CursorVisible = false; return null;
                        case ConsoleKey.LeftArrow:  if (caret > 0) caret--; break;
                        case ConsoleKey.RightArrow: if (caret < buf.Length) caret++; break;
                        case ConsoleKey.UpArrow:    caret = Math.Max(0, caret - room); break;
                        case ConsoleKey.DownArrow:  caret = Math.Min(buf.Length, caret + room); break;
                        case ConsoleKey.Home: caret = 0; break;
                        case ConsoleKey.End:  caret = buf.Length; break;
                        case ConsoleKey.Backspace:
                            if (caret > 0) { buf.Remove(caret - 1, 1); caret--; }
                            break;
                        case ConsoleKey.Delete:
                            if (caret < buf.Length) buf.Remove(caret, 1);
                            break;
                        default:
                            // Control characters (CR/LF included) are never inserted, which
                            // is what keeps the stored value a single line.
                            if (char.IsControl(key.KeyChar)) break;
                            if (f.MaxBytes > 0 &&
                                Encoding.UTF8.GetByteCount(buf.ToString()) + Encoding.UTF8.GetByteCount(key.KeyChar.ToString()) > f.MaxBytes)
                            {
                                Message($"{f.Label} is limited to {f.MaxBytes} bytes.", ConsoleColor.Yellow);
                                break;
                            }
                            buf.Insert(caret, key.KeyChar);
                            caret++;
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
                                ClearMessage();
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

        /// <summary>Split a single-line value into fixed-width display chunks (never fewer than one).</summary>
        internal static List<string> Chunk(string value, int width)
        {
            var chunks = new List<string>();
            if (width < 1) width = 1;
            for (int i = 0; i < value.Length; i += width)
                chunks.Add(value.Substring(i, Math.Min(width, value.Length - i)));
            if (chunks.Count == 0) chunks.Add("");
            return chunks;
        }

        /// <summary>
        /// Small-terminal fallback: the same fields as a top-to-bottom prompt run, each
        /// showing its default in brackets. No cursor movement, but nothing is truncated —
        /// ReadLine takes the whole value and any stray CR/LF is stripped before it is kept.
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
                    Console.WriteLine($"    {f.Label} [{f.Value}]:");
                    var entered = (Console.ReadLine() ?? "").Replace("\r", "").Replace("\n", "");
                    if (entered.Length == 0) continue;
                    if (f.Numeric && !int.TryParse(entered.Trim(), out _))
                    {
                        Console.WriteLine($"    {f.Label} must be an integer.");
                        return false;
                    }
                    if (f.MaxBytes > 0 && Encoding.UTF8.GetByteCount(entered) > f.MaxBytes)
                    {
                        Console.WriteLine($"    {f.Label} is limited to {f.MaxBytes} bytes.");
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
