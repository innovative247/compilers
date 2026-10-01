using ibsCompiler.Configuration;
using SqlTest;

namespace SqlTest.Tests;

public class SpikeAsProfileTests
{
    [Fact]
    public void PrecheckAsProfile_refuses_from_settings_alone_and_passes_a_same_server_alias() => TestScratch.Use(root =>
    {
        var settings = Path.Combine(root, "settings.json");
        File.WriteAllText(settings, "{\"Profiles\":{" +
            "\"RUN\":{\"Host\":\"h\",\"Port\":5000,\"Password\":\"x\"}," +
            "\"RO\":{\"Host\":\"h\",\"Port\":5000,\"User\":\"ro\",\"Password\":\"y\"}," +
            "\"PG\":{\"Host\":\"h\",\"Port\":5000,\"PLATFORM\":\"POSTGRES\",\"Password\":\"y\"}," +
            "\"OTHERHOST\":{\"Host\":\"k\",\"Port\":5000,\"Password\":\"y\"}," +
            "\"OTHERPORT\":{\"Host\":\"h\",\"Port\":5001,\"Password\":\"y\"}," +
            "\"NOPASS\":{\"Host\":\"h\",\"Port\":5000,\"Password\":\"\"}}}");
        var mgr = new ProfileManager(settings);

        Runner.PrecheckAsProfile(mgr, "RO", "RUN");
        string Refusal(string alias) => Assert.Throws<ArgumentException>(() => Runner.PrecheckAsProfile(mgr, alias, "RUN")).Message;
        Assert.Equal("unknown --as NOPE: no such profile", Refusal("NOPE"));
        Assert.Equal("--as PG is POSTGRES but profile RUN is SYBASE; --as must reach the same server", Refusal("PG"));
        Assert.Equal("--as OTHERHOST is k:5000 but profile RUN is h:5000; --as must reach the same server", Refusal("OTHERHOST"));
        Assert.Equal("--as OTHERPORT is h:5001 but profile RUN is h:5000; --as must reach the same server", Refusal("OTHERPORT"));
        Assert.Equal("--as NOPASS has no stored password; --as never prompts", Refusal("NOPASS"));
    });
}
