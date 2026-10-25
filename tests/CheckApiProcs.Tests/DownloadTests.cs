using System.Collections;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CheckApiProcs;

namespace CheckApiProcs.Tests;

public class DownloadTests : IDisposable
{
    private const string FtpPassword = "ftp-s3cret";
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private static Hashtable Env(bool ftp = true)
    {
        var env = new Hashtable { ["SBN_USERNAME"] = "tester", ["SBN_PASSWORD"] = Run.Password };
        if (ftp)
        {
            env[Options.FtpUserVar] = "releases";
            env[Options.FtpPasswordVar] = FtpPassword;
        }
        return env;
    }

    private static string[] Args(string consumer = "sbn-anywhere", string release = "v95.23.1") =>
        new[] { Run.Url, "--consumer", consumer, "--release", release };

    [Theory]
    [InlineData("sbn-anywhere", "v95.23.1", "/download/common/Releases/SBN_Anywhere/95/sbn-anywhere-v95.23.1.api3-procs.json")]
    [InlineData("sbn-anywhere", "v96.0.0-beta.2", "/download/common/Releases/SBN_Anywhere/96/sbn-anywhere-v96.0.0-beta.2.api3-procs.json")]
    [InlineData("sbn-media", "v95.4.0", "/download/common/Releases/SBN_Media/95/sbn-media-v95.4.0.api3-procs.json")]
    [InlineData("sbn-media", "v95.5.0-beta.1", "/download/common/Releases/SBN_Media/95/sbn-media-v95.5.0-beta.1.api3-procs.json")]
    public void RemotePath(string consumer, string tag, string expect) =>
        Assert.Equal(expect, ReleaseLocations.RemotePath(consumer, tag));

    [Theory]
    [InlineData("--consumer sbn-other --release v95.23.1", "unknown --consumer")]
    [InlineData("--consumer sbn-media --release 95.23.1", "--release must be a release tag")]
    [InlineData("--consumer sbn-media --release v95.23.1/../x", "--release must be a release tag")]
    [InlineData("--consumer sbn-media --release v95.23", "--release must be a release tag")]
    [InlineData("--consumer sbn-media", "required together")]
    [InlineData("--release v95.23.1", "required together")]
    [InlineData("--declaration x.json --consumer sbn-media --release v95.23.1", "cannot be combined")]
    [InlineData("--declaration x.json --release v95.23.1", "cannot be combined")]
    public async Task Usage_Exit2(string line, string expect)
    {
        var api = Std.Api();
        var ftp = new FakeFtp();
        var r = await Run.Raw(api, new[] { Run.Url }.Concat(line.Split(' ')).ToArray(), Env(), ftp);
        Assert.Equal(2, r.Exit);
        Assert.Contains(expect, r.Err);
        Assert.Empty(api.Calls);
        Assert.Empty(ftp.Calls);
    }

    [Theory]
    [InlineData("SBN_RELEASES_FTP_USERNAME")]
    [InlineData("SBN_RELEASES_FTP_PASSWORD")]
    public async Task MissingFtpCredentials_Exit2(string missing)
    {
        var env = Env();
        env.Remove(missing);
        var api = Std.Api();
        var ftp = new FakeFtp();
        var r = await Run.Raw(api, Args(), env, ftp);
        Assert.Equal(2, r.Exit);
        Assert.Contains(Options.FtpUserVar, r.Err);
        Assert.Contains(Options.FtpPasswordVar, r.Err);
        Assert.Contains("--declaration", r.Err);
        Assert.Empty(api.Calls);
        Assert.Empty(ftp.Calls);
    }

    [Fact]
    public async Task LocalDeclaration_NeedsNoFtpEnv()
    {
        var r = await Run.Raw(Std.Api(), new[] { Run.Url, "--declaration", _dir.Write("d.json", Std.Declaration()) }, Env(ftp: false));
        Assert.Equal(0, r.Exit);
    }

