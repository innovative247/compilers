using System.Text;

namespace ibsCompiler
{
    /// <summary>
    /// One row in a <see cref="ProfilePicker"/> list. The caller owns the presentation
    /// decisions (which details are worth showing, what colour each value is, what text
    /// the filter matches against) so the picker itself stays free of profile knowledge.
    /// </summary>
    internal sealed class PickerItem
    {
        /// <summary>Primary label — the profile name. Rendered bright on the item row.</summary>
        public string Name = "";

        /// <summary>Secondary labels shown dim after the name, e.g. profile aliases.</summary>
        public IReadOnlyList<string> Aliases = Array.Empty<string>();

        /// <summary>
        /// Field rows revealed under the item when it is expanded. Label carries no
        /// colon — the picker adds it and pads to the same column the plain listings use.
        /// </summary>
        public IReadOnlyList<(string Label, string Value, ConsoleColor Color)> Details =
            Array.Empty<(string, string, ConsoleColor)>();

        /// <summary>
        /// Lower-cased haystack the filter terms are matched against. Built by the caller
        /// so a profile can be found by anything it is known by — alias, host, username —
        /// not just by the text on screen.
        /// </summary>
        public string SearchText = "";
    }

    /// <summary>Single = pick one and go; Multi = tick several, then act on all of them.</summary>
    internal enum PickerMode
    {
        Single,
        Multi,
    }

    /// <summary>
    /// Full-screen filterable list picker. Arrow keys move, Right/Left expand and
    /// collapse an item's detail block, typing filters, Enter commits, Esc backs out.
    /// <para>
    /// TTY only — exactly like <see cref="ConsoleMenu"/>, every caller routes a
    /// redirected console (headless suite / piped stdin) to its own sequential prompt
    /// path, so this never drives a <c>ReadKey</c> loop on redirected input.
    /// </para>
    /// </summary>
    internal static class ProfilePicker
    {
        private const int MinRows = 10;
        private const int MinCols = 60;

        /// <summary>
        /// Rows above the viewport (title, hints, blank) plus rows below it (blank,
        /// filter). The viewport is whatever is left.
        /// </summary>
        private const int ViewportTop = 3;
        private const int ChromeRows = 5;

