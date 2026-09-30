using SqlTest;

namespace SqlTest.Tests;

public class WriterJournalTests
{
    private static readonly RestoreSpec Spec = new("sbnmaster", "fe_bell", "s#inc = 1", false);
    private static readonly RestoreProbe Ok = new(true, RowCount: 3, Triggers: Array.Empty<string>(), UserName: "dbo");

    private static JournalItem Item(int seq, bool identity = false) =>
        new(seq, Spec, $"tbl_test_snap_7_{seq}", new RestoreColumns(new[] { "s#inc", "name" }, identity));

    [Fact]
    public void Parse_reads_lines_options_and_multiple_specs()
    {
        var body = "-- @no-transaction\n" +
                   "-- @restore: sbnmaster..ma_alarmqueue3 where s#inc = 428379\r\n" +
                   "  --@restore: sbnmaster..fe_bell where s#inc = 428379 allow-triggers\n" +
                   "-- @restore: sbntest..t where a in (1, 2) max 20000\n" +
                   "-- @restore: sbntest..u where b = 'x' max 10 allow-triggers\n" +
                   "create proc bench_a as select 1";
        var specs = WriterJournal.ParseRestoreSpecs(body, out var error);
        Assert.Null(error);
        Assert.Equal(new[]
        {
            new RestoreSpec("sbnmaster", "ma_alarmqueue3", "s#inc = 428379", false),
            new RestoreSpec("sbnmaster", "fe_bell", "s#inc = 428379", true),
            new RestoreSpec("sbntest", "t", "a in (1, 2)", false, 20000),
            new RestoreSpec("sbntest", "u", "b = 'x'", true, 10),
        }, specs);
    }

    [Theory]
    [InlineData("-- @restore: fe_bell where s#inc = 1")]
    [InlineData("-- @restore sbnmaster..fe_bell where s#inc = 1")]
    [InlineData("-- @restore: sbnmaster..fe_bell")]
    [InlineData("-- @restore: sbnmaster..fe_bell where allow-triggers")]
    [InlineData("-- @restore: sbnmaster..fe_bell where a = 1 max 0")]
    [InlineData("-- @restore: sbnmaster..fe_bell where a = 1 allow-triggers allow-triggers")]
    public void Parse_malformed_line_is_error_with_no_specs(string line)
    {
        var body = "-- @restore: sbntest..t where a = 1\n" + line + "\n";
        var specs = WriterJournal.ParseRestoreSpecs(body, out var error);
        Assert.Empty(specs);
        Assert.Equal($"invalid @restore directive: {line}", error);
    }

    [Fact]
    public void Restore_without_no_transaction_is_refused()
    {
        var specs = WriterJournal.ResolveWriter("-- @restore: sbntest..t where a = 1", noTran: false, out var error);
        Assert.Empty(specs);
        Assert.Equal("@restore requires @no-transaction", error);
    }

    [Fact]
    public void Clean_probe_is_not_refused() => Assert.Null(WriterJournal.RefuseReason(Spec, Ok));

    [Fact]
    public void Refuse_table_missing() =>
        Assert.Equal("@restore table not found: sbnmaster..fe_bell",
                     WriterJournal.RefuseReason(Spec, new RestoreProbe(false)));

    [Fact]
    public void Refuse_predicate_failed() =>
        Assert.Equal("@restore predicate failed: Invalid column name 'x'.",
                     WriterJournal.RefuseReason(Spec, Ok with { RowCount = null, CountError = "Invalid column name 'x'." }));

    [Fact]
    public void Refuse_default_cap_exceeded()
    {
        Assert.Null(WriterJournal.RefuseReason(Spec, Ok with { RowCount = 5000 }));
        Assert.Equal("@restore matches 5001 rows (max 5000)", WriterJournal.RefuseReason(Spec, Ok with { RowCount = 5001 }));
    }

    [Fact]
    public void Refuse_max_override_exceeded()
    {
        var spec = Spec with { MaxRows = 20000 };
        Assert.Null(WriterJournal.RefuseReason(spec, Ok with { RowCount = 20000 }));
        Assert.Equal("@restore matches 20001 rows (max 20000)", WriterJournal.RefuseReason(spec, Ok with { RowCount = 20001 }));
    }

    [Fact]
    public void Refuse_trigger_without_allow_triggers()
    {
        var probe = Ok with { Triggers = new[] { "tri_fe_bell" } };
        Assert.Equal("@restore table sbnmaster..fe_bell has trigger tri_fe_bell; add allow-triggers if its side effects are acceptable",
                     WriterJournal.RefuseReason(Spec, probe));
        Assert.Null(WriterJournal.RefuseReason(Spec with { AllowTriggers = true }, probe));
    }

