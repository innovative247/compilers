using SqlTest;

namespace SqlTest.Tests;

public class ScratchProcTests
{
    private const string Source = "use sbntest\ngo\ncreate proc x as select 1\ngo\ncreate proc other as select 2\ngo\n";
    private const string JournalDelete = "delete sbntest..tbl_test_writer_journal where journal_id = 9";

    private sealed class FakeCompiler : IScratchCompiler
    {
        public HashSet<string> Known = new();
        public Func<ScratchCompile, string?> OnCompile = _ => null;
        public List<ScratchCompile> Compiled = new();
        public bool HasOption(string db, string option) => Known.Contains(option);
        public string? Compile(ScratchCompile c) { Compiled.Add(c); return OnCompile(c); }
        public string? DeployedText(string db, string proc) => null;
    }

    // Journal insert gets identity 9; a name exists in sysobjects only when Exists; a live holder answers the held query.
    private sealed class Env
    {
        public FakeExec X = new();
        public FakeCompiler C = new();
        public bool Exists, Journalled, Alive;
        public string User = "dbo";
        public List<object?[]> Held = new();

        public Env()
        {
            X.OnScalar = sql =>
                sql.StartsWith("insert sbntest..tbl_test_writer_journal") ? 9m
                : sql == "select user_name()" ? User
                : sql.Contains("..sysobjects where name") ? (Exists ? 1 : 0)
                : sql.Contains("kind = 'V'") ? (Journalled ? 1 : 0)
                : sql.StartsWith("select count(*) from master..sysprocesses") ? (Alive ? 1 : 0)
                : 1;
            X.OnRows = sql => sql.Contains("i.kind = 'V'") ? Held : new();
        }

        public ScratchSession Open()
        {
            var j = WriterSession.Begin(X, "sbntest", "t", Array.Empty<RestoreSpec>(), 1, 2);
            X.Log.Clear();
            return new ScratchSession(X, "sbntest", j, C);
        }
    }

    private static ScratchSpec Spec(string text = Source, string name = "x__t", string db = "sbntest",
                                    IReadOnlyDictionary<string, bool>? opts = null,
                                    IReadOnlyList<(string from, string to)>? renames = null) =>
        new("x", db, text, name, opts ?? new Dictionary<string, bool>(), renames ?? Array.Empty<(string, string)>());

    private static string Refusal(Action a) => Assert.Throws<ScratchRefusedException>(a).Message;

    private static object?[] HeldRow() =>
        new object?[] { 5m, 30, 300, "ann", "box", new DateTime(2026, 9, 30, 12, 0, 0), "other_t", null, null };

    // ---- refusals (design §3.8) ----

    [Fact]
    public void Refuses_a_name_over_255_characters_before_any_db_write()
    {
        var e = new Env();
        using var s = e.Open();
        var name = new string('n', 256);
        Assert.Equal($"scratch name {name} is 256 characters; the limit is 255", Refusal(() => ScratchProc.Compile(Spec(name: name), s)));
        Assert.Empty(e.X.Log);
        Assert.Empty(e.C.Compiled);
    }

    [Fact]
    public void Accepts_a_name_of_exactly_255_characters()
    {
        var e = new Env();
        using var s = e.Open();
        ScratchProc.Compile(Spec(name: new string('n', 255)), s);
        Assert.Single(e.C.Compiled);
    }

    [Fact]
    public void Refuses_an_unknown_compile_option_before_any_db_write()
    {
        var e = new Env();
        using var s = e.Open();
        var opts = new Dictionary<string, bool> { ["zz"] = true };
        Assert.Equal("unknown compile option zz: no c: line in the merged options", Refusal(() => ScratchProc.Compile(Spec(opts: opts), s)));
        Assert.Empty(e.X.Log);
    }

    [Fact]
    public void Passes_a_known_compile_option_to_the_compiler()
    {
        var e = new Env();
        e.C.Known.Add("fe001");
        using var s = e.Open();
        var opts = new Dictionary<string, bool> { ["fe001"] = false };
        ScratchProc.Compile(Spec(opts: opts), s);
        Assert.False(e.C.Compiled.Single().Options["fe001"]);
    }

