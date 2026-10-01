using AdoNetCore.AseClient;

namespace SqlTest;

/// <summary>A command cancelled because it ran past its deadline.</summary>
public sealed class CommandTimeoutException : TimeoutException
{
    public CommandTimeoutException(double seconds, Exception? inner)
        : base($"command timeout: exceeded {seconds:0.###}s and was cancelled", inner) { }
}

/// <summary>
/// Client-side command deadline. The bundled AseClient stores CommandTimeout but never
/// enforces it; Cancel() does work and leaves the connection and its transaction usable.
/// </summary>
internal static class CommandDeadline
{
    /// <summary>Wrap the whole read loop in <paramref name="execute"/>: a cancelled reader ends early without throwing.</summary>
    public static T Run<T>(AseCommand cmd, int seconds, Func<T> execute) =>
        RunWithCancel(Limit(seconds), cmd.Cancel, execute);

    // Timer rejects due times above 0xFFFFFFFE ms; anything that large means no limit.
    private const long MaxTimerMs = 0xFFFFFFFE;

    /// <summary>Null means no limit.</summary>
    internal static TimeSpan? Limit(int seconds) =>
        seconds <= 0 || seconds * 1000L > MaxTimerMs ? null : TimeSpan.FromSeconds(seconds);

    internal static T RunWithCancel<T>(TimeSpan? limit, Action cancel, Func<T> execute) =>
        limit is { } l ? RunWithCancel(l, cancel, execute) : execute();

    public static void Run(AseCommand cmd, int seconds, Action execute) =>
        Run(cmd, seconds, () => { execute(); return 0; });

    internal static T RunWithCancel<T>(TimeSpan limit, Action cancel, Func<T> execute)
    {
        var gate = new object();
        bool done = false, fired = false;
        // Cancel under the gate: from Finish() on it can no longer fire. A fire between execute
        // returning and Finish() still reports TIMEOUT; that window is microseconds and accepted.
        using var timer = new Timer(_ =>
        {
            lock (gate)
            {
                if (done) return;
                fired = true;
                try { cancel(); } catch { }
            }
        }, null, limit, Timeout.InfiniteTimeSpan);

        T result;
        try
        {
            result = execute();
        }
        catch (Exception ex)
        {
            if (Finish()) throw new CommandTimeoutException(limit.TotalSeconds, ex);
            throw;
        }
        if (Finish()) throw new CommandTimeoutException(limit.TotalSeconds, null);
        return result;

        bool Finish() { lock (gate) { done = true; return fired; } }
    }
}
