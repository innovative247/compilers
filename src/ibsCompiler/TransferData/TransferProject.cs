namespace ibsCompiler.TransferData
{
    /// <summary>
    /// Data model for transfer_data projects.
    /// JSON-serializable, stored in settings.json under "data_transfer" key.
    /// </summary>
    public class TransferProjectConfig
    {
        public ConnectionConfig Source { get; set; } = new();
        public ConnectionConfig Destination { get; set; } = new();
        public Dictionary<string, DatabaseMapping> Databases { get; set; } = new();
        public TransferOptions Options { get; set; } = new();
    }

    public class ConnectionConfig
    {
        public string Platform { get; set; } = "MSSQL";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string Username { get; set; } = "sbn0";
        public string Password { get; set; } = "ibsibs";

        public SQLServerTypes ServerType =>
            Platform.ToUpperInvariant() == "MSSQL" ? SQLServerTypes.MSSQL : SQLServerTypes.SYBASE;

        public int EffectivePort =>
            Port > 0 ? Port : (ServerType == SQLServerTypes.MSSQL ? 1433 : 5000);
    }

    public class DatabaseMapping
    {
        public string DestDatabase { get; set; } = "";
        public List<string> Tables { get; set; } = new();
        public List<string> ExcludedTables { get; set; } = new();
    }

    public class TransferOptions
    {
        public string Mode { get; set; } = "TRUNCATE";
        public int BatchSize { get; set; } = 1000;
    }
}