    [Fact]
    public void Refuses_a_dollar_i_include_before_any_db_write()
    {
        var e = new Env();
        using var s = e.Open();
        var text = "use sbntest\ngo\ncreate proc x as\n$i inc/common.sql\ngo\n";
        Assert.Equal("variant contains $i include; not supported", Refusal(() => ScratchProc.Compile(Spec(text), s)));
        Assert.Empty(e.X.Log);
    }

    [Fact]
    public void A_dollar_ir_line_is_not_an_include()
    {
        var e = new Env();
        using var s = e.Open();
        ScratchProc.Compile(Spec("use sbntest\ngo\ncreate proc x as\n$ir foo\ngo\n"), s);
        Assert.Single(e.C.Compiled);
    }

    [Fact]
    public void Refuses_a_held_name_from_a_live_journal_row_without_inserting_an_item()
    {
        var e = new Env { Alive = true };
        e.Held.Add(HeldRow());
        using var s = e.Open();
        Assert.Equal("scratch proc sbntest..x__t is held by journal row 5 (ann@box, spid 30)", Refusal(() => ScratchProc.Compile(Spec(), s)));
        Assert.DoesNotContain(e.X.Log, l => l.StartsWith("insert"));
        Assert.Empty(e.C.Compiled);
    }

    [Fact]
    public void A_dead_holders_item_does_not_hold_the_name()
    {
        var e = new Env { Alive = false, Exists = true, Journalled = true };
        e.Held.Add(HeldRow());
        using var s = e.Open();
        ScratchProc.Compile(Spec(), s);
        Assert.Single(e.C.Compiled);
    }

    [Fact]
    public void Refuses_a_holder_that_appears_after_our_insert_and_removes_our_item()
    {
        var e = new Env { Alive = true };
        var first = true;
        e.X.OnRows = sql =>
        {
            if (!sql.Contains("i.kind = 'V'")) return new();
            if (first) { first = false; return new(); }
            return new() { HeldRow() };
        };
        using var s = e.Open();
        Assert.StartsWith("scratch proc sbntest..x__t is held by journal row 5", Refusal(() => ScratchProc.Compile(Spec(), s)));
        Assert.Contains(e.X.Log, l => l.StartsWith("insert sbntest..tbl_test_writer_item") && l.Contains("'V'"));
        Assert.Contains(e.X.Log, l => l.StartsWith("delete sbntest..tbl_test_writer_item"));
        Assert.Empty(e.C.Compiled);
    }

    [Fact]
    public void Refuses_a_name_that_exists_and_is_not_a_journal_leftover()
    {
        var e = new Env { Exists = true, Journalled = false };
        using var s = e.Open();
        Assert.Equal("scratch name sbntest..x__t already exists and is not a writer journal leftover", Refusal(() => ScratchProc.Compile(Spec(), s)));
        Assert.DoesNotContain(e.X.Log, l => l.StartsWith("insert"));
    }

    [Fact]
    public void Collision_check_looks_across_all_owners_and_excludes_the_own_row()
    {
        var e = new Env { Exists = true, Journalled = true };
        using var s = e.Open();
        ScratchProc.Compile(Spec(), s);
        Assert.Contains(e.X.Log, l => l == "select count(*) from sbntest..sysobjects where name = 'x__t'");
        var v = e.X.Log.First(l => l.Contains("kind = 'V'") && l.StartsWith("select count(*)"));
        Assert.Contains("journal_id <> 9", v);
        Assert.DoesNotContain(e.X.Log, l => l.Contains("object_id('x__t')"));
    }

    [Fact]
    public void Compile_failure_refuses_with_the_output_and_drops_the_whole_chain()
    {
        var e = new Env();
        e.C.OnCompile = c => c.ScratchName == "b__t" ? " Msg 102, syntax error \n" : null;
        using var s = e.Open();
        var a = ScratchProc.Compile(Spec(name: "a__t"), s);
        Assert.Equal("compile of sbntest..b__t failed: Msg 102, syntax error", Refusal(() => ScratchProc.Compile(Spec(name: "b__t"), s)));
        Assert.True(a.Dropped);
        Assert.Empty(s.Handles);
        Assert.Contains("if object_id('a__t') is not null drop proc a__t", e.X.Log);
        Assert.Contains("if object_id('b__t') is not null drop proc b__t", e.X.Log);
        Assert.Equal(JournalDelete, e.X.Log[^1]);
    }

