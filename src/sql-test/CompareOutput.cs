using System.Data;
using System.Globalization;
using System.Text;

namespace SqlTest;

/// <summary>Builds the byte stream two runs of a call are compared on. One entry per line, `\n` line ends.</summary>
public sealed class CanonicalWriter(bool includePrint)
{
    // InfoMessage may fire on a driver thread while the reader thread appends.
    private readonly object _gate = new();
    private readonly StringBuilder _sb = new();
    private int _resultSets;
    private bool _rcSet;
    private int? _rc;

    public bool IncludePrint => includePrint;

    /// <summary>False when no rc set arrived or its value was null: the call's exec did not run.</summary>
    public bool HasReturnCode { get { lock (_gate) return _rcSet && _rc.HasValue; } }

    public void BeginResultSet(IReadOnlyList<(string Name, string Type)> columns)
    {
        lock (_gate)
        {
            _resultSets++;
            Line($"resultset {_resultSets.ToString(CultureInfo.InvariantCulture)}");
            Line("columns: " + string.Join(", ", columns.Select(c => $"{c.Name} {c.Type}")));
        }
    }

    public void Row(object?[] values)
    {
        var text = "row: " + string.Join("\t", values.Select(CompareOutput.RenderValue));
        lock (_gate) Line(text);
    }

    public void EndResultSet(int rows)
    {
        lock (_gate) Line($"rows: {rows.ToString(CultureInfo.InvariantCulture)}");
    }

    public void Print(string text)
    {
        if (!includePrint) return;
        lock (_gate) Line("print: " + OneLine(text));
    }

    public void Error(int number, int severity, string text)
    {
        lock (_gate)
            Line($"error: Msg {number.ToString(CultureInfo.InvariantCulture)}, Level {severity.ToString(CultureInfo.InvariantCulture)}: {OneLine(text)}");
    }

    public void TranCount(int actual, int expected)
    {
        lock (_gate)
            Line($"trancount: {actual.ToString(CultureInfo.InvariantCulture)} (expected {expected.ToString(CultureInfo.InvariantCulture)})");
    }

    // Held back until ToBytes so the rc line is always last, even when errors follow the select.
    public void ReturnCode(int? rc)
    {
        lock (_gate) { _rcSet = true; _rc = rc; }
    }

    public byte[] ToBytes()
    {
        lock (_gate)
        {
            var rc = _rcSet && _rc.HasValue ? _rc.Value.ToString(CultureInfo.InvariantCulture) : "none";
            return Encoding.UTF8.GetBytes(_sb.ToString() + $"rc: {rc}\n");
        }
    }

    private void Line(string s) => _sb.Append(s).Append('\n');

    // `\` first, or `a\nb` and `a<newline>b` render alike; a raw newline would shift every later diff line.
    internal static string OneLine(string s) => s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
}

public sealed record CompareDiff(long Offset, int Line, IReadOnlyList<string> OldLines, IReadOnlyList<string> NewLines);

public static class CompareOutput
{
    public const string RcColumn = "sql_test_rc";
    public const string EndOfOutput = "<end of output>";

