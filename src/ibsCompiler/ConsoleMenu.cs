using System.Text;

namespace ibsCompiler
{
    /// <summary>
    /// Shared "deferred choice" menu-entry primitive — the profile-hub editor pattern
    /// (nothing shown until the user types; the first keystroke reveals
    /// <c>Choice: 9_</c> on a prompt line with the caret parked as the trailing
    /// underscore; Enter commits, Backspace edits, Esc clears / cancels). Single source
    /// for both consumers: <see cref="ProfileEditor"/> (which renders the buffer inside
    /// its own interleaved field-nav key loop via <see cref="DrawChoiceBuffer"/>) and the
    /// standalone scrolling menus in <c>set_profile</c> (which block on
    /// <see cref="ReadDeferredChoice"/>).
    ///
    /// TTY only — every caller routes a redirected console (headless suite / piped
    /// stdin) to the plain <c>Console.ReadLine</c> prompt instead, so this never drives
    /// a <c>ReadKey</c> loop on redirected input.
    /// </summary>
    internal static class ConsoleMenu
    {
        /// <summary>
        /// Draws the in-progress choice buffer as <c>  &lt;label&gt;&lt;buf&gt;</c> in cyan at
        /// <paramref name="row"/>, padding the rest of the line to erase prior content, and
        /// parks the hardware caret right after the buffer (the blinking underscore).
        /// </summary>
        internal static void DrawChoiceBuffer(int row, string label, string buf)
        {
            int w = Math.Max(1, Console.WindowWidth - 1);
            Console.SetCursorPosition(0, row);
            var line = "  " + label + buf;
            line = line.Length < w ? line.PadRight(w) : line.Substring(0, w);
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(line);
            Console.ForegroundColor = prev;
            Console.CursorVisible = true;
            int col = 2 + label.Length + buf.Length;
            Console.SetCursorPosition(Math.Min(col, w), row);
        }

        /// <summary>
        /// Blocking deferred-choice reader for a standalone scrolling menu. The caller
        /// renders the numbered menu items first and leaves the cursor on the (blank)
        /// prompt line; this owns that single line until the user commits or cancels.
        /// <para/>
        /// <paramref name="allowText"/> = false accepts digits only (numeric menus);
        /// true also accepts letters and underscore (menus that take a profile name).
        /// <para/>
        /// Returns the committed buffer (non-empty) on Enter, <c>""</c> on Enter with an
        /// empty buffer (blank commit), or <c>null</c> when Esc is pressed with an empty
        /// buffer (cancel / back). Esc with a non-empty buffer just clears it and keeps
        /// reading; any key that is not a buffer character / Enter / Backspace / Esc is
        /// ignored.
        /// </summary>
        internal static string? ReadDeferredChoice(bool allowText = false, string label = "Choice: ")
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

            bool IsBufChar(char c) => allowText ? (char.IsLetterOrDigit(c) || c == '_') : char.IsDigit(c);

            try
            {
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    switch (key.Key)
                    {
                        case ConsoleKey.Enter:
                        {
                            var result = buf.ToString();
                            ClearLine();
                            return result; // "" when the buffer is empty
                        }
                        case ConsoleKey.Escape:
                            if (buf.Length > 0) { buf.Clear(); ClearLine(); continue; }
                            ClearLine();
                            return null;
                        case ConsoleKey.Backspace:
                            if (buf.Length > 0)
                            {
                                buf.Length--;
                                if (buf.Length == 0) ClearLine();
                                else DrawChoiceBuffer(row, label, buf.ToString());
                            }
                            continue;
                        default:
                            if (IsBufChar(key.KeyChar))
                            {
                                buf.Append(key.KeyChar);
                                DrawChoiceBuffer(row, label, buf.ToString());
                            }
                            continue;
                    }
                }
            }
            finally { Console.CursorVisible = false; }
        }
    }
}
