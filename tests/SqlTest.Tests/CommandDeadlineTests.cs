using SqlTest;

namespace SqlTest.Tests;

public class CommandDeadlineTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);

    [Fact]
    public void Completes_WithinLimit_ReturnsResult_NoCancel()
    {
        var cancelled = false;
        var r = CommandDeadline.RunWithCancel(TimeSpan.FromSeconds(30), () => cancelled = true, () => 42);
        Assert.Equal(42, r);
        Assert.False(cancelled);
    }

    // The deadline passing after execute returned must neither cancel nor throw.
    [Fact]
    public void DeadlinePassesAfterReturn_NoCancel_NoThrow()
    {
        var cancelled = false;
        Assert.Equal(7, CommandDeadline.RunWithCancel(TimeSpan.FromMilliseconds(20), () => cancelled = true, () => 7));
        Thread.Sleep(200);
        Assert.False(cancelled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4_294_968)]      // above the Timer ceiling of 0xFFFFFFFE ms
    [InlineData(int.MaxValue)]
    public void NoLimit_Values(int seconds) => Assert.Null(CommandDeadline.Limit(seconds));

    [Theory]
    [InlineData(1)]
    [InlineData(4_294_967)]      // last value the Timer accepts
    public void Limit_Values(int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), CommandDeadline.Limit(seconds));

    [Fact]
    public void NoLimit_RunsWithoutTimer()
    {
        var cancelled = false;
        Assert.Equal(3, CommandDeadline.RunWithCancel(CommandDeadline.Limit(0), () => cancelled = true, () => 3));
        Assert.False(cancelled);
    }

    [Fact]
    public void Overrun_ThatThrowsOnCancel_IsTimeout()
    {
        using var cancel = new ManualResetEventSlim();
        var ex = Assert.Throws<CommandTimeoutException>(() => CommandDeadline.RunWithCancel<int>(Short, cancel.Set, () =>
        {
            Assert.True(cancel.Wait(TimeSpan.FromSeconds(10)));
            throw new TaskCanceledException();
        }));
        Assert.IsType<TaskCanceledException>(ex.InnerException);
    }

    // A cancelled reader ends early without throwing; returning its partial rows would be a silent lie.
    [Fact]
    public void Overrun_ThatReturnsNormallyOnCancel_IsTimeout()
    {
        using var cancel = new ManualResetEventSlim();
        Assert.Throws<CommandTimeoutException>(() => CommandDeadline.RunWithCancel(Short, cancel.Set, () =>
        {
            Assert.True(cancel.Wait(TimeSpan.FromSeconds(10)));
            return 1;
        }));
    }

    [Fact]
    public void OwnError_BeforeDeadline_PropagatesUnchanged()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CommandDeadline.RunWithCancel<int>(TimeSpan.FromSeconds(30), () => { }, () => throw new InvalidOperationException()));
    }

    [Fact]
    public void CancelThatThrows_DoesNotCrash_StillTimeout()
    {
        using var fired = new ManualResetEventSlim();
        Assert.Throws<CommandTimeoutException>(() => CommandDeadline.RunWithCancel(Short,
            () => { fired.Set(); throw new InvalidOperationException("driver"); },
            () => { Assert.True(fired.Wait(TimeSpan.FromSeconds(10))); return 0; }));
    }

    [Fact]
    public void TimeoutException_ClassifiesAsTimeout()
    {
        Assert.Equal(Outcome.TIMEOUT, Runner.ClassifyUnexpected(new CommandTimeoutException(5, new TaskCanceledException())));
        Assert.Equal(Outcome.ERROR, Runner.ClassifyUnexpected(new TaskCanceledException()));
        Assert.Equal(Outcome.TIMEOUT, Runner.ClassifyUnexpected(new Exception("Socket Timeout")));
    }

    [Fact]
    public void Message_NamesTheLimit()
    {
        Assert.Contains("5s", new CommandTimeoutException(5, null).Message);
    }
}
