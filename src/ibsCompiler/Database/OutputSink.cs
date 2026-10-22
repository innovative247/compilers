using System.Runtime.InteropServices;
using System.Text;

namespace ibsCompiler.Database
{
    /// <summary>
    /// Per-call output sink that tees lines from the executor (InfoMessage PRINT/RAISERROR
    /// and streamed result-set rows) into BOTH:
    ///   1. an in-memory StringBuilder (so ExecReturn.Output stays populated for callers that
    ///      parse it — e.g. ba_upgrades_check regex, Runsql failure block);
    ///   2. the live destination — console (Console.Out, flushed per line) or a long-lived
    ///      append stream on the requested OutFile / DefaultOutFile, flushed per line, so
    ///      observers tailing the file (iwatch) see progress in real time.
    /// When captureOutput=true the live leg is suppressed — the call's only consumer is the
    /// StringBuilder, matching the historical contract.
    /// <para>
    /// <see cref="Encoding"/> (set by the Sybase executor to the session's charset) makes the
    /// live leg write the server's own bytes, as isql does: the driver decoded the text with that
    /// encoding, so encoding it again gives back exactly what the server sent. On a Windows
    /// console the console's output code page is switched to match while the sink lives, so the
    /// bytes display as the server meant them. Without it, files are UTF-8 and the console gets
    /// Console.Out's own encoding.
    /// </para>
    /// </summary>
    internal sealed class OutputSink : IDisposable
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly StringBuilder _buffer;
        private readonly FileStream? _file;
        private readonly bool _toConsole;
        private readonly bool _capture;
        private readonly object _lock = new();
        private bool _disposed;
        private Stream? _std;
        private ConsoleCodePage? _codePage;

        public Action<string> Emit { get; }

        /// <summary>The encoding server text is written in; null for the default writers.</summary>
        public Encoding? Encoding { get; set; }

        private OutputSink(StringBuilder buffer, FileStream? file, bool toConsole, bool capture)
        {
            _buffer = buffer;
            _file = file;
            _toConsole = toConsole;
            _capture = capture;
            Emit = EmitLine;
        }

        public static OutputSink Build(StringBuilder buffer, bool captureOutput, string outputFile)
        {
            var target = !string.IsNullOrEmpty(outputFile) ? outputFile : ibs_compiler_common.DefaultOutFile;

            if (captureOutput)
                return new OutputSink(buffer, null, toConsole: false, capture: true);

            if (!string.IsNullOrEmpty(target))
            {
                var fs = new FileStream(target, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                return new OutputSink(buffer, fs, toConsole: false, capture: false);
            }

            return new OutputSink(buffer, null, toConsole: true, capture: false);
        }

        private void EmitLine(string line)
        {
            line ??= string.Empty;
            if (Encoding != null)
            {
                lock (_lock)
                {
                    _buffer.AppendLine(line);
                    if (!_capture) WriteRaw(Encoding.GetBytes(line));
                }
                return;
            }
            lock (_lock)
            {
                _buffer.AppendLine(line);
                if (_capture) return;

                if (_file != null)
                {
                    var normalized = line.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');
                    var bytes = Utf8NoBom.GetBytes(normalized + "\n");
                    _file.Write(bytes, 0, bytes.Length);
                    _file.Flush();
                }
                else if (_toConsole)
                {
                    var writer = ibs_compiler_common.OutputToStdErr ? Console.Error : Console.Out;
                    writer.WriteLine(line);
                    // Per-line flush so pipes / tee / redirected tails see streaming
                    writer.Flush();
                }
            }
        }

        /// <summary>
        /// One line given as the bytes to write, exactly as isql would write them (in
        /// <see cref="Encoding"/>, which must be set) - a value cut at a byte width may end in part
        /// of a character, and those bytes are written as they are. The captured text is the line
        /// decoded with <see cref="Encoding"/>.
        /// </summary>
        public void EmitRaw(byte[] line)
        {
            lock (_lock)
            {
                _buffer.AppendLine((Encoding ?? Utf8NoBom).GetString(line));
                if (!_capture) WriteRaw(line);
            }
        }

        private void WriteRaw(byte[] line)
        {
            if (_file != null)
            {
                _file.Write(line, 0, line.Length);
                _file.WriteByte((byte)'\n');
                _file.Flush();
            }
            else if (_toConsole)
            {
                // Straight to the handle; anything Console.Out holds goes first.
                var writer = ibs_compiler_common.OutputToStdErr ? Console.Error : Console.Out;
                writer.Flush();
                if (_std == null)
                {
                    _std = ibs_compiler_common.OutputToStdErr ? Console.OpenStandardError() : Console.OpenStandardOutput();
                    if (Encoding != null) _codePage = ConsoleCodePage.Match(Encoding, ibs_compiler_common.OutputToStdErr);
                }
                var nl = System.Text.Encoding.ASCII.GetBytes(writer.NewLine);
                _std.Write(line, 0, line.Length);
                _std.Write(nl, 0, nl.Length);
                _std.Flush();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _file?.Dispose();
            _std?.Flush();
            _codePage?.Dispose();
        }

        /// <summary>
        /// Switches a Windows console's output code page to an encoding's for as long as it lives
        /// (Ctrl+C included), then puts the original back. Does nothing when the stream is
        /// redirected (the bytes are the point there) or off Windows.
        /// </summary>
        private sealed class ConsoleCodePage : IDisposable
        {
            [DllImport("kernel32.dll")]
            private static extern uint GetConsoleOutputCP();

            [DllImport("kernel32.dll")]
            private static extern bool SetConsoleOutputCP(uint codePage);

            private readonly uint _original;
            private int _restored;

            private ConsoleCodePage(uint original)
            {
                _original = original;
                Console.CancelKeyPress += OnCancel;
            }

            public static ConsoleCodePage? Match(Encoding encoding, bool stdErr)
            {
                try
                {
                    if (!OperatingSystem.IsWindows()) return null;
                    if (stdErr ? Console.IsErrorRedirected : Console.IsOutputRedirected) return null;
                    uint current = GetConsoleOutputCP();
                    uint wanted = (uint)encoding.CodePage;
                    if (current == 0 || current == wanted) return null;
                    if (!SetConsoleOutputCP(wanted)) return null;
                    return new ConsoleCodePage(current);
                }
                catch
                {
                    return null;
                }
            }

            private void OnCancel(object? sender, ConsoleCancelEventArgs e) => Restore();

            private void Restore()
            {
                if (Interlocked.Exchange(ref _restored, 1) != 0) return;
                try { SetConsoleOutputCP(_original); } catch { }
            }

            public void Dispose()
            {
                Console.CancelKeyPress -= OnCancel;
                Restore();
            }
        }
    }
}