        /// <summary>
        /// Runs the picker over <paramref name="items"/> and returns the chosen indices
        /// into that list, in list order — exactly one in <see cref="PickerMode.Single"/>,
        /// one or more in <see cref="PickerMode.Multi"/>. Returns <c>null</c> when the
        /// developer backed out (Esc) or the window is too small to draw in.
        /// <para>
        /// <paramref name="actionLabel"/> names what Enter will do ("Open", "Share",
        /// "Fetch") so the hint line reads as the action rather than as "confirm".
        /// </para>
        /// <para>
        /// <paramref name="tooSmall"/> separates the two null results: true means the
        /// window could not hold the widget (on entry OR after a shrink mid-session) and
        /// the caller must run its sequential prompt path instead, exactly as the profile
        /// editor's <see cref="ProfileEditorOutcome.TooSmall"/> does. False means the
        /// developer cancelled and nothing should happen.
        /// </para>
        /// </summary>
        internal static List<int>? Pick(string title, IReadOnlyList<PickerItem> items, PickerMode mode,
            string actionLabel, out bool tooSmall)
        {
            tooSmall = false;
            if (items.Count == 0) return null;

            if (!ConsoleMenu.TryEnsureWindow(MinRows, MinCols))
            {
                ConsoleMenu.ExplainTooSmall(title, MinRows, MinCols);
                tooSmall = true;
                return null;
            }

            var filter = new StringBuilder();
            var selected = new HashSet<int>();   // original indices — survives filtering
            var expanded = new HashSet<int>();   // original indices — survives filtering
            var visible = new List<int>();       // original indices passing the filter
            var rows = new List<(int Item, int Detail)>(); // flattened screen rows; Detail -1 = the item row

            int cursor = 0;      // index into `visible`
            int scroll = 0;      // first flattened row drawn in the viewport
            int viewport = 1;
            int lastW = 0, lastH = 0;
            bool fellBack = false;

            // Every term must match, so typing narrows monotonically. The cursor always
            // lands on the first match: after a keystroke the top of the list is the only
            // row the developer is looking at.
            void ApplyFilter()
            {
                var terms = filter.ToString()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(t => t.ToLowerInvariant())
                    .ToList();

                visible.Clear();
                for (int i = 0; i < items.Count; i++)
                {
                    var hay = items[i].SearchText ?? "";
                    if (terms.All(t => hay.Contains(t, StringComparison.OrdinalIgnoreCase)))
                        visible.Add(i);
                }

                cursor = 0;
                scroll = 0;
            }

            void Flatten()
            {
                rows.Clear();
                foreach (var idx in visible)
                {
                    rows.Add((idx, -1));
                    if (expanded.Contains(idx))
                        for (int d = 0; d < items[idx].Details.Count; d++)
                            rows.Add((idx, d));
                }
            }

            int HeaderRow(int pos)
            {
                if (pos < 0 || pos >= visible.Count) return 0;
                int want = visible[pos];
                for (int r = 0; r < rows.Count; r++)
                    if (rows[r].Item == want && rows[r].Detail < 0) return r;
                return 0;
            }

            // Paging is measured in SCREEN rows — an expanded item is several rows tall,
            // so counting items would page past a screenful or fall short of one.
            int PosNearestRow(int targetRow)
            {
                int best = 0, bestDist = int.MaxValue, pos = 0;
                for (int r = 0; r < rows.Count; r++)
                {
                    if (rows[r].Detail >= 0) continue;
                    int dist = Math.Abs(r - targetRow);
                    if (dist < bestDist) { bestDist = dist; best = pos; }
                    pos++;
                }
                return best;
            }

            // Keep the whole expanded block on screen when it fits; when it is taller
            // than the viewport, pin its header to the top so the fields read downward.
            void EnsureVisible()
            {
                if (visible.Count == 0) { scroll = 0; return; }
                int hr = HeaderRow(cursor);
                int block = 1;
                if (expanded.Contains(visible[cursor])) block += items[visible[cursor]].Details.Count;

                if (block <= viewport)
                {
                    if (hr < scroll) scroll = hr;
                    if (hr + block > scroll + viewport) scroll = hr + block - viewport;
                }
                else scroll = hr;

                scroll = Math.Clamp(scroll, 0, Math.Max(0, rows.Count - viewport));
            }

            void Scaffold()
            {
                lastW = Console.WindowWidth; lastH = Console.WindowHeight;
                viewport = Math.Max(1, Console.WindowHeight - ChromeRows);
                Flatten();
                cursor = Math.Clamp(cursor, 0, Math.Max(0, visible.Count - 1));
                EnsureVisible();
            }

            void Render()
            {
                int h = Console.WindowHeight;

                // Title + how much of the list the filter is currently showing.
                var head = new List<(string, ConsoleColor)>
                {
                    ("  ", ConsoleColor.Gray),
                    (title, ConsoleColor.White),
                    (visible.Count == items.Count
                        ? $" ({items.Count})"
                        : $" ({visible.Count} of {items.Count})", ConsoleColor.DarkGray),
                };
                DrawRow(0, head, invert: false);
                DrawRow(1, new List<(string, ConsoleColor)>
                {
                    ("  " + Hints(mode, actionLabel, selected.Count), ConsoleColor.DarkGray),
                }, invert: false);
                DrawRow(2, new List<(string, ConsoleColor)>(), invert: false);

                for (int i = 0; i < viewport; i++)
                {
                    int row = ViewportTop + i;
                    int r = scroll + i;
                    if (r >= rows.Count)
                    {
                        // Nothing left to draw here, but the line may still hold a
                        // previous frame — blank it rather than leave a ghost row.
                        if (r == 0 && visible.Count == 0)
                            DrawRow(row, new List<(string, ConsoleColor)> { ("  No matches.", ConsoleColor.DarkGray) }, invert: false);
                        else
                            DrawRow(row, new List<(string, ConsoleColor)>(), invert: false);
                        continue;
                    }

                    var (itemIdx, detail) = rows[r];
                    var item = items[itemIdx];

                    if (detail >= 0)
                    {
                        var (label, value, colour) = item.Details[detail];
                        DrawRow(row, new List<(string, ConsoleColor)>
                        {
                            ("          " + (label + ":").PadRight(13), ConsoleColor.Gray),
                            (value ?? "", colour),
                        }, invert: false);
                        continue;
                    }

                    bool onCursor = visible.Count > 0 && visible[cursor] == itemIdx;
                    var segs = new List<(string, ConsoleColor)>
                    {
                        ("  " + (onCursor ? "> " : "  "), ConsoleColor.Gray),
                    };
                    if (mode == PickerMode.Multi)
                        segs.Add((selected.Contains(itemIdx) ? "[x] " : "[ ] ", ConsoleColor.Gray));
                    segs.Add((item.Name, ConsoleColor.White));
                    if (item.Aliases.Count > 0)
                        segs.Add(($"  ({string.Join(", ", item.Aliases)})", ConsoleColor.DarkGray));

                    DrawRow(row, segs, invert: onCursor);
                }

                DrawRow(h - 2, new List<(string, ConsoleColor)>(), invert: false);
                // Last — it leaves the caret parked after the typed text, which is what
                // tells the eye that typing filters rather than jumps.
                ConsoleMenu.DrawChoiceBuffer(h - 1, "Filter", filter.ToString());
            }

            // A resize invalidates the viewport height and every cached row — rebuild
            // before the keystroke that noticed it is handled. A shrink below the
            // minimum leaves no room for the list between the hints and the filter line,
            // so the minimum is re-checked on every resize rather than only on entry.
            void RescaffoldIfResized()
            {
                if (Console.WindowWidth == lastW && Console.WindowHeight == lastH) return;
                if (!ConsoleMenu.TryEnsureWindow(MinRows, MinCols))
                {
                    fellBack = true;
                    return;
                }
                try { Console.Clear(); } catch { }
                Scaffold();
                Render();
            }

            void MoveCursor(int delta)
            {
                if (visible.Count == 0) return;
                cursor = Math.Clamp(cursor + delta, 0, visible.Count - 1);
                EnsureVisible();
            }

            void MovePage(int direction)
            {
                if (visible.Count == 0) return;
                cursor = PosNearestRow(HeaderRow(cursor) + direction * viewport);
                EnsureVisible();
            }

            try
            {
                Console.CursorVisible = false;
                try { Console.Clear(); } catch { }
                ApplyFilter();
                Scaffold();
                Render();

                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    RescaffoldIfResized();
                    if (fellBack)
                    {
                        tooSmall = true;
                        return null;
                    }

                    bool ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;

                    if (ctrl && key.Key == ConsoleKey.A && mode == PickerMode.Multi)
                    {
                        // Acts on what is ON SCREEN: with a filter typed, "all" means the
                        // matches, never the whole store hidden behind the filter.
                        bool allOn = visible.Count > 0 && visible.All(selected.Contains);
                        foreach (var idx in visible)
                        {
                            if (allOn) selected.Remove(idx);
                            else selected.Add(idx);
                        }
                        Render();
                        continue;
                    }

                    switch (key.Key)
                    {
                        case ConsoleKey.UpArrow:
                            MoveCursor(-1);
                            break;

                        case ConsoleKey.DownArrow:
                            MoveCursor(1);
                            break;

                        case ConsoleKey.PageUp:
                            MovePage(-1);
                            break;

                        case ConsoleKey.PageDown:
                            MovePage(1);
                            break;

                        case ConsoleKey.Home:
                            MoveCursor(-visible.Count);
                            break;

                        case ConsoleKey.End:
                            MoveCursor(visible.Count);
                            break;

                        case ConsoleKey.RightArrow:
                            if (visible.Count > 0 && expanded.Add(visible[cursor]))
                            {
                                Flatten();
                                EnsureVisible();
                            }
                            break;

                        case ConsoleKey.LeftArrow:
                            if (visible.Count > 0 && expanded.Remove(visible[cursor]))
                            {
                                Flatten();
                                EnsureVisible();
                            }
                            break;

                        case ConsoleKey.Tab:
                            if (mode == PickerMode.Multi && visible.Count > 0)
                            {
                                int idx = visible[cursor];
                                if (!selected.Remove(idx)) selected.Add(idx);
                            }
                            break;

                        case ConsoleKey.Enter:
                        {
                            if (visible.Count == 0) break; // nothing to commit to
                            if (mode == PickerMode.Single)
                                return new List<int> { visible[cursor] };

                            // Enter with nothing ticked means "this one" — the common case
                            // is a single pick and tabbing first would be ceremony.
                            if (selected.Count == 0) selected.Add(visible[cursor]);
                            return Enumerable.Range(0, items.Count).Where(selected.Contains).ToList();
                        }

                        case ConsoleKey.Escape:
                            if (filter.Length > 0)
                            {
                                filter.Clear();
                                ApplyFilter();
                                Flatten();
                                EnsureVisible();
                                break;
                            }
                            return null;

                        case ConsoleKey.Backspace:
                            if (filter.Length > 0)
                            {
                                filter.Length--;
                                ApplyFilter();
                                Flatten();
                                EnsureVisible();
                            }
                            break;

                        default:
                            if (!char.IsControl(key.KeyChar) && key.KeyChar != '\0')
                            {
                                filter.Append(key.KeyChar);
                                ApplyFilter();
                                Flatten();
                                EnsureVisible();
                            }
                            break;
                    }

                    Render();
                }
            }
            finally
            {
                Console.ResetColor();
                Console.CursorVisible = true;
                // Hand the caller a clean screen — whatever it prints next starts at the
                // top instead of underneath a half-erased widget.
                try { Console.Clear(); } catch { }
                // Say why the widget vanished mid-session before the sequential prompts
                // start printing over the top of where it was.
                if (fellBack) ConsoleMenu.ExplainTooSmall(title, MinRows, MinCols);
            }
        }

