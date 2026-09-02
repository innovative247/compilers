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
        /// Ensures the console window is at least <paramref name="rows"/> x
        /// <paramref name="cols"/> before a full-screen widget draws, growing it
        /// where the host allows: classic conhost (Windows PowerShell / cmd) honors
        /// <c>Console.SetWindowSize</c>, while Windows Terminal and Unix terminals
        /// ignore or refuse it — the attempt is best-effort and any refusal is
        /// swallowed. Returns true when the window meets the minimum afterwards;
        /// on false the caller falls back (see <see cref="ExplainTooSmall"/>).
        /// </summary>
        internal static bool TryEnsureWindow(int rows, int cols)
        {
            try
            {
                if (Console.WindowHeight >= rows && Console.WindowWidth >= cols) return true;
                if (!OperatingSystem.IsWindows()) return false;
                int w = Math.Min(Math.Max(Console.WindowWidth, cols), Console.LargestWindowWidth);
                int h = Math.Min(Math.Max(Console.WindowHeight, rows), Console.LargestWindowHeight);
                // Grow the buffer first — SetWindowSize refuses a window larger than
                // the buffer. Only ever grow; shrinking the buffer clips scrollback.
                if (Console.BufferWidth < w) Console.BufferWidth = w;
                if (Console.BufferHeight < h) Console.BufferHeight = h;
                Console.SetWindowSize(w, h);
                return Console.WindowHeight >= rows && Console.WindowWidth >= cols;
            }
            catch { return false; }
        }

        /// <summary>
        /// Resize-safe replacement for <c>Console.SetCursorPosition</c>. Every full-screen
        /// widget caches absolute row numbers at scaffold time; shrinking the window makes
        /// those rows fall outside the (now smaller) buffer, and the raw call throws an
        /// <see cref="ArgumentOutOfRangeException"/> mid-render. Clamp into the current
        /// buffer instead and swallow anything the host still refuses — the widget's
        /// resize-aware key loop re-scaffolds on the next keystroke, so a clamped frame is
        /// at worst one stale redraw, never a crash.
        /// </summary>
        internal static void MoveTo(int left, int top)
        {
            try
            {
                int maxLeft = Math.Max(0, Console.BufferWidth - 1);
                int maxTop = Math.Max(0, Console.BufferHeight - 1);
                Console.SetCursorPosition(
                    Math.Clamp(left, 0, maxLeft),
                    Math.Clamp(top, 0, maxTop));
            }
            catch { }
        }

        /// <summary>
        /// Actionable companion to a failed <see cref="TryEnsureWindow"/>: tells the
        /// user the exact size the widget needs, what this window is, and how to get
        /// the full-screen experience next time.
        /// </summary>
        internal static void ExplainTooSmall(string what, int rows, int cols)
        {
            Console.WriteLine();
            Console.WriteLine($"  Terminal too small for {what} — it needs {cols} cols x {rows} rows; this window is {Console.WindowWidth}x{Console.WindowHeight}.");
            Console.WriteLine("  Falling back to sequential prompts. Enlarge or maximize the window and rerun the command for the full-screen editor.");
        }

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
            MoveTo(0, row);
            var labelText = BuildLabel(label, defaultChoice);
            var line = "  " + labelText + buf;
            line = line.Length < w ? line.PadRight(w) : line.Substring(0, w);
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(line);
            Console.ForegroundColor = prev;
            Console.CursorVisible = true;
            int col = 2 + labelText.Length + buf.Length;
            MoveTo(Math.Min(col, w), row);
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
            int lastW = Console.WindowWidth, lastH = Console.WindowHeight;

            // Never cache the width — a resize between keystrokes changes it under us.
            int W() => Math.Max(1, Console.WindowWidth - 1);

            void ClearLine()
            {
                MoveTo(0, row);
                Console.Write(new string(' ', W()));
                MoveTo(0, row);
            }

            void Redraw() => DrawChoiceBuffer(row, label, buf.ToString(), defaultChoice);

            bool IsBufChar(char c) => allowText ? (char.IsLetterOrDigit(c) || c == '_') : char.IsDigit(c);

            // This primitive owns ONE line layered over a plain scrolling menu the CALLER
            // printed with WriteLine — it has no scaffold of its own to rebuild, and
            // clearing the screen would wipe the caller's menu. On a resize the prompt
            // line may have moved: a same-buffer terminal reflows and scrolls the menu
            // text (so the absolute row is stale), while conhost leaves it where it was.
            // The hardware caret is the one thing that tracks the line through either —
            // DrawChoiceBuffer parks it on the prompt row after every draw — so re-home
            // `row` from the LIVE cursor position, then redraw the prompt there.
            void SyncOnResize()
            {
                if (Console.WindowWidth == lastW && Console.WindowHeight == lastH) return;
                lastW = Console.WindowWidth; lastH = Console.WindowHeight;
                try { row = Console.CursorTop; } catch { }
                row = Math.Clamp(row, 0, Math.Max(0, Console.BufferHeight - 1));
                Redraw();
            }

            try
            {
                Redraw(); // always-visible: the label renders before the first keystroke
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    SyncOnResize();
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
