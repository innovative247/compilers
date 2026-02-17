using System.Text.Json;
using ibsCompiler.Database;

namespace ibsCompiler.TransferData
{
    /// <summary>
    /// Executes extract and insert phases for transfer_data projects.
    /// Uses existing ISqlExecutor.BulkCopy() for managed BCP operations.
    /// </summary>
    public class TransferRunner
    {
        private readonly TransferProjectConfig _config;
        private readonly string _projectName;
        private readonly string _dataDir;

        public TransferRunner(string projectName, TransferProjectConfig config)
        {
            _projectName = projectName;
            _config = config;
            _dataDir = Path.Combine(Directory.GetCurrentDirectory(), $"transfer_data_{projectName}");
        }

        /// <summary>
        /// Run full transfer: extract then insert.
        /// </summary>
        public bool RunFull()
        {
            Console.WriteLine();
            Console.WriteLine($"  Starting full transfer for '{_projectName}'...");
            Console.WriteLine();

            if (!RunExtract()) return false;
            Console.WriteLine();
            if (!RunInsert()) return false;

            Console.WriteLine();
            Console.WriteLine("  Transfer complete.");
            return true;
        }

        /// <summary>
        /// Extract phase: BulkCopy OUT from source to data files.
        /// </summary>
        public bool RunExtract()
        {
            Directory.CreateDirectory(_dataDir);
            Console.WriteLine($"  === EXTRACT PHASE ===");
            Console.WriteLine($"  Source: {_config.Source.Host}:{_config.Source.EffectivePort} ({_config.Source.Platform})");
            Console.WriteLine($"  Data dir: {_dataDir}");
            Console.WriteLine();

            var sourceProfile = DatabaseDiscovery.BuildProfile(_config.Source);
            int totalTables = _config.Databases.Sum(d => d.Value.Tables.Count);
            int current = 0;
            bool allOk = true;

            foreach (var dbKvp in _config.Databases)
            {
                var dbName = dbKvp.Key;
                var mapping = dbKvp.Value;
                var manifest = new Dictionary<string, long>();

                foreach (var table in mapping.Tables)
                {
                    current++;
                    var dataFile = Path.Combine(_dataDir, $"{dbName}_{table}.bcp");

                    Console.Write($"  [{current}/{totalTables}] Extracting: {dbName}..{table}  ");

                    try
                    {
                        using var executor = SqlExecutorFactory.Create(sourceProfile);
                        var result = executor.BulkCopy($"{dbName}..{table}", BcpDirection.OUT, dataFile);

                        if (result.Returncode)
                        {
                            long rowCount = 0;
                            if (long.TryParse(result.Output?.Trim(), out var rc))
                                rowCount = rc;
                            else
                                rowCount = CountFileLines(dataFile);

                            manifest[table] = rowCount;
                            Console.WriteLine($"{rowCount} rows  OK");
                        }
                        else
                        {
                            Console.WriteLine($"FAILED: {result.Output}");
                            allOk = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"ERROR: {ex.Message}");
                        allOk = false;
                    }
                }

                // Write manifest for this database
                var manifestPath = Path.Combine(_dataDir, $"{dbName}_manifest.json");
                File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest,
                    new JsonSerializerOptions { WriteIndented = true }));
            }

            Console.WriteLine();
            Console.WriteLine($"  Extract phase {(allOk ? "completed successfully" : "completed with errors")}.");
            return allOk;
        }

        /// <summary>
        /// Insert phase: BulkCopy IN from data files to destination.
        /// </summary>
        public bool RunInsert()
        {
            if (!Directory.Exists(_dataDir))
            {
                Console.Error.WriteLine($"  Data directory not found: {_dataDir}");
                Console.Error.WriteLine("  Run extract first.");
                return false;
            }

            Console.WriteLine($"  === INSERT PHASE ===");
            Console.WriteLine($"  Destination: {_config.Destination.Host}:{_config.Destination.EffectivePort} ({_config.Destination.Platform})");
            Console.WriteLine($"  Mode: {_config.Options.Mode}");
            Console.WriteLine();

            var destProfile = DatabaseDiscovery.BuildProfile(_config.Destination);
            int totalTables = _config.Databases.Sum(d => d.Value.Tables.Count);
            int current = 0;
            bool allOk = true;

            foreach (var dbKvp in _config.Databases)
            {
                var srcDbName = dbKvp.Key;
                var mapping = dbKvp.Value;
                var destDbName = string.IsNullOrEmpty(mapping.DestDatabase) ? srcDbName : mapping.DestDatabase;

                // Load manifest for verification
                var manifestPath = Path.Combine(_dataDir, $"{srcDbName}_manifest.json");
                var manifest = new Dictionary<string, long>();
                if (File.Exists(manifestPath))
                {
                    try
                    {
                        manifest = JsonSerializer.Deserialize<Dictionary<string, long>>(
                            File.ReadAllText(manifestPath)) ?? new Dictionary<string, long>();
                    }
                    catch { }
                }

                foreach (var table in mapping.Tables)
                {
                    current++;
                    var dataFile = Path.Combine(_dataDir, $"{srcDbName}_{table}.bcp");

                    if (!File.Exists(dataFile))
                    {
                        Console.WriteLine($"  [{current}/{totalTables}] Skipping: {destDbName}..{table}  (no data file)");
                        continue;
                    }

                    Console.Write($"  [{current}/{totalTables}] Inserting: {destDbName}..{table}  ");

                    try
                    {
                        // TRUNCATE mode: clear destination table first
                        if (_config.Options.Mode == "TRUNCATE")
                        {
                            using var executor = SqlExecutorFactory.Create(destProfile);
                            var truncResult = executor.ExecuteSql($"TRUNCATE TABLE {table}", destDbName, captureOutput: true);
                            if (!truncResult.Returncode)
                            {
                                Console.WriteLine($"TRUNCATE FAILED: {truncResult.Output}");
                                allOk = false;
                                continue;
                            }
                        }

                        using var insertExecutor = SqlExecutorFactory.Create(destProfile);
                        var result = insertExecutor.BulkCopy($"{destDbName}..{table}", BcpDirection.IN, dataFile);

                        if (result.Returncode)
                        {
                            var expectedRows = manifest.TryGetValue(table, out var exp) ? exp : -1;
                            var fileRows = CountFileLines(dataFile);
                            var status = expectedRows >= 0 && fileRows != expectedRows
                                ? $"WARNING: expected {expectedRows}"
                                : "OK";
                            Console.WriteLine($"{fileRows} rows  {status}");
                        }
                        else
                        {
                            Console.WriteLine($"FAILED: {result.Output}");
                            allOk = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"ERROR: {ex.Message}");
                        allOk = false;
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine($"  Insert phase {(allOk ? "completed successfully" : "completed with errors")}.");
            return allOk;
        }

        private static long CountFileLines(string path)
        {
            if (!File.Exists(path)) return 0;
            long count = 0;
            using var reader = new StreamReader(path);
            while (reader.ReadLine() != null) count++;
            return count;
        }
    }
}
