namespace ibsCompiler
{
    /// <summary>
    /// A small vertical field form for the message browser: every field of an add /
    /// edit / translate is on screen at once, Up/Down moves between them, Enter edits
    /// the focused one. It is the TUI counterpart of the SBN GUI's message detail
    /// panel, where s#msgno, company, language and the text are all visible together
    /// instead of arriving as a chain of one-shot prompts.
    ///
    /// Deliberately simpler than <see cref="ProfileEditor"/>: no dirty markers, no
    /// action menu, no live applicability. Read-only rows (the reserved s#msgno, an
    /// edit's key columns) render dim and are skipped by the cursor.
    /// </summary>
    internal static class MessageForm
    {
        /// <summary>One row of the form. <see cref="Value"/> carries the default in and the answer out.</summary>
        internal sealed class Field
        {
            public string Label { get; set; } = "";
            public string Value { get; set; } = "";
            /// <summary>Displayed but not editable — the cursor skips it.</summary>
            public bool ReadOnly { get; set; }
            /// <summary>Reject anything that is not an integer.</summary>
            public bool Numeric { get; set; }
            /// <summary>Dim trailing note, e.g. "(reserved)" or "(base message)".</summary>
            public string? Note { get; set; }
        }

        /// <summary>
        /// Render <paramref name="fields"/> and drive the edit loop until the user saves or
        /// cancels. <paramref name="validate"/> runs on save and returns an error string to
        /// keep the form open, or null to accept. Returns false when the user cancelled.
        /// </summary>
        public static bool Run(string title, IList<Field> fields, Func<IList<Field>, string?>? validate = null)
        {
            // Never drive a ReadKey loop on a redirected console; callers fall back to
            // sequential prompts. The browser already guards this, so this is belt-and-braces.
            if (Console.IsInputRedirected || Console.IsOutputRedirected) return false;

            // Layout: blank + title + blank + N rows + blank + footer + blank + prompt row,
            // plus one spare row below it so the Enter that ends an inline ReadLine never
            // scrolls the buffer out from under the cached row numbers.
            if (Console.WindowHeight < fields.Count + 8 || Console.WindowWidth < 40)
                return RunSequential(title, fields, validate);

            const string footer = "  [Up/Down] move  [Enter] edit  [S] save  [Esc] cancel";

            Console.WriteLine();
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  " + title);
            Console.ForegroundColor = prev;
            Console.WriteLine();
            for (int i = 0; i < fields.Count; i++) Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine(footer);
            Console.WriteLine();
            // Reserve the prompt row AND the spare row under it via WriteLine (which scrolls
            // the buffer if needed, unlike SetCursorPosition), then work back from where the
            // cursor landed so every cached row number is guaranteed to exist.
            Console.WriteLine();
            Console.WriteLine();
            int promptRow = Console.CursorTop - 2;
            int startRow = promptRow - 2 - fields.Count - 1;

            int cursor = FirstEditable(fields);
            if (cursor < 0) cursor = 0;

            void DrawRow(int idx, bool isCursor)
            {
                var f = fields[idx];
                Console.SetCursorPosition(0, startRow + idx);
                var pointer = isCursor && !f.ReadOnly ? ">" : " ";
                var note = string.IsNullOrEmpty(f.Note) ? "" : $"   {f.Note}";
                var line = $"  {pointer} {f.Label,-16}: {f.Value}{note}";
                line = Fit(line);
                if (f.ReadOnly)
                {
                    var p = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write(line);
                    Console.ForegroundColor = p;
                }
                else Console.Write(line);
            }

            void Render()
            {
                for (int i = 0; i < fields.Count; i++) DrawRow(i, i == cursor);
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

            void ClearPrompt()
            {
                Console.SetCursorPosition(0, promptRow);
                Console.Write(Fit(""));
            }

            try
            {
                Console.CursorVisible = false;
                Render();
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    switch (key.Key)
                    {
                        case ConsoleKey.UpArrow:
                            cursor = StepCursor(fields, cursor, -1);
                            Render();
                            break;
                        case ConsoleKey.DownArrow:
                            cursor = StepCursor(fields, cursor, +1);
                            Render();
                            break;
                        case ConsoleKey.Escape:
                            ClearPrompt();
                            Console.SetCursorPosition(0, promptRow);
                            Console.WriteLine();
                            return false;
                        case ConsoleKey.Enter:
                            {
                                var f = fields[cursor];
                                if (f.ReadOnly) break;
                                ClearPrompt();
                                Console.SetCursorPosition(0, promptRow);
                                Console.CursorVisible = true;
                                Console.Write($"  {f.Label} [{f.Value}]: ");
                                var entered = Console.ReadLine() ?? "";
                                Console.CursorVisible = false;
                                if (entered.Length > 0)
                                {
                                    if (f.Numeric && !int.TryParse(entered.Trim(), out _))
                                    {
                                        Message($"{f.Label} must be an integer.", ConsoleColor.Red);
                                        Render();
                                        break;
                                    }
                                    f.Value = f.Numeric ? entered.Trim() : entered;
                                }
                                ClearPrompt();
                                Render();
                                break;
                            }
                        default:
                            // S saves from anywhere; a stray key just redraws.
                            if (key.Key == ConsoleKey.S && (key.Modifiers & ConsoleModifiers.Control) == 0)
                            {
                                var error = validate?.Invoke(fields);
                                if (error != null)
                                {
                                    Message(error, ConsoleColor.Red);
                                    Render();
                                    break;
                                }
                                ClearPrompt();
                                Console.SetCursorPosition(0, promptRow);
                                Console.WriteLine();
                                return true;
                            }
                            break;
                    }
                }
            }
            finally { Console.CursorVisible = true; }
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

        /// <summary>Move the cursor by <paramref name="delta"/>, skipping read-only rows and stopping at the ends.</summary>
        private static int StepCursor(IList<Field> fields, int cursor, int delta)
        {
            int i = cursor + delta;
            while (i >= 0 && i < fields.Count && fields[i].ReadOnly) i += delta;
            return (i >= 0 && i < fields.Count) ? i : cursor;
        }

        private static string Fit(string s)
        {
            int w = Math.Max(1, Console.WindowWidth - 1);
            return s.Length < w ? s.PadRight(w) : s.Substring(0, w);
        }
    }
}
