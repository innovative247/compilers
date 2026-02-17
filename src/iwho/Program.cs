using ibsCompiler;

if (!VersionCheck.CheckForUpdates("iwho", args)) return 0;

return iwho_main.Run(args);
