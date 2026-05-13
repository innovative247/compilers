using ibsCompiler;

if (!VersionCheck.CheckForUpdates("set_profile", args)) return 0;

// set_profile owns its own arg parsing — pass through verbatim.
return set_profile_main.Run(args);
