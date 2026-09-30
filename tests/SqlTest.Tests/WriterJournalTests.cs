using SqlTest;

namespace SqlTest.Tests;

public class WriterJournalTests
{
    private static readonly RestoreSpec Spec = new("sbnmaster", "fe_bell", "s#inc = 1", false);
    private static readonly RestoreProbe Ok = new(true, RowCount: 3, Triggers: Array.Empty<string>(), UserName: "dbo");

    // Records every statement; throws on the first statement containing FailOn.
    private sealed class FakeExec : ISqlExec
    {
        public readonly List<string> Log = new();
        public string? FailOn;
        public Func<string, object?> OnScalar = _ => 1;
        public Func<string, List<object?[]>> OnRows = _ => new();

        private void Hit(string sql)
        {
            Log.Add(sql);
            if (FailOn != null && sql.Contains(FailOn)) throw new InvalidOperationException("boom");
        }
        public int Exec(string sql) { Hit(sql); return 1; }
        public object? Scalar(string sql) { Hit(sql); return OnScalar(sql); }
        public List<object?[]> Rows(string sql) { Hit(sql); return OnRows(sql); }
    }

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
        Assert.Equal(2, x.Log.Count);
        Assert.All(x.Log, sql =>
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
        var s = WriterSession.Begin(x, "sbntest", "test_w", new[] { Spec, Spec2 });
        Assert.Equal(9L, s.JournalId);
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

    [Fact]
    public void Begin_failure_part_way_undoes_its_own_snapshots_and_rows()
    {
        var x = BeginExec();
        x.FailOn = "into sbntest..tbl_test_snap_9_2";
        Assert.Throws<InvalidOperationException>(() => WriterSession.Begin(x, "sbntest", "test_w", new[] { Spec, Spec2 }));
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
        var ex = Assert.Throws<WriterHeldException>(() => WriterSession.Begin(x, "sbntest", "test_w", new[] { Spec, Spec2 }));
        Assert.Equal("@restore table sbnmaster..fe_bell is held by journal row 8 (jens@mac, spid 17)", ex.Message);
        Assert.DoesNotContain(x.Log, l => l.Contains(" into sbntest..tbl_test_snap_"));
        Assert.Equal(new[]
        {
            "delete sbntest..tbl_test_writer_item where journal_id = 9",
            "delete sbntest..tbl_test_writer_journal where journal_id = 9",
        }, x.Log.TakeLast(2));
    }
}
