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
