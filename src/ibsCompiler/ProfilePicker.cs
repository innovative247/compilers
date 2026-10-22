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

        /// <summary>Shown after the aliases on the item row, e.g. who shared it. Empty for none.</summary>
        public string Tag = "";

        /// <summary>
        /// Short state label drawn in its own column before the name, e.g. "New". Empty
        /// for none; when no row has one the column is not drawn at all.
        /// </summary>
        public string Status = "";

        /// <summary>Colour of <see cref="Status"/>.</summary>
        public ConsoleColor StatusColor = ConsoleColor.Gray;

        /// <summary>Colour of <see cref="Name"/> on the item row.</summary>
        public ConsoleColor NameColor = ConsoleColor.White;

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
    /// What a picker hands back: the menu choice that ended it (menu pickers only, see
    /// <see cref="ProfilePicker.PickWithMenu"/>), the marked rows (or the row under the
    /// cursor when none are marked) and the row under the cursor on its own, all as
    /// indices into the item list.
    /// </summary>
    internal sealed class PickerResult
    {
        /// <summary>The menu number entered at "Choice:"; empty for a plain pick.</summary>
        public string Choice = "";
        /// <summary>The filter text when the picker ended, so a redraw can put it back.</summary>
        public string Filter = "";
        public List<int> Picked = new();
        /// <summary>-1 when the filter left nothing on screen.</summary>
        public int Cursor = -1;
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
        /// Rows below the viewport: a blank separator, the "showing N of M" line and the
        /// "Filter:" line on the bottom row. A menu picker adds its numbered menu above
        /// them and the "Choice:" line under them. Above the viewport sit the title and
        /// the hint line(s), which can wrap, so the viewport top is worked out per layout.
        /// </summary>
        private const int FooterRows = 3;

        /// <summary>
        /// Runs the picker over <paramref name="items"/> and returns the chosen indices
        /// into that list, in list order — exactly one in <see cref="PickerMode.Single"/>,
        /// one or more in <see cref="PickerMode.Multi"/>. Returns <c>null</c> when the
        /// developer backed out (Esc) or the window is too small to draw in.
        /// <para>
        /// <paramref name="actionLabel"/> names what Enter will do ("Open", "Share",
        /// "Sync") so the hint line reads as the action rather than as "confirm".
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
            return Run(title, items, mode, actionLabel, null, 0, out tooSmall)?.Picked;
        }

        /// <summary>
        /// Multi-select picker with a numbered menu directly under the list: Tab marks,
        /// digits fill the green "Choice:" line and Enter submits a number from
        /// <paramref name="menu"/> (Enter on an empty choice does nothing); letters, space,
        /// '-', '_' and '.' filter as in the plain picker. Esc backs out from either line.
        /// <paramref name="startCursor"/> puts the cursor back on the row the caller last
        /// acted on, <paramref name="startFilter"/> puts back the filter it was using, and
        /// <paramref name="legend"/> (optional) is one coloured line drawn above the menu.
        /// Returns null on Esc (Back) or when <paramref name="tooSmall"/>.
        /// </summary>
        internal static PickerResult? PickWithMenu(string title, IReadOnlyList<PickerItem> items,
            IReadOnlyList<(string Num, string Label)> menu, int startCursor, out bool tooSmall,
            string startFilter = "", IReadOnlyList<(string Text, ConsoleColor Color)>? legend = null)
        {
            return Run(title, items, PickerMode.Multi, "", menu, startCursor, out tooSmall, startFilter, legend);
        }

        private static PickerResult? Run(string title, IReadOnlyList<PickerItem> items, PickerMode mode,
            string actionLabel, IReadOnlyList<(string Num, string Label)>? menu, int startCursor, out bool tooSmall,
            string startFilter = "", IReadOnlyList<(string Text, ConsoleColor Color)>? legend = null)
        {
            tooSmall = false;
            if (items.Count == 0) return null;
            bool keyed = menu != null;
            if (!keyed) legend = null;
            // Menu block: blank + legend + one row per entry, then the Choice row under the filter.
            int menuRows = keyed ? menu!.Count + 2 + (legend != null ? 1 : 0) : 0;
            int minRows = MinRows + menuRows;
            var choice = new StringBuilder();

            if (!ConsoleMenu.TryEnsureWindow(minRows, MinCols))
            {
                ConsoleMenu.ExplainTooSmall(title, minRows, MinCols);
                tooSmall = true;
                return null;
            }

            var filter = new StringBuilder(startFilter ?? "");
            var selected = new HashSet<int>();   // original indices — survives filtering
            var expanded = new HashSet<int>();   // original indices — survives filtering
            var visible = new List<int>();       // original indices passing the filter
            var rows = new List<(int Item, int Detail)>(); // flattened screen rows; Detail -1 = the item row

            int cursor = 0;      // index into `visible`
            int scroll = 0;      // first flattened row drawn in the viewport
            int viewport = 1;
            int viewportTop = 2; // title + hint line(s)
            int lastW = 0, lastH = 0;
            bool fellBack = false;
            // Menu pickers only: whether the last thing typed went to Choice (Backspace edits it).
            bool filterFocus = false;  // menu picker: which input line has the caret (Choice first)
            // Status column width; 0 = no row has a status, so no column.
            int statusWidth = items.Max(it => (it.Status ?? "").Length);

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

            // Hint lines are packed for the widest selected count so the layout does not
            // shift as rows are marked.
            List<string> HintLines(int selectedCount) =>
                WrapHints(Hints(mode, actionLabel, selectedCount, keyed), Math.Max(1, Console.WindowWidth - 3));

            void Scaffold()
            {
                lastW = Console.WindowWidth; lastH = Console.WindowHeight;
                viewportTop = 1 + HintLines(items.Count).Count;
                viewport = Math.Max(1, Console.WindowHeight - viewportTop - FooterRows - menuRows);
                Flatten();
                cursor = Math.Clamp(cursor, 0, Math.Max(0, visible.Count - 1));
                EnsureVisible();
            }

            void Render()
            {
                int h = Console.WindowHeight;
                // Hide the caret while drawing, or it is seen chasing every row written;
                // the menu picker shows it again on its input line at the end.
                ConsoleMenu.HideCaret();

                DrawRow(0, new List<(string, ConsoleColor)>
                {
                    ("  ", ConsoleColor.Gray),
                    (title, ConsoleColor.White),
                }, invert: false);
                var hintLines = HintLines(selected.Count);
                for (int hr = 1; hr < viewportTop; hr++)
                {
                    DrawRow(hr, new List<(string, ConsoleColor)>
                    {
                        ("  " + (hr - 1 < hintLines.Count ? hintLines[hr - 1] : ""), ConsoleColor.Gray),
                    }, invert: false);
                }

                // Plain picker: filter on the bottom row, under the list it narrows.
                if (!keyed)
                {
                    DrawRow(h - 3, new List<(string, ConsoleColor)>(), invert: false);
                    DrawRow(h - 2, new List<(string, ConsoleColor)>
                    {
                        ($"  showing {visible.Count} of {items.Count}   (type to filter, case-insensitive)", ConsoleColor.Gray),
                    }, invert: false);
                    DrawRow(h - 1, new List<(string, ConsoleColor)>
                    {
                        ($"  Filter: {filter}_", ConsoleColor.Cyan),
                    }, invert: false);
                }

                // A menu picker draws only as many list rows as there are (up to the
                // viewport) so its menu sits directly under the last one.
                int listRows = keyed ? Math.Min(viewport, Math.Max(1, rows.Count)) : viewport;
                for (int i = 0; i < listRows; i++)
                {
                    int row = viewportTop + i;
                    int r = scroll + i;
                    if (r >= rows.Count)
                    {
                        // Nothing left to draw here, but the line may still hold a
                        // previous frame — blank it rather than leave a ghost row.
                        if (r == 0 && visible.Count == 0)
                            DrawRow(row, new List<(string, ConsoleColor)> { ("  No matches.", ConsoleColor.Gray) }, invert: false);
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
                    if (statusWidth > 0)
                        segs.Add(((item.Status ?? "").PadRight(statusWidth + 2), item.StatusColor));
                    segs.Add((item.Name, item.NameColor));
                    if (item.Aliases.Count > 0)
                        segs.Add(($"  ({string.Join(", ", item.Aliases)})", ConsoleColor.Gray));
                    if (item.Tag.Length > 0)
                        segs.Add(($"  {item.Tag}", ConsoleColor.Gray));

                    DrawRow(row, segs, invert: onCursor);
                }

                if (!keyed) return;

                // Menu picker footer, straight under the list: blank, numbered menu,
                // blank, "showing N of M" (only while filtered), Filter, Choice. Rows
                // below it are blanked - the list can shrink as the filter narrows.
                int r0 = viewportTop + listRows;
                DrawRow(r0++, new List<(string, ConsoleColor)>(), invert: false);
                if (legend != null)
                {
                    var legendSegs = new List<(string, ConsoleColor)> { ("  ", ConsoleColor.Gray) };
                    legendSegs.AddRange(legend);
                    DrawRow(r0++, legendSegs, invert: false);
                }
                foreach (var (num, label) in menu!)
                {
                    DrawRow(r0++, new List<(string, ConsoleColor)>
                    {
                        ("  ", ConsoleColor.Gray),
                        ($"{num}.".PadRight(4), ConsoleColor.Cyan),
                        (label, ConsoleColor.Gray),
                    }, invert: false);
                }
                DrawRow(r0++, new List<(string, ConsoleColor)>(), invert: false);
                if (filter.Length > 0)
                {
                    DrawRow(r0++, new List<(string, ConsoleColor)>
                    {
                        ($"  showing {visible.Count} of {items.Count}", ConsoleColor.Gray),
                    }, invert: false);
                }
                // Two input lines; the focused one is Green with the caret and says how to move on.
                int filterRow = r0++, choiceRow = r0++;
                DrawRow(filterRow, new List<(string, ConsoleColor)>
                {
                    ("  Filter: ", filterFocus ? ConsoleColor.Green : ConsoleColor.Gray),
                    (filter.ToString(), ConsoleColor.Gray),
                    (filterFocus ? "   (Enter or Tab: go to Choice)" : "", ConsoleColor.Gray),
                }, invert: false);
                DrawRow(choiceRow, new List<(string, ConsoleColor)>
                {
                    ("  Choice: ", filterFocus ? ConsoleColor.Gray : ConsoleColor.Green),
                    (choice.ToString(), ConsoleColor.Gray),
                    (filterFocus ? "" : "   (Tab: back to Filter)", ConsoleColor.Gray),
                }, invert: false);
                for (; r0 < h; r0++) DrawRow(r0, new List<(string, ConsoleColor)>(), invert: false);
                int caretCol = filterFocus ? 10 + filter.Length : 10 + choice.Length;
                ConsoleMenu.ShowCaretAt(Math.Min(caretCol, Math.Max(0, Console.WindowWidth - 2)), filterFocus ? filterRow : choiceRow);
            }

            PickerResult Result(string chosen)
            {
                var result = new PickerResult { Choice = chosen, Filter = filter.ToString() };
                if (visible.Count > 0) result.Cursor = visible[cursor];
                if (selected.Count > 0)
                    result.Picked = Enumerable.Range(0, items.Count).Where(selected.Contains).ToList();
                else if (visible.Count > 0)
                    result.Picked = new List<int> { visible[cursor] };
                return result;
            }

            void Refilter()
            {
                ApplyFilter();
                Flatten();
                EnsureVisible();
            }

            // A resize invalidates the viewport height and every cached row — rebuild
            // before the keystroke that noticed it is handled. A shrink below the
            // minimum leaves no room for the header, list and filter lines, so the
            // minimum is re-checked on every resize rather than only on entry.
            void RescaffoldIfResized()
            {
                if (Console.WindowWidth == lastW && Console.WindowHeight == lastH) return;
                if (!ConsoleMenu.TryEnsureWindow(minRows, MinCols))
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
                cursor = Math.Max(0, visible.IndexOf(startCursor));
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
                    bool alt = (key.Modifiers & ConsoleModifiers.Alt) != 0;

                    // Menu picker: two input lines. Filter has the caret first; Enter or Tab moves
                    // to Choice, where a menu number and Enter runs it and Tab goes back.
                    if (keyed && !ctrl && !alt)
                    {
                        bool filterChar = char.IsLetterOrDigit(key.KeyChar) || key.KeyChar is '-' or '_' or '.'
                                          || (key.KeyChar == ' ' && filter.Length > 0);
                        if (filterFocus)
                        {
                            if (filterChar)
                            {
                                filter.Append(key.KeyChar);
                                Refilter();
                                Render();
                                continue;
                            }
                            if (key.Key is ConsoleKey.Enter or ConsoleKey.Tab)
                            {
                                filterFocus = false;
                                Render();
                                continue;
                            }
                            if (key.Key == ConsoleKey.Backspace)
                            {
                                if (filter.Length > 0) { filter.Length--; Refilter(); }
                                Render();
                                continue;
                            }
                            if (key.Key == ConsoleKey.Escape) return null;   // Back, from either line
                        }
                        else
                        {
                            if (char.IsDigit(key.KeyChar))
                            {
                                if (choice.Length < 3) choice.Append(key.KeyChar);
                                Render();
                                continue;
                            }
                            if (filterChar && key.KeyChar != ' ')
                            {
                                // A letter typed here is meant for the filter.
                                filterFocus = true;
                                choice.Clear();
                                filter.Append(key.KeyChar);
                                Refilter();
                                Render();
                                continue;
                            }
                            if (key.Key == ConsoleKey.Enter)
                            {
                                // Empty choice: nothing. Not on the menu: clear it and keep reading.
                                if (choice.Length == 0) continue;
                                var chosen = choice.ToString();
                                choice.Clear();
                                if (menu!.Any(m => m.Num == chosen)) return Result(chosen);
                                Render();
                                continue;
                            }
                            if (key.Key == ConsoleKey.Escape) return null;   // Back, from either line
                            if (key.Key == ConsoleKey.Tab
                                || (key.Key == ConsoleKey.Backspace && choice.Length == 0))
                            {
                                filterFocus = true;
                                choice.Clear();
                                Render();
                                continue;
                            }
                            if (key.Key == ConsoleKey.Backspace)
                            {
                                choice.Length--;
                                Render();
                                continue;
                            }
                        }
                        // Any other printable key is neither a choice nor filter input.
                        if (!char.IsControl(key.KeyChar) && key.KeyChar != '\0' && key.KeyChar != ' ') continue;
                    }

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

                        case ConsoleKey.Spacebar when keyed:
                        case ConsoleKey.Tab when !keyed:
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
                                return new PickerResult { Cursor = visible[cursor], Picked = new List<int> { visible[cursor] } };

                            // Enter with nothing ticked means "this one" — the common case
                            // is a single pick and tabbing first would be ceremony.
                            return Result("");
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
                if (fellBack) ConsoleMenu.ExplainTooSmall(title, minRows, MinCols);
            }
        }

        /// <summary>
        /// The key hints under the title, one entry per key, with the live selected
        /// count. A menu picker's actions are its numbered menu, not hints.
        /// </summary>
        private static List<string> Hints(PickerMode mode, string actionLabel, int selectedCount, bool menu)
        {
            string up = Glyphs.Up, down = Glyphs.Down, right = Glyphs.Right, left = Glyphs.Left;
            var action = actionLabel.ToLowerInvariant();
            if (menu)
            {
                return new List<string>
                {
                    $"{up}{down} move",
                    $"{right} details  {left} collapse",
                    $"Space mark ({selectedCount} marked)",
                    "Esc back",
                };
            }
            return mode switch
            {
                PickerMode.Single => new List<string> { $"{up}{down} move", $"{right} expand  {left} collapse", $"Enter {action}", "Backspace trim", "Esc back" },
                _ => new List<string> { $"{up}{down} move", $"{right} expand  {left} collapse", "Tab select", "Ctrl+A all", $"Enter {action} ({selectedCount} selected)", "Backspace trim", "Esc back" },
            };
        }

        /// <summary>Packs hint entries into lines no wider than <paramref name="width"/>, never splitting one.</summary>
        private static List<string> WrapHints(List<string> hints, int width)
        {
            var lines = new List<string>();
            var line = new StringBuilder();
            foreach (var hint in hints)
            {
                if (line.Length > 0 && line.Length + 3 + hint.Length > width)
                {
                    lines.Add(line.ToString());
                    line.Clear();
                }
                if (line.Length > 0) line.Append("   ");
                line.Append(hint);
            }
            if (line.Length > 0) lines.Add(line.ToString());
            return lines;
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
