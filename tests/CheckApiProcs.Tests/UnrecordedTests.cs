using System.Text.Json;
using System.Text.Json.Nodes;

namespace CheckApiProcs.Tests;

/// <summary>procedures[].unrecorded (format doc § 1, § 3).</summary>
public class UnrecordedTests : IDisposable
{
    private const string Marked = "g_pat_agency";
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private string Decl(string json) => _dir.Write("api3-procs.json", json);

    private static object StdProc() => Std.DeclProc(Std.Proc, Std.Params(), Std.Sets());
    private static object MarkedProc(string name = Marked) => new { name, unrecorded = true };

    [Fact]
    public async Task MarkedPresent_Unverified_Exit0()
    {
        var api = Std.Api().List(Marked);
        var r = await Run.Check(api, Decl(Std.Declaration(StdProc(), MarkedProc())));
        Assert.Equal(0, r.Exit);
        Assert.Contains("2 procs: 1 UNVERIFIED, 1 OK", r.Out);
        Assert.Contains($"{Marked}  UNVERIFIED  (warning)", r.Out);
        Assert.DoesNotContain(api.Calls, c => c.EndsWith("/map/" + Marked));
    }

    [Fact]
    public async Task MarkedAbsent_Missing_Exit1()
    {
        var r = await Run.Check(Std.Api(), Decl(Std.Declaration(StdProc(), MarkedProc())));
        Assert.Equal(1, r.Exit);
        Assert.Contains($"{Marked}  MISSING", r.Out);
    }

    [Fact]
    public async Task MarkedWithParameters_Rejected_Exit2()
    {
        var withParams = new { name = Marked, unrecorded = true, parameters = Array.Empty<object>() };
        var r = await Run.Check(Std.Api().List(Marked), Decl(Std.Declaration(StdProc(), withParams)));
        Assert.Equal(2, r.Exit);
        Assert.Contains(Marked, r.Err);
        Assert.Contains("unrecorded", r.Err);
    }

    [Fact]
    public async Task UnrecordedNotBoolean_Rejected_Exit2()
    {
        var r = await Run.Check(Std.Api(), Decl(Std.Declaration(StdProc(), new { name = Marked, unrecorded = "yes" })));
        Assert.Equal(2, r.Exit);
        Assert.Contains("unrecorded", r.Err);
    }

    [Fact]
    public async Task ProcFilter_Marked_Works()
    {
        var r = await Run.Check(Std.Api().List(Marked), Decl(Std.Declaration(StdProc(), MarkedProc())), "--proc", Marked, "--quiet");
        Assert.Equal(0, r.Exit);
        Assert.Equal($"{Marked} UNVERIFIED", r.Out.Trim());
    }

    [Fact]
    public async Task Json_Unverified()
    {
        var r = await Run.Check(Std.Api().List(Marked), Decl(Std.Declaration(StdProc(), MarkedProc())), "--json");
        Assert.Equal(0, r.Exit);
        using var doc = JsonDocument.Parse(r.Out);
        var m = doc.RootElement.GetProperty("results")[1];
        Assert.Equal("UNVERIFIED", m.GetProperty("verdict").GetString());
        Assert.False(m.GetProperty("failing").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("counts").GetProperty("UNVERIFIED").GetInt32());
    }

    [Fact]
    public async Task Record_Marked_NameOnly_RoundTrips()
    {
        var names = _dir.Write("names.json",
            $"{{\"formatVersion\":\"1.0\",\"procedures\":[{{\"name\":\"{Std.Proc}\"}},{{\"name\":\"{Marked}\",\"unrecorded\":true}}]}}");
        var outPath = _dir.Path_("recorded.json");
        var rec = await Run.Record(Std.Api(), names, outPath);
        Assert.Equal(0, rec.Exit);

        using (var doc = JsonDocument.Parse(File.ReadAllText(outPath)))
        {
            var root = doc.RootElement;
            Assert.Equal("1.0", root.GetProperty("formatVersion").GetString());
            var unmarked = root.GetProperty("procedures")[0];
            Assert.False(unmarked.TryGetProperty("unrecorded", out _));
            Assert.True(unmarked.TryGetProperty("parameters", out _));
            var marked = root.GetProperty("procedures")[1];
            Assert.Equal(Marked, marked.GetProperty("name").GetString());
            Assert.True(marked.GetProperty("unrecorded").GetBoolean());
            Assert.False(marked.TryGetProperty("parameters", out _));
            Assert.False(marked.TryGetProperty("resultSets", out _));
        }

        var chk = await Run.Check(Std.Api().List(Marked), outPath);
        Assert.Equal(0, chk.Exit);
        Assert.Contains("2 procs: 1 UNVERIFIED, 1 OK", chk.Out);
    }

