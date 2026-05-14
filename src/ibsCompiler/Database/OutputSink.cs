using System.Text;

namespace ibsCompiler.Database
{
    /// <summary>
    /// Per-call output sink that tees lines from the executor (InfoMessage PRINT/RAISERROR
    /// and streamed result-set rows) into BOTH:
    ///   1. an in-memory StringBuilder (so ExecReturn.Output stays populated for callers that
    ///      parse it — e.g. ba_upgrades_check regex, Runsql failure block);
    ///   2. the live destination — console (Console.Out, flushed per line) or a long-lived
    ///      AutoFlush StreamWriter pointed at the requested OutFile / DefaultOutFile, so
    ///      observers tailing the file (iwatch) see progress in real time.
    /// When captureOutput=true the live leg is suppressed — the call's only consumer is the
    /// StringBuilder, matching the historical contract.
    /// </summary>
    internal sealed class OutputSink : IDisposable
    {
        private readonly StringBuilder _buffer;
        private readonly StreamWriter? _writer;
        private readonly bool _toConsole;
        private readonly bool _capture;
        private readonly object _lock = new();
        private bool _disposed;

        public Action<string> Emit { get; }

        private OutputSink(StringBuilder buffer, StreamWriter? writer, bool toConsole, bool capture)
        {
            _buffer = buffer;
            _writer = writer;
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
                var writer = new StreamWriter(fs) { NewLine = "\n", AutoFlush = true };
                return new OutputSink(buffer, writer, toConsole: false, capture: false);
            }

            return new OutputSink(buffer, null, toConsole: true, capture: false);
        }

        private void EmitLine(string line)
        {
            line ??= string.Empty;
            lock (_lock)
            {
                _buffer.AppendLine(line);
                if (_capture) return;

                if (_writer != null)
                {
                    var normalized = line.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');
                    _writer.WriteLine(normalized);
                }
                else if (_toConsole)
                {
                    if (ibs_compiler_common.OutputToStdErr)
                        Console.Error.WriteLine(line);
                    else
                        Console.Out.WriteLine(line);
                    // Per-line flush so pipes / tee / redirected tails see streaming
                    Console.Out.Flush();
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _writer?.Dispose();
        }
    }
}
