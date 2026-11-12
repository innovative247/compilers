using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace CheckApiProcs;

/// <summary>Result of one curl run: exit code, stdout bytes (at most MaxBytes + 1), stderr text.</summary>
internal record CurlResult(int Exit, byte[] Body, string Stderr);

/// <summary>
/// Explicit FTPS through the system curl, for macOS: .NET there cannot resume the control
/// connection's TLS session on the data connection, which the release server requires.
/// Same guarantees as FtpsTransport; credentials go to curl on stdin, never on the command line.
/// </summary>
public sealed class CurlTransport : IReleaseTransport
{
    public const string CurlPath = "/usr/bin/curl";

    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _timeout;
    private readonly Func<IReadOnlyList<string>, string, CancellationToken, Task<CurlResult>> _run;

    public CurlTransport(string host = ReleaseLocations.Host, int port = ReleaseLocations.Port, TimeSpan? timeout = null)
        : this(host, port, timeout, RunCurlAsync) { }

    internal CurlTransport(string host, int port, TimeSpan? timeout,
        Func<IReadOnlyList<string>, string, CancellationToken, Task<CurlResult>> run)
    {
        _host = host;
        _port = port;
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
        _run = run;
    }

    public async Task<string> DownloadAsync(string remotePath, string user, string password)
    {
        foreach (var v in new[] { remotePath, user, password })
            if (v.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new ReleaseDownloadException("FTP path or credentials contain a line break");
        if (!Ascii.IsValid(user) || !Ascii.IsValid(password))
            throw new ReleaseDownloadException($"{Options.FtpUserVar} or {Options.FtpPasswordVar} contains a non-ASCII character");
        // curl splits "user:pass" at the first colon.
        if (user.Contains(':'))
            throw new ReleaseDownloadException($"{Options.FtpUserVar} contains a colon");

        CurlResult r;
        using (var cts = new CancellationTokenSource(_timeout))
        {
            try { r = await _run(Args(remotePath), Config(user, password), cts.Token); }
            catch (OperationCanceledException) { throw new ReleaseDownloadException($"timed out after {_timeout.TotalSeconds:0}s"); }
        }
        return Result(r, password);
    }

    internal IReadOnlyList<string> Args(string remotePath)
    {
        var path = string.Join('/', remotePath.TrimStart('/').Split('/').Select(Uri.EscapeDataString));
        return new[]
        {
            "-q",                       // first: ignore ~/.curlrc, which could add -k
            "--noproxy", "*",           // an http proxy would get the login in plaintext
            "-sS",
            "--ssl-reqd",
            "--tlsv1.2", "--tls-max", "1.2",
            "--ftp-pasv", "--ftp-skip-pasv-ip",
            "--max-time", ((int)Math.Ceiling(_timeout.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--max-filesize", FtpsTransport.MaxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            // One absolute RETR from /, like FtpsTransport; a CWD into a missing folder is not a 550 to curl.
            "--ftp-method", "nocwd",
            "-K", "-",
            $"ftp://{_host}:{_port}/%2F{path}",
        };
    }

    /// <summary>curl config-file syntax: inside double quotes, backslash and double quote are escaped.</summary>
    internal static string Config(string user, string password) =>
        $"user = \"{Escape(user)}:{Escape(password)}\"\n";

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    internal static string Result(CurlResult r, string password)
    {
        if (r.Exit != 0)
        {
            var err = Redact(r.Stderr.Trim(), password);
            // 78: RETR answered 550. 9: a denied CWD, defensive only since nocwd sends none.
            if (r.Exit is 78 or 9)
                throw new ReleaseNotFoundException($"not published on the release server ({err})");
            if (r.Exit == 63)
                throw new ReleaseDownloadException($"declaration exceeds {FtpsTransport.MaxBytes / (1024 * 1024)} MiB");
            throw new ReleaseDownloadException($"curl exit {r.Exit}" + (err.Length > 0 ? $" ({err})" : ""));
        }
        var body = r.Body;
        if (body.Length > FtpsTransport.MaxBytes)
            throw new ReleaseDownloadException($"declaration exceeds {FtpsTransport.MaxBytes / (1024 * 1024)} MiB");
        var start = body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(body, start, body.Length - start);
    }

    private static string Redact(string text, string password) =>
        password.Length == 0 ? text : text.Replace(Escape(password), "***").Replace(password, "***");

    /// <summary>Variables that would swap the trust store or TLS backend, or write a TLS key log.</summary>
    internal static readonly string[] ScrubbedEnv =
        { "CURL_CA_BUNDLE", "SSL_CERT_FILE", "SSL_CERT_DIR", "SSLKEYLOGFILE", "CURL_SSL_BACKEND" };

    internal static ProcessStartInfo StartInfo(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(CurlPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var v in ScrubbedEnv) psi.Environment.Remove(v);
        return psi;
    }

    private static async Task<CurlResult> RunCurlAsync(IReadOnlyList<string> args, string stdin, CancellationToken ct)
    {
        using var p = new Process { StartInfo = StartInfo(args) };
        try { p.Start(); }
        catch (Win32Exception ex) { throw new ReleaseDownloadException($"cannot run {CurlPath}: {ex.Message}"); }

        try
        {
            await p.StandardInput.WriteAsync(stdin.AsMemory(), ct);
            p.StandardInput.Close();
            var err = p.StandardError.ReadToEndAsync(ct);
            var body = await ReadCappedAsync(p.StandardOutput.BaseStream, ct);
            if (body.Length > FtpsTransport.MaxBytes) p.Kill(entireProcessTree: true);
            await p.WaitForExitAsync(ct);
            // A killed over-cap run reports the cap, not the kill.
            return new CurlResult(body.Length > FtpsTransport.MaxBytes ? 0 : p.ExitCode, body, await err);
        }
        catch (OperationCanceledException)
        {
            Kill(p);
            throw;
        }
        // EPIPE when curl exits before reading stdin, or a broken pipe on its output.
        catch (IOException ex)
        {
            Kill(p);
            throw new ReleaseDownloadException(ex.Message);
        }
    }

    private static void Kill(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    /// <summary>Reads at most MaxBytes + 1 bytes, enough to tell the cap was exceeded.</summary>
    private static async Task<byte[]> ReadCappedAsync(Stream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while (ms.Length <= FtpsTransport.MaxBytes && (n = await s.ReadAsync(buf, ct)) > 0)
            ms.Write(buf, 0, (int)Math.Min(n, FtpsTransport.MaxBytes + 1 - ms.Length));
        return ms.ToArray();
    }
}
