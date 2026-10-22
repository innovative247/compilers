using System.Text;

namespace ibsCompiler.Database
{
    /// <summary>
    /// One column of a result set as the isql-style formatter lays it out. Platform-neutral:
    /// each executor maps its driver's column metadata to a display width and alignment
    /// (rules in docs/architecture.md, "isql output format").
    /// </summary>
    internal sealed class IsqlColumn
    {
        public string Name = "";
        /// <summary>Display width: max(type width, name length).</summary>
        public int Width;
        /// <summary>Numbers, money and dates are right-aligned; everything else left.</summary>
        public bool RightAlign;
        /// <summary>text/image values are printed whole; isql never breaks them at the line width.</summary>
        public bool NoChunk;
    }

    /// <summary>
    /// Renders result sets, row counts, return statuses and server messages exactly as the
    /// Sybase OCS isql client prints them, line by line through an emit callback. It is fed in
    /// server order (result-set start, rows, done, return status, messages) by the executor.
    /// </summary>
    internal sealed class IsqlFormatter
    {
        /// <summary>isql's page width when none is given (-w) and the output is not a console.</summary>
        public const int DefaultWidth = 80;
        /// <summary>isql never displays a column wider than this.</summary>
        public const int MaxColumnWidth = 512;
        private const int TabWidth = 8;

        private readonly Action<string> _emit;
        private readonly int _width;
        private IsqlColumn[] _cols = Array.Empty<IsqlColumn>();
        private bool[] _breakBefore = Array.Empty<bool>();
        // A result set has been displayed and its count not yet printed: the count follows a blank line.
        private bool _afterResultSet;
        // A result set has started but its heading is not printed yet.
        private bool _headerPending;
        // The latest row count the server flagged and isql has not printed yet (see Done).
        private int? _heldCount;
        // A compute row ended a group: the next row of the result set repeats the heading.
        private bool _reheader;

        /// <summary>
        /// The text it is fed is UTF-8 "byte text" (one character per byte, as the Sybase executor
        /// feeds a utf8 session's text), so a value is never cut between the bytes of one character.
        /// </summary>
        public bool Utf8ByteText { get; init; }

        public IsqlFormatter(Action<string> emit, int width)
        {
            _emit = emit;
            _width = width > TabWidth + 2 ? width : DefaultWidth;
        }

        /// <summary>
        /// The width to format at: the console window's when writing to a console, else isql's
        /// default of 80 (redirected output, -O files, captured output).
        /// </summary>
        public static int ResolveWidth(bool captured, string outputFile)
        {
            if (captured || !string.IsNullOrEmpty(outputFile) || !string.IsNullOrEmpty(ibs_compiler_common.DefaultOutFile))
                return DefaultWidth;
            try
            {
                bool redirected = ibs_compiler_common.OutputToStdErr ? Console.IsErrorRedirected : Console.IsOutputRedirected;
                if (!redirected && Console.WindowWidth > 0) return Console.WindowWidth;
            }
            catch { }
            return DefaultWidth;
        }

        public void BeginResultSet(IsqlColumn[] columns)
        {
            _cols = columns;
            _breakBefore = LineBreaks(columns);
            _reheader = false;

            // isql prints the heading once the first fetch succeeds - with the first row, or at the
            // end of an empty result set - so a statement that fails before any row shows none.
            _headerPending = true;
        }

        // A column moves to a new, tab-indented line when it and the row's closing space would
        // pass the width. A column alone on its line is never moved, so it may overflow.
        private bool[] LineBreaks(IsqlColumn[] columns)
        {
            var breaks = new bool[columns.Length];
            int pos = 0;
            for (int j = 0; j < columns.Length; j++)
            {
                if (j > 0 && pos + columns[j].Width + 2 > _width)
                {
                    breaks[j] = true;
                    pos = TabWidth;
                }
                pos += 1 + columns[j].Width;
            }
            return breaks;
        }

        private void FlushHeader()
        {
            if (!_headerPending) return;
            _headerPending = false;
            WriteFixed(_cols, _breakBefore, j => _cols[j].Name.PadRight(_cols[j].Width));
            WriteFixed(_cols, _breakBefore, j => new string('-', _cols[j].Width));
            _afterResultSet = true;
        }

        /// <summary>One row; a null value prints as NULL.</summary>
        public void Row(string?[] values)
        {
            if (_reheader)
            {
                // The row starts the next group of a compute ... by: isql heads it again.
                _reheader = false;
                _headerPending = true;
            }
            FlushHeader();
            WriteRow(_cols, _breakBefore, values);
        }