    [Fact]
    public void Refuse_not_dbo() =>
        Assert.Equal("@restore needs dbo in sbnmaster (run as GONZO_TEST)",
                     WriterJournal.RefuseReason(Spec, Ok with { UserName = "guest" }));

    [Fact]
    public void Refuse_journal_unavailable() =>
        Assert.Equal("writer journal unavailable: no create table permission",
                     WriterJournal.RefuseReason(Spec, new RestoreProbe(false, JournalError: "no create table permission")));

    [Fact]
    public void Refuse_table_held_by_journal_row() =>
        Assert.Equal("@restore table sbnmaster..fe_bell is held by journal row 42 (jens@mac, spid 17)",
                     WriterJournal.RefuseReason(Spec, Ok with { HeldBy = new HeldRow(42, "jens", "mac", 17) }));

    [Fact]
    public void Columns_drop_timestamp_and_detect_identity()
    {
        var cols = WriterJournal.SelectRestoreColumns(new[]
        {
            new object?[] { "id", "numeric", 0x80 },
            new object?[] { "name", "varchar", 0 },
            new object?[] { "ts", "timestamp", 0 },
            new object?[] { "2nd", "int", 8 },
        });
        Assert.Equal(new[] { "id", "name", "[2nd]" }, cols.Columns);
        Assert.True(cols.HasIdentity);
    }

    [Fact]
    public void Restore_sql_wraps_identity_insert_around_the_transaction()
    {
        var sql = WriterJournal.BuildRestoreSql(Spec, "sbntest..snap", new RestoreColumns(new[] { "id", "name" }, true), "delete item");
        Assert.Equal(new[]
        {
            "set identity_insert sbnmaster..fe_bell on",
            "begin tran",
            "delete sbnmaster..fe_bell where s#inc = 1",
            "insert sbnmaster..fe_bell (id, name) select id, name from sbntest..snap",
            "delete item",
            "commit tran",
            "set identity_insert sbnmaster..fe_bell off",
        }, sql);

        var plain = WriterJournal.BuildRestoreSql(Spec, "sbntest..snap", new RestoreColumns(new[] { "name" }, false), "delete item");
        Assert.DoesNotContain(plain, s => s.Contains("identity_insert"));
    }

    [Fact]
    public void EnsureTables_defers_create_table_through_exec()
    {
        var x = new FakeExec();
        WriterJournal.EnsureTables(x, "sbntest");
        Assert.Equal(3, x.Log.Count);
        Assert.Equal("if col_length('sbntest..tbl_test_writer_journal', 'test_spid') is null " +
                     "exec('alter table sbntest..tbl_test_writer_journal add test_spid int null, test_kpid int null')", x.Log[2]);
        Assert.All(x.Log.Take(2), sql =>
        {
            Assert.StartsWith("if object_id('sbntest..tbl_test_writer_", sql);
            Assert.Contains(") is null exec('create table sbntest..tbl_test_writer_", sql);
        });
    }

    [Fact]
    public void Restore_failure_rolls_back_and_keeps_journal_row()
    {
        var x = new FakeExec { FailOn = "insert sbnmaster..fe_bell" };
        var msg = WriterJournal.Restore(x, "sbntest", 7, new[] { Item(1, identity: true), Item(2) });
        Assert.Equal("restore failed: boom | boom; journal row 7 kept", msg);
        Assert.Contains("if @@trancount > 0 rollback tran", x.Log);
        Assert.Contains("set identity_insert sbnmaster..fe_bell off", x.Log);
        Assert.DoesNotContain(x.Log, s => s.StartsWith("delete sbntest..tbl_test_writer_journal"));
        Assert.DoesNotContain(x.Log, s => s.StartsWith("delete sbntest..tbl_test_writer_item"));
        Assert.DoesNotContain("commit tran", x.Log);
        Assert.DoesNotContain(x.Log, s => s.StartsWith("drop table"));
    }

    [Fact]
    public void Restore_success_deletes_items_and_journal_row()
    {
        var x = new FakeExec();
        Assert.Null(WriterJournal.Restore(x, "sbntest", 7, new[] { Item(1), Item(2) }));
        foreach (var seq in new[] { 1, 2 })
        {
            var item = x.Log.IndexOf($"delete sbntest..tbl_test_writer_item where journal_id = 7 and seq = {seq}");
            var drop = x.Log.IndexOf($"drop table sbntest..tbl_test_snap_7_{seq}");
            Assert.True(item >= 0 && drop > item, "item row deleted before the snapshot is dropped");
            Assert.Equal("commit tran", x.Log[item + 1]);
        }
        Assert.Equal("delete sbntest..tbl_test_writer_journal where journal_id = 7", x.Log[^1]);
    }