    [Fact]
    public void A_throwing_compiler_is_a_compile_failure()
    {
        var e = new Env();
        e.C.OnCompile = _ => throw new InvalidOperationException("no server");
        using var s = e.Open();
        Assert.Equal("compile of sbntest..x__t failed: no server", Refusal(() => ScratchProc.Compile(Spec(), s)));
        Assert.Empty(s.Handles);
    }

    [Fact]
    public void Compile_failure_with_a_failed_drop_reports_both_and_keeps_the_rows()
    {
        var e = new Env();
        e.C.OnCompile = _ => "bad";
        e.X.FailOn = "drop proc";
        using var s = e.Open();
        var msg = Refusal(() => ScratchProc.Compile(Spec(), s));
        Assert.StartsWith("compile of sbntest..x__t failed: bad; scratch proc sbntest..x__t not dropped", msg);
        Assert.DoesNotContain(e.X.Log, l => l.StartsWith("delete sbntest..tbl_test_writer_"));
    }

    // ---- compile, run, drop in each outcome ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compile_run_and_drop_on_normal_exit_and_on_exception(bool throws)
    {
        var e = new Env();
        ScratchHandle? h = null;
        var s = e.Open();
        using (s)
        {
            try
            {
                using var handle = h = ScratchProc.Compile(Spec(), s);
                if (throws) throw new InvalidOperationException("test batch blew up");
            }
            catch (InvalidOperationException) when (throws) { }
        }
        Assert.True(h!.Dropped);
        Assert.Empty(s.Handles);
        var log = e.X.Log;
        int drop = log.FindIndex(l => l == "if object_id('x__t') is not null drop proc x__t");
        int item = log.FindIndex(l => l.StartsWith("delete sbntest..tbl_test_writer_item"));
        Assert.True(drop >= 0 && drop < item);
        Assert.Equal(JournalDelete, log[^1]);
        Assert.Single(e.C.Compiled);
    }

    [Fact]
    public void Compiler_receives_the_selected_renamed_text()
    {
        var e = new Env();
        using var s = e.Open();
        ScratchProc.Compile(Spec(), s);
        Assert.Equal("use sbntest\ngo\ncreate proc x__t as select 1\ngo\n", e.C.Compiled.Single().Text);
    }

    [Fact]
    public void Item_is_journalled_before_the_compile()
    {
        var e = new Env();
        e.C.OnCompile = _ => { Assert.Contains(e.X.Log, l => l.StartsWith("insert sbntest..tbl_test_writer_item") && l.Contains("'V'")); return null; };
        using var s = e.Open();
        ScratchProc.Compile(Spec(), s);
    }

    [Fact]
    public void Failed_drop_keeps_the_item_and_the_journal_row()
    {
        var e = new Env();
        using var s = e.Open();
        var h = ScratchProc.Compile(Spec(db: "sbnmaster"), s);
        e.X.FailOn = "drop proc";
        Assert.StartsWith("scratch proc sbnmaster..x__t not dropped: boom", h.Drop());
        Assert.False(h.Dropped);
        Assert.DoesNotContain(e.X.Log, l => l.StartsWith("delete sbntest..tbl_test_writer_"));
        // The home database is restored even though the drop failed.
        Assert.Equal("use sbntest", e.X.Log[^1]);
        Assert.Contains("use sbnmaster", e.X.Log);
    }

    [Fact]
    public void Failed_item_delete_keeps_the_journal_row_and_reports()
    {
        var e = new Env();
        using var s = e.Open();
        var h = ScratchProc.Compile(Spec(), s);
        e.X.FailOn = "delete sbntest..tbl_test_writer_item";
        Assert.StartsWith("scratch proc sbntest..x__t journal item not deleted", h.Drop());
        Assert.DoesNotContain(e.X.Log, l => l.StartsWith("delete sbntest..tbl_test_writer_journal"));
    }

