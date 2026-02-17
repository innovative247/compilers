using ibsCompiler;

if (!VersionCheck.CheckForUpdates("iplanext", args)) return 0;

return iplanext_main.Run(args);
