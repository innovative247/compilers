using ibsCompiler;
using System.Runtime.InteropServices;

const string Usage =
    "Usage: iwatch <filename>\n" +
    "       iwatch -l [processname] [-t seconds]\n" +
    "       iwatch -k <pid>";
if (!VersionCheck.CheckForUpdates("iwatch", args, Usage)) return 0;

if (args.Length == 0)
{
    Console.Error.WriteLine(Usage);
    return 1;
}

var killArg = args.FirstOrDefault(a => a.Equals("-k", StringComparison.OrdinalIgnoreCase) ||
                                       (a.StartsWith("-k", StringComparison.OrdinalIgnoreCase) && a.Length > 2));
var listArg = args.FirstOrDefault(a => a.Equals("-l", StringComparison.OrdinalIgnoreCase) ||
                                       (a.StartsWith("-l", StringComparison.OrdinalIgnoreCase) && a.Length > 2));

if (killArg == null && listArg == null)
    goto watchFile;

// iwatch -k <pid> / iwatch -k<pid>  (runs first when combined with -l)
if (killArg != null)
{
    int killArgIndex = Array.IndexOf(args, killArg);
    string pidStr = killArg.Length > 2 ? killArg.Substring(2) :
                    (args.Length > killArgIndex + 1 ? args[killArgIndex + 1] : "");
    if (!int.TryParse(pidStr, out var killPid))
    {
        Console.Error.WriteLine("Usage: iwatch -k <pid>");
        return 1;
    }
    try
    {
        var target = System.Diagnostics.Process.GetProcessById(killPid);
        var name = target.ProcessName;
        target.Kill();
        Console.WriteLine($"Killed {name} [{killPid}]");
        if (listArg != null) Console.WriteLine();
    }
    catch (ArgumentException)
    {
        Console.Error.WriteLine($"iwatch: no process with PID {killPid}");
        if (listArg == null) return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"iwatch: failed to kill {killPid}: {ex.Message}");
        if (listArg == null) return 1;
    }
}

// iwatch -l <processname> / iwatch -l<processname> [-t seconds]
if (listArg != null)
{
    int listArgIndex = Array.IndexOf(args, listArg);
    string procName = listArg.Length > 2 ? listArg.Substring(2) :
                      (args.Length > listArgIndex + 1 && !args[listArgIndex + 1].StartsWith("-") ? args[listArgIndex + 1] : "");
    int timer = 0;
    for (int i = 0; i < args.Length; i++)
    {
        if ((args[i] == "-t" || args[i] == "--timer") && i + 1 < args.Length)
            int.TryParse(args[++i], out timer);
        else if (args[i].StartsWith("-t") && args[i].Length > 2 && int.TryParse(args[i].Substring(2), out var t))
            timer = t;
    }

    do
    {
        if (timer > 0) Console.WriteLine($"[{DateTime.Now:HH:mm:ss}]");
        var procs = string.IsNullOrEmpty(procName)
            ? System.Diagnostics.Process.GetProcesses()
            : System.Diagnostics.Process.GetProcessesByName(procName);
        if (procs.Length == 0)
        {
            Console.WriteLine($"No processes found with name '{procName}'.");
        }
        else
        {
            Console.WriteLine($"{"PID",-8} {"Started",-12} {"CPU",-10}  Name");
            Console.WriteLine(new string('-', 48));
            foreach (var p in procs.OrderBy(p => p.ProcessName).ThenBy(p => p.Id))
            {
                string started = "";
                string cpu = "";
                try { started = p.StartTime.ToString("HH:mm:ss"); } catch { }
                try { cpu = p.TotalProcessorTime.ToString(@"h\:mm\:ss"); } catch { }
                Console.WriteLine($"{p.Id,-8} {started,-12} {cpu,-10}  {p.ProcessName}");
            }
        }
        if (timer > 0)
        {
            Thread.Sleep(timer * 1000);
            Console.WriteLine();
        }
    } while (timer > 0);
}

return 0;

watchFile:

var filename = Path.IsPathRooted(args[0]) ? args[0] : Path.GetFullPath(args[0]);

// Wait up to 5 seconds for the file to appear (background process may not have created it yet)
var waited = 0;
while (!File.Exists(filename) && waited < 5000)
{
    Thread.Sleep(100);
    waited += 100;
}

if (!File.Exists(filename))
{
    Console.Error.WriteLine($"iwatch: file not found after 5 seconds: {filename}");
    return 1;
}

System.Diagnostics.ProcessStartInfo psi;
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    // Escape single quotes in path for PowerShell
    var escaped = filename.Replace("'", "''");
    psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "powershell",
        Arguments = $"-NoProfile -Command \"Get-Content -Path '{escaped}' -Wait -Tail 0\"",
        UseShellExecute = false
    };
}
else
{
    psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "tail",
        Arguments = $"-n 0 -f \"{filename}\"",
        UseShellExecute = false
    };
}

var proc = System.Diagnostics.Process.Start(psi);
proc?.WaitForExit();
return proc?.ExitCode ?? 0;
