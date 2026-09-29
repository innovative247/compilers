using System.Text.RegularExpressions;
using ibsCompiler;

namespace SqlTest;

public abstract record IoLine;
public sealed record TableLine(string Table, long Logical, long Physical) : IoLine;
public sealed record WritesLine(long Writes) : IoLine;

public sealed record TableIo(string Table, long LogicalReads, long PhysicalReads);
public sealed record IoMeasure(long LogicalReads, long PhysicalReads, long Writes, IReadOnlyList<TableIo> Tables);

public sealed record BudgetCheck(string Label, long MaxReads, long Actual)
{
    public bool Failed => Actual > MaxReads;
    public string FailMessage => $"{Label} (max_reads={MaxReads} actual={Actual})";
}

/// <summary>Engine seam for per-statement I/O statistics (one class per server type).</summary>
public interface IIoMeter
{
    string EnableSql { get; }
    string DisableSql { get; }
    bool TryParse(int number, string text, out IoLine? line);
    bool IsStatMessage(int number);
}

public static class IoMeters
{
    public static IIoMeter? For(SQLServerTypes serverType) =>
        serverType == SQLServerTypes.SYBASE ? new SybaseIoMeter() : null;
}

public sealed class SybaseIoMeter : IIoMeter
{
    private const int TableMessage  = 3615;
    private const int WritesMessage = 3614;

    private static readonly Regex TableRe = new(
        @"^Table: (\S+?)(?: \([^)]*\))? scan count \d+, logical reads: \(regular=\d+ apf=\d+ total=(\d+)\), physical reads: \(regular=\d+ apf=\d+ total=(\d+)\)",
        RegexOptions.Compiled);
    private static readonly Regex WritesRe = new(@"^Total writes for this command: (\d+)", RegexOptions.Compiled);

    public string EnableSql  => "set statistics io on";
    public string DisableSql => "set statistics io off";
    public bool IsStatMessage(int number) => number is TableMessage or WritesMessage;

    // Keyed on the message number so a print that looks like a stat line is never counted.
    public bool TryParse(int number, string text, out IoLine? line)
    {
        line = null;
        text = text.Trim();
        if (number == TableMessage)
        {
            var m = TableRe.Match(text);
            if (m.Success) line = new TableLine(m.Groups[1].Value, long.Parse(m.Groups[2].Value), long.Parse(m.Groups[3].Value));
        }
        else if (number == WritesMessage)
        {
            var m = WritesRe.Match(text);
            if (m.Success) line = new WritesLine(long.Parse(m.Groups[1].Value));
        }
        return line != null;
    }
}

/// <summary>
/// Region state machine over the message stream: `@sql-test:measure-start` resets,
/// `@sql-test:assert-max-reads &lt;n&gt; &lt;label&gt;` checks the region's logical reads so far.
/// </summary>
public sealed class MeasureAccumulator
{
    public const string MarkerPrefix = "@sql-test:";
    private const string StartMarker  = MarkerPrefix + "measure-start";
    public const string AssertMarker  = MarkerPrefix + "assert-max-reads";
    private static readonly Regex AssertRe = new(@"^@sql-test:assert-max-reads\s+(\d+)\s*(.*)$", RegexOptions.Compiled);

    private readonly IIoMeter _meter;
    private readonly Dictionary<string, (long l, long p)> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _tableOrder = new();
    private readonly List<BudgetCheck> _budgets = new();
    private readonly List<string> _failures = new();
    private long _logical, _physical, _writes;
    private bool _started;

    public MeasureAccumulator(IIoMeter meter) => _meter = meter;

    public IReadOnlyList<BudgetCheck> Budgets => _budgets;
    public IReadOnlyList<string> Failures => _failures;
    public string? Error { get; private set; }

    public IoMeasure Current => new(_logical, _physical, _writes,
        _tableOrder.Select(t => new TableIo(t, _tables[t].l, _tables[t].p)).ToList());

    public void Feed(int number, string text)
    {
        if (_meter.TryParse(number, text, out var line))
        {
            switch (line)
            {
                case TableLine t:
                    _logical += t.Logical; _physical += t.Physical;
                    if (!_tables.TryGetValue(t.Table, out var cur)) _tableOrder.Add(t.Table);
                    _tables[t.Table] = (cur.l + t.Logical, cur.p + t.Physical);
                    break;
                case WritesLine w:
                    _writes += w.Writes;
                    break;
            }
            return;
        }
        // A dropped stat line would under-count reads and let a budget pass.
        if (_meter.IsStatMessage(number))
        {
            Error ??= $"unparseable I/O statistics line: {text.Trim()}";
            return;
        }

        var msg = text.Trim();
        if (msg == StartMarker)
        {
            _started = true;
            _logical = _physical = _writes = 0;
            _tables.Clear(); _tableOrder.Clear();
            return;
        }
        var am = AssertRe.Match(msg);
        if (!am.Success) return;
        if (!_started) { Error ??= "budget without pro_test_measure_start"; return; }
        // Parse failures must not throw out of RunOne (parallel runs share the process).
        if (!long.TryParse(am.Groups[1].Value, out var max)) { Error ??= $"invalid budget marker: {msg}"; return; }
        var label = am.Groups[2].Value.Trim();
        var check = new BudgetCheck(label.Length > 0 ? label : "reads", max, _logical);
        _budgets.Add(check);
        if (check.Failed) _failures.Add(check.FailMessage);
    }
}