    [Fact]
    public void Restore_drop_failure_is_a_warning_not_a_failure()
    {
        var x = new FakeExec { FailOn = "drop table sbntest..tbl_test_snap_7_1" };
        var warnings = new List<string>();
        Assert.Null(WriterJournal.Restore(x, "sbntest", 7, new[] { Item(1), Item(2) }, warnings));
        Assert.Equal("snapshot sbntest..tbl_test_snap_7_1 not dropped: boom", Assert.Single(warnings));
        Assert.Contains("drop table sbntest..tbl_test_snap_7_2", x.Log);
        Assert.Equal("delete sbntest..tbl_test_writer_journal where journal_id = 7", x.Log[^1]);
    }

    [Fact]
    public void Restore_missing_snapshot_is_a_failure_in_process()
    {
        var x = new FakeExec { OnScalar = sql => sql.Contains("tbl_test_snap_7_2") ? null : 1 };
        Assert.Equal("restore failed: snapshot sbntest..tbl_test_snap_7_2 missing; journal row 7 kept",
                     WriterJournal.Restore(x, "sbntest", 7, new[] { Item(1), Item(2) }));
        Assert.DoesNotContain("delete sbntest..tbl_test_writer_item where journal_id = 7 and seq = 2", x.Log);
        Assert.DoesNotContain(x.Log, s => s.StartsWith("delete sbntest..tbl_test_writer_journal"));
    }

    [Fact]
    public void Restore_sweep_skips_product_delete_when_snapshot_is_missing()
    {
        var x = new FakeExec { OnScalar = sql => sql.Contains("tbl_test_snap_7_2") ? null : 1 };
        Assert.Null(WriterJournal.Restore(x, "sbntest", 7, new[] { Item(1), Item(2) }, tolerateMissingSnapshot: true));
        Assert.Single(x.Log, s => s.StartsWith("delete sbnmaster..fe_bell"));
        Assert.Contains("insert sbnmaster..fe_bell (s#inc, name) select s#inc, name from sbntest..tbl_test_snap_7_1", x.Log);
        Assert.Contains("delete sbntest..tbl_test_writer_item where journal_id = 7 and seq = 2", x.Log);
        Assert.DoesNotContain("drop table sbntest..tbl_test_snap_7_2", x.Log);
    }

    private static readonly RestoreSpec Spec2 = new("sbntest", "t", "a = 1", false);

    // Journal insert returns identity 9; columns come back for every spec.
    private static FakeExec BeginExec() => new()
    {
        OnScalar = sql => sql.StartsWith("insert sbntest..tbl_test_writer_journal") ? 9m : null,
        OnRows = sql => sql.Contains("syscolumns") ? new() { new object?[] { "a", "int", 0 } } : new(),
    };

    [Fact]
    public void Begin_journals_items_then_checks_held_then_snapshots()
    {
        var x = BeginExec();
        var s = WriterSession.Begin(x, "sbntest", "test_w", new[] { Spec, Spec2 }, 21, 210);
        Assert.Equal(9L, s.JournalId);
        Assert.Contains(x.Log, l => l.StartsWith("insert sbntest..tbl_test_writer_journal (spid, kpid, test_spid, test_kpid,")
                                    && l.Contains("select @@spid, p.kpid, 21, 210,"));
        int At(string prefix) => x.Log.FindIndex(l => l.StartsWith(prefix));
        var journal = At("insert sbntest..tbl_test_writer_journal");
        var item2 = x.Log.FindIndex(l => l.StartsWith("insert sbntest..tbl_test_writer_item") && l.Contains("(9, 2,"));
        var held = At("select j.journal_id");
        var snap1 = At("select a into sbntest..tbl_test_snap_9_1 from sbnmaster..fe_bell where s#inc = 1");
        var snap2 = At("select a into sbntest..tbl_test_snap_9_2 from sbntest..t where a = 1");
        Assert.True(journal >= 0 && journal < item2 && item2 < held && held < snap1 && snap1 < snap2, string.Join("\n", x.Log));
        Assert.Contains(x.Log, l => l.StartsWith("select j.journal_id") && l.EndsWith("and j.journal_id <> 9"));
        Assert.DoesNotContain(x.Log, l => l.StartsWith("delete") || l.StartsWith("drop"));
    }

    // Restore runs on its own exec (no deadline in production), never on Begin's.
    [Fact]
    public void Restore_uses_the_restore_exec()
    {
        var x = BeginExec();
        var r = new FakeExec();
        var s = WriterSession.Begin(x, "sbntest", "test_w", new[] { Spec }, 21, 210, restoreX: r);
        var begun = x.Log.Count;
        Assert.Null(s.Restore());
        Assert.Equal(begun, x.Log.Count);
        Assert.Contains(r.Log, l => l.StartsWith("drop table sbntest..tbl_test_snap_9_1"));
    }