    [Fact]
    public void DropAll_drops_callers_before_callees()
    {
        var e = new Env();
        using var s = e.Open();
        ScratchProc.Compile(Spec(name: "callee__t"), s);
        ScratchProc.Compile(Spec(name: "caller__t"), s);
        e.X.Log.Clear();
        Assert.Null(s.DropAll());
        var drops = e.X.Log.Where(l => l.Contains("drop proc")).ToList();
        Assert.EndsWith("drop proc caller__t", drops[0]);
        Assert.EndsWith("drop proc callee__t", drops[1]);
        Assert.Empty(s.Handles);
        Assert.Single(e.X.Log, l => l.StartsWith("delete sbntest..tbl_test_writer_journal"));
    }

    [Fact]
    public void Dropping_twice_is_a_no_op()
    {
        var e = new Env();
        using var s = e.Open();
        var h = ScratchProc.Compile(Spec(), s);
        Assert.Null(h.Drop());
        e.X.Log.Clear();
        Assert.Null(h.Drop());
        Assert.Empty(e.X.Log);
    }

    [Fact]
    public void Refuses_a_compile_when_the_user_is_not_dbo_in_the_db()
    {
        var e = new Env { User = "jens" };
        using var s = e.Open();
        Assert.Equal("scratch compile needs dbo in sbntest (run as GONZO_TEST)", Refusal(() => ScratchProc.Compile(Spec(), s)));
        Assert.DoesNotContain(e.X.Log, l => l.StartsWith("insert"));
        Assert.Equal("use sbntest", e.X.Log[^1]);
        Assert.Empty(e.C.Compiled);
    }

    [Theory]
    [InlineData("x; drop table t", "sbntest")]
    [InlineData("x__t", "sbn test")]
    public void Refuses_an_unsafe_identifier_before_any_db_write(string name, string db)
    {
        var e = new Env();
        using var s = e.Open();
        Assert.StartsWith("invalid scratch identifier: ", Refusal(() => ScratchProc.Compile(Spec(name: name, db: db), s)));
        Assert.Empty(e.X.Log);
    }

    [Fact]
    public void A_variant_cannot_be_added_once_the_journal_row_is_deleted()
    {
        var e = new Env();
        using var s = e.Open();
        ScratchProc.Compile(Spec(name: "a__t"), s).Dispose();
        Assert.Equal(JournalDelete, e.X.Log[^1]);
        e.X.Log.Clear();
        Assert.Throws<InvalidOperationException>(() => ScratchProc.Compile(Spec(name: "b__t"), s));
        Assert.DoesNotContain(e.X.Log, l => l.StartsWith("insert"));
        Assert.DoesNotContain(e.C.Compiled, c => c.ScratchName == "b__t");
    }

    // ---- chains (Variants.CompileAll) ----

    private const string Chain = "use sbntest\ngo\ncreate proc a as exec b\ngo\ncreate proc b as select 1\ngo\n";
    private static readonly (string, string)[] ChainRenames = { ("a", "a__t"), ("b", "b__t") };

    private static ScratchSpec Member(string proc, IReadOnlyDictionary<string, bool>? opts = null) =>
        new(proc, "sbntest", Chain, proc + "__t", opts ?? new Dictionary<string, bool>(), ChainRenames);

    [Fact]
    public void Precheck_refuses_a_whole_chain_before_any_compile()
    {
        var e = new Env();
        e.C.Known.Add("fe001");
        using var s = e.Open();
        var specs = new[] { Member("b", new Dictionary<string, bool> { ["fe001"] = false }),
                            Member("a", new Dictionary<string, bool> { ["zz999"] = true }) };
        Assert.Equal("unknown compile option zz999: no c: line in the merged options",
                     Refusal(() => Variants.CompileAll(specs, s)));
        Assert.Empty(e.X.Log);
        Assert.Empty(e.C.Compiled);
    }

    [Fact]
    public void CompileAll_compiles_callee_first_with_every_member_renamed()
    {
        var e = new Env();
        using var s = e.Open();
        var handles = Variants.CompileAll(new[] { Member("b"), Member("a") }, s);
        Assert.Equal(new[] { "b__t", "a__t" }, handles.Select(h => h.Name));
        Assert.Equal("use sbntest\ngo\ncreate proc a__t as exec b__t\ngo\n", e.C.Compiled[1].Text);
    }

