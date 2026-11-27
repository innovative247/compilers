using System.Globalization;

namespace ibsCompiler
{
    /// <summary>Row-returning query, for diff and readback. Implemented by the three executors.</summary>
    public interface ISqlRowReader
    {
        List<object?[]> QueryRows(string sql, string database);
    }

    public enum LiveColType { Int, Char, VarChar, NVarChar }

    /// <summary>How NULL compares with a blank value. ASE stores '' as ' ', so a blank never reads back as ''.</summary>
    public enum LiveNullCompare
    {
        /// <summary>NULL differs from any string, blank included.</summary>
        Strict,
        /// <summary>NULL equals blank on Sybase only.</summary>
        BlankOnSybase,
        /// <summary>NULL equals blank on every dialect.</summary>
        Blank,
    }

    /// <param name="Compare">False: a difference here never makes a row change, but a changed row still writes it (chg_tm).</param>
    public sealed record LiveColumn(string Name, LiveColType Type,
                                    LiveNullCompare Null = LiveNullCompare.Strict, bool TrimTrailing = true, bool Compare = true);

    /// <summary>
    /// One target table. <see cref="Key"/> must be the table's unique key: every delete and update
    /// is keyed on all of it, so a statement touches only that row.
    /// </summary>
    public sealed class LiveTable
    {
        public string Label = "";
        public string Placeholder = "";
        public LiveColumn[] Columns = Array.Empty<LiveColumn>();
        public int[] Key = Array.Empty<int>();

        public int IndexOf(string name) => Array.FindIndex(Columns, c => c.Name == name);

        /// <summary>Comparable form of one value: NULL is null, ints canonical, trailing blanks trimmed per column.</summary>
        public string? Norm(int col, string? v, SQLServerTypes d)
        {
            var c = Columns[col];
            if (v == null) return null;
            if (c.Type == LiveColType.Int)
                return long.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                    ? n.ToString(CultureInfo.InvariantCulture) : v.Trim();
            var s = c.TrimTrailing ? v.TrimEnd(' ') : v;
            bool blankIsNull = c.Null == LiveNullCompare.Blank || (c.Null == LiveNullCompare.BlankOnSybase && d == SQLServerTypes.SYBASE);
            return blankIsNull && s.Trim(' ') == "" ? null : s;
        }

        public bool Same(int col, string? a, string? b, SQLServerTypes d) => Norm(col, a, d) == Norm(col, b, d);

        public string KeyOf(string?[] values, SQLServerTypes d) =>
            string.Join("\u001f", Key.Select(k => Norm(k, values[k], d) ?? "\u0000NULL"));

        public string KeyText(string?[] values) => string.Join("/", Key.Select(k => values[k]?.TrimEnd(' ') ?? "NULL"));
    }

    /// <summary>Decides that an existing row stays as it is: it is neither deleted nor updated.</summary>
    /// <param name="current">The database row.</param>
    /// <param name="target">The target row with the same key, or null when the target has none.</param>
    public delegate bool LiveKeep(string?[] current, string?[]? target);

    /// <summary>The complete target content of one table (or of the part of it selected by <see cref="Where"/>).</summary>
    public sealed class LiveTarget
    {
        public LiveTable Table = new();
        public List<string?[]> Rows = new();
        public LiveKeep? Keep;
        /// <summary>Optional filter (SQL, resolved through Options) limiting the rows read, and so the rows that can be deleted.</summary>
        public string? Where;
    }

    public enum LiveChangeKind { Delete, Update, Insert }

    public sealed class LiveChange
    {
        public LiveChangeKind Kind;
        public LiveTable Table = new();
        /// <summary>Key values of the database row (delete, update).</summary>
        public string?[] Current = Array.Empty<string?>();
        /// <summary>Target row (update, insert).</summary>
        public string?[]? Target;
        /// <summary>Columns that differ (update only).</summary>
        public int[] Changed = Array.Empty<int>();

        public string KeyText => Table.KeyText(Kind == LiveChangeKind.Insert ? Target! : Current);
    }

    public sealed class LiveDiff
    {
        public LiveTable Table = new();
        public List<LiveChange> Deletes = new(), Updates = new(), Inserts = new();
        public int Kept;
        public bool IsEmpty => Deletes.Count + Updates.Count + Inserts.Count == 0;
    }

    public enum LiveMode { Live, Reload }

    /// <summary>Options shared by every tool on the live path (--live/--reload/--batch-size/--pause-ms/--dry-run).</summary>
    public sealed class LiveSettings
    {
        public LiveMode Mode = LiveCompile.DefaultMode;
        public int BatchSize = LiveCompile.DefaultBatchSize;
        public int PauseMs;
        public bool DryRun;
        /// <summary>Where --dry-run writes the planned SQL; null picks a name in the temp folder.</summary>
        public string? PlanFile;
    }
}