        /// <summary>The dim key line under the title, with the live selected count.</summary>
        private static string Hints(PickerMode mode, string actionLabel, int selectedCount)
        {
            string up = Glyphs.Up, down = Glyphs.Down, right = Glyphs.Right, left = Glyphs.Left;
            var action = actionLabel.ToLowerInvariant();
            return mode == PickerMode.Single
                ? $"{up}{down} move   {right} expand  {left} collapse   Enter {action}   Esc back   type to filter"
                : $"{up}{down} move   {right} expand  {left} collapse   Tab select   Ctrl+A all   Enter {action} ({selectedCount} selected)   Esc back   type to filter";
        }

        /// <summary>
        /// Draws one padded line as coloured segments, truncating at the window edge.
        /// Never writes the last column: a write there wraps the cursor and scrolls the
        /// whole frame up by one row.
        /// </summary>
        private static void DrawRow(int row, List<(string Text, ConsoleColor Color)> segments, bool invert)
        {
            int w = Math.Max(1, Console.WindowWidth - 1);
            ConsoleMenu.MoveTo(0, row);

            var prevF = Console.ForegroundColor;
            var prevB = Console.BackgroundColor;
            if (invert) Console.BackgroundColor = ConsoleColor.Gray;

            int used = 0;
            foreach (var (text, colour) in segments)
            {
                if (used >= w) break;
                var slice = text.Length > w - used ? text.Substring(0, w - used) : text;
                Console.ForegroundColor = invert ? ConsoleColor.Black : colour;
                Console.Write(slice);
                used += slice.Length;
            }

            if (used < w)
            {
                Console.ForegroundColor = invert ? ConsoleColor.Black : prevF;
                Console.Write(new string(' ', w - used));
            }

            Console.ForegroundColor = prevF;
            Console.BackgroundColor = prevB;
        }

        /// <summary>
        /// Arrow glyphs, with an ASCII fallback for consoles on a legacy code page.
        /// </summary>
        private static class Glyphs
        {
            public static string Up    => ConsoleMenu.SupportsUnicode ? "↑" : "Up";
            public static string Down  => ConsoleMenu.SupportsUnicode ? "↓" : "/Dn";
            public static string Right => ConsoleMenu.SupportsUnicode ? "→" : "Right";
            public static string Left  => ConsoleMenu.SupportsUnicode ? "←" : "Left";
        }
    }
}
