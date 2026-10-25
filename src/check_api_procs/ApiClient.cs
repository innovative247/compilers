using System.Text;
using System.Text.Json;

namespace CheckApiProcs;

public class ApiException : Exception
{
    public ApiException(string message) : base(message) { }
}

public class ServerProc
{
    public string Name { get; set; } = "";
    public string? CreatedOn { get; set; }
    public string? CompiledOn { get; set; }
    public bool Outdated { get; set; }
}

public class LiveParam
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Default { get; set; } = "";
    public string Output { get; set; } = "";
    public string Required { get; set; } = "";
}

public class LiveMap
{
    public List<LiveParam> Parameters { get; } = new();
    public Dictionary<string, List<string>> ResultSets { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Framework engines (no resultSets) merge every set into one resultSet list.</summary>
    public List<string>? FlatResultSet { get; set; }
    public string? CreatedOn { get; set; }
}

/// <summary>Read-only APIEngine client: custom User-Agent, User-Token auth, per-request timeout, one retry on 5xx.</summary>
public class ApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly TimeSpan _timeout;
    private readonly Redactor _redact;
    private string? _token;

    public ApiClient(HttpMessageHandler handler, string baseUrl, TimeSpan timeout, string version, Redactor redact)
    {
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        // Cloudflare 403s default user agents.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"check_api_procs/{version}");
        _base = baseUrl.TrimEnd('/');
        _timeout = timeout;
        _redact = redact;
    }

    public void Dispose() => _http.Dispose();

    public async Task<string> GetVersionAsync()
    {
        using var doc = await SendAsync(HttpMethod.Get, "/api/v1/diagnostic/version", null);
        return ResultOf(doc.RootElement).ToString();
    }

    public async Task AuthAsync(string user, string password)
    {
        var body = JsonSerializer.Serialize(new { username = user, password });
        using var doc = await SendAsync(HttpMethod.Post, "/auth", body);
        var token = ResultOf(doc.RootElement);
        if (token.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(token.GetString()))
            throw new ApiException("POST /auth: no token in the response");
        _token = token.GetString();
        _redact.Add(_token!);
    }

    public async Task<List<ServerProc>> ListAsync()
    {
        using var doc = await SendAsync(HttpMethod.Get, "/api/v1/list", null);
        var names = Prop(ResultOf(doc.RootElement), "names");
        if (names is not { ValueKind: JsonValueKind.Array } arr) throw new ApiException("GET /api/v1/list: no names array");
        return arr.EnumerateArray().Select(n => new ServerProc
        {
            Name = Str(n, "name") ?? "",
            CreatedOn = Str(n, "createdOn"),
            CompiledOn = Str(n, "compiledOn"),
            Outdated = Prop(n, "outdated") is { } o && (o.ValueKind == JsonValueKind.True ||
                       (o.ValueKind == JsonValueKind.String && o.GetString()!.Equals("true", StringComparison.OrdinalIgnoreCase))),
        }).Where(p => p.Name != "").ToList();
    }

    public async Task<LiveMap> MapAsync(string proc)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"/api/v1/map/{Uri.EscapeDataString(proc)}", null);
        var root = ResultOf(doc.RootElement);
        var map = new LiveMap { CreatedOn = Str(root, "createdOn") };
        if (Prop(root, "parameters") is { ValueKind: JsonValueKind.Array } ps)
            foreach (var p in ps.EnumerateArray())
                map.Parameters.Add(new LiveParam
                {
                    Name = Str(p, "parm_name") ?? "",
                    Type = Str(p, "parm_type") ?? "",
                    Default = Str(p, "parm_default") ?? "",
                    Output = Str(p, "parm_output") ?? "",
                    Required = Str(p, "parm_required") ?? "",
                });
        if (Prop(root, "resultSets") is { ValueKind: JsonValueKind.Object } rs)
            foreach (var set in rs.EnumerateObject())
                map.ResultSets[set.Name] = set.Value.ValueKind == JsonValueKind.Array
                    ? set.Value.EnumerateArray().Select(c => Str(c, "resultset_name") ?? "").ToList()
                    : new List<string>();
        else if (Prop(root, "resultSet") is { ValueKind: JsonValueKind.Array } flat)
            map.FlatResultSet = flat.EnumerateArray().Select(c => Str(c, "resultset_name") ?? "").ToList();
        return map;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string? json)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var req = new HttpRequestMessage(method, _base + path);
            if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (_token != null) req.Headers.Add("User-Token", _token);
            using var cts = new CancellationTokenSource(_timeout);
            HttpResponseMessage resp;
            string text;
            try
            {
                resp = await _http.SendAsync(req, cts.Token);
                text = await resp.Content.ReadAsStringAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new ApiException($"{method} {path}: timed out after {_timeout.TotalSeconds:0}s");
            }
            catch (HttpRequestException ex)
            {
                throw new ApiException(_redact.Apply($"{method} {path}: {ex.Message}"));
            }
            using (resp)
            {
                var code = (int)resp.StatusCode;
                // GET only: a retried POST /auth would send the password again.
                if (code >= 500 && attempt == 0 && method == HttpMethod.Get) continue;
                if (!resp.IsSuccessStatusCode)
                    throw new ApiException(_redact.Apply($"{method} {path}: HTTP {code} {resp.ReasonPhrase}{Snippet(text)}"));
                try { return JsonDocument.Parse(text.Length == 0 ? "{}" : text); }
                catch (JsonException) { throw new ApiException(_redact.Apply($"{method} {path}: response is not JSON{Snippet(text)}")); }
            }
        }
        throw new InvalidOperationException("unreachable: the second attempt always returns or throws");
    }

    private static string Snippet(string text)
    {
        var t = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length == 0 ? "" : ": " + (t.Length > 200 ? t[..200] + "..." : t);
    }

    // APIEngine wraps most payloads in {"result": ...}.
    private static JsonElement ResultOf(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && Prop(root, "result") is { } r ? r : root;

    private static JsonElement? Prop(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static string? Str(JsonElement e, string name) => Prop(e, name) switch
    {
        null => null,
        { ValueKind: JsonValueKind.Null } => null,
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { } v => v.ToString(),
    };
}

/// <summary>Masks credentials in every line the tool prints.</summary>
public class Redactor
{
    private readonly List<string> _secrets = new();
    public void Add(string? secret) { if (!string.IsNullOrEmpty(secret)) lock (_secrets) _secrets.Add(secret); }
    public string Apply(string text)
    {
        lock (_secrets)
            foreach (var s in _secrets.OrderByDescending(s => s.Length))
                text = text.Replace(s, "***");
        return text;
    }

    /// <summary>Scheme, host, port and path only; a userinfo password is also masked wherever it appears.</summary>
    public string Url(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "<invalid url>";
        if (u.UserInfo.Length > 0)
        {
            var i = u.UserInfo.IndexOf(':');
            if (i >= 0)
            {
                Add(u.UserInfo[(i + 1)..]);
                Add(Uri.UnescapeDataString(u.UserInfo[(i + 1)..]));
            }
        }
        return u.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
    }
}