    [Fact]
    public void Begin_failure_part_way_undoes_its_own_snapshots_and_rows()
    {
        var x = BeginExec();
        x.FailOn = "into sbntest..tbl_test_snap_9_2";
        Assert.Throws<InvalidOperationException>(() => WriterSession.Begin(x, "sbntest", "test_w", new[] { Spec, Spec2 }, 21, 210));
        var undo = x.Log.SkipWhile(l => !l.StartsWith("select a into sbntest..tbl_test_snap_9_2")).Skip(1).ToList();
        Assert.Equal(new[]
        {
            "drop table sbntest..tbl_test_snap_9_1",
            "delete sbntest..tbl_test_writer_item where journal_id = 9",
            "delete sbntest..tbl_test_writer_journal where journal_id = 9",
        }, undo);
    }

    [Fact]
    public void Begin_refuses_when_another_journal_row_took_the_table()
    {
        var x = BeginExec();
        var rows = x.OnRows;
        x.OnRows = sql => sql.StartsWith("select j.journal_id") && sql.Contains("'fe_bell'")
            ? new() { new object?[] { 8m, "jens", "mac", 17 } } : rows(sql);
        var ex = Assert.Throws<WriterHeldException>(() => WriterSession.Begin(x, "sbntest", "test_w", new[] { Spec, Spec2 }, 21, 210));
        Assert.Equal("@restore table sbnmaster..fe_bell is held by journal row 8 (jens@mac, spid 17)", ex.Message);
        Assert.DoesNotContain(x.Log, l => l.Contains(" into sbntest..tbl_test_snap_"));
        Assert.Equal(new[]
        {
            "delete sbntest..tbl_test_writer_item where journal_id = 9",
            "delete sbntest..tbl_test_writer_journal where journal_id = 9",
        }, x.Log.TakeLast(2));
    }

    // Sweep fake: one journal row 7 (control 17/170, test 18/180) with the given items; Log is shared by both execs.
    private static FakeExec SweepExec(bool alive = false, int claimed = 1, string? missingSnap = null, params object?[][] items)
    {
        var x = new FakeExec();
        x.OnScalar = sql =>
            sql.StartsWith("select count(*) from master..sysprocesses") ? (alive ? 1 : 0)
            : sql.StartsWith("declare @k") ? claimed
            : missingSnap != null && sql.Contains(missingSnap) ? null
            : 1;
        x.OnRows = sql =>
            sql.StartsWith("select journal_id") ? new() { new object?[] { 7m, 17, 170, "jens", "mac", new DateTime(2026, 9, 30, 12, 0, 0), "test_w", 18, 180 } }
            : sql.StartsWith("select seq") ? items.ToList()
            : sql.Contains("syscolumns") ? new() { new object?[] { "s#inc", "int", 0 }, new object?[] { "name", "varchar", 0 } }
            : new();
        return x;
    }

    private static object?[] RItem(int seq) => new object?[] { seq, "R", "sbnmaster", "fe_bell", "s#inc = 1", $"tbl_test_snap_7_{seq}", false };

    private static List<string> RunSweep(FakeExec x)
    {
        var lines = new List<string>();
        WriterSweep.Sweep(x, x, "sbntest", lines.Add);
        return lines;
    }

    private const string Row7 = "writer journal row 7 (test_w, jens@mac, 2026-09-30 12:00:00)";

    [Fact]
    public void Sweep_without_journal_table_reads_nothing()
    {
        var x = SweepExec();
        x.OnScalar = _ => null;
        Assert.Empty(RunSweep(x));
        Assert.Single(x.Log);
    }

    [Fact]
    public void Sweep_restores_and_deletes_a_dead_row()
    {
        var x = SweepExec(items: new[] { RItem(1), RItem(2) });
        Assert.Equal($"sql-test: {Row7} restored", Assert.Single(RunSweep(x)));
        var claim = x.Log.FindIndex(l => l.StartsWith("declare @k"));
        var firstDelete = x.Log.FindIndex(l => l.StartsWith("delete sbnmaster..fe_bell"));
        Assert.True(claim >= 0 && claim < firstDelete, "claim before any restore");
        Assert.Contains("where journal_id = 7 and spid = 17 and kpid = 170", x.Log[claim]);
        Assert.Contains("insert sbnmaster..fe_bell (s#inc, name) select s#inc, name from sbntest..tbl_test_snap_7_2", x.Log);
        Assert.Contains("drop table sbntest..tbl_test_snap_7_1", x.Log);
        Assert.Equal("delete sbntest..tbl_test_writer_journal where journal_id = 7", x.Log[^1]);
    }

