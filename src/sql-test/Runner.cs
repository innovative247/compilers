using System.Diagnostics;
using System.Text;
using AdoNetCore.AseClient;
using ibsCompiler.Configuration;

namespace SqlTest;

/// <summary>
/// Discovers and executes SQL unit-test procs.
///
/// Classification model (priority: FAIL > SKIP > ERROR > PASS):
/// - Tests signal FAIL via `raiserror 50001` (registered with sp_addmessage on the
///   server; the preceding `print 'FAIL: ...'` line is captured via InfoMessage and
///   used as the failure message).
/// - Tests signal SKIP via `raiserror 50002` (same pattern).
/// - Any other severity-11+ Sybase error is classified ERROR.
/// - No exception thrown means PASS.
///
/// Each test runs inside `begin tran ... rollback tran` so fixture mutations to
/// the production database are reversed at end-of-test.
/// </summary>
public class Runner
{
    private const int FailErrorNumber = 50001;
    private const int SkipErrorNumber = 50002;

    private readonly ResolvedProfile _profile;
    private readonly Options _opts;

    public Runner(ResolvedProfile profile, Options opts)
    {
        _profile = profile;
        _opts = opts;
        // Sybase TDS may negotiate a non-UTF8 charset (e.g. cp850); without this
        // the InfoMessage stream throws "unsupported charset" on connection.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public List<string> Discover()
    {
        using var conn = new AseConnection(BuildConnectionString(_opts.Database));
        conn.Open();
        using var cmd = new AseCommand(
            "select name from sysobjects " +
            "where type = 'P' and name like @pat escape '\\' " +
            "order by name",
            conn);
        cmd.Parameters.Add("@pat", _opts.Pattern);

        var names = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    public TestResult RunOne(string name)
    {
        var stopwatch = Stopwatch.StartNew();
        var messages = new List<AseError>();

        using var conn = new AseConnection(BuildConnectionString(_opts.Database));

        conn.InfoMessage += (_, e) =>
        {
            foreach (AseError err in e.Errors)
            {
                if (err.Severity >= 11) continue; // exceptions handle these
                var msg = err.Message ?? "";
                if (msg.StartsWith("Changed client character set") ||
                    msg.StartsWith("Changed database context") ||
                    msg.StartsWith("Changed language setting"))
                    continue;
                messages.Add(err);
            }
        };

        try
        {
            conn.Open();
        }
        catch (Exception ex)
        {
            return new TestResult(name, Outcome.ERROR,
                $"connection failed: {ex.Message}",
                stopwatch.Elapsed.TotalSeconds, ex.ToString());
        }

        using var tx = conn.BeginTransaction();
        try
        {
            using var cmd = new AseCommand($"exec {name}", conn, tx);
            cmd.CommandTimeout = _opts.TimeoutSeconds;
            cmd.ExecuteNonQuery();
            try { tx.Rollback(); } catch { /* tx already gone */ }

            stopwatch.Stop();
            return new TestResult(name, Outcome.PASS, "",
                stopwatch.Elapsed.TotalSeconds, JoinMessages(messages));
        }
        catch (AseException ex)
        {
            try { tx.Rollback(); } catch { /* server may have already rolled back */ }
            stopwatch.Stop();
            return Classify(name, ex, messages, stopwatch.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            try { tx.Rollback(); } catch { }
            stopwatch.Stop();
            // Generic timeout / connection abort
            var outcome = ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                ? Outcome.TIMEOUT
                : Outcome.ERROR;
            return new TestResult(name, outcome, ex.Message,
                stopwatch.Elapsed.TotalSeconds, ex.ToString());
        }
    }

    private TestResult Classify(string name, AseException ex, List<AseError> info, double duration)
    {
        // Find user-defined FAIL/SKIP markers among the raised errors first.
        foreach (AseError err in ex.Errors)
        {
            if (err.MessageNumber == FailErrorNumber)
            {
                var msg = LastMatching(info, "FAIL:") ?? "FAIL";
                return new TestResult(name, Outcome.FAIL, msg, duration, JoinMessages(info, ex.Errors));
            }
            if (err.MessageNumber == SkipErrorNumber)
            {
                var msg = LastMatching(info, "SKIP:") ?? "SKIP";
                return new TestResult(name, Outcome.SKIP, msg, duration, JoinMessages(info, ex.Errors));
            }
        }

        // Genuine Sybase error (sev 16+ not from our raiserror)
        var first = ex.Errors.Count > 0 ? ex.Errors[0] : null;
        var headline = first != null
            ? $"Msg {first.MessageNumber}, Level {first.Severity}: {first.Message}"
            : ex.Message;
        return new TestResult(name, Outcome.ERROR, headline, duration, JoinMessages(info, ex.Errors));
    }

    private static string? LastMatching(List<AseError> messages, string prefix)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var m = (messages[i].Message ?? "").TrimEnd();
            if (m.StartsWith(prefix)) return m;
        }
        return null;
    }

    private static string JoinMessages(List<AseError> info, AseErrorCollection? errors = null)
    {
        var sb = new StringBuilder();
        foreach (var m in info)
            sb.AppendLine((m.Message ?? "").TrimEnd());
        if (errors != null)
            foreach (AseError e in errors)
                sb.AppendLine($"Msg {e.MessageNumber}, Level {e.Severity}: {(e.Message ?? "").TrimEnd()}");
        return sb.ToString();
    }

    private string BuildConnectionString(string database)
    {
        var sb = new StringBuilder();
        sb.Append($"Data Source={_profile.Host}");
        sb.Append($";Port={_profile.Port}");
        sb.Append($";User ID={_profile.User}");
        sb.Append($";Password={_profile.Pass}");
        if (!string.IsNullOrEmpty(database))
            sb.Append($";Database={database}");
        sb.Append(";Pooling=false");
        return sb.ToString();
    }
}
