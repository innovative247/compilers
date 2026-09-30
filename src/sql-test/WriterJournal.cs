using System.Globalization;
using System.Text.RegularExpressions;
using AdoNetCore.AseClient;

namespace SqlTest;

/// <summary>`-- @restore: <db>..<table> where <pred> [allow-triggers] [max <n>]`.</summary>
public record RestoreSpec(string Db, string Table, string Predicate, bool AllowTriggers, int MaxRows = 5000)
{
    public string FullName => $"{Db}..{Table}";
}

/// <summary>A journal row that already names a restore table.</summary>
public record HeldRow(long JournalId, string Login, string Host, int Spid);

/// <summary>
/// Discovery-time facts about one spec, read on the control connection.
/// JournalError is global (tables in the home db); the rest are per spec.
/// </summary>
public record RestoreProbe(
    bool TableExists,
    long? RowCount = null,
    string? CountError = null,
    IReadOnlyList<string>? Triggers = null, // insert/delete trigger names
    string? UserName = null,                // user_name() inside <db>
    string? JournalError = null,
    HeldRow? HeldBy = null);

/// <summary>Insert list for a restore: timestamp columns dropped.</summary>
public record RestoreColumns(IReadOnlyList<string> Columns, bool HasIdentity);

/// <summary>One journal item as the restore needs it.</summary>
public record JournalItem(int Seq, RestoreSpec Spec, string Snapshot, RestoreColumns Columns);

/// <summary>Another journal row names a restore table; the message is the refusal reason.</summary>
public sealed class WriterHeldException(string message) : Exception(message);

/// <summary>SQL seam so the restore path is testable without a server.</summary>
public interface ISqlExec
{
    int Exec(string sql);
    object? Scalar(string sql);
    List<object?[]> Rows(string sql);
}

public sealed class AseSqlExec : ISqlExec
{
    private readonly AseConnection _conn;
    private readonly int _timeoutSeconds;

    public AseSqlExec(AseConnection conn, int timeoutSeconds = 0)
    {
        _conn = conn;
        _timeoutSeconds = timeoutSeconds;
    }

    private AseCommand Cmd(string sql) => new(sql, _conn) { CommandTimeout = _timeoutSeconds };

    public int Exec(string sql)
    {
        using var cmd = Cmd(sql);
        return cmd.ExecuteNonQuery();
    }