    [Fact]
    public void Sweep_liveness_counts_either_connection_and_claim_clears_the_test_pair()
    {
        var x = SweepExec(items: new[] { RItem(1) });
        RunSweep(x);
        Assert.Contains("select count(*) from master..sysprocesses where (spid = 17 and kpid = 170) or (spid = 18 and kpid = 180)", x.Log);
        Assert.Contains(x.Log, l => l.StartsWith("declare @k") && l.Contains("test_spid = null, test_kpid = null"));
    }

    // Rows journalled before the test pair existed have nulls: liveness falls back to the control pair.
    [Fact]
    public void Sweep_row_without_test_pair_checks_the_control_pair_only()
    {
        var x = SweepExec(items: new[] { RItem(1) });
        var rows = x.OnRows;
        x.OnRows = sql => sql.StartsWith("select journal_id")
            ? new() { new object?[] { 7m, 17, 170, "jens", "mac", new DateTime(2026, 9, 30, 12, 0, 0), "test_w", null, null } }
            : rows(sql);
        RunSweep(x);
        Assert.Contains("select count(*) from master..sysprocesses where (spid = 17 and kpid = 170)", x.Log);
    }

    [Fact]
    public void Sweep_adds_the_test_pair_columns_to_an_older_journal_table()
    {
        var x = SweepExec(items: new[] { RItem(1) });
        RunSweep(x);
        var alter = x.Log.FindIndex(l => l.StartsWith("if col_length('sbntest..tbl_test_writer_journal', 'test_spid') is null"));
        var read = x.Log.FindIndex(l => l.StartsWith("select journal_id") && l.Contains("test_spid, test_kpid"));
        Assert.True(alter >= 0 && alter < read, string.Join("\n", x.Log));
    }

    // A live test connection (runner killed, batch still running) keeps the row.
    [Fact]
    public void Sweep_leaves_a_row_alive_on_its_test_connection_only()
    {
        var x = SweepExec(items: new[] { RItem(1) });
        var scalar = x.OnScalar;
        x.OnScalar = sql => sql.StartsWith("select count(*) from master..sysprocesses")
            ? (sql.Contains("or (spid = 18 and kpid = 180)") ? 1 : 0) : scalar(sql);
        Assert.Empty(RunSweep(x));
        Assert.DoesNotContain(x.Log, l => l.StartsWith("declare @k") || l.StartsWith("delete"));
    }

    [Fact]
    public void Sweep_snapshot_under_another_owner_fails_and_keeps_the_row()
    {
        var x = SweepExec(missingSnap: "object_id('sbntest..tbl_test_snap_7_1')", items: new[] { RItem(1) });
        Assert.Equal($"sql-test: WARNING: {Row7} could not be restored: snapshot tbl_test_snap_7_1 belongs to another owner",
                     Assert.Single(RunSweep(x)));
        Assert.Contains("select count(*) from sbntest..sysobjects where name = 'tbl_test_snap_7_1'", x.Log);
        Assert.DoesNotContain(x.Log, l => l.StartsWith("delete"));
    }

    [Fact]
    public void Sweep_restores_on_the_restore_exec()
    {
        var x = SweepExec(items: new[] { RItem(1) });
        var r = new FakeExec { OnScalar = x.OnScalar, OnRows = x.OnRows };
        WriterSweep.Sweep(x, r, "sbntest", _ => { });
        Assert.Contains(r.Log, l => l.StartsWith("delete sbnmaster..fe_bell"));
        Assert.DoesNotContain(x.Log, l => l.StartsWith("delete sbnmaster..fe_bell"));
    }

    [Fact]
    public void Sweep_leaves_a_live_row_unclaimed()
    {
        var x = SweepExec(alive: true, items: new[] { RItem(1) });
        Assert.Empty(RunSweep(x));
        Assert.DoesNotContain(x.Log, l => l.StartsWith("declare @k") || l.StartsWith("select seq") || l.StartsWith("delete"));
    }

    [Fact]
    public void Sweep_lost_claim_restores_nothing()
    {
        var x = SweepExec(claimed: 0, items: new[] { RItem(1) });
        Assert.Empty(RunSweep(x));
        Assert.Contains(x.Log, l => l.StartsWith("declare @k"));
        Assert.DoesNotContain(x.Log, l => l.StartsWith("delete") || l.StartsWith("insert") || l.StartsWith("drop"));
    }