    /// <summary>Writes every result set; only a final single-column `sql_test_rc` set becomes the rc line. Returns the result sets written.</summary>
    public static int Drain(IDataReader reader, CanonicalWriter writer)
    {
        int sets = 0;
        // A proc's own set may carry the rc column name; only the last set is the wrapper's select.
        (List<(string, string)> Cols, List<object?[]> Rows)? held = null;
        do
        {
            if (reader.FieldCount == 0) continue;
            if (held is { } h) { Write(writer, h.Cols, h.Rows); sets++; held = null; }
            var cols = new List<(string, string)>(reader.FieldCount);
            for (int i = 0; i < reader.FieldCount; i++) cols.Add((reader.GetName(i), TypeName(reader, i)));
            if (cols.Count == 1 && string.Equals(cols[0].Item1, RcColumn, StringComparison.OrdinalIgnoreCase))
            {
                var rcRows = new List<object?[]>();
                while (reader.Read()) rcRows.Add(new[] { reader.IsDBNull(0) ? null : reader.GetValue(0) });
                held = (cols, rcRows);
                continue;
            }
            writer.BeginResultSet(cols);
            int rows = 0;
            while (reader.Read())
            {
                var row = new object?[reader.FieldCount];
                for (int i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                writer.Row(row);
                rows++;
            }
            writer.EndResultSet(rows);
            sets++;
        } while (reader.NextResult());
        if (held is { } last)
            writer.ReturnCode(last.Rows.Count == 0 || last.Rows[^1][0] is null
                ? null
                : Convert.ToInt32(last.Rows[^1][0], CultureInfo.InvariantCulture));
        return sets;
    }

    private static void Write(CanonicalWriter writer, List<(string, string)> cols, List<object?[]> rows)
    {
        writer.BeginResultSet(cols);
        foreach (var r in rows) writer.Row(r);
        writer.EndResultSet(rows.Count);
    }

    private static string TypeName(IDataReader reader, int i)
    {
        string? name = null;
        try { name = reader.GetDataTypeName(i); } catch (NotSupportedException) { } catch (InvalidOperationException) { }
        if (string.IsNullOrWhiteSpace(name)) name = reader.GetFieldType(i).Name;
        return name.ToLowerInvariant();
    }

    public static string RenderValue(object? v) => v switch
    {
        null or DBNull => "NULL",
        string s => Quote(s),
        char c => Quote(c.ToString()),
        bool b => b ? "1" : "0",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss.ffffff zzz", CultureInfo.InvariantCulture),
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        double x => x.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        IFormattable fm => fm.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('\'');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\'': sb.Append("''"); break;
                case '\\': sb.Append(@"\\"); break;
                case '\t': sb.Append(@"\t"); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.Append('\'').ToString();
    }

    /// <summary>Null when identical. Line is 1-based; each side carries the line before, at and after the offset.</summary>
    public static CompareDiff? FirstDifference(byte[] oldBytes, byte[] newBytes)
    {
        int n = Math.Min(oldBytes.Length, newBytes.Length);
        int at = 0;
        while (at < n && oldBytes[at] == newBytes[at]) at++;
        if (at == n && oldBytes.Length == newBytes.Length) return null;

        // Bytes before the offset are shared, so the line number is the same on both sides.
        int line = 1;
        for (int i = 0; i < at; i++) if (oldBytes[i] == (byte)'\n') line++;
        return new CompareDiff(at, line, Around(oldBytes, line), Around(newBytes, line));
    }

    public static IReadOnlyList<string> FormatDiff(string callName, CompareDiff d)
    {
        var lines = new List<string> { $"compare {callName}: differs at byte {d.Offset.ToString(CultureInfo.InvariantCulture)} (line {d.Line.ToString(CultureInfo.InvariantCulture)})" };
        int first = d.Line > 1 ? d.Line - 1 : d.Line;
        for (int i = 0; i < d.OldLines.Count; i++) lines.Add($"  old {(first + i).ToString(CultureInfo.InvariantCulture)}: {d.OldLines[i]}");
        for (int i = 0; i < d.NewLines.Count; i++) lines.Add($"  new {(first + i).ToString(CultureInfo.InvariantCulture)}: {d.NewLines[i]}");
        return lines;
    }

    private static IReadOnlyList<string> Around(byte[] bytes, int line)
    {
        var all = SplitLines(bytes);
        var result = new List<string>();
        for (int l = Math.Max(1, line - 1); l <= line + 1; l++)
        {
            if (l > all.Count) { result.Add(EndOfOutput); break; }
            result.Add(all[l - 1]);
        }
        return result;
    }

    private static List<string> SplitLines(byte[] bytes)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            lines.Add(Encoding.UTF8.GetString(bytes, start, i - start));
            start = i + 1;
        }
        if (start < bytes.Length) lines.Add(Encoding.UTF8.GetString(bytes, start, bytes.Length - start));
        return lines;
    }
}