    [Fact]
    public async Task Downloaded_Check_Exit0()
    {
        var ftp = new FakeFtp { Respond = _ => Std.Declaration() };
        var r = await Run.Raw(Std.Api(), Args("sbn-media", "v95.23.1"), Env(), ftp);
        Assert.Equal(0, r.Exit);
        Assert.Contains("1 procs: 1 OK", r.Out);
        Assert.Contains("ftp://ftp.innovative247.com:990/download/common/Releases/SBN_Media/95/sbn-media-v95.23.1.api3-procs.json", r.Out);
        var call = Assert.Single(ftp.Calls);
        Assert.Equal("releases", call.User);
        Assert.Equal(FtpPassword, call.Password);
    }

    [Fact]
    public async Task NotPublished_Exit2()
    {
        var api = Std.Api();
        var ftp = new FakeFtp { Respond = _ => throw new ReleaseNotFoundException("not published on the release server (550 No such file)") };
        var r = await Run.Raw(api, Args(), Env(), ftp);
        Assert.Equal(2, r.Exit);
        Assert.Contains("550", r.Err);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task DownloadFailure_Exit3_NoFtpPassword()
    {
        var api = Std.Api();
        var ftp = new FakeFtp { Respond = _ => throw new ReleaseDownloadException($"login failed (530 bad password {FtpPassword})") };
        var r = await Run.Raw(api, Args(), Env(), ftp);
        Assert.Equal(3, r.Exit);
        Assert.Contains("download failed", r.Err);
        Assert.DoesNotContain(FtpPassword, r.Out + r.Err);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task FtpPassword_NeverInOutput()
    {
        var api = Std.Api();
        api.Override = req => req.RequestUri!.AbsolutePath == "/api/v1/list"
            ? FakeApi.Json(HttpStatusCode.InternalServerError, new { error = $"echo {FtpPassword}" })
            : null;
        var ftp = new FakeFtp { Respond = _ => Std.Declaration() };
        var r = await Run.Raw(api, Args(), Env(), ftp);
        Assert.Equal(3, r.Exit);
        Assert.DoesNotContain(FtpPassword, r.Out + r.Err);
    }

    [Fact]
    public async Task Transport_ConnectRefused_DownloadException()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var t = new FtpsTransport("127.0.0.1", port, TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ReleaseDownloadException>(() => t.DownloadAsync("/x.json", "u", FtpPassword));
    }

    [Fact]
    public async Task Transport_AuthTlsRefused_FailsClosed()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        var received = new StringBuilder();
        var server = Task.Run(async () =>
        {
            using var c = await l.AcceptTcpClientAsync();
            var s = c.GetStream();
            var r = new StreamReader(s, Encoding.ASCII);
            var w = new StreamWriter(s, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await w.WriteLineAsync("220-welcome");
            await w.WriteLineAsync("220 ready");
            received.AppendLine(await r.ReadLineAsync());
            await w.WriteLineAsync("502 AUTH not supported");
            // Anything sent after the refusal would land here.
            var next = await r.ReadLineAsync();
            if (next != null) received.AppendLine(next);
        });
        try
        {
            var t = new FtpsTransport("127.0.0.1", port, TimeSpan.FromSeconds(5));
            var ex = await Assert.ThrowsAsync<ReleaseDownloadException>(() => t.DownloadAsync("/x.json", "u", FtpPassword));
            Assert.Contains("AUTH TLS", ex.Message);
            await server.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("AUTH TLS", received.ToString().Trim());
        }
        finally { l.Stop(); }
    }

    [Theory]
    [InlineData("v95.23.1\n")]
    [InlineData("v\u0669\u0665.23.1")]
    [InlineData("v95.\u0662.1")]
    [InlineData("v95.23.1 ")]
    public void Tag_Rejected(string tag) => Assert.False(ReleaseLocations.IsValidTag(tag));

    [Theory]
    [InlineData("v95.23.1")]
    [InlineData("v96.0.0-beta.2")]
    [InlineData("v95.4.0+build.7")]
    public void Tag_Accepted(string tag) => Assert.True(ReleaseLocations.IsValidTag(tag));

    [Theory]
    [InlineData("/x\r\nDELE y", "u", FtpPassword)]
    [InlineData("/x.json", "u\u00e9", FtpPassword)]
    [InlineData("/x.json", "u", "p\u00e4ss")]
    public async Task Transport_BadInput_ThrowsWithoutConnecting(string path, string user, string password)
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try
        {
            var t = new FtpsTransport("127.0.0.1", ((IPEndPoint)l.LocalEndpoint).Port, TimeSpan.FromSeconds(5));
            var ex = await Assert.ThrowsAsync<ReleaseDownloadException>(() => t.DownloadAsync(path, user, password));
            Assert.DoesNotContain(password, ex.Message);
            Assert.False(l.Pending());
        }
        finally { l.Stop(); }
    }

    [Fact]
    public async Task Transport_NonAsciiPassword_NamesVariable()
    {
        var t = new FtpsTransport("127.0.0.1", 1, TimeSpan.FromSeconds(5));
        var ex = await Assert.ThrowsAsync<ReleaseDownloadException>(() => t.DownloadAsync("/x.json", "u", "p\u00e4ss"));
        Assert.Contains(Options.FtpPasswordVar, ex.Message);
        Assert.Contains("non-ASCII", ex.Message);
    }

    [Fact]
    public async Task Transport_NoGreeting_TimesOut()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try
        {
            var t = new FtpsTransport("127.0.0.1", ((IPEndPoint)l.LocalEndpoint).Port, TimeSpan.FromMilliseconds(300));
            var ex = await Assert.ThrowsAsync<ReleaseDownloadException>(() => t.DownloadAsync("/x.json", "u", FtpPassword));
            Assert.Contains("timed out", ex.Message);
        }
        finally { l.Stop(); }
    }