        /// <summary>
        /// One row of a compute clause (its aggregates, left to right, each at its own type's width):
        /// a heading line of the aggregates' (blank) names, <c>Compute Result:</c>, a separator and
        /// the values. A row of the result set after it - the next group of a compute ... by -
        /// repeats the result set's heading. Compute rows count toward the statement's rows.
        /// </summary>
        public void ComputeRow(IsqlColumn[] columns, string?[] values)
        {
            FlushHeader();
            var breaks = LineBreaks(columns);
            WriteFixed(columns, breaks, j => columns[j].Name.PadRight(columns[j].Width));
            _emit("Compute Result:");
            WriteFixed(columns, breaks, j => new string('-', columns[j].Width));
            WriteRow(columns, breaks, values);
            _afterResultSet = true;
            _reheader = true;
        }

        private void WriteRow(IsqlColumn[] cols, bool[] breakBefore, string?[] values)
        {
            var line = new StringBuilder();
            int pos = 0;
            for (int j = 0; j < cols.Length; j++)
            {
                var col = cols[j];
                if (breakBefore[j])
                {
                    _emit(line.ToString());
                    line.Clear().Append('\t');
                    pos = TabWidth;
                }
                var v = (j < values.Length ? values[j] : null) ?? "NULL";
                if (col.RightAlign)
                {
                    var cell = v.PadLeft(col.Width);
                    line.Append(' ').Append(cell);
                    pos += 1 + cell.Length;
                    continue;
                }
                // A value that will not fit before the width is cut into pieces: the first fills
                // the line, the rest go on tab-indented lines; the last piece is padded to what is
                // left of the column width.
                int consumed = 0;
                if (!col.NoChunk)
                {
                    while (pos + (v.Length - consumed) + 2 > _width)
                    {
                        int n = Math.Max(1, _width - pos - 1);
                        // isql never cuts inside a character: the piece ends before one that would not fit.
                        if (Utf8ByteText)
                        {
                            int cut = consumed + n;
                            while (cut > consumed + 1 && cut < v.Length && (v[cut] & 0xC0) == 0x80) cut--;
                            n = cut - consumed;
                        }
                        line.Append(' ').Append(v, consumed, n);
                        _emit(line.ToString());
                        line.Clear().Append('\t');
                        pos = TabWidth;
                        consumed += n;
                    }
                }
                var rest = v.Substring(consumed).PadRight(Math.Max(0, col.Width - consumed));
                line.Append(' ').Append(rest);
                pos += 1 + rest.Length;
            }
            line.Append(' ');
            _emit(line.ToString());
        }

        /// <summary>
        /// A statement finished (inProc: a statement inside a procedure; isProc: the procedure
        /// itself). A top-level statement prints its own row count unless set nocount is on.
        /// Inside a procedure only a statement that ends a result set prints one, and what it
        /// prints is the latest count the server flagged in that procedure - isql's client library
        /// keeps a count until it is reported, so a result set selected under set nocount on still
        /// reports an earlier statement's count once.
        /// </summary>
        public void Done(bool hasCount, int count, bool isError = false, bool inProc = false, bool isProc = false)
        {
            _reheader = false;
            if (_headerPending)
            {
                if (isError) _headerPending = false;
                else FlushHeader();
            }
            if (hasCount) _heldCount = count;
            if (isProc)
            {
                _heldCount = null;
            }
            else if (!inProc || _afterResultSet)
            {
                if (_heldCount is int n && (hasCount || inProc))
                {
                    if (_afterResultSet) _emit("");
                    _emit(n == 1 ? "(1 row affected)" : $"({n} rows affected)");
                }
                _heldCount = null;
            }
            _afterResultSet = false;
        }

        public void ReturnStatus(int status)
        {
            _headerPending = false;
            _afterResultSet = false;
            _emit($"(return status = {status})");
        }

        /// <summary>A print / informational message: its text, word-wrapped at the width.</summary>
        public void Message(string text) => WriteWrapped(text);

        /// <summary>A server error, in isql's three-part layout.</summary>
        public void Error(int number, int severity, int state, string? server, string? proc, int lineNum, string text) =>
            Error(number.ToString(System.Globalization.CultureInfo.InvariantCulture), severity, state, server, proc, lineNum, text);

