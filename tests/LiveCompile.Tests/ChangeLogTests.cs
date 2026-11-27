using ibsCompiler;

namespace LiveCompile.Tests;

public class ChangeLogTests
{
    [Fact]
    public void MsgGuiRow_AndDescriptionFits()
    {
        var desc = LiveChangeLog.Description("gui messages", null, Enumerable.Range(10000, 100).Select(i => i.ToString()).ToList());
        Assert.Equal(200, desc.Length);
        Assert.StartsWith("gui messages: 10000,10001", desc);
        Assert.Equal("options MOD 53492", LiveChangeLog.Description("options", "53492", new List<string>()));
        var lines = string.Join("\n", LiveChangeLog.MsgGuiLines("it's"));
        Assert.Contains("id = 'gclog2'", lines);
        Assert.Contains("ba_gen_chg_log_new '0', @msg, 'MSG_GUI'", lines);
        Assert.Contains("' it''s', 1, 255)", lines);
    }
}
