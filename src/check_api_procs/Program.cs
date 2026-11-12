using CheckApiProcs;
using ibsCompiler;

if (!VersionCheck.CheckForUpdates("check_api_procs", args, CheckApiProcs.Options.Usage))
    return 0;

return await App.RunAsync(args, Console.Out, Console.Error, Environment.GetEnvironmentVariables(),
    new HttpClientHandler(),
    // .NET on macOS cannot resume the TLS session the release server requires on the data connection.
    OperatingSystem.IsMacOS() ? new CurlTransport() : new FtpsTransport());
