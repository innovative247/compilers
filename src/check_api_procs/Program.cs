using CheckApiProcs;
using ibsCompiler;

if (!VersionCheck.CheckForUpdates("check_api_procs", args, CheckApiProcs.Options.Usage))
    return 0;

return await App.RunAsync(args, Console.Out, Console.Error, Environment.GetEnvironmentVariables(),
    new HttpClientHandler(), new FtpsTransport());