    [Fact]
    public void CompileAll_on_a_same_file_chain_compiles_each_name_once()
    {
        var e = new Env();
        using var s = e.Open();
        Variants.CompileAll(new[] { Member("b"), Member("a") }, s);
        Assert.Equal(new[] { "b__t", "a__t" }, e.C.Compiled.Select(c => c.ScratchName));
        Assert.Equal("use sbntest\ngo\ncreate proc b__t as select 1\ngo\n", e.C.Compiled[0].Text);
        Assert.Equal("use sbntest\ngo\ncreate proc a__t as exec b__t\ngo\n", e.C.Compiled[1].Text);
    }

    [Fact]
    public void A_collision_on_a_later_member_drops_the_earlier_members()
    {
        var e = new Env();
        var scalar = e.X.OnScalar;
        e.X.OnScalar = sql => sql.Contains("sysobjects where name = 'a__t'") ? 1 : scalar(sql);
        using var s = e.Open();
        Assert.Equal("scratch name sbntest..a__t already exists and is not a writer journal leftover",
                     Refusal(() => Variants.CompileAll(new[] { Member("b"), Member("a") }, s)));
        Assert.Contains("if object_id('b__t') is not null drop proc b__t", e.X.Log);
        Assert.Empty(s.Handles);
        Assert.Single(e.C.Compiled);
    }

    [Fact]
    public void A_compile_failure_on_a_later_member_drops_the_earlier_members()
    {
        var e = new Env();
        e.C.OnCompile = c => c.ScratchName == "a__t" ? "Msg 102" : null;
        using var s = e.Open();
        Assert.Equal("compile of sbntest..a__t failed: Msg 102",
                     Refusal(() => Variants.CompileAll(new[] { Member("b"), Member("a") }, s)));
        Assert.Contains("if object_id('b__t') is not null drop proc b__t", e.X.Log);
        Assert.Empty(s.Handles);
    }

    [Fact]
    public void A_batch_naming_two_members_refuses_the_chain_before_any_compile()
    {
        var e = new Env();
        using var s = e.Open();
        var text = Chain + "grant execute on a to public\ngrant execute on b to public\ngo\n";
        var specs = new[] { Member("b") with { SourceText = text }, Member("a") with { SourceText = text } };
        Assert.Equal("a batch naming b also names a, which the same file creates; not supported",
                     Refusal(() => Variants.CompileAll(specs, s)));
        Assert.Empty(e.C.Compiled);
        Assert.Empty(s.Handles);
    }

    [Fact]
    public void Runsql_framing_lines_are_stripped_from_the_error_text()
    {
        var raw = "Running: /tmp/sql-test-variant-1-x__t.sql on srv.sbntest\nMsg 102, syntax error\nElapsed: 00:00:01\n";
        Assert.Equal("Msg 102, syntax error", RunsqlScratchCompiler.StripFraming(raw));
    }

    // ---- shared journal row ----

    [Fact]
    public void Scratch_only_session_creates_and_then_deletes_its_own_journal_row()
    {
        var e = new Env();
        var j = WriterSession.Begin(e.X, "sbntest", "t", Array.Empty<RestoreSpec>(), 1, 2);
        Assert.Contains(e.X.Log, l => l.StartsWith("insert sbntest..tbl_test_writer_journal"));
        Assert.DoesNotContain(e.X.Log, l => l.StartsWith("delete"));
        using var s = new ScratchSession(e.X, "sbntest", j, e.C);
        ScratchProc.Compile(Spec(), s);
        Assert.Null(s.DropAll());
        Assert.Equal(JournalDelete, e.X.Log[^1]);
    }

    [Fact]
    public void Mixed_session_restore_keeps_the_row_and_the_later_drop_deletes_it()
    {
        var e = new Env();
        e.X.OnScalar = sql => sql.StartsWith("insert sbntest..tbl_test_writer_journal") ? 9m
            : sql.StartsWith("select object_id") ? 1
            : sql == "select user_name()" ? "dbo"
            : sql.Contains("..sysobjects where name") ? 0 : null;
        e.X.OnRows = sql => sql.Contains("syscolumns") ? new() { new object?[] { "a", "int", 0 } } : new();
        var spec = new RestoreSpec("sbnmaster", "fe_bell", "s#inc = 1", false);
        var j = WriterSession.Begin(e.X, "sbntest", "t", new[] { spec }, 1, 2);
        using var s = new ScratchSession(e.X, "sbntest", j, e.C);
        ScratchProc.Compile(Spec(), s);

        Assert.Null(j.Restore());
        Assert.DoesNotContain(e.X.Log, l => l.StartsWith("delete sbntest..tbl_test_writer_journal"));
        Assert.Null(s.DropAll());
        Assert.Equal(JournalDelete, e.X.Log[^1]);
    }

