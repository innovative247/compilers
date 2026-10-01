using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SqlTest;

public sealed record SvnResult(int ExitCode, string Text, string Error);

public interface ISvnSource
{
    SvnResult Cat(string workingCopyPath, string rev);
}

public sealed class SvnCli(TimeSpan timeout, string executable = "svn") : ISvnSource
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private static readonly Regex RevRe = new(@"^\d{1,10}$");

    public SvnCli() : this(DefaultTimeout) { }

    // Working-copy path pegs at BASE, so a moved file is traced back through its own history.
    public SvnResult Cat(string workingCopyPath, string rev)
    {
        if (!RevRe.IsMatch(rev)) return new SvnResult(-1, "", $"invalid revision {rev}");

        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "cat", "-r", rev, "--non-interactive", "--", workingCopyPath })
            psi.ArgumentList.Add(a);

        Process p;
        try { p = Process.Start(psi) ?? throw new Win32Exception(); }
        catch (Win32Exception) { return new SvnResult(-1, "", $"{executable} not found on PATH"); }

        using (p)
        {
            p.StandardInput.Close();
            var sw = Stopwatch.StartNew();
            // Drain both pipes before the wait, or a full pipe deadlocks the child.
            var stdout = new MemoryStream();
            var outTask = p.StandardOutput.BaseStream.CopyToAsync(stdout);
            var errTask = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(timeout))
            {
                Kill(p);
                return new SvnResult(-1, "", $"svn cat timed out after {timeout.TotalSeconds:0}s");
            }
            // Pipes close at exit; the remaining budget only guards a grandchild holding them.
            var left = timeout - sw.Elapsed;
            if (!Task.WaitAll(new Task[] { outTask, errTask }, left > TimeSpan.Zero ? left : TimeSpan.Zero))
            {
                Kill(p);
                return new SvnResult(-1, "", $"svn cat timed out after {timeout.TotalSeconds:0}s");
            }
            return new SvnResult(p.ExitCode, Decode(stdout.ToArray()), errTask.Result.Trim());
        }
    }

    // As runsql reads a source file (new StreamReader(path)): UTF-8, BOM detection, invalid bytes to U+FFFD.
    internal static string Decode(byte[] bytes)
    {
        using var r = new StreamReader(new MemoryStream(bytes));
        return r.ReadToEnd();
    }

    private static void Kill(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
    }
}
