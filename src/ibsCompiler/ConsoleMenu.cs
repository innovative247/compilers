using System.Text;

namespace ibsCompiler
{
    /// <summary>
    /// Shared always-visible choice-prompt primitive — the label (<c>Choice: </c> or,
    /// with a default supplied, <c>Choice [x]: </c>) renders the instant the prompt is
    /// entered, before any keystroke; typed characters fill in after it exactly as
    /// before (Backspace edits, Esc clears / cancels). Single source for every
    /// consumer: <see cref="ProfileEditor"/> (which renders the buffer inside its own
    /// interleaved field-nav key loop via <see cref="DrawChoiceBuffer"/>), the
    /// standalone scrolling menus in <c>set_profile</c> and <c>set_messages</c> (which
    /// block on <see cref="ReadDeferredChoice"/>), and the <c>MessageBrowser</c>
    /// scrolling group picker (direct <see cref="DrawChoiceBuffer"/> calls layered over
    /// its own key loop).
    ///
    /// TTY only — every caller routes a redirected console (headless suite / piped
    /// stdin) to the plain <c>Console.ReadLine</c> prompt instead, so this never drives
    /// a <c>ReadKey</c> loop on redirected input.
    /// </summary>
    internal static class ConsoleMenu
    {
        /// <summary>
        /// Builds the rendered label text: <c>"Choice: "</c>, or <c>"Choice [x]: "</c>
        /// when <paramref name="defaultChoice"/> is supplied.
        /// </summary>
        private static string BuildLabel(string label, string? defaultChoice)
            => defaultChoice != null ? $"{label} [{defaultChoice}]: " : $"{label}: ";

        /// <summary>
        /// Draws the choice row — label (always shown) plus whatever has been typed so
        /// far — as <c>  &lt;label&gt;&lt;buf&gt;</c> in cyan at <paramref name="row"/>,
        /// padding the rest of the line to erase prior content, and parks the hardware
        /// caret right after the buffer (the blinking underscore). Call with an empty
        /// <paramref name="buf"/> to render the label alone (entry-time / idle state).
        /// </summary>
        internal static void DrawChoiceBuffer(int row, string label, string buf, string? defaultChoice = null)
        {
            int w = Math.Max(1, Console.WindowWidth - 1);
            Console.SetCursorPosition(0, row);
            var labelText = BuildLabel(label, defaultChoice);
            var line = "  " + labelText + buf;
            line = line.Length < w ? line.PadRight(w) : line.Substring(0, w);
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(line);
            Console.ForegroundColor = prev;
            Console.CursorVisible = true;
            int col = 2 + labelText.Length + buf.Length;
            Console.SetCursorPosition(Math.Min(col, w), row);
        }

        /// <summary>
        /// Blocking always-visible choice reader for a standalone scrolling menu. The
        /// caller renders the numbered menu items first and leaves the cursor on the
        /// prompt line; this owns that single line until the user commits or cancels,
        /// rendering <c>Choice: </c> (or <c>Choice [x]: </c> when
        /// <paramref name="defaultChoice"/> is supplied) immediately, before the first
        /// keystroke.
        /// <para/>
        /// <paramref name="allowText"/> = false accepts digits only (numeric menus);
        /// true also accepts letters and underscore (menus that take a profile name).
        /// <para/>
        /// Returns the committed buffer (non-empty) on Enter. Enter on an EMPTY buffer
        /// selects <paramref name="defaultChoice"/> when one was supplied; with no
        /// default, Enter on empty is a no-op that keeps reading (never accepts a blank
        /// choice). Esc with an empty buffer returns <c>null</c> (cancel / back); Esc
        /// with a non-empty buffer just clears it and keeps reading. Any key that is not
        /// a buffer character / Enter / Backspace / Esc is ignored.
        /// </summary>
        internal static string? ReadDeferredChoice(bool allowText = false, string label = "Choice", string? defaultChoice = null)
        {
            var buf = new StringBuilder();
            int row = Console.CursorTop;
            int w = Math.Max(1, Console.WindowWidth - 1);

            void ClearLine()
            {
                Console.SetCursorPosition(0, row);
                Console.Write(new string(' ', w));
                Console.SetCursorPosition(0, row);
            }

            void Redraw() => DrawChoiceBuffer(row, label, buf.ToString(), defaultChoice);

            bool IsBufChar(char c) => allowText ? (char.IsLetterOrDigit(c) || c == '_') : char.IsDigit(c);

            try
            {
                Redraw(); // always-visible: the label renders before the first keystroke
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    switch (key.Key)
                    {
                        case ConsoleKey.Enter:
                        {
                            if (buf.Length == 0)
                            {
                                if (defaultChoice == null) continue; // no default → re-prompt, never accept blank
                                ClearLine();
                                return defaultChoice;
                            }
                            var result = buf.ToString();
                            ClearLine();
                            return result;
                        }
                        case ConsoleKey.Escape:
                            if (buf.Length > 0) { buf.Clear(); Redraw(); continue; }
                            ClearLine();
                            return null;
                        case ConsoleKey.Backspace:
                            if (buf.Length > 0)
                            {
                                buf.Length--;
                                Redraw();
                            }
                            continue;
                        default:
                            if (IsBufChar(key.KeyChar))
                            {
                                buf.Append(key.KeyChar);
                                Redraw();
                            }
                            continue;
                    }
                }
            }
            finally { Console.CursorVisible = false; }
        }
    }
}
