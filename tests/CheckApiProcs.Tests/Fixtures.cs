using System.Collections;
using System.Net;
using System.Text;
using System.Text.Json;

namespace CheckApiProcs.Tests;

public sealed class TempDir : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "check_api_procs_" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Root);
    public string Path_(string name) => Path.Combine(Root, name);
    public string Write(string name, string content)
    {
        var p = Path_(name);
        File.WriteAllText(p, content);
        return p;
    }
    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public record LiveParamSpec(string Name, string Type, string Default = "no_default", bool Output = false, bool? Required = null)
{
    public bool IsRequired => Required ?? Default == "no_default";
}

/// <summary>A passcard-shaped proc on the engine and its matching recorded declaration.</summary>
public static class Std
{
    public const string Proc = "c_ma_test";

    public static List<LiveParamSpec> Params() => new()
    {
        new("@s_ins", "int"),
        new("@deal_id", "varchar(12)"),
        new("@userid", "varchar(8)", "null"),
        new("@s_pc", "int", "null", true),
        new("@name", "varchar(40)", "NULL"),
    };

    public static Dictionary<string, string[]> Sets() => new()
    {
        ["PASSCARD"] = new[] { "s_pc", "name" },
        ["resultSet1"] = new[] { "code" },
    };

    public static FakeApi Api() => new FakeApi().Add(Proc, Params(), Sets());

    public static object DeclProc(string name, IEnumerable<LiveParamSpec> ps, Dictionary<string, string[]> sets) => new
    {
        name,
        parameters = ps.Select(p => new
        {
            name = p.Name, type = p.Type, @default = p.Default == "no_default" ? null : p.Default,
            output = p.Output, required = p.IsRequired,
        }),
        resultSets = sets.Select(s => new { name = s.Key, columns = s.Value }),
    };

    public static string Declaration(params object[] procs) => JsonSerializer.Serialize(new
    {
        formatVersion = "1.0",
        consumer = "sbn-media",
        release = "v95.23.1",
        referenceEngine = new { url = "https://apiengine.test", version = "08.96.1" },
        recordedAt = "2026-10-22T09:30:00Z",
        procedures = procs,
    });

    public static string Declaration() => Declaration(DeclProc(Proc, Params(), Sets()));
}

/// <summary>Serves the APIEngine shapes: version and auth wrapped in result, list and map bare as Api3Controller returns them; anything else is 404.</summary>
public class FakeApi : HttpMessageHandler
{
    public const string Token = "tok-123";
    public List<string> Calls { get; } = new();
    public List<object> Listed { get; } = new();
    public Dictionary<string, object> Maps { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }

    public FakeApi List(string name, bool outdated = false, string createdOn = "2026-10-01 10:00")
    {
        Listed.Add(new { name, createdOn, compiledOn = "2026-09-30 09:00", outdated });
        return this;
    }

    public FakeApi Add(string name, IEnumerable<LiveParamSpec> ps, Dictionary<string, string[]> sets, bool outdated = false)
    {
        List(Checker.ListName(name), outdated);
        Maps[name] = Map(ps, sets);
        return this;
    }

    public static object Map(IEnumerable<LiveParamSpec> ps, Dictionary<string, string[]> sets) => new
    {
        createdOn = "2026-10-01 10:00",
        parameters = ps.Select(p => new
        {
            parm_name = p.Name, parm_type = p.Type, parm_default = p.Default,
            parm_output = p.Output ? "true" : "false", parm_required = p.IsRequired ? "true" : "false",
        }),
        resultSets = sets.ToDictionary(s => s.Key, s => s.Value.Select(c => new { resultset_name = c })),
        // A copy of the first set; the checker must ignore it.
        resultSet = sets.Take(1).Select(s => s.Value.Select(c => new { resultset_name = "IGNORED_" + c })).FirstOrDefault(),
    };

    public static HttpResponseMessage Json(HttpStatusCode code, object body) =>
        new(code) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var path = req.RequestUri!.AbsolutePath;
        lock (Calls) Calls.Add($"{req.Method} {path}");
        var o = Override?.Invoke(req);
        if (o != null) return Task.FromResult(o);

        if (path == "/api/v1/diagnostic/version") return Ok(new { result = "08.96.1" });
        if (path == "/auth") return Ok(new { result = Token });
        if (!req.Headers.TryGetValues("User-Token", out var t) || t.Single() != Token)
            return Task.FromResult(Json(HttpStatusCode.Unauthorized, new { error = "no token" }));
        if (path == "/api/v1/api3/list") return Ok(new { search = "", names = Listed });
        const string mapPrefix = "/api/v1/api3/map/";
        if (path.StartsWith(mapPrefix))
        {
            var name = path[mapPrefix.Length..];
            return Maps.TryGetValue(name, out var m)
                ? Ok(m)
                : Task.FromResult(Json(HttpStatusCode.NotFound, new { error = $"no map for {name}" }));
        }
        return Task.FromResult(Json(HttpStatusCode.NotFound, new { error = path }));
    }

    private static Task<HttpResponseMessage> Ok(object body) => Task.FromResult(Json(HttpStatusCode.OK, body));
}

public record RunResult(int Exit, string Out, string Err);

public static class Run
{
    public const string Url = "https://apiengine.test";
    public const string Password = "s3cret-pw";

    public static Task<RunResult> Check(FakeApi api, string declPath, params string[] extra) =>
        Raw(api, new[] { Url, "--declaration", declPath }.Concat(extra).ToArray());

    public static Task<RunResult> Record(FakeApi api, string namesPath, string outPath, string url = Url) =>
        Raw(api, new[] { url, "--record", "--names", namesPath, "--consumer", "sbn-media", "--release", "v95.23.1", "--out", outPath });

    public static async Task<RunResult> Raw(HttpMessageHandler? api, string[] args, IDictionary? env = null)
    {
        env ??= new Hashtable { ["SBN_USERNAME"] = "tester", ["SBN_PASSWORD"] = Password };
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CheckApiProcs.App.RunAsync(args, o, e, env, api);
        return new RunResult(code, o.ToString(), e.ToString());
    }
}