    [Fact]
    public void Sweep_missing_snapshot_skips_product_delete_and_removes_rows()
    {
        var x = SweepExec(missingSnap: "tbl_test_snap_7_1", items: new[] { RItem(1) });
        Assert.Equal($"sql-test: {Row7} restored", Assert.Single(RunSweep(x)));
        Assert.DoesNotContain(x.Log, l => l.StartsWith("delete sbnmaster..fe_bell") || l.StartsWith("drop table"));
        Assert.Contains("delete sbntest..tbl_test_writer_item where journal_id = 7 and seq = 1", x.Log);
        Assert.Equal("delete sbntest..tbl_test_writer_journal where journal_id = 7", x.Log[^1]);
    }

    [Fact]
    public void Sweep_restore_failure_warns_and_keeps_the_row()
    {
        var x = SweepExec(items: new[] { RItem(1) });
        x.FailOn = "insert sbnmaster..fe_bell";
        Assert.Equal($"sql-test: WARNING: {Row7} could not be restored: boom", Assert.Single(RunSweep(x)));
        Assert.Contains("if @@trancount > 0 rollback tran", x.Log);
        Assert.DoesNotContain(x.Log, l => l.StartsWith("delete sbntest..tbl_test_writer_"));
    }

    private static readonly object?[] VItem = { 2, "V", "sbntest", "test_w_v1", "", null, false };

    [Fact]
    public void Sweep_without_dropVariant_leaves_variant_rows_whole_with_a_note()
    {
        var x = SweepExec(items: new[] { RItem(1), VItem });
        Assert.Equal($"sql-test: note: {Row7} holds variant items; left for a later sweep", Assert.Single(RunSweep(x)));
        Assert.DoesNotContain(x.Log, l => l.StartsWith("declare @k") || l.StartsWith("delete") || l.StartsWith("drop"));
    }

    [Fact]
    public void Sweep_mixed_dead_row_restores_then_drops_then_deletes_the_row()
    {
        var x = SweepExec(items: new[] { RItem(1), new object?[] { 2, "V", "sbnmaster", "test_w_v1", "", null, false } });
        var lines = new List<string>();
        WriterSweep.Sweep(x, x, "sbntest", lines.Add, ScratchProc.SweepDropper(x, "sbntest"));
        Assert.Equal($"sql-test: {Row7} restored", Assert.Single(lines));

        int restore = x.Log.FindIndex(l => l.StartsWith("insert sbnmaster..fe_bell"));
        int drop = x.Log.FindIndex(l => l.Contains("drop proc test_w_v1"));
        int item = x.Log.IndexOf("delete sbntest..tbl_test_writer_item where journal_id = 7 and seq = 2");
        Assert.True(restore >= 0 && restore < drop && drop < item);
        Assert.Equal("use sbnmaster", x.Log[drop - 1]);
        Assert.Equal("use sbntest", x.Log[drop + 1]);
        Assert.Equal("delete sbntest..tbl_test_writer_journal where journal_id = 7", x.Log[^1]);
        Assert.Single(x.Log, l => l.StartsWith("delete sbntest..tbl_test_writer_journal"));
    }

    [Fact]
    public void Sweep_of_a_dead_row_keeps_a_proc_a_live_session_holds_and_deletes_only_the_item()
    {
        var x = SweepExec(items: new[] { VItem });
        var (scalar, rows) = (x.OnScalar, x.OnRows);
        x.OnScalar = sql => sql.StartsWith("select count(*) from master..sysprocesses") && sql.Contains("spid = 30") ? 1 : scalar(sql);
        x.OnRows = sql => sql.Contains("i.kind = 'V'")
            ? new() { new object?[] { 5m, 30, 300, "ann", "box", new DateTime(2026, 9, 30, 12, 0, 0), "other_t", null, null } }
            : rows(sql);
        var lines = new List<string>();
        WriterSweep.Sweep(x, x, "sbntest", lines.Add, ScratchProc.SweepDropper(x, "sbntest"));
        Assert.Equal($"sql-test: {Row7} restored", Assert.Single(lines));
        Assert.DoesNotContain(x.Log, l => l.Contains("drop proc"));
        Assert.Contains("delete sbntest..tbl_test_writer_item where journal_id = 7 and seq = 2", x.Log);
    }

    [Fact]
    public void Sweep_restore_failure_still_drops_the_variants_and_keeps_the_row()
    {
        var x = SweepExec(items: new[] { RItem(1), VItem });
        x.FailOn = "insert sbnmaster..fe_bell";
        var lines = new List<string>();
        WriterSweep.Sweep(x, x, "sbntest", lines.Add, ScratchProc.SweepDropper(x, "sbntest"));
        Assert.Equal($"sql-test: WARNING: {Row7} could not be restored: boom", Assert.Single(lines));
        Assert.Contains(x.Log, l => l.EndsWith("drop proc test_w_v1"));
        Assert.DoesNotContain(x.Log, l => l.StartsWith("delete sbntest..tbl_test_writer_journal"));
    }

