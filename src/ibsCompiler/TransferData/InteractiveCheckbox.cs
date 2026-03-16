namespace ibsCompiler.TransferData
{
    /// <summary>
    /// Cross-platform interactive checkbox selection widget.
    /// Arrow keys to navigate, space to toggle, A=all, N=none, Enter=confirm, Escape/Q=cancel.
    /// </summary>
    public static class InteractiveCheckbox
    {
        public static List<string>? Select(string prompt, List<string> items, HashSet<string>? preSelected = null)
        {
            if (items.Count == 0) return new List<string>();

            // If not interactive, fall back to text input
            if (!Console.IsInputRedirected && Console.IsOutputRedirected)
                return FallbackSelect(prompt, items, preSelected);

            var selected = new HashSet<string>(preSelected ?? new HashSet<string>());
            int cursor = 0;
            int scrollOffset = 0;
            int visibleRows = Math.Min(items.Count, Math.Max(Console.WindowHeight - 4, 5));

            Console.WriteLine(prompt);
            Console.WriteLine("  [Space]=toggle  [A]=all  [N]=none  [Enter]=confirm  [Esc]=cancel");
            Console.WriteLine();

            int startRow = Console.CursorTop;

            void Render()
            {
                Console.SetCursorPosition(0, startRow);
                for (int i = 0; i < visibleRows; i++)
                {
                    int idx = scrollOffset + i;
                    if (idx >= items.Count) break;

                    var marker = selected.Contains(items[idx]) ? "[x]" : "[ ]";
                    var pointer = idx == cursor ? ">" : " ";
                    var line = $"  {pointer} {marker} {items[idx]}";
                    // Pad to clear previous content
                    if (line.Length < Console.WindowWidth - 1)
                        line = line.PadRight(Console.WindowWidth - 1);
                    else
                        line = line.Substring(0, Console.WindowWidth - 1);
                    Console.Write(line);
                    if (i < visibleRows - 1)
                        Console.WriteLine();
                }
                Console.SetCursorPosition(0, startRow + (cursor - scrollOffset));
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
                            if (cursor > 0)
                            {
                                cursor--;
                                if (cursor < scrollOffset)
                                    scrollOffset = cursor;
                            }
                            break;

                        case ConsoleKey.DownArrow:
                            if (cursor < items.Count - 1)
                            {
                                cursor++;
                                if (cursor >= scrollOffset + visibleRows)
                                    scrollOffset = cursor - visibleRows + 1;
                            }
                            break;

                        case ConsoleKey.Spacebar:
                            if (selected.Contains(items[cursor]))
                                selected.Remove(items[cursor]);
                            else
                                selected.Add(items[cursor]);
                            break;

                        case ConsoleKey.A:
                            foreach (var item in items) selected.Add(item);
                            break;

                        case ConsoleKey.N:
                            selected.Clear();
                            break;

                        case ConsoleKey.Enter:
                            Console.SetCursorPosition(0, startRow + visibleRows);
                            Console.WriteLine();
                            var count = selected.Count;
                            Console.WriteLine($"  Selected {count} of {items.Count} items.");
                            return items.Where(i => selected.Contains(i)).ToList();

                        case ConsoleKey.Escape:
                        case ConsoleKey.Q:
                            Console.SetCursorPosition(0, startRow + visibleRows);
                            Console.WriteLine();
                            Console.WriteLine("  Cancelled.");
                            return null;
                    }

                    Render();
                }
            }
            finally
            {
                Console.CursorVisible = true;
            }
        }

        /// <summary>
        /// Interactive checkbox with section headers.
        /// Rows are either selectable items or non-selectable section headers (marked by the headers set).
        /// Section headers are rendered without checkboxes and are skipped during navigation.
        /// </summary>
        public static List<string>? SelectWithSections(
            string prompt,
            List<string> rows,
            HashSet<int> headerIndices,
            HashSet<string>? preSelected = null)
        {
            if (rows.Count == 0) return new List<string>();

            // Build list of selectable indices
            var selectableIndices = new List<int>();
            for (int i = 0; i < rows.Count; i++)
                if (!headerIndices.Contains(i))
                    selectableIndices.Add(i);

            if (selectableIndices.Count == 0) return new List<string>();

            if (!Console.IsInputRedirected && Console.IsOutputRedirected)
                return FallbackSelectWithSections(prompt, rows, headerIndices, preSelected);

            var selected = new HashSet<int>();
            if (preSelected != null)
            {
                foreach (var idx in selectableIndices)
                    if (preSelected.Contains(rows[idx]))
                        selected.Add(idx);
            }

            int cursorPos = 0; // index into selectableIndices
            int scrollOffset = 0;
            int visibleRows = Math.Min(rows.Count, Math.Max(Console.WindowHeight - 4, 5));

            Console.WriteLine(prompt);
            Console.WriteLine();

            int startRow = Console.CursorTop;

            void Render()
            {
                Console.SetCursorPosition(0, startRow);
                for (int i = 0; i < visibleRows; i++)
                {
                    int rowIdx = scrollOffset + i;
                    if (rowIdx >= rows.Count) break;

                    string line;
                    if (headerIndices.Contains(rowIdx))
                    {
                        // Section header — no checkbox, no pointer
                        line = $"  {rows[rowIdx]}";
                    }
                    else
                    {
                        var marker = selected.Contains(rowIdx) ? "[x]" : "[ ]";
                        var pointer = selectableIndices[cursorPos] == rowIdx ? ">" : " ";
                        line = $"  {pointer} {marker}  {rows[rowIdx]}";
                    }

                    if (line.Length < Console.WindowWidth - 1)
                        line = line.PadRight(Console.WindowWidth - 1);
                    else
                        line = line.Substring(0, Console.WindowWidth - 1);
                    Console.Write(line);
                    if (i < visibleRows - 1)
                        Console.WriteLine();
                }

                // Position cursor on the current selectable row
                int cursorRowIdx = selectableIndices[cursorPos];
                Console.SetCursorPosition(0, startRow + (cursorRowIdx - scrollOffset));
            }

            // Print instructions below the list area
            Console.SetCursorPosition(0, startRow + visibleRows);
            Console.WriteLine();
            Console.WriteLine("  [Space]=toggle  [A]=all  [N]=none  [Enter]=confirm  [Esc]=cancel");
            int footerEnd = Console.CursorTop;

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
                            if (cursorPos > 0)
                            {
                                cursorPos--;
                                int idx = selectableIndices[cursorPos];
                                if (idx < scrollOffset)
                                    scrollOffset = Math.Max(0, idx - 1); // show header above if possible
                            }
                            break;

                        case ConsoleKey.DownArrow:
                            if (cursorPos < selectableIndices.Count - 1)
                            {
                                cursorPos++;
                                int idx = selectableIndices[cursorPos];
                                if (idx >= scrollOffset + visibleRows)
                                    scrollOffset = idx - visibleRows + 1;
                            }
                            break;

                        case ConsoleKey.Spacebar:
                        {
                            int idx = selectableIndices[cursorPos];
                            if (selected.Contains(idx))
                                selected.Remove(idx);
                            else
                                selected.Add(idx);
                            break;
                        }

                        case ConsoleKey.A:
                            foreach (var idx in selectableIndices) selected.Add(idx);
                            break;

                        case ConsoleKey.N:
                            selected.Clear();
                            break;

                        case ConsoleKey.Enter:
                            Console.SetCursorPosition(0, footerEnd);
                            Console.WriteLine();
                            return selectableIndices
                                .Where(i => selected.Contains(i))
                                .Select(i => rows[i])
                                .ToList();

                        case ConsoleKey.Escape:
                        case ConsoleKey.Q:
                            Console.SetCursorPosition(0, footerEnd);
                            Console.WriteLine();
                            Console.WriteLine("  Cancelled.");
                            return null;
                    }

                    Render();
                }
            }
            finally
            {
                Console.CursorVisible = true;
            }
        }

        private static List<string>? FallbackSelectWithSections(
            string prompt, List<string> rows, HashSet<int> headerIndices, HashSet<string>? preSelected)
        {
            Console.WriteLine(prompt);
            int num = 1;
            var numToIdx = new Dictionary<int, int>();
            for (int i = 0; i < rows.Count; i++)
            {
                if (headerIndices.Contains(i))
                {
                    Console.WriteLine($"\n  {rows[i]}");
                }
                else
                {
                    var marker = preSelected?.Contains(rows[i]) == true ? "*" : " ";
                    Console.WriteLine($"  {marker} {num}. {rows[i]}");
                    numToIdx[num] = i;
                    num++;
                }
            }
            Console.Write("Enter numbers (comma-separated), A=all, N=none, or blank to cancel: ");
            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input)) return null;
            if (input.ToUpperInvariant() == "A")
                return rows.Where((_, i) => !headerIndices.Contains(i)).ToList();
            if (input.ToUpperInvariant() == "N") return new List<string>();

            var result = new List<string>();
            foreach (var part in input.Split(','))
            {
                if (int.TryParse(part.Trim(), out var n) && numToIdx.ContainsKey(n))
                    result.Add(rows[numToIdx[n]]);
            }
            return result;
        }

        /// <summary>
        /// Fallback for non-interactive terminals: comma-separated numbers.
        /// </summary>
        private static List<string>? FallbackSelect(string prompt, List<string> items, HashSet<string>? preSelected)
        {
            Console.WriteLine(prompt);
            for (int i = 0; i < items.Count; i++)
            {
                var marker = preSelected?.Contains(items[i]) == true ? "*" : " ";
                Console.WriteLine($"  {marker} {i + 1}. {items[i]}");
            }
            Console.Write("Enter numbers (comma-separated), A=all, N=none, or blank to cancel: ");
            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input)) return null;
            if (input.ToUpperInvariant() == "A") return new List<string>(items);
            if (input.ToUpperInvariant() == "N") return new List<string>();

            var result = new List<string>();
            foreach (var part in input.Split(','))
            {
                if (int.TryParse(part.Trim(), out var num) && num >= 1 && num <= items.Count)
                    result.Add(items[num - 1]);
            }
            return result;
        }
    }
}