    public object? Scalar(string sql)
    {
        using var cmd = Cmd(sql);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    public List<object?[]> Rows(string sql)
    {
        using var cmd = Cmd(sql);
        using var reader = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (int i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
}

internal static class WriterJournal
{
    public const int DefaultMaxRows = 5000;
    public const string JournalTable = "tbl_test_writer_journal";
    public const string ItemTable = "tbl_test_writer_item";

    private static readonly Regex RestoreRe = new(@"^\s*--\s*@restore(?![\w-])(.*)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex LineRe = new(@"^\s*:\s*([A-Za-z_][\w#$@]*)\.\.([A-Za-z_#][\w#$@]*)\s+where\s+(.+)$", RegexOptions.IgnoreCase);
    private static readonly Regex AllowTriggersRe = new(@"^(.*?)(?:^|\s+)allow-triggers$", RegexOptions.IgnoreCase);
    private static readonly Regex MaxRe = new(@"^(.*?)(?:^|\s+)max\s+(\d{1,9})$", RegexOptions.IgnoreCase);

    // Any bad line voids all specs: a partial restore set would leak the rest.
    internal static IReadOnlyList<RestoreSpec> ParseRestoreSpecs(string? body, out string? error)
    {
        error = null;
        var specs = new List<RestoreSpec>();
        if (body == null) return specs;
        foreach (Match m in RestoreRe.Matches(body))
        {
            var spec = ParseLine(m.Groups[1].Value.Trim());
            if (spec == null)
            {
                error = $"invalid @restore directive: {m.Value.Trim()}";
                return Array.Empty<RestoreSpec>();
            }
            specs.Add(spec);
        }
        return specs;
    }

    private static RestoreSpec? ParseLine(string rest)
    {
        var m = LineRe.Match(rest);
        if (!m.Success) return null;
        var pred = m.Groups[3].Value.Trim();
        bool allow = false;
        int? max = null;
        for (int i = 0; i < 2; i++)
        {
            var a = AllowTriggersRe.Match(pred);
            if (a.Success && !allow) { allow = true; pred = a.Groups[1].Value.TrimEnd(); continue; }
            var x = MaxRe.Match(pred);
            if (x.Success && max == null)
            {
                max = int.Parse(x.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture);
                pred = x.Groups[1].Value.TrimEnd();
                continue;
            }
            break;
        }
        if (pred.Length == 0 || max == 0) return null;
        if (AllowTriggersRe.IsMatch(pred) || MaxRe.IsMatch(pred)) return null; // repeated option
        return new RestoreSpec(m.Groups[1].Value, m.Groups[2].Value, pred, allow, max ?? DefaultMaxRows);
    }

    internal static bool HasRestoreLine(string? body) => body != null && RestoreRe.IsMatch(body);

    internal static IReadOnlyList<RestoreSpec> ResolveWriter(string? body, bool noTran, out string? error)
    {
        var specs = ParseRestoreSpecs(body, out error);
        if (error != null || specs.Count == 0) return specs;
        if (!noTran)
        {
            error = "@restore requires @no-transaction";
            return Array.Empty<RestoreSpec>();
        }
        return specs;
    }

    internal static string? RefuseReason(RestoreSpec spec, RestoreProbe probe)
    {
        var t = spec.FullName;
        if (probe.JournalError != null) return $"writer journal unavailable: {probe.JournalError}";
        if (!probe.TableExists) return $"@restore table not found: {t}";
        if (!string.Equals(probe.UserName, "dbo", StringComparison.OrdinalIgnoreCase))
            return $"@restore needs dbo in {spec.Db} (run as GONZO_TEST)";
        if (probe.CountError != null) return $"@restore predicate failed: {probe.CountError}";
        if (probe.RowCount > spec.MaxRows) return $"@restore matches {probe.RowCount} rows (max {spec.MaxRows})";
        if (!spec.AllowTriggers && probe.Triggers is { Count: > 0 })
            return $"@restore table {t} has trigger {probe.Triggers[0]}; add allow-triggers if its side effects are acceptable";
        if (probe.HeldBy is { } h) return HeldReason(spec, h);
        return null;
    }

    internal static string HeldReason(RestoreSpec spec, HeldRow h) =>
        $"@restore table {spec.FullName} is held by journal row {h.JournalId} ({h.Login}@{h.Host}, spid {h.Spid})";

    /// <summary>First journal row other than <paramref name="ownJournalId"/> that names the spec's table.</summary>
    internal static HeldRow? ReadHeld(ISqlExec x, string home, RestoreSpec s, long ownJournalId = 0) =>
        x.Rows(
            $"select j.journal_id, j.login, j.hostname, j.spid from {home}..{JournalTable} j, {home}..{ItemTable} i " +
            $"where i.journal_id = j.journal_id and i.kind = 'R' and i.db = {Lit(s.Db)} and i.obj = {Lit(s.Table)}" +
            (ownJournalId != 0 ? $" and j.journal_id <> {ownJournalId}" : ""))
            .Select(r => new HeldRow(Convert.ToInt64(r[0], CultureInfo.InvariantCulture), Convert.ToString(r[1])!.Trim(),
                                     Convert.ToString(r[2])?.Trim() ?? "", Convert.ToInt32(r[3], CultureInfo.InvariantCulture)))
            .FirstOrDefault();

    /// <summary>Rows are (name, type name, status) from syscolumns/systypes in colid order.</summary>
    internal static RestoreColumns SelectRestoreColumns(IEnumerable<object?[]> rows)
    {
        var cols = new List<string>();
        bool identity = false;
        foreach (var r in rows)
        {
            // ASE rejects timestamp columns in an insert list.
            if (string.Equals(Convert.ToString(r[1])?.Trim(), "timestamp", StringComparison.OrdinalIgnoreCase)) continue;
            cols.Add(Runner.QuoteIdent(Convert.ToString(r[0])!.Trim()));
            if ((Convert.ToInt32(r[2], CultureInfo.InvariantCulture) & 0x80) != 0) identity = true;
        }
        return new RestoreColumns(cols, identity);
    }

    internal static string ColumnsSql(RestoreSpec s) =>
        $"select c.name, t.name, c.status from {s.Db}..syscolumns c, {s.Db}..systypes t " +
        $"where c.id = object_id('{s.FullName}') and c.usertype = t.usertype order by c.colid";

    internal static string SnapshotName(long journalId, int seq) => $"tbl_test_snap_{journalId}_{seq}";

    // Separate statements, not one batch: ASE continues a batch past a failed
    // insert, so a batched `commit tran` would keep the delete. The item row goes
    // in the same transaction so it can never outlive the restored rows.
    internal static IReadOnlyList<string> BuildRestoreSql(RestoreSpec s, string snapshot, RestoreColumns cols, string itemDelete)
    {
        var list = string.Join(", ", cols.Columns);
        var sql = new List<string>();
        if (cols.HasIdentity) sql.Add($"set identity_insert {s.FullName} on");
        sql.Add("begin tran");
        sql.Add($"delete {s.FullName} where {s.Predicate}");
        sql.Add($"insert {s.FullName} ({list}) select {list} from {snapshot}");
        sql.Add(itemDelete);
        sql.Add("commit tran");
        if (cols.HasIdentity) sql.Add($"set identity_insert {s.FullName} off");
        return sql;
    }

    internal static string Lit(string s) => "'" + s.Replace("'", "''") + "'";

    // ASE resolves create table at batch compile, so the DDL runs via exec() to let the guard skip it.
    internal static void EnsureTables(ISqlExec x, string home)
    {
        x.Exec($"if object_id('{home}..{JournalTable}') is null exec({Lit(JournalDdl(home))})");
        x.Exec($"if object_id('{home}..{ItemTable}') is null exec({Lit(ItemDdl(home))})");
    }

    private static string JournalDdl(string home) => $@"create table {home}..{JournalTable} (
  journal_id numeric(18,0) identity,
  spid int not null, kpid int not null,
  login varchar(30) not null, hostname varchar(30) null,
  started datetime not null, test varchar(255) not null,
  state varchar(10) not null) lock datarows";

    private static string ItemDdl(string home) => $@"create table {home}..{ItemTable} (
  journal_id numeric(18,0) not null, seq int not null, kind char(1) not null,
  db varchar(30) not null, obj varchar(255) not null,
  predicate varchar(2000) not null, snapshot varchar(255) null,
  has_identity bit default 0 not null) lock datarows";

    /// <summary>Discovery probe for one spec; control connection sits in <paramref name="home"/>.</summary>
    internal static RestoreProbe Probe(ISqlExec x, RestoreSpec s, string home)
    {
        try { EnsureTables(x, home); }
        catch (Exception ex) { return new RestoreProbe(false, JournalError: ex.Message); }

        if (x.Scalar($"select object_id('{s.FullName}')") == null) return new RestoreProbe(false);

        string? user;
        x.Exec($"use {s.Db}");
        try { user = Convert.ToString(x.Scalar("select user_name()"))?.Trim(); }
        finally { x.Exec($"use {home}"); }

        long? count = null;
        string? countError = null;
        try { count = Convert.ToInt64(x.Scalar($"select count(*) from {s.FullName} where {s.Predicate}"), CultureInfo.InvariantCulture); }
        catch (Exception ex) { countError = ex.Message; }

        var triggers = x.Rows(
            $"select t.name from {s.Db}..sysobjects o, {s.Db}..sysobjects t " +
            $"where o.id = object_id('{s.FullName}') and t.id in (o.instrig, o.deltrig) and t.id <> 0")
            .Select(r => Convert.ToString(r[0])!.Trim()).ToList();

        return new RestoreProbe(true, count, countError, triggers, user, HeldBy: ReadHeld(x, home, s));
    }

    /// <summary>
    /// Restores each item in its own transaction, in declared order. The journal
    /// row goes only when every item succeeded. Null on success; a snapshot that
    /// could not be dropped is a warning, not a failure.
    /// </summary>
    /// <param name="tolerateMissingSnapshot">
    /// Sweep only: there a missing snapshot means setup never reached it, so its
    /// rows are left alone. In-process, Begin created every snapshot, so a missing
    /// one is a failure.
    /// </param>
    internal static string? Restore(ISqlExec x, string home, long journalId, IEnumerable<JournalItem> items,
                                    List<string>? warnings = null, bool tolerateMissingSnapshot = false)
    {
        var failures = new List<string>();
        foreach (var it in items)
        {
            var snap = $"{home}..{it.Snapshot}";
            var itemDelete = $"delete {home}..{ItemTable} where journal_id = {journalId} and seq = {it.Seq}";
            try
            {
                if (x.Scalar($"select object_id('{snap}')") == null)
                {
                    if (!tolerateMissingSnapshot) { failures.Add($"snapshot {snap} missing"); continue; }
                    x.Exec(itemDelete);
                    continue;
                }
                RunRestore(x, BuildRestoreSql(it.Spec, snap, it.Columns, itemDelete), it);
            }
            catch (Exception ex) { failures.Add(ex.Message); continue; }
            try { x.Exec($"drop table {snap}"); }
            catch (Exception ex) { warnings?.Add($"snapshot {snap} not dropped: {ex.Message}"); }
        }
        if (failures.Count > 0)
            return $"restore failed: {string.Join(" | ", failures)}; journal row {journalId} kept";
        try { x.Exec($"delete {home}..{JournalTable} where journal_id = {journalId}"); }
        catch (Exception ex) { return $"restore failed: {ex.Message}; journal row {journalId} kept"; }
        return null;
    }

    private static void RunRestore(ISqlExec x, IReadOnlyList<string> sql, JournalItem it)
    {
        try { foreach (var s in sql) x.Exec(s); }
        catch
        {
            try { x.Exec("if @@trancount > 0 rollback tran"); } catch { /* original error wins */ }
            if (it.Columns.HasIdentity)
                try { x.Exec($"set identity_insert {it.Spec.FullName} off"); } catch { /* original error wins */ }
            throw;
        }
    }
}

/// <summary>One writer test's journal row, item rows and snapshots on the control connection.</summary>
public sealed class WriterSession
{
    private readonly ISqlExec _x;
    private readonly string _home;
    private readonly List<JournalItem> _items = new();

    public long JournalId { get; private set; }
    public IReadOnlyList<JournalItem> Items => _items;
    public List<string> Warnings { get; } = new();

    private WriterSession(ISqlExec x, string home) { _x = x; _home = home; }

    /// <summary>
    /// Commits the recipe before the test writes. A part-way failure undoes what
    /// it created and rethrows; nothing on the product database has changed yet.
    /// </summary>
    public static WriterSession Begin(ISqlExec x, string home, string testName, IReadOnlyList<RestoreSpec> specs)
    {
        var s = new WriterSession(x, home);
        var snaps = new List<string>();
        WriterJournal.EnsureTables(x, home);
        try
        {
            s.JournalId = Convert.ToInt64(x.Scalar(
                $"insert {home}..{WriterJournal.JournalTable} (spid, kpid, login, hostname, started, test, state) " +
                $"select @@spid, p.kpid, suser_name(), p.hostname, getdate(), {WriterJournal.Lit(testName)}, 'pending' " +
                $"from master..sysprocesses p where p.spid = @@spid " +
                "select @@identity"), CultureInfo.InvariantCulture);
            if (s.JournalId == 0) throw new InvalidOperationException("journal insert returned no identity");

            for (int i = 0; i < specs.Count; i++)
            {
                var spec = specs[i];
                var cols = WriterJournal.SelectRestoreColumns(x.Rows(WriterJournal.ColumnsSql(spec)));
                var item = new JournalItem(i + 1, spec, WriterJournal.SnapshotName(s.JournalId, i + 1), cols);
                x.Exec($"insert {home}..{WriterJournal.ItemTable} (journal_id, seq, kind, db, obj, predicate, snapshot, has_identity) " +
                       $"values ({s.JournalId}, {item.Seq}, 'R', {WriterJournal.Lit(spec.Db)}, {WriterJournal.Lit(spec.Table)}, " +
                       $"{WriterJournal.Lit(spec.Predicate)}, {WriterJournal.Lit(item.Snapshot)}, {(cols.HasIdentity ? 1 : 0)})");
                s._items.Add(item);
            }
            // Discovery saw no holder, but another runner may have journalled the table since.
            // Checking after our own insert means two racers both refuse rather than both run.
            foreach (var it in s._items)
                if (WriterJournal.ReadHeld(x, home, it.Spec, s.JournalId) is { } h)
                    throw new WriterHeldException(WriterJournal.HeldReason(it.Spec, h));
            foreach (var it in s._items)
            {
                var list = string.Join(", ", it.Columns.Columns);
                x.Exec($"select {list} into {home}..{it.Snapshot} from {it.Spec.FullName} where {it.Spec.Predicate}");
                snaps.Add(it.Snapshot);
            }
            return s;
        }
        catch
        {
            s.Undo(snaps);
            throw;
        }
    }

    private void Undo(List<string> snaps)
    {
        try
        {
            foreach (var snap in snaps) _x.Exec($"drop table {_home}..{snap}");
            if (JournalId != 0)
            {
                _x.Exec($"delete {_home}..{WriterJournal.ItemTable} where journal_id = {JournalId}");
                _x.Exec($"delete {_home}..{WriterJournal.JournalTable} where journal_id = {JournalId}");
            }
        }
        catch { /* the setup error is reported; leftovers stay for the sweep */ }
    }

    /// <summary>Null on success, else `restore failed: ...; journal row <id> kept`.</summary>
    public string? Restore() => WriterJournal.Restore(_x, _home, JournalId, _items, Warnings);
}