    [Fact]
    public void Mixed_session_drop_first_keeps_the_row_until_the_restore_finishes()
    {
        var e = new Env();
        e.X.OnScalar = sql => sql.StartsWith("insert sbntest..tbl_test_writer_journal") ? 9m
            : sql.StartsWith("select object_id") ? 1
            : sql == "select user_name()" ? "dbo"
            : sql.Contains("..sysobjects where name") ? 0 : null;
        e.X.OnRows = sql => sql.Contains("syscolumns") ? new() { new object?[] { "a", "int", 0 } } : new();
        var spec = new RestoreSpec("sbnmaster", "fe_bell", "s#inc = 1", false);
        var j = WriterSession.Begin(e.X, "sbntest", "t", new[] { spec }, 1, 2);
        using var s = new ScratchSession(e.X, "sbntest", j, e.C);
        ScratchProc.Compile(Spec(), s);

        Assert.Null(s.DropAll());
        Assert.DoesNotContain(e.X.Log, l => l.StartsWith("delete sbntest..tbl_test_writer_journal"));
        Assert.Null(j.Restore());
        Assert.Equal(JournalDelete, e.X.Log[^1]);
    }

    // ---- Runner.VariantLifecycle ----

    private static readonly TestResult Passed = new("t", Outcome.PASS, "", 1, "");
    private const string DropB = "if object_id('b__t') is not null drop proc b__t";

    [Fact]
    public void Lifecycle_drops_the_chain_after_a_pass()
    {
        var e = new Env();
        using var s = e.Open();
        var r = Runner.VariantLifecycle("t", s, new[] { Member("b"), Member("a") }, null, new System.Diagnostics.Stopwatch(),
            drop =>
            {
                Assert.Equal(2, s.Handles.Count);
                drop();
                Assert.Empty(s.Handles);
                return Passed;
            });
        Assert.Same(Passed, r);
        Assert.Contains(DropB, e.X.Log);
        Assert.Equal(JournalDelete, e.X.Log[^1]);
    }

    [Fact]
    public void Lifecycle_drops_the_chain_when_the_run_throws()
    {
        var e = new Env();
        using var s = e.Open();
        Assert.Throws<InvalidOperationException>(() =>
            Runner.VariantLifecycle("t", s, new[] { Member("b"), Member("a") }, null, new System.Diagnostics.Stopwatch(),
                _ => throw new InvalidOperationException("boom")));
        Assert.Empty(s.Handles);
        Assert.Contains(DropB, e.X.Log);
        Assert.Equal(JournalDelete, e.X.Log[^1]);
    }

    [Fact]
    public void Lifecycle_folds_a_drop_error_into_the_result()
    {
        var e = new Env();
        using var s = e.Open();
        var r = Runner.VariantLifecycle("t", s, new[] { Member("b") }, null, new System.Diagnostics.Stopwatch(), drop =>
        {
            e.X.FailOn = "drop proc b__t";
            drop();
            return Passed;
        });
        Assert.Equal(Outcome.ERROR, r.Outcome);
        Assert.StartsWith("scratch proc sbntest..b__t not dropped:", r.Message);
        Assert.Single(s.Handles);
        Assert.DoesNotContain(e.X.Log, l => l.StartsWith("delete sbntest..tbl_test_writer_"));
    }

    [Fact]
    public void Lifecycle_reports_no_drop_error_when_the_retry_after_release_succeeds()
    {
        var e = new Env();
        using var s = e.Open();
        var r = Runner.VariantLifecycle("t", s, new[] { Member("b") }, null, new System.Diagnostics.Stopwatch(), drop =>
        {
            e.X.FailOn = "drop proc b__t";
            drop();
            Assert.Single(s.Handles);
            return Passed;
        }, release: () => e.X.FailOn = null);
        Assert.Same(Passed, r);
        Assert.Empty(s.Handles);
        Assert.Equal(JournalDelete, e.X.Log[^1]);
    }