    [Fact]
    public void Finalise_with_variant_items_keeps_the_row_until_the_last_variant_goes()
    {
        var (x, _) = NewBench();
        var j = WriterSession.Begin(x, "sbntest", "w", new[] { Spec }, null, null);
        var seq = j.AddVariant("sbntest", "w_v1");
        x.Log.Clear();
        Assert.Null(j.Finalise());
        Assert.Contains("drop table sbntest..tbl_test_snap_9_1", x.Log);
        Assert.DoesNotContain(x.Log, l => l.StartsWith("delete sbntest..tbl_test_writer_journal"));
        Assert.Null(j.RemoveVariant(seq));
        Assert.Equal("delete sbntest..tbl_test_writer_journal where journal_id = 9", x.Log[^1]);
    }

    [Fact]
    public void Sweep_failed_variant_drop_warns_and_keeps_the_journal_row()
    {
        var x = SweepExec(items: new[] { VItem });
        x.FailOn = "drop proc";
        var lines = new List<string>();
        WriterSweep.Sweep(x, x, "sbntest", lines.Add, ScratchProc.SweepDropper(x, "sbntest"));
        var line = Assert.Single(lines);
        Assert.StartsWith($"sql-test: WARNING: {Row7} could not be restored: scratch proc sbntest..test_w_v1 not dropped", line);
        Assert.DoesNotContain(x.Log, l => l.StartsWith("delete sbntest..tbl_test_writer_"));
    }

    // Bench session: snapshots exist, so the keep-restore reaches the product statements.
    private static (FakeExec X, WriterBench B) NewBench()
    {
        var x = new FakeExec
        {
            OnScalar = sql => sql.StartsWith("insert sbntest..tbl_test_writer_journal") ? 9m
                            : sql.StartsWith("select object_id") ? 1 : null,
            OnRows = sql => sql.Contains("syscolumns") ? new() { new object?[] { "a", "int", 0 } } : new(),
        };
        var b = new WriterBench(WriterSession.Begin(x, "sbntest", "bench_w", new[] { Spec, Spec2 }, null, null));
        x.Log.Clear();
        return (x, b);
    }

    // Mirrors RunOne's writer path: set the pair, run the batch, keep-restore.
    private static Func<int, TestResult> BenchRun(WriterBench b, Func<int, Outcome>? outcome = null, Action<int>? before = null) => i =>
    {
        before?.Invoke(i);
        b.SetTestPair(30 + i, 300 + i);
        var err = b.RestoreKeep();
        return Runner.WithRestore(new TestResult("bench_w", outcome?.Invoke(i) ?? Outcome.PASS, "", 0, ""), err);
    };

    private static readonly string[] FinaliseSql =
    {
        "drop table sbntest..tbl_test_snap_9_1",
        "delete sbntest..tbl_test_writer_item where journal_id = 9 and seq = 1",
        "drop table sbntest..tbl_test_snap_9_2",
        "delete sbntest..tbl_test_writer_item where journal_id = 9 and seq = 2",
        "delete sbntest..tbl_test_writer_journal where journal_id = 9",
    };

    [Fact]
    public void Bench_warmup_and_count_2_keep_restores_three_times_then_finalises_once()
    {
        var (x, b) = NewBench();
        var ran = WriterBench.Drive(b, 2, BenchRun(b));
        Assert.Equal(3, ran.Count);
        Assert.All(ran, r => Assert.Equal(Outcome.PASS, r.Outcome));
        Assert.Equal(3, x.Log.Count(l => l == "delete sbnmaster..fe_bell where s#inc = 1"));
        Assert.Equal(3, x.Log.Count(l => l == "delete sbntest..t where a = 1"));
        var tail = x.Log.Skip(x.Log.FindLastIndex(l => l == "commit tran") + 1).ToList();
        Assert.Equal(FinaliseSql, tail);
        Assert.Equal(FinaliseSql.Length, x.Log.Count(l => l.StartsWith("drop") || l.StartsWith("delete sbntest..tbl_test_writer")));
    }

    [Fact]
    public void Keep_restore_has_no_item_delete_and_no_drop()
    {
        var (x, b) = NewBench();
        Assert.Null(b.Session.RestoreKeep());
        Assert.Contains("delete sbnmaster..fe_bell where s#inc = 1", x.Log);
        Assert.Contains("insert sbnmaster..fe_bell (a) select a from sbntest..tbl_test_snap_9_1", x.Log);
        Assert.DoesNotContain(x.Log, l => l.StartsWith("drop") || l.StartsWith("delete sbntest..tbl_test_writer"));
    }

