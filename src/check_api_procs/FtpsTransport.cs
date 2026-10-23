using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;

namespace CheckApiProcs;

/// <summary>
/// Minimal explicit-FTPS client: AUTH TLS before login, TLS 1.2, protected data channel.
/// It never sends USER or PASS, or opens a data channel, without TLS.
/// </summary>
public sealed class FtpsTransport : IReleaseTransport
{
    public const int MaxBytes = 16 * 1024 * 1024;
    private const int MaxLine = 8 * 1024;

    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _timeout;
    private readonly RemoteCertificateValidationCallback? _certCheck;

    /// <summary>certCheck null: the system's certificate validation.</summary>
    public FtpsTransport(string host = ReleaseLocations.Host, int port = ReleaseLocations.Port,
        TimeSpan? timeout = null, RemoteCertificateValidationCallback? certCheck = null)
    {
        _host = host;
        _port = port;
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
        _certCheck = certCheck;
    }

    public async Task<string> DownloadAsync(string remotePath, string user, string password)
    {
        foreach (var v in new[] { remotePath, user, password })
            if (v.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new ReleaseDownloadException("FTP path or credentials contain a line break");
        // Commands go out as ASCII; anything else would be sent mangled.
        if (!Ascii.IsValid(user) || !Ascii.IsValid(password))
            throw new ReleaseDownloadException($"{Options.FtpUserVar} or {Options.FtpPasswordVar} contains a non-ASCII character");

        using var cts = new CancellationTokenSource(_timeout);
        try
        {
            return await RunAsync(remotePath, user, password, cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new ReleaseDownloadException($"timed out after {_timeout.TotalSeconds:0}s");
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException)
        {
            throw new ReleaseDownloadException(ex.Message);
        }
    }

    private async Task<string> RunAsync(string path, string user, string password, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(_host, _port, ct);
        Stream ctl = tcp.GetStream();

        Expect(await ReadReplyAsync(ctl, ct), "greeting", 220);
        await SendAsync(ctl, "AUTH TLS", ct);
        var auth = await ReadReplyAsync(ctl, ct);
        if (auth.Code != 234)
            throw new ReleaseDownloadException($"server refused AUTH TLS ({auth.Text}); a plaintext login is never attempted");

        await using var tls = new SslStream(ctl, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsClientAsync(TlsOptions(), ct);
        ctl = tls;

        await SendAsync(ctl, "USER " + user, ct);
        var u = await ReadReplyAsync(ctl, ct);
        if (u.Code == 331)
        {
            await SendAsync(ctl, "PASS " + password, ct);
            u = await ReadReplyAsync(ctl, ct);
        }
        if (u.Code != 230) throw new ReleaseDownloadException($"login failed ({u.Text})");

        await SendAsync(ctl, "PBSZ 0", ct);
        Expect(await ReadReplyAsync(ctl, ct), "PBSZ 0", 200);
        await SendAsync(ctl, "PROT P", ct);
        Expect(await ReadReplyAsync(ctl, ct), "PROT P", 200);
        await SendAsync(ctl, "TYPE I", ct);
        Expect(await ReadReplyAsync(ctl, ct), "TYPE I", 200);

        int dataPort;
        await SendAsync(ctl, "EPSV", ct);
        var ep = await ReadReplyAsync(ctl, ct);
        if (ep.Code == 229) dataPort = ParseEpsv(ep.Text);
        else
        {
            await SendAsync(ctl, "PASV", ct);
            var pv = await ReadReplyAsync(ctl, ct);
            Expect(pv, "PASV", 227);
            dataPort = ParsePasv(pv.Text);
        }

        // The PASV address is ignored; the control socket's peer, not a fresh DNS answer, gets the data channel.
        using var dataTcp = new TcpClient();
        await dataTcp.ConnectAsync(((IPEndPoint)tcp.Client.RemoteEndPoint!).Address, dataPort, ct);

        await SendAsync(ctl, "RETR " + path, ct);
        var retr = await ReadReplyAsync(ctl, ct);
        if (retr.Code == 550) throw new ReleaseNotFoundException($"not published on the release server ({retr.Text})");
        if (retr.Code != 150 && retr.Code != 125)
            throw new ReleaseDownloadException($"RETR failed ({retr.Text})");

        byte[] body;
        await using (var data = new SslStream(dataTcp.GetStream(), leaveInnerStreamOpen: false))
        {
            await data.AuthenticateAsClientAsync(TlsOptions(), ct);
            body = await ReadCappedAsync(data, ct);
        }

        var done = await ReadReplyAsync(ctl, ct);
        if (done.Code != 226 && done.Code != 250)
            throw new ReleaseDownloadException($"transfer not completed ({done.Text})");

        try { await SendAsync(ctl, "QUIT", ct); } catch (IOException) { }

        var start = body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(body, start, body.Length - start);
    }

    private SslClientAuthenticationOptions TlsOptions() => new()
    {
        TargetHost = _host,
        EnabledSslProtocols = SslProtocols.Tls12,
        RemoteCertificateValidationCallback = _certCheck,
    };

    private async Task<byte[]> ReadCappedAsync(Stream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            if (ms.Length + n > MaxBytes)
                throw new ReleaseDownloadException($"declaration exceeds {MaxBytes / (1024 * 1024)} MiB");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    private void Expect(Reply r, string what, int code)
    {
        if (r.Code != code) throw new ReleaseDownloadException($"{what} failed ({r.Text})");
    }

    internal record Reply(int Code, string Text);

    private static async Task SendAsync(Stream s, string line, CancellationToken ct)
    {
        await s.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"), ct);
        await s.FlushAsync(ct);
    }

    /// <summary>Reads one reply, following a multi-line "nnn-" reply to its "nnn " end.</summary>
    internal static async Task<Reply> ReadReplyAsync(Stream s, CancellationToken ct)
    {
        var first = await ReadLineAsync(s, ct);
        if (first.Length < 3 || !int.TryParse(first[..3], NumberStyles.None, CultureInfo.InvariantCulture, out var code))
            throw new IOException("malformed FTP reply");
        if (first.Length > 3 && first[3] == '-')
        {
            var end = first[..3] + " ";
            string line;
            do line = await ReadLineAsync(s, ct); while (!line.StartsWith(end, StringComparison.Ordinal));
            return new Reply(code, line.Trim());
        }
        return new Reply(code, first.Trim());
    }

    // Byte at a time so nothing is buffered past the reply when the stream switches to TLS.
    private static async Task<string> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await s.ReadAsync(one, ct) == 0) throw new IOException("connection closed by the FTP server");
            if (one[0] == '\n') return sb.ToString().TrimEnd('\r');
            if (sb.Length >= MaxLine) throw new IOException("FTP reply line too long");
            sb.Append((char)one[0]);
        }
    }

    private static readonly Regex Epsv = new(@"\(\|\|\|(\d{1,5})\|\)", RegexOptions.CultureInvariant);
    private static readonly Regex Pasv = new(@"(\d{1,3}),(\d{1,3}),(\d{1,3}),(\d{1,3}),(\d{1,3}),(\d{1,3})", RegexOptions.CultureInvariant);

    internal static int ParseEpsv(string text)
    {
        var m = Epsv.Match(text);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var p) || p is < 1 or > 65535)
            throw new IOException("malformed EPSV reply");
        return p;
    }

    internal static int ParsePasv(string text)
    {
        var m = Pasv.Match(text);
        if (!m.Success) throw new IOException("malformed PASV reply");
        var o = new int[7];
        for (var i = 1; i <= 6; i++)
            if ((o[i] = int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture)) > 255) throw new IOException("malformed PASV reply");
        var p = o[5] * 256 + o[6];
        if (p is < 1 or > 65535) throw new IOException("malformed PASV reply");
        return p;
    }
}
