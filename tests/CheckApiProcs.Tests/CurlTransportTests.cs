using System.Text;
using CheckApiProcs;

namespace CheckApiProcs.Tests;

public class CurlTransportTests
{
    private const string Password = "p\"a\\ss-s3cret";

    private sealed class FakeCurl
    {
        public IReadOnlyList<string>? Args;
        public string? Stdin;
        public Func<CancellationToken, Task<CurlResult>> Respond = _ => Task.FromResult(new CurlResult(0, Encoding.UTF8.GetBytes("{}"), ""));

        public CurlTransport Transport(TimeSpan? timeout = null) =>
            new("ftp.example.test", 990, timeout, (a, s, ct) => { Args = a; Stdin = s; return Respond(ct); });
    }

    [Fact]
    public async Task Args_Exact_NoCredentials_NoInsecure()
    {
        var f = new FakeCurl();
        await f.Transport().DownloadAsync("/download/x/a-v95.24.0+b.json", "releases", Password);
        Assert.Equal(new[]
        {
            "-q", "--noproxy", "*", "-sS", "--ssl-reqd", "--tlsv1.2", "--tls-max", "1.2",
            "--ftp-pasv", "--ftp-skip-pasv-ip", "--max-time", "60", "--max-filesize", "16777216",
            "--ftp-method", "nocwd", "-K", "-",
            "ftp://ftp.example.test:990/%2Fdownload/x/a-v95.24.0%2Bb.json",
        }, f.Args);
    }

    [Fact]
    public void StartInfo_ScrubsTlsOverrideEnv()
    {
        // A literal list, so a name dropped from ScrubbedEnv fails here instead of passing vacuously.
        var names = new[] { "CURL_CA_BUNDLE", "SSL_CERT_FILE", "SSL_CERT_DIR", "SSLKEYLOGFILE", "CURL_SSL_BACKEND" };
        var saved = names.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        foreach (var v in names) Environment.SetEnvironmentVariable(v, "/tmp/evil");
        try
        {
            var psi = CurlTransport.StartInfo(new[] { "-q" });
            Assert.Equal(CurlTransport.CurlPath, psi.FileName);
            foreach (var v in names) Assert.False(psi.Environment.ContainsKey(v), v);
        }
        finally
        {
            foreach (var (v, old) in saved) Environment.SetEnvironmentVariable(v, old);
        }
    }

    [Fact]
    public async Task Stdin_ConfigEscapesBackslashAndQuote()
    {
        var f = new FakeCurl();
        await f.Transport().DownloadAsync("/x.json", "rel\\\"u", Password);
        Assert.Equal("user = \"rel\\\\\\\"u:p\\\"a\\\\ss-s3cret\"\n", f.Stdin);
    }

    [Theory]
    [InlineData("/x\r\nDELE y", "u", Password)]
    [InlineData("/x.json", "ué", Password)]
    [InlineData("/x.json", "u", "päss")]
    [InlineData("/x.json", "u:v", Password)]
    public async Task BadInput_ThrowsWithoutRunning(string path, string user, string password)
    {
        var f = new FakeCurl();
        var ex = await Assert.ThrowsAsync<ReleaseDownloadException>(() => f.Transport().DownloadAsync(path, user, password));
        Assert.DoesNotContain(password, ex.Message);
        Assert.Null(f.Args);
    }

    [Theory]
    [InlineData(78, "curl: (78) The file does not exist")]
    [InlineData(9, "curl: (9) Server denied you to change to the given directory")]
    public async Task NotFound(int exit, string stderr)
    {
        var f = new FakeCurl { Respond = _ => Task.FromResult(new CurlResult(exit, Array.Empty<byte>(), stderr)) };
        var ex = await Assert.ThrowsAsync<ReleaseNotFoundException>(() => f.Transport().DownloadAsync("/x.json", "u", Password));
        Assert.Contains("not published", ex.Message);
    }

    [Fact]
    public async Task Failure_StderrAndExitCode_PasswordRedacted()
    {
        var stderr = $"curl: (67) Access denied: 530 bad password {Password} / p\\\"a\\\\ss-s3cret\n";
        var f = new FakeCurl { Respond = _ => Task.FromResult(new CurlResult(67, Array.Empty<byte>(), stderr)) };
        var ex = await Assert.ThrowsAsync<ReleaseDownloadException>(() => f.Transport().DownloadAsync("/x.json", "u", Password));
        Assert.Contains("curl exit 67", ex.Message);
        Assert.Contains("530", ex.Message);
        Assert.DoesNotContain("s3cret", ex.Message);
    }

    [Fact]
    public async Task ConnectErrorMentioning550_IsNotNotFound()
    {
        var f = new FakeCurl { Respond = _ => Task.FromResult(new CurlResult(28, Array.Empty<byte>(), "curl: (28) Failed to connect to ftp.example.test port 990 after 550 ms: Timeout was reached")) };
        var ex = await Assert.ThrowsAsync<ReleaseDownloadException>(() => f.Transport().DownloadAsync("/x.json", "u", Password));
        Assert.Contains("curl exit 28", ex.Message);
    }

    [Fact]
    public async Task MaxFilesizeExit_Rejected()
    {
        var f = new FakeCurl { Respond = _ => Task.FromResult(new CurlResult(63, Array.Empty<byte>(), "curl: (63) Maximum file size exceeded")) };
        var ex = await Assert.ThrowsAsync<ReleaseDownloadException>(() => f.Transport().DownloadAsync("/x.json", "u", Password));
        Assert.Contains("exceeds 16 MiB", ex.Message);
    }

    [Fact]
    public async Task BodyOverCap_Rejected()
    {
        var f = new FakeCurl { Respond = _ => Task.FromResult(new CurlResult(0, new byte[FtpsTransport.MaxBytes + 1], "")) };
        var ex = await Assert.ThrowsAsync<ReleaseDownloadException>(() => f.Transport().DownloadAsync("/x.json", "u", Password));
        Assert.Contains("exceeds 16 MiB", ex.Message);
    }

    [Fact]
    public async Task Bom_Stripped()
    {
        var body = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"a\":1}")).ToArray();
        var f = new FakeCurl { Respond = _ => Task.FromResult(new CurlResult(0, body, "")) };
        Assert.Equal("{\"a\":1}", await f.Transport().DownloadAsync("/x.json", "u", Password));
    }

    [Fact]
    public async Task Cancelled_TimesOut()
    {
        var f = new FakeCurl { Respond = async ct => { await Task.Delay(Timeout.Infinite, ct); return null!; } };
        var ex = await Assert.ThrowsAsync<ReleaseDownloadException>(() => f.Transport(TimeSpan.FromMilliseconds(200)).DownloadAsync("/x.json", "u", Password));
        Assert.Contains("timed out", ex.Message);
    }
}
