namespace ibsCompiler.TransferData
{
    /// <summary>
    /// Interactive wizard for creating/editing transfer_data projects.
    /// Matches the Python transfer_data.py interactive flow.
    /// </summary>
    public static class ProjectWizard
    {
        public static TransferProjectConfig? Create(TransferProjectStore store)
        {
            Console.WriteLine();
            Console.Write("  Project name: ");
            var name = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(name)) return null;

            var existing = store.Load(name);
            if (existing != null)
            {
                Console.Write($"  Project '{name}' already exists. Overwrite? [y/N]: ");
                if (Console.ReadLine()?.Trim().ToLowerInvariant() != "y")
                    return null;
            }

            var config = new TransferProjectConfig();

            // Step 1: Source connection
            Console.WriteLine();
            Console.WriteLine("  --- Source Connection ---");
            config.Source = EditConnection(config.Source);
            if (config.Source == null!) return null;

            Console.Write("  Test connection? [Y/n]: ");
            if (Console.ReadLine()?.Trim().ToLowerInvariant() != "n")
            {
                Console.Write("  Testing... ");
                if (DatabaseDiscovery.TestConnection(config.Source))
                    Console.WriteLine("OK");
                else
                {
                    Console.WriteLine("FAILED");
                    Console.Write("  Continue anyway? [y/N]: ");
                    if (Console.ReadLine()?.Trim().ToLowerInvariant() != "y")
                        return null;
                }
            }
            store.Save(name, config);

            // Step 2: Destination connection
            Console.WriteLine();
            Console.WriteLine("  --- Destination Connection ---");
            config.Destination = EditConnection(config.Destination);
            if (config.Destination == null!) return null;

            Console.Write("  Test connection? [Y/n]: ");
            if (Console.ReadLine()?.Trim().ToLowerInvariant() != "n")
            {
                Console.Write("  Testing... ");
                if (DatabaseDiscovery.TestConnection(config.Destination))
                    Console.WriteLine("OK");
                else
                {
                    Console.WriteLine("FAILED");
                    Console.Write("  Continue anyway? [y/N]: ");
                    if (Console.ReadLine()?.Trim().ToLowerInvariant() != "y")
                        return null;
                }
            }
            store.Save(name, config);

            // Step 3: Database selection
            Console.WriteLine();
            Console.WriteLine("  --- Database Selection ---");
            if (!SelectDatabases(config))
                return null;
            store.Save(name, config);

            // Step 4: Table selection per database
            Console.WriteLine();
            Console.WriteLine("  --- Table Selection ---");
            SelectTables(config);
            store.Save(name, config);

            // Step 5: Transfer options
            Console.WriteLine();
            Console.WriteLine("  --- Transfer Options ---");
            config.Options = EditOptions(config.Options);
            store.Save(name, config);

            Console.WriteLine();
            Console.WriteLine($"  Project '{name}' created successfully.");
            return config;
        }

        public static ConnectionConfig EditConnection(ConnectionConfig current)
        {
            var conn = new ConnectionConfig
            {
                Platform = current.Platform,
                Host = current.Host,
                Port = current.Port,
                Username = current.Username,
                Password = current.Password
            };

            Console.Write($"  Platform (MSSQL/SYBASE) [{conn.Platform}]: ");
            var input = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(input))
                conn.Platform = input.ToUpperInvariant();

            Console.Write($"  Host [{conn.Host}]: ");
            input = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(input))
                conn.Host = input;

            if (string.IsNullOrEmpty(conn.Host))
            {
                Console.WriteLine("  Host is required.");
                return null!;
            }

            var defaultPort = conn.ServerType == SQLServerTypes.MSSQL ? 1433 : 5000;
            Console.Write($"  Port [{(conn.Port > 0 ? conn.Port : defaultPort)}]: ");
            input = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(input) && int.TryParse(input, out var port))
                conn.Port = port;
            else if (conn.Port == 0)
                conn.Port = defaultPort;

            Console.Write($"  Username [{conn.Username}]: ");
            input = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(input))
                conn.Username = input;

            Console.Write($"  Password [{(string.IsNullOrEmpty(conn.Password) ? "" : "****")}]: ");
            input = ReadPassword();
            if (!string.IsNullOrEmpty(input))
                conn.Password = input;

            return conn;
        }

        public static bool SelectDatabases(TransferProjectConfig config)
        {
            Console.Write("  Fetching databases from source... ");
            var databases = DatabaseDiscovery.GetDatabases(config.Source);
            if (databases.Count == 0)
            {
                Console.WriteLine("none found.");
                Console.Write("  Enter database names manually (comma-separated): ");
                var manual = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(manual)) return false;
                databases = manual.Split(',').Select(d => d.Trim()).Where(d => !string.IsNullOrEmpty(d)).ToList();
            }
            else
            {
                Console.WriteLine($"{databases.Count} found.");
            }

            Console.WriteLine();
            var selected = InteractiveCheckbox.Select("  Select databases to transfer:",
                databases, new HashSet<string>(config.Databases.Keys));

            if (selected == null || selected.Count == 0) return false;

            // Build database mappings
            var newDatabases = new Dictionary<string, DatabaseMapping>();
            foreach (var db in selected)
            {
                Console.Write($"  Destination name for '{db}' [{db}]: ");
                var dest = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(dest)) dest = db;

                // Preserve existing mapping if re-selected
                if (config.Databases.TryGetValue(db, out var existing))
                {
                    existing.DestDatabase = dest;
                    newDatabases[db] = existing;
                }
                else
                {
                    newDatabases[db] = new DatabaseMapping { DestDatabase = dest };
                }
            }

            config.Databases = newDatabases;
            return true;
        }

        public static void SelectTables(TransferProjectConfig config)
        {
            foreach (var kvp in config.Databases)
            {
                var dbName = kvp.Key;
                var mapping = kvp.Value;

                Console.WriteLine();
                Console.Write($"  Fetching tables from '{dbName}'... ");
                var tables = DatabaseDiscovery.GetTables(config.Source, dbName);
                if (tables.Count == 0)
                {
                    Console.WriteLine("none found.");
                    Console.Write($"  Enter table names manually (comma-separated): ");
                    var manual = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(manual))
                        mapping.Tables = manual.Split(',').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();
                    continue;
                }

                Console.WriteLine($"{tables.Count} found.");

                var preSelected = new HashSet<string>(mapping.Tables.Count > 0
                    ? mapping.Tables
                    : tables); // Default: all selected

                var selected = InteractiveCheckbox.Select($"  Select tables from '{dbName}':", tables, preSelected);
                if (selected != null)
                {
                    mapping.Tables = selected;
                    mapping.ExcludedTables = tables.Except(selected).ToList();
                }
            }
        }

        public static TransferOptions EditOptions(TransferOptions current)
        {
            var options = new TransferOptions
            {
                Mode = current.Mode,
                BatchSize = current.BatchSize
            };

            Console.Write($"  Mode (TRUNCATE/APPEND) [{options.Mode}]: ");
            var input = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(input))
                options.Mode = input.ToUpperInvariant();

            Console.Write($"  Batch size [{options.BatchSize}]: ");
            input = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(input) && int.TryParse(input, out var batch))
                options.BatchSize = batch;

            return options;
        }

        private static string ReadPassword()
        {
            var password = new System.Text.StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (password.Length > 0)
                    {
                        password.Length--;
                        Console.Write("\b \b");
                    }
                    continue;
                }
                password.Append(key.KeyChar);
                Console.Write("*");
            }
            Console.WriteLine();
            return password.ToString();
        }
    }
}
