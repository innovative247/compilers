using System.Collections;
using System.Globalization;
using ibsCompiler;

namespace CheckApiProcs;

public static class App
{
    /// <summary>Exit codes: 0 pass, 1 failing verdict or record failure, 2 usage or bad declaration, 3 connectivity/auth/list failure.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter o, TextWriter err, IDictionary env, HttpMessageHandler? handler)
    {
        Options opts;
        try
        {
            var parsed = Options.Parse(args);
            if (parsed == null) { err.WriteLine(Options.Usage); return 0; }
            opts = parsed;
            opts.ResolveCredentials(k => env[k] as string);
        }
        catch (ArgumentException ex)
        {
            err.WriteLine($"check_api_procs: {ex.Message}");
            err.WriteLine(Options.Usage);
            return 2;
        }

        var redact = new Redactor();
        redact.Add(opts.Password);
        var url = redact.Url(opts.Url);

        var source = new FileDeclarationSource(opts.Record ? opts.Names! : opts.Declaration!);
        Declaration decl;
        try { decl = Declaration.Parse(await source.ReadAsync(), recorded: !opts.Record); }
        catch (Exception ex) when (ex is DeclarationException or IOException or UnauthorizedAccessException)
        {
            err.WriteLine(redact.Apply($"check_api_procs: {source.Describe}: {ex.Message}"));
            return 2;
        }
        if (decl.Procedures.Count == 0)
        {
            err.WriteLine($"check_api_procs: {source.Describe}: no procedures");
            return 2;
        }

        var procs = decl.Procedures;
        if (opts.Procs.Count > 0)
        {
            var declared = new HashSet<string>(procs.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            var unknown = opts.Procs.Where(p => !declared.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (unknown.Count > 0)
            {
                err.WriteLine($"check_api_procs: not declared in {source.Describe}: {string.Join(", ", unknown)}");
                return 2;
            }
            var wanted = new HashSet<string>(opts.Procs, StringComparer.OrdinalIgnoreCase);
            procs = procs.Where(p => wanted.Contains(p.Name)).ToList();
        }

        if (handler == null) throw new ArgumentNullException(nameof(handler));
        using var api = new ApiClient(handler, url, TimeSpan.FromSeconds(opts.TimeoutSeconds), VersionInfo.Version, redact);

        string version;
        List<ServerProc> list;
        try
        {
            version = await api.GetVersionAsync();
            await api.AuthAsync(opts.User, opts.Password);
            list = await api.ListAsync();
        }
        catch (Exception ex)
        {
            err.WriteLine($"check_api_procs: {redact.Apply(ex.Message)}");
            return 3;
        }

        if (opts.Record) return await RecordAsync(api, opts, url, version, procs, list, o, err, redact);

        var results = await Checker.RunAsync(api, procs, list, opts.Concurrency);
        var header = new ReportHeader(url, version, decl.Consumer, decl.Release, source.Describe, decl.EngineVersion, decl.RecordedAt);
        var sw = new StringWriter();
        if (opts.Json) Report.WriteJson(sw, header, results);
        else if (opts.Quiet) Report.WriteQuiet(sw, results);
        else Report.WriteText(sw, header, results);
        o.Write(redact.Apply(sw.ToString()));
        return results.Any(r => Verdicts.Failing(r.Verdict)) ? 1 : 0;
    }

    /// <summary>All or nothing: any missing name, map error or stale map writes no file.</summary>
    private static async Task<int> RecordAsync(ApiClient api, Options opts, string url, string version,
        List<DeclProc> names, List<ServerProc> list, TextWriter o, TextWriter err, Redactor redact)
    {
        var fetched = await Checker.FetchAsync(api, names, list, opts.Concurrency);
        var failed = false;
        foreach (var f in fetched.Where(f => f.Listed == null))
        {
            err.WriteLine($"check_api_procs: {f.Proc.Name}: not in /api/v1/api3/list");
            failed = true;
        }
        foreach (var f in fetched.Where(f => f.Error != null))
        {
            err.WriteLine(redact.Apply($"check_api_procs: {f.Proc.Name}: map error: {f.Error}"));
            failed = true;
        }
        // A stale map would bake an out-of-date signature into the release declaration.
        foreach (var f in fetched.Where(f => Checker.IsStale(f.Listed) && f.Error == null))
        {
            err.WriteLine($"check_api_procs: {f.Proc.Name}: MAP-STALE: {Report.StaleHint(f.Proc.Name)}");
            failed = true;
        }
        if (failed)
        {
            err.WriteLine($"check_api_procs: nothing written to {opts.Out}");
            return 1;
        }

        var decl = new Declaration
        {
            Version = Declaration.FormatVersion,
            Consumer = opts.Consumer,
            Release = opts.Release,
            EngineUrl = url,
            EngineVersion = version,
            RecordedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        };
        foreach (var f in fetched)
            decl.Procedures.Add(new DeclProc
            {
                Name = f.Proc.Name,
                Parameters = f.Map!.Parameters.Select(Checker.Convert).ToList(),
                ResultSets = f.Map.ResultSets.Select(s => new DeclResultSet(s.Key, s.Value)).ToList(),
            });
        try { decl.Write(opts.Out!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            err.WriteLine($"check_api_procs: {opts.Out}: {ex.Message}");
            return 1;
        }
        o.WriteLine($"recorded {decl.Procedures.Count} procs from {url} (engine {version}) to {opts.Out}");
        return 0;
    }
}
