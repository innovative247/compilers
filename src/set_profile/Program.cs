using ibsCompiler;

if (!VersionCheck.CheckForUpdates("set_profile", args)) return 0;

return set_profile_main.Run(args);