        /// <summary>A server error whose number is not an integer (a PostgreSQL SQLSTATE).</summary>
        public void Error(string number, int severity, int state, string? server, string? proc, int lineNum, string text)
        {
            _emit($"Msg {number}, Level {severity}, State {state}:");
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(server)) parts.Add($"Server '{server}'");
            if (!string.IsNullOrEmpty(proc)) parts.Add($"Procedure '{proc}'");
            if (lineNum > 0) parts.Add($"Line {lineNum}");
            if (parts.Count > 0) _emit(string.Join(", ", parts) + ":");
            WriteWrapped(text);
        }

        /// <summary>isql -e: each line of the batch, numbered from 1.</summary>
        public static IEnumerable<string> EchoLines(string batch)
        {
            var lines = batch.Replace("\r\n", "\n").Split('\n');
            int count = lines.Length;
            if (count > 0 && lines[count - 1].Length == 0) count--; // the batch's own line end
            for (int i = 0; i < count; i++)
                yield return $"{i + 1}> {lines[i]}";
        }

        // ---- Values as isql prints them, shared by every executor ----

        private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
        private static readonly string[] MonthNames =
            { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

        /// <summary>A column at its display width: the type width capped at 512, then widened to the name.</summary>
        public static IsqlColumn Column(string name, int width, bool rightAlign, bool noChunk = false)
        {
            name ??= "";
            width = Math.Min(Math.Max(width, 0), MaxColumnWidth);
            return new IsqlColumn { Name = name, Width = Math.Max(width, name.Length), RightAlign = rightAlign, NoChunk = noChunk };
        }

        /// <summary>money: 2 places with thousands separators, rounded half away from zero.</summary>
        public static string Money(decimal m) => Math.Round(m, 2, MidpointRounding.AwayFromZero).ToString("N2", Inv);

        /// <summary>float/real: 6 places, or 1.000000000000e+20 when that passes the 27-character column.</summary>
        public static string Float(double x)
        {
            var s = x.ToString("F6", Inv);
            return s.Length > 27 ? x.ToString("0.000000000000e+00", Inv) : s;
        }

        /// <summary>numeric at its declared scale.</summary>
        public static string Numeric(decimal d, int scale) =>
            scale >= 0 ? d.ToString("F" + scale, Inv) : d.ToString(Inv);

        public static string Bit(bool b) => b ? "1" : "0";

        /// <summary>datetime: <c>Jan  2 2026  3:04AM</c>.</summary>
        public static string DateTimeValue(DateTime dt) => DateValue(dt) + " " + TimeValue(dt.Hour, dt.Minute);

        /// <summary>date: <c>Jan  2 2026</c>.</summary>
        public static string DateValue(DateTime d) => $"{MonthNames[d.Month - 1]} {d.Day,2} {d.Year}";

        /// <summary>time: <c> 3:04AM</c>.</summary>
        public static string TimeValue(int hour, int minute)
        {
            int h12 = hour % 12 == 0 ? 12 : hour % 12;
            return $"{h12,2}:{minute:00}{(hour < 12 ? "AM" : "PM")}";
        }

        /// <summary>binary: 0x and lower-case hex.</summary>
        public static string Hex(byte[] bytes) => "0x" + Convert.ToHexString(bytes).ToLowerInvariant();

        // Header and separator: every column at its full width, never cut.
        private void WriteFixed(IsqlColumn[] cols, bool[] breakBefore, Func<int, string> cell)
        {
            var line = new StringBuilder();
            for (int j = 0; j < cols.Length; j++)
            {
                if (breakBefore[j])
                {
                    _emit(line.ToString());
                    line.Clear().Append('\t');
                }
                line.Append(' ').Append(cell(j));
            }
            line.Append(' ');
            _emit(line.ToString());
        }

        // A line longer than the width breaks at its last space before the width (the space is
        // dropped); a line with no such space is printed whole.
        private void WriteWrapped(string text)
        {
            text = (text ?? "").Replace("\r\n", "\n");
            if (text.EndsWith("\n")) text = text.Substring(0, text.Length - 1); // the server's own line end
            foreach (var raw in text.Split('\n'))
            {
                var s = raw;
                while (s.Length > _width)
                {
                    int cut = s.LastIndexOf(' ', _width - 1);
                    if (cut <= 0) break;
                    _emit(s.Substring(0, cut));
                    s = s.Substring(cut + 1);
                }
                _emit(s);
            }
        }
    }
}