    [Fact]
    public void Bench_early_break_still_finalises_once()
    {
        var (x, b) = NewBench();
        var ran = WriterBench.Drive(b, 2, BenchRun(b, i => i == 1 ? Outcome.FAIL : Outcome.PASS));
        Assert.Equal(2, ran.Count);
        Assert.Empty(b.Finalise());
        Assert.Equal(FinaliseSql, x.Log.Skip(x.Log.FindLastIndex(l => l == "commit tran") + 1));
    }

    [Fact]
    public void Bench_exception_still_finalises_once()
    {
        var (x, b) = NewBench();
        Assert.Throws<InvalidOperationException>(() =>
            WriterBench.Drive(b, 2, BenchRun(b, before: i => { if (i == 2) throw new InvalidOperationException("run"); })));
        Assert.Equal(FinaliseSql, x.Log.Skip(x.Log.FindLastIndex(l => l == "commit tran") + 1));
    }

    [Fact]
    public void Bench_failed_keep_restore_skips_finalise_and_keeps_the_journal_row()
    {
        var (x, b) = NewBench();
        var warnings = new List<string>();
        var ran = WriterBench.Drive(b, 2, BenchRun(b, before: i => { if (i == 1) x.FailOn = "insert sbntest..t"; }), warnings.Add);
        Assert.Equal(2, ran.Count);
        Assert.Equal(Outcome.ERROR, ran[^1].Outcome);
        Assert.Equal("restore failed: boom; journal row 9 kept", ran[^1].Message);
        Assert.Empty(warnings);
        Assert.DoesNotContain(x.Log, l => l.StartsWith("drop") || l.StartsWith("delete sbntest..tbl_test_writer"));
    }

    [Fact]
    public void Bench_updates_the_test_pair_before_each_run()
    {
        var (x, b) = NewBench();
        WriterBench.Drive(b, 2, BenchRun(b));
        var pairs = x.Log.Where(l => l.StartsWith("update sbntest..tbl_test_writer_journal")).ToList();
        Assert.Equal(new[]
        {
            "update sbntest..tbl_test_writer_journal set test_spid = 30, test_kpid = 300 where journal_id = 9",
            "update sbntest..tbl_test_writer_journal set test_spid = 31, test_kpid = 301 where journal_id = 9",
            "update sbntest..tbl_test_writer_journal set test_spid = 32, test_kpid = 302 where journal_id = 9",
        }, pairs);
        // Each pair lands before that run's restore; the control pair is never touched.
        var restores = x.Log.Select((l, i) => (l, i)).Where(t => t.l == "delete sbnmaster..fe_bell where s#inc = 1").Select(t => t.i).ToList();
        for (int k = 0; k < 3; k++) Assert.True(x.Log.IndexOf(pairs[k]) < restores[k]);
        Assert.DoesNotContain(x.Log, l => l.Contains("set spid") || l.Contains(" kpid ="));
    }

    [Fact]
    public void Begin_without_a_test_pair_journals_nulls()
    {
        var x = BeginExec();
        WriterSession.Begin(x, "sbntest", "bench_w", new[] { Spec }, null, null);
        Assert.Contains(x.Log, l => l.StartsWith("insert sbntest..tbl_test_writer_journal") && l.Contains("select @@spid, p.kpid, null, null,"));
    }

    [Theory]
    [InlineData("fe_bell")]
    [InlineData("FE_BELL")]
    [InlineData("sbnmaster..Fe_Bell")]
    [InlineData("T")]
    [InlineData("#tmp")]
    [InlineData("tbl_test_x")]
    [InlineData("TBL_TEST_capture")]
    public void TouchedNotRestored_excludes_restored_temp_and_runner_tables(string table) =>
        Assert.Empty(WriterJournal.TouchedNotRestored(new[] { table }, new[] { Spec, Spec2 }));

    [Fact]
    public void TouchedNotRestored_lists_other_tables_once_in_order()
    {
        var got = WriterJournal.TouchedNotRestored(new[] { "ma_alarmqueue3", "fe_bell", "fe_sms", "MA_ALARMQUEUE3" }, new[] { Spec, Spec2 });
        Assert.Equal(new[] { "ma_alarmqueue3", "fe_sms" }, got);
        Assert.Equal("touched, not restored (reads only?): fe_sms", WriterJournal.TouchedLine("fe_sms"));
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, "@restore table not found", false)]
    [InlineData(false, null, false)]
    public void WriterBench_opens_a_session_only_for_an_unrefused_writer(bool writer, string? error, bool expected)
    {
        var restores = writer ? new[] { Spec } : null;
        var tc = new TestCase("bench_x", null, "bench_x", null, Error: error, Restores: restores);
        Assert.Equal(expected, WriterBench.OpensSession(tc));
    }
}
