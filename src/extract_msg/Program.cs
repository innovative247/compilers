using ibsCompiler;

if (!VersionCheck.CheckForUpdates("extract_msg", args)) return 0;

return extract_msg_main.Run(args);