    [Fact]
    public async Task Transport_ProtPRefused_NoTypeOrRetr()
    {
        using var srv = new LoopbackFtps(cmd => cmd == "PROT P" ? "534 PROT P refused" : null);
        var ex = await Assert.ThrowsAsync<ReleaseDownloadException>(() => srv.Client().DownloadAsync("/x.json", "u", FtpPassword));
        Assert.Contains("PROT P", ex.Message);
        var got = await srv.Done();
        Assert.Contains("PROT P", got);
        Assert.DoesNotContain(got, c => c.StartsWith("TYPE") || c.StartsWith("RETR"));
    }

    [Fact]
    public async Task Transport_Retr550_NotFound()
    {
        using var srv = new LoopbackFtps(cmd => cmd.StartsWith("RETR ") ? "550 No such file" : null);
        var ex = await Assert.ThrowsAsync<ReleaseNotFoundException>(() => srv.Client().DownloadAsync("/x.json", "u", FtpPassword));
        Assert.Contains("550", ex.Message);
        Assert.Contains("RETR /x.json", await srv.Done());
    }

    [Fact]
    public async Task Transport_TlsDownload_ReturnsBody()
    {
        using var srv = new LoopbackFtps(_ => null) { Body = Encoding.UTF8.GetBytes("{\"a\":1}") };
        Assert.Equal("{\"a\":1}", await srv.Client().DownloadAsync("/x.json", "u", FtpPassword));
        Assert.Contains("PASS " + FtpPassword, await srv.Done());
    }

    [Fact]
    public async Task Transport_BodyOverCap_Rejected()
    {
        using var srv = new LoopbackFtps(_ => null) { Body = new byte[FtpsTransport.MaxBytes + 1] };
        var ex = await Assert.ThrowsAsync<ReleaseDownloadException>(() => srv.Client().DownloadAsync("/x.json", "u", FtpPassword));
        Assert.Contains($"exceeds {FtpsTransport.MaxBytes / (1024 * 1024)} MiB", ex.Message);
    }

    [Theory]
    [InlineData("229 Entering Extended Passive Mode (|||40123|)", 40123)]
    public void ParseEpsv(string text, int port) => Assert.Equal(port, FtpsTransport.ParseEpsv(text));

