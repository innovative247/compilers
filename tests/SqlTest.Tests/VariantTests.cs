using ibsCompiler;
using ibsCompiler.Configuration;
using SqlTest;

namespace SqlTest.Tests;

public class VariantTests
{
    private sealed class FakeCompiler : IScratchCompiler
    {
        public Dictionary<string, string> Tokens = new();
        public bool HasOption(string db, string option) => true;
        public int Compiles;
        public string? Compile(ScratchCompile c) { Compiles++; return null; }
        public string Expand(string db, string text) => Tokens.Aggregate(text, (t, kv) => t.Replace(kv.Key, kv.Value));
        public Dictionary<string, string> Deployed = new();
        public string? DeployedText(string db, string proc) => Deployed.GetValueOrDefault(proc);
    }

    private static VariantSpec? Parse(string body, out string? error) => Variants.Parse(body, out error);

    private static string ParseError(string body)
    {
        Assert.Null(Parse(body, out var error));
        return Assert.IsType<string>(error);
    }

    private static void Write(string root, string rel, string text)
    {
        var path = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string Refusal(Action a) => Assert.Throws<ScratchRefusedException>(a).Message;

    // ---- Parse (design §3.2) ----

    [Fact]
    public void Parses_options_tag_chain_and_a_source_override()
    {
        var v = Parse("create proc t as\n-- @variant: fe001=- ba215=+ as fe001off chain a_ma_alarmqueuerem > ma_alarmqueuerem\n" +
                      "-- @variant-source: ma_alarmqueuerem = css/ss/ba/pro_x.sql\nselect 1", out var error);
        Assert.Null(error);
        Assert.NotNull(v);
        Assert.Equal("fe001off", v.Tag);
        Assert.Equal(new[] { "a_ma_alarmqueuerem", "ma_alarmqueuerem" }, v.Chain);
        Assert.False(v.Options["fe001"]);
        Assert.True(v.Options["ba215"]);
        Assert.Equal("css/ss/ba/pro_x.sql", v.Sources["ma_alarmqueuerem"]);
        Assert.Equal("variant fe001off: a_ma_alarmqueuerem > ma_alarmqueuerem", v.Describe);
        Assert.Equal("a_ma_alarmqueuerem__fe001off", v.ScratchName("a_ma_alarmqueuerem"));
    }

    [Fact]
    public void A_one_element_chain_is_valid_and_no_directive_is_no_variant()
    {
        Assert.Equal(new[] { "x" }, Parse("-- @variant: fe001=+ as t chain x", out _)!.Chain);
        Assert.Null(Parse("select 1", out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("-- @variant: fe001=- as t chain x\n-- @variant: fe001=+ as u chain x", "second @variant line: -- @variant: fe001=+ as u chain x")]
    [InlineData("-- @variant: fe001 as t chain x", "invalid @variant option: fe001 (expected <opt>=+ or <opt>=-)")]
    [InlineData("-- @variant: as t chain x", "@variant needs at least one <opt>=<+|-> option")]
    [InlineData("-- @variant: fe001=- fe001=+ as t chain x", "duplicate @variant option: fe001")]
    [InlineData("-- @variant: fe001=- as FE chain x", "invalid @variant tag: 'FE' (expected [a-z0-9]{1,16})")]
    [InlineData("-- @variant: fe001=- as abcdefghijklmnopq chain x", "invalid @variant tag: 'abcdefghijklmnopq' (expected [a-z0-9]{1,16})")]
    [InlineData("-- @variant: fe001=- chain x", "invalid @variant option: chain (expected <opt>=+ or <opt>=-)")]
    [InlineData("-- @variant: fe001=- as t", "@variant is missing `chain <proc> [> <proc> ...]`")]
    [InlineData("-- @variant: fe001=- as t x > y", "@variant is missing `chain <proc> [> <proc> ...]`")]
    [InlineData("-- @variant: fe001=- as t chain x > db..y", "invalid @variant chain member: 'db..y'")]
    [InlineData("-- @variant: fe001=- as t chain x\n-- @variant-source: z = css/ss/a/pro_z.sql", "@variant-source for z: not in the chain")]
    [InlineData("-- @variant-source: z = css/ss/a/pro_z.sql", "@variant-source without @variant")]
    public void Refuses_a_malformed_directive(string body, string expected) => Assert.Equal(expected, ParseError(body));

    [Fact]
    public void SameAs_compares_options_chain_and_sources()
    {
        var a = Parse("-- @variant: fe001=- ba215=+ as t chain x > y", out _)!;
        Assert.True(a.SameAs(Parse("-- @variant: ba215=+ fe001=- as t chain x > y", out _)!));
        Assert.False(a.SameAs(Parse("-- @variant: fe001=+ ba215=+ as t chain x > y", out _)!));
        Assert.False(a.SameAs(Parse("-- @variant: fe001=- ba215=+ as t chain x", out _)!));
    }

    // ---- SourceLocator (design §3.3) ----

    [Fact]
    public void Locator_finds_not_found_ambiguous_and_honours_an_override() => TestScratch.Use(root =>
    {
        Write(root, "css/ss/ba/pro_ba_one.sql", "use sbntest\ngo\ncreate procedure uniq as select 1\ngo\ncreate proc twin as select 1\ngo\n");
        Write(root, "css/ss/fe/pro_fe_two.sql", "create proc twin as select 2\ngo\n");
        Write(root, "css/ss/fe/tbl_fe_skip.sql", "create proc hidden as select 3\ngo\n");
        var loc = new SourceLocator(root);

        Assert.Equal("css/ss/ba/pro_ba_one.sql", loc.Locate("UNIQ").RelPath);
        Assert.Equal("variant source for hidden not found", Refusal(() => loc.Locate("hidden")));
        Assert.Equal("ambiguous: css/ss/ba/pro_ba_one.sql, css/ss/fe/pro_fe_two.sql", Refusal(() => loc.Locate("twin")));
        Assert.Equal("create proc twin as select 2\ngo\n", loc.Locate("twin", "css/ss/fe/pro_fe_two.sql").Text);
        Assert.Equal("variant source for twin: css/ss/no.sql not found", Refusal(() => loc.Locate("twin", "css/ss/no.sql")));
        Assert.Equal("variant source css/ss/fe/pro_fe_two.sql does not create uniq",
                     Refusal(() => loc.Locate("uniq", "css/ss/fe/pro_fe_two.sql")));
    });

    // ---- batch selection and rename (design §3.4) ----

    [Fact]
    public void SelectBatches_isolates_one_proc_of_a_multi_proc_file()
    {
        const string file = "use &dbpro&\ngo\nif object_id('p') is not null drop proc p\ngo\ncreate proc p as select 1\ngo\n" +
                            "if object_id('q') is not null drop proc q\ngo\ncreate proc q as exec p\ngo\ngrant execute on q to public\ngo\n" +
                            "grant execute on p to public\ngo\n";
        Assert.Equal(new[] { "use &dbpro&", "if object_id('q') is not null drop proc q", "create proc q as exec p",
                             "grant execute on q to public" },
                     ScratchProc.SelectBatches(file, "q"));
    }

    [Fact]
    public void SelectBatches_leaves_the_callers_create_batch_out_of_the_callees_selection()
    {
        const string file = "use &dbpro&\ngo\nif object_id('p') is not null drop proc p\ngo\ncreate proc p as select 1\ngo\n" +
                            "grant execute on p to public\ngo\nexec sp_procxmode p, 'anymode'\ngo\n" +
                            "if object_id('q') is not null drop proc q\ngo\nCREATE PROCEDURE q as exec p\ngo\n" +
                            "grant execute on q to public\ngo\n";
        Assert.Equal(new[] { "use &dbpro&", "if object_id('p') is not null drop proc p", "create proc p as select 1",
                             "grant execute on p to public", "exec sp_procxmode p, 'anymode'" },
                     ScratchProc.SelectBatches(file, "p"));
        Assert.Equal(new[] { "use &dbpro&", "if object_id('q') is not null drop proc q", "CREATE PROCEDURE q as exec p",
                             "grant execute on q to public" },
                     ScratchProc.SelectBatches(file, "q"));
    }

    [Fact]
    public void SelectBatches_keeps_a_use_batch_by_its_first_statement_only()
    {
        const string file = "-- header\n/* build notes */\nuse &dbpro&\ngo\n" +
                            "create proc p as select 1\ngo\n" +
                            "create proc q as\n/*\nuse p for the lookup\n*/\nexec p\ngo\n" +
                            "/*\nuse with care\n*/\nif object_id('z') is not null drop proc z\ngo\n";
        Assert.Equal(new[] { "-- header\n/* build notes */\nuse &dbpro&", "create proc p as select 1" },
                     ScratchProc.SelectBatches(file, "p"));
    }

    [Fact]
    public void SelectBatches_and_the_locator_recognise_create_or_replace() => TestScratch.Use(root =>
    {
        const string file = "create proc p as select 1\ngo\ncreate or replace procedure q as exec p\ngo\n";
        Assert.Equal(new[] { "create proc p as select 1" }, ScratchProc.SelectBatches(file, "p"));
        Assert.Equal(new[] { "create or replace procedure q as exec p" }, ScratchProc.SelectBatches(file, "q"));
        Write(root, "css/ss/ba/pro_ba_q.sql", file);
        Assert.Equal("css/ss/ba/pro_ba_q.sql", new SourceLocator(root).Locate("q").RelPath);
    });

    [Fact]
    public void SelectBatches_refuses_a_kept_batch_that_names_another_proc_of_the_file()
    {
        const string file = "create proc p as select 1\ngo\ncreate proc q as exec p\ngo\n" +
                            "grant execute on p to public\ngrant execute on q to public\ngo\n";
        Assert.Equal("a batch naming p also names q, which the same file creates; not supported",
                     Refusal(() => ScratchProc.SelectBatches(file, "p")));
    }

    [Fact]
    public void Rename_keeps_a_x_and_x_01_and_renames_through_a_qualifier()
    {
        Assert.Equal("exec a_x; exec x_01; exec &dbpro&..x__t; exec x__t",
                     ScratchProc.Rename("exec a_x; exec x_01; exec &dbpro&..x; exec x", new[] { ("x", "x__t") }));
    }

    // ---- chain and Build ----

    [Fact]
    public void ChainIsLinked_needs_each_member_to_name_the_next()
    {
        var a = ("a", "create proc a as exec b\ngo\n");
        var b = ("b", "create proc b as select 1\ngo\n");
        Assert.Null(Variants.ChainIsLinked(new[] { a, b }));
        Assert.Null(Variants.ChainIsLinked(new[] { b }));
        Assert.Equal("chain break: b does not call a", Variants.ChainIsLinked(new[] { b, a }));
        Assert.Equal("chain break: a does not call b", Variants.ChainIsLinked(new[] { ("a", "create proc a as exec b_01\ngo\n"), b }));
    }

    [Fact]
    public void Build_returns_callee_first_with_the_expanded_use_db() => TestScratch.Use(root =>
    {
        Write(root, "css/ss/fe/pro_a.sql", "use &dbinpr3&\ngo\ncreate proc a as exec b\ngo\n");
        Write(root, "css/ss/fe/pro_b.sql", "create proc b as select 1\ngo\n");
        var v = Parse("-- @variant: fe001=- as t chain a > b", out _)!;
        var c = new FakeCompiler { Tokens = { ["&dbinpr3&"] = "sbnmaster" } };

        var built = Variants.Build(v, new SourceLocator(root), c, "sbntest");
        Assert.Equal(new[] { ("b", "sbntest", "b__t", "css/ss/fe/pro_b.sql"), ("a", "sbnmaster", "a__t", "css/ss/fe/pro_a.sql") },
                     built.Select(x => (x.Spec.Proc, x.Spec.Db, x.Spec.ScratchName, x.RelPath)));
        Assert.Equal("use &dbinpr3&\ngo\ncreate proc a__t as exec b__t\ngo\n", ScratchProc.Render(built[1].Spec));

        c.Tokens.Clear();
        Assert.Equal("use &dbinpr3& in css/ss/fe/pro_a.sql does not resolve in the merged options",
                     Refusal(() => Variants.Build(v, new SourceLocator(root), c, "sbntest")));
        Write(root, "css/ss/fe/pro_a.sql", "create proc a as select 1\ngo\n");
        Assert.Equal("chain break: a does not call b", Refusal(() => Variants.Build(v, new SourceLocator(root), c, "sbntest")));
    });

    // ---- Options.SetCompileOption (design §3.5) ----

    [Fact]
    public void SetCompileOption_forces_an_option_in_memory_and_leaves_the_cache_file_alone() => TestScratch.Use(root =>
    {
        var profile = new ResolvedProfile { IsProfile = true, ProfileName = $"sqltest-{Guid.NewGuid():N}", IRPath = root };
        var vars = new CommandVariables { Server = "srv", Database = "sbntest", Command = "" };
        var cachePath = new ibsCompiler.Options(vars, profile).ResolvedOptionsPath;
        string Line(string k, string v) => k.PadRight(40) + v.PadRight(200);
        // The cache lives in the system temp dir by design; a fresh nonempty file is loaded as-is.
        File.WriteAllLines(cachePath, new[] { Line("&if_fe001&", ""), Line("&endif_fe001&", ""), Line("&ifn_fe001&", "/*"),
                                              Line("&endifn_fe001&", "*/"), Line("&dbinpr3&", "sbnmaster") });
        try
        {
            var before = File.ReadAllBytes(cachePath);
            var opts = new ibsCompiler.Options(vars, profile);
            Assert.True(opts.GenerateOptionFiles());
            Assert.True(opts.GetCompileOption("fe001"));
            Assert.Null(opts.GetCompileOption("zz999"));
            Assert.False(opts.SetCompileOption("zz999", true));

            Assert.True(opts.SetCompileOption("fe001", false));
            Assert.False(opts.GetCompileOption("fe001"));
            Assert.Equal("/* x */ y", opts.ReplaceWord("&if_fe001& x &endif_fe001& &ifn_fe001&y&endifn_fe001&"));
            Assert.True(opts.SetCompileOption("fe001", true));
            Assert.Equal("x /*y*/", opts.ReplaceWord("&if_fe001&x &endif_fe001&&ifn_fe001&y&endifn_fe001&"));
            Assert.Equal(before, File.ReadAllBytes(cachePath));

            Assert.Equal("unknown compile option zz999: no c: line in the merged options",
                         RunsqlScratchCompiler.ForceOptions(opts, new Dictionary<string, bool> { ["zz999"] = true }));
            var rc = new RunsqlScratchCompiler(profile, "srv");
            Assert.True(rc.HasOption("sbntest", "fe001"));
            Assert.False(rc.HasOption("sbntest", "zz999"));
            Assert.Equal("use sbnmaster", rc.Expand("sbntest", "use &dbinpr3&"));
        }
        finally { File.Delete(cachePath); }
    });

    // ---- --variant-profile (design §9 decision 2) ----

    private static string OptionsCache(ResolvedProfile p, params (string k, string v)[] lines)
    {
        var path = new ibsCompiler.Options(new CommandVariables { Server = "srv", Database = "sbntest", Command = "" }, p).ResolvedOptionsPath;
        File.WriteAllLines(path, lines.Select(l => l.k.PadRight(40) + l.v.PadRight(200)));
        return path;
    }

    [Fact]
    public void Variant_profile_supplies_the_options_layer_while_the_compile_connects_as_the_runner() => TestScratch.Use(root =>
    {
        var runner = new ResolvedProfile { IsProfile = true, ProfileName = $"sqltest-run-{Guid.NewGuid():N}", IRPath = root };
        var prod = new ResolvedProfile { IsProfile = true, ProfileName = $"sqltest-prod-{Guid.NewGuid():N}", IRPath = root };
        var caches = new[]
        {
            OptionsCache(runner, ("&dbinpr3&", "sbntestdb")),
            OptionsCache(prod, ("&if_fe001&", ""), ("&endif_fe001&", ""), ("&ifn_fe001&", "/*"), ("&endifn_fe001&", "*/"),
                         ("&dbinpr3&", "sbnmaster")),
        };
        try
        {
            var calls = new List<(string what, string profile)>();
            var control = new FakeExec { OnScalar = _ => null };
            var rc = new RunsqlScratchCompiler(runner, "srv", prod, withControl: use => use(control),
                run: (vars, p, opts) =>
                {
                    calls.Add(("run", p.ProfileName));
                    calls.Add(("options", opts.ReplaceWord("&dbinpr3& &if_fe001&on&endif_fe001&")));
                    return true;
                });
            Assert.True(rc.HasOption("sbntest", "fe001"));
            Assert.Equal("use sbnmaster", rc.Expand("sbntest", "use &dbinpr3&"));
            Assert.Null(rc.Compile(new ScratchCompile("sbntest", "p", "p__t", "select 1", new Dictionary<string, bool> { ["fe001"] = false })));
            Assert.Null(rc.DeployedText("sbntest", "p"));
            Assert.Equal(new[] { ("run", runner.ProfileName), ("options", "sbnmaster /*on*/") }, calls);
            // The drift read goes through the runner's control connection, not a compile.
            Assert.Equal(new[] { "select object_id('sbntest.dbo.p')" }, control.Log);

            // Without the flag the runner's own layer applies, as before W4b.
            var plain = new RunsqlScratchCompiler(runner, "srv");
            Assert.False(plain.HasOption("sbntest", "fe001"));
            Assert.Equal("use sbntestdb", plain.Expand("sbntest", "use &dbinpr3&"));
        }
        finally { foreach (var c in caches) File.Delete(c); }
    });

    [Fact]
    public void ResolveVariantProfile_refuses_an_unknown_name_and_keeps_the_runner_source() => TestScratch.Use(root =>
    {
        var settings = Path.Combine(root, "settings.json");
        File.WriteAllText(settings, "{\"Profiles\":{\"GONZO\":{\"Company\":\"202\",\"Host\":\"h\",\"Port\":5000,\"Password\":\"x\"}," +
                                    "\"SAME\":{\"Host\":\"h\",\"Password\":\"x\",\"SQL_SOURCE\":\"/src/ir/\"}}}");
        var mgr = new ProfileManager(settings);
        var runner = new ResolvedProfile { IsProfile = true, ProfileName = "GONZO_TEST", IRPath = "/src/ir", Pass = "p" };

        Assert.Same(runner, Runner.ResolveVariantProfile(mgr, null, runner));
        Assert.Equal("unknown --variant-profile NOPE: no such profile",
                     Assert.Throws<ArgumentException>(() => Runner.ResolveVariantProfile(mgr, "NOPE", runner)).Message);
        var p = Runner.ResolveVariantProfile(mgr, "gonzo", runner);
        Assert.Equal(("GONZO", "202", "/src/ir", true), (p.ProfileName, p.Company, p.IRPath, p.IsProfile));
        Assert.Equal("/src/ir", Runner.ResolveVariantProfile(mgr, "SAME", runner).IRPath);
    });

    [Fact]
    public void ResolveVariantProfile_refuses_another_sql_source_naming_both_paths() => TestScratch.Use(root =>
    {
        var settings = Path.Combine(root, "settings.json");
        File.WriteAllText(settings, "{\"Profiles\":{\"PROD\":{\"Host\":\"h\",\"Password\":\"x\",\"SQL_SOURCE\":\"/prod/ir\"}}}");
        var runner = new ResolvedProfile { IsProfile = true, ProfileName = "GONZO_TEST", IRPath = "/src/ir", Pass = "p" };

        Assert.Equal("--variant-profile PROD has SQL source /prod/ir, but profile GONZO_TEST has /src/ir; " +
                     "the variant profile must share the runner's SQL source",
                     Assert.Throws<ArgumentException>(() => Runner.ResolveVariantProfile(new ProfileManager(settings), "PROD", runner)).Message);
    });

    [Fact]
    public void PrecheckVariantProfile_refuses_unknown_names_and_other_server_types_from_settings_alone() => TestScratch.Use(root =>
    {
        var settings = Path.Combine(root, "settings.json");
        File.WriteAllText(settings, "{\"Profiles\":{\"RUN\":{\"Host\":\"h\"},\"SYB\":{\"Host\":\"h\",\"PLATFORM\":\"SYBASE\"}," +
                                    "\"PG\":{\"Host\":\"h\",\"PLATFORM\":\"POSTGRES\"}}}");
        var mgr = new ProfileManager(settings);

        Runner.PrecheckVariantProfile(mgr, null, "RUN");
        Runner.PrecheckVariantProfile(mgr, "syb", "RUN");
        Assert.Equal("unknown --variant-profile NOPE: no such profile",
                     Assert.Throws<ArgumentException>(() => Runner.PrecheckVariantProfile(mgr, "NOPE", "RUN")).Message);
        Assert.Equal("--variant-profile PG is POSTGRES but profile RUN is SYBASE; an options layer only fits its own server type",
                     Assert.Throws<ArgumentException>(() => Runner.PrecheckVariantProfile(mgr, "PG", "RUN")).Message);
    });

    // ---- drift refusal (design §9 decision 3) ----

    private const string DriftSource = "use &dbpro&\ngo\n-- header p\ncreate proc p as\n  select &one&\ngo\ngrant execute on p to public\ngo\n";

    private static List<(ScratchSpec, string)> DriftSpecs(params string[] procs) =>
        procs.Select(p => (new ScratchSpec(p, "sbntest", DriftSource.Replace(" p", $" {p}"), $"{p}__t",
                                            new Dictionary<string, bool> { ["fe001"] = false }, []),
                           $"css/ss/x/pro_{p}.sql")).ToList();

    private static FakeCompiler DriftCompiler(string? deployed)
    {
        var c = new FakeCompiler { Tokens = { ["&dbpro&"] = "sbntest", ["&one&"] = "1" } };
        if (deployed != null) c.Deployed["p"] = deployed;
        return c;
    }

    [Theory]
    [InlineData("-- header p\ncreate proc p as\n  select 1")]
    [InlineData("  -- header p\r\n create   proc p as\r\n\n\tselect 1\n")]
    public void Drift_passes_identical_and_whitespace_only_differences(string deployed) =>
        Variants.CheckDrift(DriftSpecs("p"), DriftCompiler(deployed));

    [Fact]
    public void Drift_refuses_code_joined_onto_a_comment_line()
    {
        // Joined, `create proc p as` sits inside the `--` comment: a different proc, not whitespace.
        Assert.Contains("first difference at character 11",
                        Refusal(() => Variants.CheckDrift(DriftSpecs("p"), DriftCompiler("  -- header p create   proc p as\r\n\tselect 1\n"))));
    }

    [Fact]
    public void Drift_refuses_a_real_difference_with_an_excerpt()
    {
        Assert.Equal("variant source for p differs from the deployed proc (css/ss/x/pro_p.sql); update the working copy or redeploy, " +
                     "or pass --variant-profile <deploying profile>; " +
                     "first difference at character 36: local \"der p\\ncreate proc p as\\nselect 1\", deployed \"der p\\ncreate proc p as\\nselect 2\"",
                     Refusal(() => Variants.CheckDrift(DriftSpecs("p"), DriftCompiler("-- header p\ncreate proc p as\n  select 2"))));
        // Case matters: only whitespace is normalised.
        Refusal(() => Variants.CheckDrift(DriftSpecs("p"), DriftCompiler("-- header p\nCREATE proc p as\n  select 1")));
    }

    [Fact]
    public void Drift_skips_a_member_that_is_not_deployed_and_notes_it_under_verbose()
    {
        var notes = new List<string>();
        Variants.CheckDrift(DriftSpecs("q", "p"), DriftCompiler("-- header p\ncreate proc p as\nselect 1"), notes.Add);
        Assert.Equal(new[] { "q is not deployed in sbntest; drift check skipped" }, notes);
    }

    [Fact]
    public void ReadDeployed_concatenates_colid_rows_for_dbo_and_returns_null_when_absent()
    {
        var x = new FakeExec
        {
            OnScalar = _ => 42,
            OnRows = _ => new() { new object?[] { "-- header p\ncreate proc p a", 0 }, new object?[] { "s\n  sel", 0 }, new object?[] { "ect 1", DBNull.Value } },
        };
        Assert.Equal("-- header p\ncreate proc p as\n  select 1", ScratchProc.ReadDeployed(x, "sbntest", "p"));
        Assert.Equal(new[] { "select object_id('sbntest.dbo.p')",
                             "select text, status from sbntest..syscomments where id = object_id('sbntest.dbo.p') and number = 1 order by colid2, colid" },
                     x.Log);

        var absent = new FakeExec { OnScalar = _ => DBNull.Value };
        Assert.Null(ScratchProc.ReadDeployed(absent, "sbntest", "p"));
        Assert.Single(absent.Log);
        Assert.Throws<ScratchRefusedException>(() => ScratchProc.ReadDeployed(absent, "sbntest", "p;drop"));
    }

    [Fact]
    public void ReadDeployed_refuses_hidden_text_rather_than_reporting_drift()
    {
        var x = new FakeExec { OnScalar = _ => 42, OnRows = _ => new() { new object?[] { "", 0 }, new object?[] { "", (short)1 } } };
        Assert.Equal("deployed text of p is hidden (sp_hidetext); drift cannot be checked",
                     Refusal(() => ScratchProc.ReadDeployed(x, "sbntest", "p")));
    }

    // ---- pre-connection block of RunOne ----

    [Fact]
    public void PrepareVariant_drift_refusal_returns_ERROR_before_any_compile() => TestScratch.Use(root =>
    {
        Write(root, "css/ss/x/pro_p.sql", DriftSource);
        var c = DriftCompiler("-- header p\ncreate proc p as\n  select 2");
        var tc = new TestCase("test_v", null, "test_v", null) { Variant = Parse("-- @variant: fe001=- as t chain p", out _) };
        var specs = new List<ScratchSpec>();

        var r = Runner.PrepareVariant(tc, new SourceLocator(root), c, "sbntest", false, specs, System.Diagnostics.Stopwatch.StartNew());
        Assert.NotNull(r);
        Assert.Equal(Outcome.ERROR, r.Outcome);
        Assert.StartsWith("variant source for p differs from the deployed proc", r.Message);
        // No specs reach RunOne, which returns before its connection, journal row and compile.
        Assert.Equal((0, 0), (specs.Count, c.Compiles));

        c.Deployed["p"] = "-- header p\ncreate proc p as\n  select 1";
        Assert.Null(Runner.PrepareVariant(tc, new SourceLocator(root), c, "sbntest", false, specs, System.Diagnostics.Stopwatch.StartNew()));
        Assert.Equal(new[] { "p__t" }, specs.Select(s => s.ScratchName));
        Assert.Null(Runner.PrepareVariant(tc with { Variant = null }, new SourceLocator(root), c, "sbntest", false, specs, System.Diagnostics.Stopwatch.StartNew()));
    });
}
