namespace LiveCompile.Tests;

public class CharsetTests
{
    [Fact]
    public void ToServer_StoresDataBytes_AndReadbackRoundTrips()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var server = ibsCompiler.Database.SybaseExecutor.CharsetToEncoding("cp850")!;
        var data = ibsCompiler.Database.SybaseExecutor.CharsetToEncoding("cp1252")!;
        var sent = ibsCompiler.Database.SybaseExecutor.ToServer("æ é ø", server, data);
        Assert.Equal("µ Ú °", sent);
        // AseClient encodes the batch text in the session charset (cp850): the stored bytes are the cp1252 ones.
        Assert.Equal(new byte[] { 0xE6, 0x20, 0xE9, 0x20, 0xF8 }, server.GetBytes(sent));
        Assert.Equal("æ é ø", ibsCompiler.Database.SybaseExecutor.FromServer(server.GetString(new byte[] { 0xE6, 0x20, 0xE9, 0x20, 0xF8 }), server, data));
    }
}
