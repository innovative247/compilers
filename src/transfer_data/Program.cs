using ibsCompiler;
using ibsCompiler.TransferData;

if (!VersionCheck.CheckForUpdates("transfer_data", args)) return 0;

return transfer_data_main.Run(args);