    [Fact]
    public async Task Record_MixedShapes_BareStringAndMarkedObject()
    {
        var names = _dir.Write("names.json", $"{{\"procedures\":[\"{Std.Proc}\",{{\"name\":\"{Marked}\",\"unrecorded\":true}}]}}");
        var outPath = _dir.Path_("recorded.json");
        var rec = await Run.Record(Std.Api(), names, outPath);
        Assert.Equal(0, rec.Exit);
        Assert.Contains("recorded 2 procs", rec.Out);
    }

    [Fact]
    public async Task Record_StaleMark_Fails()
    {
        var names = _dir.Write("names.json", $"{{\"procedures\":[{{\"name\":\"{Std.Proc}\",\"unrecorded\":true}}]}}");
        var outPath = _dir.Path_("recorded.json");
        var api = Std.Api();
        var r = await Run.Record(api, names, outPath);
        Assert.Equal(1, r.Exit);
        Assert.Contains($"{Std.Proc}: marked unrecorded but present", r.Err);
        Assert.DoesNotContain(api.Calls, c => c.EndsWith("/map/" + Std.Proc));
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public async Task Record_UnmarkedMissing_Fails_MarkedAbsentNotReported()
    {
        var names = _dir.Write("names.json", $"{{\"procedures\":[{{\"name\":\"c_nope\"}},{{\"name\":\"{Marked}\",\"unrecorded\":true}}]}}");
        var outPath = _dir.Path_("recorded.json");
        var r = await Run.Record(Std.Api(), names, outPath);
        Assert.Equal(1, r.Exit);
        Assert.Contains("c_nope", r.Err);
        Assert.DoesNotContain(Marked, r.Err);
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public async Task MarkedStale_Unverified_NoStaleFlag()
    {
        var api = Std.Api().List(Marked, outdated: true);
        var decl = Decl(Std.Declaration(StdProc(), MarkedProc()));
        var text = await Run.Check(api, decl);
        Assert.Equal(0, text.Exit);
        Assert.Contains($"{Marked}  UNVERIFIED  (warning)", text.Out);
        Assert.DoesNotContain(Report.StaleHint(Marked), text.Out);

        var json = await Run.Check(api, decl, "--json");
        using var doc = JsonDocument.Parse(json.Out);
        var m = doc.RootElement.GetProperty("results")[1];
        Assert.Equal("UNVERIFIED", m.GetProperty("verdict").GetString());
        Assert.False(m.GetProperty("stale").GetBoolean());
        Assert.Equal(JsonValueKind.Null, m.GetProperty("hint").ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task UnrecordedFalseOrNull_IsUnmarked(bool? value)
    {
        var proc = JsonSerializer.SerializeToNode(StdProc())!.AsObject();
        proc["unrecorded"] = value;
        var api = Std.Api();
        var r = await Run.Check(api, Decl(Std.Declaration(proc)));
        Assert.Equal(0, r.Exit);
        Assert.Contains("1 procs: 1 OK", r.Out);
        Assert.Contains($"GET /api/v1/map/{Std.Proc}", api.Calls);
    }

    [Fact]
    public async Task MarkedWithResultSetsOnly_Rejected_Exit2()
    {
        var withSets = new { name = Marked, unrecorded = true, resultSets = Array.Empty<object>() };
        var r = await Run.Check(Std.Api().List(Marked), Decl(Std.Declaration(StdProc(), withSets)));
        Assert.Equal(2, r.Exit);
        Assert.Contains($"{Marked} is marked unrecorded but has parameters or resultSets", r.Err);
    }

    [Fact]
    public async Task UnrecordedNotBoolean_NoName_NamesAProcedure()
    {
        var r = await Run.Check(Std.Api(), Decl(Std.Declaration(StdProc(), new { unrecorded = "yes" })));
        Assert.Equal(2, r.Exit);
        Assert.Contains("a procedure: unrecorded must be true or false", r.Err);
        Assert.DoesNotContain(": : ", r.Err);
    }

    // Documented limitation (check_api_procs.md, Name matching): presence is by 32-character prefix.
    [Fact]
    public async Task LongMarkedName_SharedPrefix_Unverified_DocumentedLimitation()
    {
        var prefix = new string('x', 32);
        string a = prefix + "_a", b = prefix + "_b";
        var api = new FakeApi().Add(a, Std.Params(), Std.Sets());
        var r = await Run.Check(api, Decl(Std.Declaration(Std.DeclProc(a, Std.Params(), Std.Sets()), MarkedProc(b))));
        Assert.Equal(0, r.Exit);
        Assert.Contains($"{b}  UNVERIFIED  (warning)", r.Out);
        Assert.DoesNotContain(api.Calls, c => c.EndsWith("/map/" + b));
    }
}
