using System.Collections;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CheckApiProcs.Tests;

public class VerdictTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private string Decl(string? json = null) => _dir.Write("api3-procs.json", json ?? Std.Declaration());

    private static FakeApi ApiWith(List<LiveParamSpec> ps, Dictionary<string, string[]>? sets = null, bool outdated = false) =>
        new FakeApi().Add(Std.Proc, ps, sets ?? Std.Sets(), outdated);

    [Fact]
    public async Task AllMatch_Ok_Exit0()
    {
        var r = await Run.Check(Std.Api(), Decl());
        Assert.Equal(0, r.Exit);
        Assert.Contains("1 procs: 1 OK", r.Out);
    }

    [Fact]
    public async Task Missing_Fails()
    {
        var r = await Run.Check(new FakeApi().List("c_other"), Decl());
        Assert.Equal(1, r.Exit);
        Assert.Contains("MISSING", r.Out);
    }

    [Fact]
    public async Task LongName_PrefixMatch()
    {
        var name = "c_ma_a_very_long_procedure_name_over_32_chars";
        Assert.True(name.Length > 32);
        var api = new FakeApi().List(name[..32]);
        api.Maps[name] = FakeApi.Map(Std.Params(), Std.Sets());
        var r = await Run.Check(api, Decl(Std.Declaration(Std.DeclProc(name, Std.Params(), Std.Sets()))));
        Assert.Equal(0, r.Exit);
        Assert.Contains("1 OK", r.Out);
        Assert.Contains($"GET /api/v1/api3/map/{name}", api.Calls);
    }

    public static TheoryData<string> Changes => new() { "rename", "retype", "reorder", "default", "output" };

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task ParamChanged_Fails(string change)
    {
        var ps = Std.Params();
        switch (change)
        {
            case "rename": ps[1] = ps[1] with { Name = "@deal_no" }; break;
            case "retype": ps[1] = ps[1] with { Type = "varchar(16)" }; break;
            case "reorder": (ps[0], ps[1]) = (ps[1], ps[0]); break;
            case "default": ps[2] = ps[2] with { Default = "'x'" }; break;
            case "output": ps[3] = ps[3] with { Output = false }; break;
        }
        var r = await Run.Check(ApiWith(ps), Decl());
        Assert.Equal(1, r.Exit);
        Assert.Contains("PARAM-DIFF", r.Out);
    }

    [Fact]
    public async Task RecordedOptionalRemoved_Fails()
    {
        var ps = Std.Params();
        ps.RemoveAll(p => p.Name == "@name");
        var r = await Run.Check(ApiWith(ps), Decl());
        Assert.Equal(1, r.Exit);
        Assert.Contains("@name: removed on the engine", r.Out);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task ExtraOptional_Ok(int at)
    {
        var ps = Std.Params();
        ps.Insert(at, new("@extra", "int", "null"));
        var r = await Run.Check(ApiWith(ps), Decl());
        Assert.Equal(0, r.Exit);
        Assert.Contains("1 OK", r.Out);
    }

    [Fact]
    public async Task ExtraRequired_Fails()
    {
        var ps = Std.Params();
        ps.Add(new("@need", "int"));
        var r = await Run.Check(ApiWith(ps), Decl());
        Assert.Equal(1, r.Exit);
        Assert.Contains("@need: required by the engine", r.Out);
    }

    [Fact]
    public async Task Normalisation_Ok()
    {
        var api = Std.Api();
        // Raw engine spellings the declaration records as null / booleans.
        api.Maps[Std.Proc] = new
        {
            createdOn = "2026-10-01 10:00",
            parameters = new[]
            {
                new { parm_name = "@s_ins", parm_type = "INT", parm_default = "NO_DEFAULT", parm_output = "False", parm_required = "TRUE" },
                new { parm_name = "@deal_id", parm_type = "varchar( 12 )", parm_default = "no_default", parm_output = "false", parm_required = "true" },
                new { parm_name = "@userid", parm_type = "varchar(8)", parm_default = "null", parm_output = "false", parm_required = "false" },
                new { parm_name = "@s_pc", parm_type = "int", parm_default = "null", parm_output = "True", parm_required = "false" },
                new { parm_name = "@name", parm_type = "varchar(40)", parm_default = "NULL", parm_output = "false", parm_required = "false" },
            },
            resultSets = Std.Sets().ToDictionary(s => s.Key, s => s.Value.Select(c => new { resultset_name = c })),
        };
        var r = await Run.Check(api, Decl());
        Assert.Equal(0, r.Exit);
        Assert.Contains("1 OK", r.Out);
    }

    [Fact]
    public async Task NullKeywordCase_Ok()
    {
        var ps = Std.Params();
        ps[2] = ps[2] with { Default = "NULL" };
        var r = await Run.Check(ApiWith(ps), Decl());
        Assert.Equal(0, r.Exit);
        Assert.Contains("1 OK", r.Out);
    }

    [Fact]
    public async Task QuotedDefaultCase_Fails()
    {
        var recorded = Std.Params();
        recorded[2] = recorded[2] with { Default = "'x'" };
        var live = Std.Params();
        live[2] = live[2] with { Default = "'X'" };
        var r = await Run.Check(ApiWith(live), Decl(Std.Declaration(Std.DeclProc(Std.Proc, recorded, Std.Sets()))));
        Assert.Equal(1, r.Exit);
        Assert.Contains("PARAM-DIFF", r.Out);
    }

    [Fact]
    public async Task ColumnDiff_WarnsOnly()
    {
        var sets = Std.Sets();
        sets["PASSCARD"] = new[] { "s_pc", "full_name" };
        var r = await Run.Check(ApiWith(Std.Params(), sets), Decl());
        Assert.Equal(0, r.Exit);
        Assert.Contains("RESULT-DIFF  (warning)", r.Out);
    }

    [Fact]
    public async Task ResultSetDecoy_Ignored()
    {
        var api = Std.Api();
        var r = await Run.Check(api, Decl());
        Assert.Equal(0, r.Exit);
        Assert.Contains("1 OK", r.Out);
        Assert.DoesNotContain("IGNORED_", r.Out);
    }

    [Fact]
    public async Task Stale_ReportsHint_NotFail()
    {
        var r = await Run.Check(ApiWith(Std.Params(), outdated: true), Decl());
        Assert.Equal(0, r.Exit);
        Assert.Contains("MAP-STALE", r.Out);
        Assert.Contains(Report.StaleHint(Std.Proc), r.Out);
    }

    [Fact]
    public async Task NeverMapped_NotStale_Check()
    {
        var api = new FakeApi().List(Std.Proc, outdated: true, createdOn: "");
        api.Maps[Std.Proc] = FakeApi.Map(Std.Params(), Std.Sets());
        var r = await Run.Check(api, Decl());
        Assert.Equal(0, r.Exit);
        Assert.Contains("1 OK", r.Out);
        Assert.DoesNotContain("MAP-STALE", r.Out);
    }

    [Fact]
    public async Task NeverMapped_NotStale_Record()
    {
        var api = new FakeApi().List(Std.Proc, outdated: true, createdOn: "");
        api.Maps[Std.Proc] = FakeApi.Map(Std.Params(), Std.Sets());
        var names = _dir.Write("names.json", "{\"procedures\":[\"c_ma_test\"]}");
        var outPath = _dir.Path_("recorded.json");
        var r = await Run.Record(api, names, outPath);
        Assert.Equal(0, r.Exit);
        Assert.True(File.Exists(outPath));
    }

    [Fact]
    public async Task StaleAndDiff_Fails()
    {
        var ps = Std.Params();
        ps[0] = ps[0] with { Type = "smallint" };
        var r = await Run.Check(ApiWith(ps, outdated: true), Decl());
        Assert.Equal(1, r.Exit);
        Assert.Contains("PARAM-DIFF", r.Out);
        Assert.Contains("remap.py", r.Out);
    }

    [Fact]
    public async Task MapError_Fails()
    {
        var api = Std.Api();
        api.Override = req => req.RequestUri!.AbsolutePath.StartsWith("/api/v1/api3/map/")
            ? FakeApi.Json(HttpStatusCode.InternalServerError, new { error = "map exploded" })
            : null;
        var r = await Run.Check(api, Decl());
        Assert.Equal(1, r.Exit);
        Assert.Contains("MAP-ERROR", r.Out);
    }

    [Theory]
    [InlineData("auth")]
    [InlineData("unreachable")]
    [InlineData("list")]
    public async Task Connectivity_Exit3(string failure)
    {
        var api = Std.Api();
        api.Override = req => (failure, req.RequestUri!.AbsolutePath) switch
        {
            ("auth", "/auth") => FakeApi.Json(HttpStatusCode.Unauthorized, new { error = "bad login" }),
            ("unreachable", _) => throw new HttpRequestException("connection refused"),
            ("list", "/api/v1/api3/list") => FakeApi.Json(HttpStatusCode.InternalServerError, new { error = "list broke" }),
            _ => null,
        };
        var r = await Run.Check(api, Decl());
        Assert.Equal(3, r.Exit);
    }

    [Fact]
    public async Task GetRetriedOnce_On5xx()
    {
        var api = Std.Api();
        var failures = 0;
        api.Override = req => req.RequestUri!.AbsolutePath.StartsWith("/api/v1/api3/map/") && failures++ == 0
            ? FakeApi.Json(HttpStatusCode.ServiceUnavailable, new { error = "busy" })
            : null;
        var r = await Run.Check(api, Decl());
        Assert.Equal(0, r.Exit);
        Assert.Equal(2, api.Calls.Count(c => c.StartsWith("GET /api/v1/api3/map/")));
    }

    [Fact]
    public async Task AuthNotRetried_On5xx()
    {
        var api = Std.Api();
        api.Override = req => req.RequestUri!.AbsolutePath == "/auth"
            ? FakeApi.Json(HttpStatusCode.InternalServerError, new { error = "auth broke" })
            : null;
        var r = await Run.Check(api, Decl());
        Assert.Equal(3, r.Exit);
        Assert.Equal(1, api.Calls.Count(c => c == "POST /auth"));
    }

    [Theory]
    [InlineData("", "<engine-url> is required")]
    [InlineData("https://apiengine.test", "--declaration is required")]
    [InlineData("https://apiengine.test --declaration x.json --bogus", "unknown argument '--bogus'")]
    [InlineData("https://apiengine.test --declaration x.json --json --quiet", "cannot be combined")]
    [InlineData("http://apiengine.test --declaration x.json", "must be https")]
    [InlineData("https://apiengine.test --declaration x.json --names x.json", "require --record")]
    [InlineData("https://apiengine.test --record --proc c_ma_test --names x.json --consumer c --release r --out o.json", "--proc is check mode only")]
    [InlineData("https://apiengine.test https://second.test --declaration x.json", "unexpected extra argument")]
    public async Task Usage_Exit2(string line, string expect)
    {
        var args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var api = Std.Api();
        var r = await Run.Raw(api, args);
        Assert.Equal(2, r.Exit);
        Assert.Contains(expect, r.Err);
        Assert.DoesNotContain("second.test", r.Err);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task HttpLoopback_Ok()
    {
        var r = await Run.Raw(Std.Api(), new[] { "http://localhost:5000", "--declaration", Decl() });
        Assert.Equal(0, r.Exit);
        Assert.Contains("1 OK", r.Out);
    }

    [Theory]
    [InlineData("SBN_USERNAME")]
    [InlineData("SBN_PASSWORD")]
    public async Task MissingCredentials_Exit2(string missing)
    {
        var env = new Hashtable { ["SBN_USERNAME"] = "tester", ["SBN_PASSWORD"] = Run.Password };
        env.Remove(missing);
        var api = Std.Api();
        var r = await Run.Raw(api, new[] { Run.Url, "--declaration", Decl() }, env);
        Assert.Equal(2, r.Exit);
        Assert.Contains("credentials required", r.Err);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task UnknownMajor_Exit2()
    {
        var r = await Run.Check(Std.Api(), Decl(Std.Declaration().Replace("\"formatVersion\":\"1.0\"", "\"formatVersion\":\"2.0\"")));
        Assert.Equal(2, r.Exit);
        Assert.Contains("formatVersion 2.0", r.Err);
    }

    [Fact]
    public async Task NamesOnlyInCheckMode_Exit2()
    {
        var r = await Run.Check(Std.Api(), Decl("{\"formatVersion\":\"1.0\",\"procedures\":[\"c_ma_test\"]}"));
        Assert.Equal(2, r.Exit);
        Assert.Contains("names list", r.Err);
    }

    [Fact]
    public async Task UndeclaredProcFilter_Exit2()
    {
        var api = Std.Api();
        var r = await Run.Check(api, Decl(), "--proc", "c_not_declared");
        Assert.Equal(2, r.Exit);
        Assert.Contains("c_not_declared", r.Err);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task NeverBuildsOrExecutes()
    {
        var api = ApiWith(Std.Params(), outdated: true);
        await Run.Check(api, Decl());
        var names = _dir.Write("names.json", "{\"procedures\":[\"c_ma_test\"]}");
        await Run.Record(api, names, _dir.Path_("out.json"));
        Assert.Contains(api.Calls, c => c.Contains("/api3/map/"));
        Assert.DoesNotContain(api.Calls, c => c.Contains("/build") || c.Contains("/execute"));
    }

    [Fact]
    public async Task Record_RoundTrips()
    {
        var api = Std.Api();
        var names = _dir.Write("names.json", "{\"procedures\":[\"c_ma_test\"]}");
        var outPath = _dir.Path_("recorded.json");
        var rec = await Run.Record(api, names, outPath);
        Assert.Equal(0, rec.Exit);

        var text = File.ReadAllText(outPath);
        Assert.DoesNotContain("\r", text);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        Assert.Equal("1.0", root.GetProperty("formatVersion").GetString());
        Assert.Equal("sbn-media", root.GetProperty("consumer").GetString());
        Assert.Equal("v95.23.1", root.GetProperty("release").GetString());
        Assert.Equal(Run.Url, root.GetProperty("referenceEngine").GetProperty("url").GetString());
        Assert.Equal("08.96.1", root.GetProperty("referenceEngine").GetProperty("version").GetString());
        Assert.True(DateTime.TryParse(root.GetProperty("recordedAt").GetString(), out _));
        var proc = root.GetProperty("procedures")[0];
        Assert.Equal(Std.Proc, proc.GetProperty("name").GetString());
        var p0 = proc.GetProperty("parameters")[0];
        Assert.Equal("@s_ins", p0.GetProperty("name").GetString());
        Assert.Equal("int", p0.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, p0.GetProperty("default").ValueKind);
        Assert.False(p0.GetProperty("output").GetBoolean());
        Assert.True(p0.GetProperty("required").GetBoolean());
        Assert.True(proc.GetProperty("parameters")[3].GetProperty("output").GetBoolean());
        var set = proc.GetProperty("resultSets")[0];
        Assert.Equal("PASSCARD", set.GetProperty("name").GetString());
        Assert.Equal("s_pc", set.GetProperty("columns")[0].GetString());

        var chk = await Run.Check(api, outPath);
        Assert.Equal(0, chk.Exit);
        Assert.Contains("1 procs: 1 OK", chk.Out);
    }

    [Fact]
    public async Task Record_MissingName_Fails()
    {
        var names = _dir.Write("names.json", "{\"procedures\":[\"c_ma_test\",\"c_nope\"]}");
        var outPath = _dir.Path_("recorded.json");
        var r = await Run.Record(Std.Api(), names, outPath);
        Assert.Equal(1, r.Exit);
        Assert.Contains("c_nope", r.Err);
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public async Task Record_StaleMap_Fails()
    {
        var names = _dir.Write("names.json", "{\"procedures\":[\"c_ma_test\"]}");
        var outPath = _dir.Path_("recorded.json");
        var r = await Run.Record(ApiWith(Std.Params(), outdated: true), names, outPath);
        Assert.Equal(1, r.Exit);
        Assert.Contains(Std.Proc, r.Err);
        Assert.Contains(Report.StaleHint(Std.Proc), r.Err);
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public async Task RecordedAt_InvariantCulture()
    {
        var names = _dir.Write("names.json", "{\"procedures\":[\"c_ma_test\"]}");
        var outPath = _dir.Path_("recorded.json");
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("th-TH");
        try { Assert.Equal(0, (await Run.Record(Std.Api(), names, outPath)).Exit); }
        finally { CultureInfo.CurrentCulture = saved; }
        using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
        var at = doc.RootElement.GetProperty("recordedAt").GetString()!;
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", at);
        Assert.StartsWith(DateTime.UtcNow.Year.ToString(CultureInfo.InvariantCulture), at);
    }

    [Fact]
    public async Task Json_Report()
    {
        var ps = Std.Params();
        ps[0] = ps[0] with { Type = "smallint" };
        var sets = Std.Sets();
        sets["PASSCARD"] = new[] { "other" };
        var api = new FakeApi().Add(Std.Proc, Std.Params(), Std.Sets(), outdated: true).Add("c_ma_two", ps, sets);
        var r = await Run.Check(api, Decl(Std.Declaration(
            Std.DeclProc(Std.Proc, Std.Params(), Std.Sets()), Std.DeclProc("c_ma_two", Std.Params(), Std.Sets()))), "--json");
        Assert.Equal(1, r.Exit);
        using var doc = JsonDocument.Parse(r.Out);
        var results = doc.RootElement.GetProperty("results");
        var one = results[0];
        Assert.Equal(Std.Proc, one.GetProperty("proc").GetString());
        Assert.Equal("MAP-STALE", one.GetProperty("verdict").GetString());
        Assert.True(one.GetProperty("stale").GetBoolean());
        Assert.False(one.GetProperty("paramDiff").GetBoolean());
        Assert.False(one.GetProperty("resultDiff").GetBoolean());
        var two = results[1];
        Assert.Equal("PARAM-DIFF", two.GetProperty("verdict").GetString());
        Assert.False(two.GetProperty("stale").GetBoolean());
        Assert.True(two.GetProperty("paramDiff").GetBoolean());
        Assert.True(two.GetProperty("resultDiff").GetBoolean());
    }

    [Fact]
    public async Task NoSecretsInOutput()
    {
        const string url = "https://alice:pw-in-url@apiengine.test/?apikey=q-secret";
        var api = ApiWith(Std.Params(), outdated: true);
        var chk = await Run.Raw(api, new[] { url, "--declaration", Decl() });
        var names = _dir.Write("names.json", "{\"procedures\":[\"c_ma_test\"]}");
        var outPath = _dir.Path_("recorded.json");
        var rec = await Run.Record(Std.Api(), names, outPath, url);
        Assert.Equal(0, rec.Exit);
        var all = chk.Out + chk.Err + rec.Out + rec.Err + File.ReadAllText(outPath);
        Assert.Contains("apiengine.test", chk.Out);
        foreach (var secret in new[] { Run.Password, FakeApi.Token, "pw-in-url", "alice", "q-secret", "apikey" })
            Assert.DoesNotContain(secret, all);
    }

    [Fact]
    public async Task NoSecretsInOutput_MapError()
    {
        var api = Std.Api();
        api.Override = req => req.RequestUri!.AbsolutePath.StartsWith("/api/v1/api3/map/")
            ? FakeApi.Json(HttpStatusCode.InternalServerError, new { error = $"leak {FakeApi.Token} {Run.Password}" })
            : null;
        var r = await Run.Check(api, Decl());
        Assert.Equal(1, r.Exit);
        Assert.Contains("MAP-ERROR", r.Out);
        foreach (var secret in new[] { Run.Password, FakeApi.Token })
            Assert.DoesNotContain(secret, r.Out + r.Err);
    }

    [Fact]
    public async Task NoSecretsInOutput_Exit3()
    {
        var api = Std.Api();
        api.Override = req => req.RequestUri!.AbsolutePath == "/auth"
            ? FakeApi.Json(HttpStatusCode.Unauthorized, new { error = $"bad login for {Run.Password}" })
            : null;
        var r = await Run.Check(api, Decl());
        Assert.Equal(3, r.Exit);
        Assert.Contains("POST /auth", r.Err);
        Assert.DoesNotContain(Run.Password, r.Out + r.Err);
    }
}
