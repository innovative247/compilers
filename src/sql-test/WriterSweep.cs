using System.Globalization;

namespace SqlTest;

/// <summary>A journal row as the sweep reads it.</summary>
public record JournalRow(long JournalId, int Spid, int Kpid, string Login, string Host, DateTime Started, string Test,
                         int? TestSpid = null, int? TestKpid = null)
{
    public string Describe =>
        $"{JournalId} ({Test}, {Login}@{Host}, {Started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)})";
}

/// <summary>One item row: kind 'R' restores a table, 'V' drops a variant proc (W4).</summary>
public record SweepItem(int Seq, char Kind, string Db, string Obj, string Predicate, string? Snapshot, bool HasIdentity);

/// <summary>
/// Restores journal rows whose runner is gone (design §2.4). A row that cannot be
/// restored stays, so ReadHeld keeps refusing writer tests on its tables.
/// </summary>
internal static class WriterSweep
{
    /// <summary>
    /// Reports one line per swept, failed or skipped row. Throws only when the sweep
    /// itself cannot run (connection, journal read, liveness or claim).
    /// </summary>
    /// <param name="dropVariant">
    /// Drops one 'V' item and deletes its item row; null on success, else the error.
    /// Without it, rows holding variants are left whole for a runner that has one.
    /// </param>
    internal static void Sweep(ISqlExec x, ISqlExec restoreX, string home, Action<string> report,
                               Func<SweepItem, string?>? dropVariant = null)
    {
        // Never journalled on this database: nothing to sweep, and plain runs must not create the tables.
        if (x.Scalar($"select object_id('{home}..{WriterJournal.JournalTable}')") == null) return;
        WriterJournal.EnsureTestPairColumns(x, home);

        foreach (var row in ReadRows(x, home))
        {
            if (IsAlive(x, row)) continue;
            var items = ReadItems(x, home, row.JournalId);
            if (dropVariant == null && items.Any(i => i.Kind == 'V'))
            {
                report($"sql-test: note: writer journal row {row.Describe} holds variant items; left for a later sweep");
                continue;
            }
            if (!Claim(x, home, row)) continue; // another runner took it

            var warnings = new List<string>();
            string? detail;
            try { detail = RestoreRow(x, restoreX, home, row, items, dropVariant, warnings); }
            catch (Exception ex) { detail = ex.Message; }
            foreach (var w in warnings) report($"sql-test: warning: writer journal row {row.JournalId}: {w}");
            report(detail == null
                ? $"sql-test: writer journal row {row.Describe} restored"
                : $"sql-test: WARNING: writer journal row {row.Describe} could not be restored: {detail}");
        }
    }

    internal static List<JournalRow> ReadRows(ISqlExec x, string home) =>
        x.Rows($"select journal_id, spid, kpid, login, hostname, started, test, test_spid, test_kpid " +
               $"from {home}..{WriterJournal.JournalTable} order by journal_id")
            .Select(r => new JournalRow(
                Convert.ToInt64(r[0], CultureInfo.InvariantCulture),
                Convert.ToInt32(r[1], CultureInfo.InvariantCulture),
                Convert.ToInt32(r[2], CultureInfo.InvariantCulture),
                Convert.ToString(r[3])!.Trim(), Convert.ToString(r[4])?.Trim() ?? "",
                Convert.ToDateTime(r[5], CultureInfo.InvariantCulture), Convert.ToString(r[6])!.Trim(),
                r[7] == null ? null : Convert.ToInt32(r[7], CultureInfo.InvariantCulture),
                r[8] == null ? null : Convert.ToInt32(r[8], CultureInfo.InvariantCulture)))
            .ToList();

    // kpid tells a reused spid from the original. A killed runner's test batch runs on, still writing.
    internal static bool IsAlive(ISqlExec x, JournalRow row) =>
        Convert.ToInt32(x.Scalar(
            $"select count(*) from master..sysprocesses where (spid = {row.Spid} and kpid = {row.Kpid})" +
            (row.TestSpid is { } ts && row.TestKpid is { } tk ? $" or (spid = {ts} and kpid = {tk})" : "")),
            CultureInfo.InvariantCulture) > 0;

    internal static List<SweepItem> ReadItems(ISqlExec x, string home, long journalId) =>
        x.Rows($"select seq, kind, db, obj, predicate, snapshot, has_identity from {home}..{WriterJournal.ItemTable} " +
               $"where journal_id = {journalId} order by seq")
            .Select(r => new SweepItem(
                Convert.ToInt32(r[0], CultureInfo.InvariantCulture),
                char.ToUpperInvariant(Convert.ToString(r[1])!.Trim()[0]),
                Convert.ToString(r[2])!.Trim(), Convert.ToString(r[3])!.Trim(), Convert.ToString(r[4])!.Trim(),
                Convert.ToString(r[5])?.Trim(), Convert.ToBoolean(r[6], CultureInfo.InvariantCulture)))
            .ToList();

    // CAS on the dead (spid, kpid); taking our own spid makes a failed or orphaned claim look dead again next run.
    internal static bool Claim(ISqlExec x, string home, JournalRow row) =>
        Convert.ToInt32(x.Scalar(
            "declare @k int select @k = kpid from master..sysprocesses where spid = @@spid " +
            $"update {home}..{WriterJournal.JournalTable} set state = 'restoring', spid = @@spid, kpid = @k, " +
            "test_spid = null, test_kpid = null " +
            $"where journal_id = {row.JournalId} and spid = {row.Spid} and kpid = {row.Kpid} " +
            "select @@rowcount"), CultureInfo.InvariantCulture) == 1;

    private static string? RestoreRow(ISqlExec x, ISqlExec restoreX, string home, JournalRow row, List<SweepItem> items,
                                      Func<SweepItem, string?>? dropVariant, List<string> warnings)
    {
        var failures = new List<string>();
        if (dropVariant != null)
            foreach (var v in items.Where(i => i.Kind == 'V'))
                if (dropVariant(v) is { } err) failures.Add(err);

        var restores = items.Where(i => i.Kind == 'R').Select(i =>
        {
            var spec = new RestoreSpec(i.Db, i.Obj, i.Predicate, AllowTriggers: true);
            var cols = WriterJournal.SelectRestoreColumns(x.Rows(WriterJournal.ColumnsSql(spec)));
            // The item row is what Begin saw; the column status is re-read and could differ.
            return new JournalItem(i.Seq, spec, i.Snapshot ?? "", cols with { HasIdentity = i.HasIdentity });
        }).ToList();

        // A missing snapshot means the runner died before taking it: its rows were never written.
        var detail = WriterJournal.RestoreDetail(restoreX, home, row.JournalId, restores, warnings,
                                                 tolerateMissingSnapshot: true, deleteJournalRow: failures.Count == 0);
        if (detail != null) failures.Add(detail);
        return failures.Count == 0 ? null : string.Join(" | ", failures);
    }
}
