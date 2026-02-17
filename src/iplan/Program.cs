using ibsCompiler;

if (!VersionCheck.CheckForUpdates("iplan", args)) return 0;

return iplan_main.Run(args);