    [Theory]
    [InlineData("227 Entering Passive Mode (10,0,0,5,156,123)", 156 * 256 + 123)]
    public void ParsePasv(string text, int port) => Assert.Equal(port, FtpsTransport.ParsePasv(text));

    [Theory]
    [InlineData("227 Entering Passive Mode (10,0,0,5,256,1)")]
    [InlineData("227 Entering Passive Mode (10,0,0,5,1,256)")]
    [InlineData("227 Entering Passive Mode (10,0,999,5,1,1)")]
    public void ParsePasv_OctetOver255(string text) => Assert.Throws<IOException>(() => FtpsTransport.ParsePasv(text));
}

/// <summary>A loopback explicit-FTPS server with a self-signed certificate; override answers a command or returns null for the default.</summary>
internal sealed class LoopbackFtps : IDisposable
{
    private readonly TcpListener _ctl = new(IPAddress.Loopback, 0);
    private readonly TcpListener _data = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2 _cert = SelfSigned();
    private readonly List<string> _received = new();
    private readonly Task _run;

    public byte[] Body { get; init; } = Array.Empty<byte>();

    public LoopbackFtps(Func<string, string?> overrideReply)
    {
        _ctl.Start();
        _data.Start();
        _run = Task.Run(() => Serve(overrideReply));
    }

    public FtpsTransport Client() =>
        new("127.0.0.1", ((IPEndPoint)_ctl.LocalEndpoint).Port, TimeSpan.FromSeconds(10), (_, _, _, _) => true);

    /// <summary>The commands received, once the client has closed the control connection.</summary>
    public async Task<List<string>> Done()
    {
        await _run.WaitAsync(TimeSpan.FromSeconds(10));
        lock (_received) return _received.ToList();
    }

    // Exported and reloaded: some platforms refuse an ephemeral key for a TLS server.
    private static X509Certificate2 SelfSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        using var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return new X509Certificate2(c.Export(X509ContentType.Pfx));
    }

    private async Task<SslStream> Tls(Stream s)
    {
        var tls = new SslStream(s, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = _cert,
            EnabledSslProtocols = SslProtocols.Tls12,
        });
        return tls;
    }

    private async Task Serve(Func<string, string?> overrideReply)
    {
        try
        {
            using var c = await _ctl.AcceptTcpClientAsync();
            Stream s = c.GetStream();
            var r = new StreamReader(s, Encoding.ASCII);
            var w = new StreamWriter(s, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await w.WriteLineAsync("220 ready");
            Record(await r.ReadLineAsync());
            await w.WriteLineAsync("234 go ahead");
            await using var tls = await Tls(s);
            r = new StreamReader(tls, Encoding.ASCII);
            w = new StreamWriter(tls, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            string? cmd;
            while ((cmd = await r.ReadLineAsync()) != null)
            {
                Record(cmd);
                var o = overrideReply(cmd);
                if (o != null) { await w.WriteLineAsync(o); continue; }
                var verb = cmd.Split(' ')[0];
                switch (verb)
                {
                    case "USER": await w.WriteLineAsync("331 password"); break;
                    case "PASS": await w.WriteLineAsync("230 logged in"); break;
                    case "EPSV": await w.WriteLineAsync($"229 ok (|||{((IPEndPoint)_data.LocalEndpoint).Port}|)"); break;
                    case "RETR":
                        await w.WriteLineAsync("150 opening");
                        try
                        {
                            using var d = await _data.AcceptTcpClientAsync();
                            await using var dt = await Tls(d.GetStream());
                            await dt.WriteAsync(Body);
                            await dt.ShutdownAsync();
                        }
                        catch (IOException) { }
                        await w.WriteLineAsync("226 done");
                        break;
                    case "QUIT": await w.WriteLineAsync("221 bye"); return;
                    default: await w.WriteLineAsync("200 ok"); break;
                }
            }
        }
        catch (IOException) { }
        catch (AuthenticationException) { }
    }

    private void Record(string? line)
    {
        if (line != null) lock (_received) _received.Add(line);
    }

    public void Dispose()
    {
        _ctl.Stop();
        _data.Stop();
        _cert.Dispose();
    }
}