    [Fact]
    public void Lifecycle_refusal_compiles_nothing_and_closes_a_scratch_only_row()
    {
        var e = new Env();
        using var s = e.Open();
        var ran = false;
        var r = Runner.VariantLifecycle("t", s, new[] { Member("b", new Dictionary<string, bool> { ["fe001"] = false }) }, null,
            new System.Diagnostics.Stopwatch(), _ => { ran = true; return Passed; });
        Assert.False(ran);
        Assert.Equal(Outcome.ERROR, r.Outcome);
        Assert.Contains("fe001", r.Message);
        Assert.Empty(e.C.Compiled);
        Assert.Equal(new[] { JournalDelete }, e.X.Log);
    }

    // ---- helpers ----

    [Fact]
    public void Rename_is_whole_word_and_leaves_scratch_names_alone()
    {
        var text = "create proc x as exec a_x exec x_01 exec &dbpro&..x exec x";
        var r = ScratchProc.Rename(text, new[] { ("x", "x__t") });
        Assert.Equal("create proc x__t as exec a_x exec x_01 exec &dbpro&..x__t exec x__t", r);
    }

    [Fact]
    public void Render_keeps_use_batches_and_batches_naming_the_proc()
    {
        var text = "use sbntest\ngo\ncreate proc x as select 1\ngo\ncreate proc a_x as select 2\ngo\n";
        Assert.Equal("use sbntest\ngo\ncreate proc x__t as select 1\ngo\n", ScratchProc.Render(Spec(text)));
    }

    [Fact]
    public void Render_applies_BatchEdit_before_the_rename()
    {
        var spec = Spec() with { BatchEdit = b => b.Append("-- marker x").ToList() };
        Assert.EndsWith("-- marker x__t\ngo\n", ScratchProc.Render(spec));
    }

    [Fact]
    public void SplitBatchesWithLines_reports_the_first_kept_line_of_each_batch()
    {
        // 1 use / 2 go / 3 "" / 4 "" / 5 create / 6 select / 7 go / 8 "  " / 9 grant / 10 go / 11 ""
        var text = "use sbntest\r\ngo\r\n\r\n\r\ncreate proc x as\r\nselect 1\r\ngo\r\n  \r\ngrant execute on x to public\r\ngo\r\n";
        var b = ScratchProc.SplitBatchesWithLines(text);
        Assert.Equal(new[] { ("use sbntest", 1), ("create proc x as\nselect 1", 5), ("  \ngrant execute on x to public", 8) }, b);
    }

    [Fact]
    public void SplitBatchesWithLines_counts_lines_after_a_blank_batch_and_without_a_trailing_go()
    {
        var b = ScratchProc.SplitBatchesWithLines("go\n\ngo\n\nselect 1\n\n");
        Assert.Equal(new[] { ("select 1", 5) }, b);
    }

    [Fact]
    public void SplitBatches_output_is_unchanged()
    {
        Assert.Equal(new[] { "use sbntest", "create proc x as select 1", "create proc other as select 2" }, ScratchProc.SplitBatches(Source));
        Assert.Equal(new[] { "  \nselect 1", "select 2\n " }, ScratchProc.SplitBatches("\n\n  \nselect 1\n\nGO\r\n\n \ngo\nselect 2\n \n\n"));
    }

    [Fact]
    public void CreatesProc_and_CreateLineOffset_find_the_create_line()
    {
        var batch = "-- header\n\n  CREATE PROCEDURE X @a int\nas select 1";
        Assert.True(ScratchProc.CreatesProc(batch, "x"));
        Assert.False(ScratchProc.CreatesProc(batch, "y"));
        Assert.False(ScratchProc.CreatesProc("exec x", "x"));
        Assert.Equal(2, ScratchProc.CreateLineOffset(batch, "x"));
        Assert.Equal(0, ScratchProc.CreateLineOffset("create proc x as select 1", "x"));
        Assert.Null(ScratchProc.CreateLineOffset(batch, "y"));
    }
}
