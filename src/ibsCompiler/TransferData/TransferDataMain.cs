namespace ibsCompiler.TransferData
{
    /// <summary>
    /// Main menu for transfer_data. Replaces the stub in TransferData.cs.
    /// Port of Python transfer_data.py interactive menu-driven tool.
    /// </summary>
    public static class transfer_data_main
    {
        public static int Run(string[] args)
        {
            var store = new TransferProjectStore();

            Console.WriteLine();
            Console.WriteLine("  Data Transfer Utility");

            while (true)
            {
                var projects = store.ListProjects();
                Console.WriteLine();
                Console.WriteLine($"  Main Menu ({projects.Count} project{(projects.Count == 1 ? "" : "s")})");
                Console.WriteLine("    1. Create new project");
                Console.WriteLine("    2. View a project");
                Console.WriteLine("    3. Delete a project");
                Console.WriteLine("    4. Run a project");
                Console.WriteLine("    5. Open settings.json");
                Console.WriteLine("   99. Exit");
                Console.Write("  Choice: ");

                var choice = Console.ReadLine()?.Trim();
                switch (choice)
                {
                    case "1":
                        ProjectWizard.Create(store);
                        break;

                    case "2":
                        ViewProject(store);
                        break;

                    case "3":
                        DeleteProject(store);
                        break;

                    case "4":
                        RunProjectMenu(store);
                        break;

                    case "5":
                        OpenSettings(store);
                        break;

                    case "99":
                        return 0;

                    default:
                        Console.WriteLine("  Invalid choice.");
                        break;
                }
            }
        }

        private static void ViewProject(TransferProjectStore store)
        {
            var name = ChooseProject(store, "view");
            if (name == null) return;

            var config = store.Load(name);
            if (config == null) { Console.WriteLine("  Project not found."); return; }

            Console.WriteLine();
            Console.WriteLine($"  Project: {name}");
            Console.WriteLine();
            Console.WriteLine($"  Source:       {config.Source.Platform} {config.Source.Host}:{config.Source.EffectivePort}  user={config.Source.Username}");
            Console.WriteLine($"  Destination:  {config.Destination.Platform} {config.Destination.Host}:{config.Destination.EffectivePort}  user={config.Destination.Username}");
            Console.WriteLine($"  Mode:         {config.Options.Mode}  batch={config.Options.BatchSize}");
            Console.WriteLine();

            foreach (var db in config.Databases)
            {
                var dest = string.IsNullOrEmpty(db.Value.DestDatabase) ? db.Key : db.Value.DestDatabase;
                Console.WriteLine($"  Database: {db.Key} -> {dest}  ({db.Value.Tables.Count} tables)");
                foreach (var table in db.Value.Tables)
                    Console.WriteLine($"    - {table}");
            }
        }

        private static void DeleteProject(TransferProjectStore store)
        {
            var name = ChooseProject(store, "delete");
            if (name == null) return;

            Console.Write($"  Delete project '{name}'? [y/N]: ");
            if (Console.ReadLine()?.Trim().ToLowerInvariant() == "y")
            {
                store.Delete(name);
                Console.WriteLine($"  Project '{name}' deleted.");
            }
        }

        private static void RunProjectMenu(TransferProjectStore store)
        {
            var name = ChooseProject(store, "run");
            if (name == null) return;

            var config = store.Load(name);
            if (config == null) { Console.WriteLine("  Project not found."); return; }

            while (true)
            {
                Console.WriteLine();
                Console.WriteLine($"  Run Project: {name}");
                Console.WriteLine("    1. Run transfer");
                Console.WriteLine("    2. Edit source connection");
                Console.WriteLine("    3. Edit destination connection");
                Console.WriteLine("    4. Edit databases and tables");
                Console.WriteLine("    5. Edit transfer options");
                Console.WriteLine("   98. Back");
                Console.WriteLine("   99. Exit");
                Console.Write("  Choice: ");

                var choice = Console.ReadLine()?.Trim();
                switch (choice)
                {
                    case "1":
                        RunTransferSubmenu(name, config);
                        break;

                    case "2":
                        Console.WriteLine();
                        Console.WriteLine("  --- Edit Source Connection ---");
                        config.Source = ProjectWizard.EditConnection(config.Source);
                        store.Save(name, config);
                        Console.WriteLine("  Source connection updated.");
                        break;

                    case "3":
                        Console.WriteLine();
                        Console.WriteLine("  --- Edit Destination Connection ---");
                        config.Destination = ProjectWizard.EditConnection(config.Destination);
                        store.Save(name, config);
                        Console.WriteLine("  Destination connection updated.");
                        break;

                    case "4":
                        Console.WriteLine();
                        Console.WriteLine("  --- Edit Databases and Tables ---");
                        if (ProjectWizard.SelectDatabases(config))
                        {
                            ProjectWizard.SelectTables(config);
                            store.Save(name, config);
                            Console.WriteLine("  Databases and tables updated.");
                        }
                        break;

                    case "5":
                        Console.WriteLine();
                        Console.WriteLine("  --- Edit Transfer Options ---");
                        config.Options = ProjectWizard.EditOptions(config.Options);
                        store.Save(name, config);
                        Console.WriteLine("  Transfer options updated.");
                        break;

                    case "98":
                        return;

                    case "99":
                        Environment.Exit(0);
                        return;

                    default:
                        Console.WriteLine("  Invalid choice.");
                        break;
                }
            }
        }

        private static void RunTransferSubmenu(string name, TransferProjectConfig config)
        {
            Console.WriteLine();
            Console.WriteLine("    1. Full transfer (extract + insert)");
            Console.WriteLine("    2. Extract only");
            Console.WriteLine("    3. Insert only");
            Console.Write("  Choice: ");

            var runner = new TransferRunner(name, config);

            var choice = Console.ReadLine()?.Trim();
            switch (choice)
            {
                case "1":
                    runner.RunFull();
                    break;
                case "2":
                    runner.RunExtract();
                    break;
                case "3":
                    runner.RunInsert();
                    break;
                default:
                    Console.WriteLine("  Invalid choice.");
                    break;
            }
        }

        private static string? ChooseProject(TransferProjectStore store, string action)
        {
            var projects = store.ListProjects();
            if (projects.Count == 0)
            {
                Console.WriteLine("  No projects configured.");
                return null;
            }

            Console.WriteLine();
            Console.WriteLine($"  Select project to {action}:");
            for (int i = 0; i < projects.Count; i++)
                Console.WriteLine($"    {i + 1}. {projects[i]}");
            Console.Write("  Choice: ");

            var input = Console.ReadLine()?.Trim();
            if (int.TryParse(input, out var idx) && idx >= 1 && idx <= projects.Count)
                return projects[idx - 1];

            // Also accept name directly
            if (projects.Contains(input ?? ""))
                return input;

            Console.WriteLine("  Invalid selection.");
            return null;
        }

        private static void OpenSettings(TransferProjectStore store)
        {
            var path = store.SettingsPath;
            Console.WriteLine($"  Settings file: {path}");

            try
            {
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true
                    });
                }
                else
                {
                    // Try xdg-open on Linux, open on macOS
                    var cmd = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                        System.Runtime.InteropServices.OSPlatform.OSX) ? "open" : "xdg-open";
                    System.Diagnostics.Process.Start(cmd, path);
                }
            }
            catch
            {
                Console.WriteLine("  Could not open file. Please open it manually.");
            }
        }
    }
}
