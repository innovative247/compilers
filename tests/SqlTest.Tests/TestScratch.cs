namespace SqlTest.Tests;

// Scratch lives under the repo, never the system temp dir; a failing test leaves its folder for inspection.
internal static class TestScratch
{
    private static readonly string RunDir = Path.Combine(RepoRoot(), ".test-scratch",
        $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}");

    public static T Use<T>(Func<string, T> body)
    {
        var dir = Path.Combine(RunDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var result = body(dir);
        Directory.Delete(dir, recursive: true);
        try { Directory.Delete(RunDir); } catch (IOException) { }   // still in use by another test
        return result;
    }

    public static void Use(Action<string> body) => Use<bool>(d => { body(d); return true; });

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
                return d.FullName;
        throw new InvalidOperationException($"no repository root above {AppContext.BaseDirectory}");
    }
}
