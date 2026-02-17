namespace ibsCompiler
{
    public static class VersionInfo
    {
        public static string Version =>
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
