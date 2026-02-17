using System.Diagnostics;
using System.Runtime.InteropServices;
using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace ibsCompiler
{
    /// <summary>
    /// Interactive menu systems for eact, eopt, eloc, and compile_msg/set_messages.
    /// Matches the Python compiler menu UX with edit prompts, confirmations, and mode selection.
    /// </summary>
    public static class InteractiveMenus
    {
        #region Shared Helpers

        /// <summary>
        /// These commands require options/placeholder resolution and cannot work in raw mode.
        /// </summary>
        private static bool CheckRawMode(ResolvedProfile profile)
        {
            if (profile.RawMode)
            {
                Console.WriteLine("ERROR: This command requires option file processing and is not available in raw mode.");
                Console.WriteLine("Edit the profile and set Raw Mode to 'No' to use this command.");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Prompts user for yes/no with a default value.
        /// Matches Python console_yes_no().
        /// </summary>
        public static bool ConsoleYesNo(string prompt, bool defaultYes = true)
        {
            var hint = defaultYes ? "Y/n" : "y/N";
            while (true)
            {
                Console.Write($"{prompt} ({hint}): ");
                var response = Console.ReadLine()?.Trim().ToLower() ?? "";
                if (response == "")
                    return defaultYes;
                if (response == "y" || response == "yes")
                    return true;
                if (response == "n" || response == "no")
                    return false;
                Console.WriteLine("Please answer 'y' or 'n'.");
            }
        }

        /// <summary>
        /// Launches the user's preferred editor for a file and waits for it to close.
        /// Resolution order: $EDITOR, $VISUAL, vim, vi, notepad (Windows).
        /// Matches Python launch_editor().
        /// </summary>
        public static void LaunchEditor(string filePath)
        {
            string? editor = null;

            // 1. $EDITOR
            var envEditor = Environment.GetEnvironmentVariable("EDITOR")?.Trim();
            if (!string.IsNullOrEmpty(envEditor))
                editor = envEditor;

            // 2. $VISUAL
            if (editor == null)
            {
                var envVisual = Environment.GetEnvironmentVariable("VISUAL")?.Trim();
                if (!string.IsNullOrEmpty(envVisual))
                    editor = envVisual;
            }

            // 3. Try vim, vi
            if (editor == null)
            {
                foreach (var candidate in new[] { "vim", "vi" })
                {
                    try
                    {
                        var p = Process.Start(new ProcessStartInfo
                        {
                            FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which",
                            Arguments = candidate,
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                        p?.WaitForExit();
                        if (p?.ExitCode == 0)
                        {
                            editor = candidate;
                            break;
                        }
                    }
                    catch { }
                }
            }

            // 4. Windows fallback: notepad
            if (editor == null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                editor = "notepad";

            if (editor == null)
            {
                Console.WriteLine("ERROR: No editor found. Set the EDITOR environment variable.");
                return;
            }

            try
            {
                var psi = new ProcessStartInfo(editor, filePath)
                {
                    UseShellExecute = false
                };
                var proc = Process.Start(psi);
                proc?.WaitForExit();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Could not launch editor '{editor}': {ex.Message}");
            }
        }

        #endregion

        #region set_actions (eact)

        /// <summary>
        /// Interactive menu for eact/set_actions.
        /// Matches Python set_actions.py main():
        ///   1. Prompt to edit actions file (default: Yes)
        ///   2. Prompt to edit actions_dtl file (default: Yes)
        ///   3. Prompt to compile into database (default: Yes)
        ///   4. Run compile_actions
        /// </summary>
        public static int RunSetActions(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor)
        {
            if (CheckRawMode(profile)) return 1;

            var profileName = profile.IsProfile ? profile.ProfileName : cmdvars.ServerNameOnly;
            var actionsFile = ibs_compiler_common.GetPath_Actions(cmdvars, profile);
            var actionsDtlFile = ibs_compiler_common.GetPath_ActionsDetail(profile);

            // Check files exist
            if (!File.Exists(actionsFile))
            {
                Console.WriteLine($"ERROR: actions file not found: {actionsFile}");
                return 1;
            }
            if (!File.Exists(actionsDtlFile))
            {
                Console.WriteLine($"ERROR: actions_dtl file not found: {actionsDtlFile}");
                return 1;
            }

            // Prompt to edit actions file (default: Yes)
            if (ConsoleYesNo($"Edit {actionsFile}?"))
                LaunchEditor(actionsFile);

            // Prompt to edit actions_dtl file (default: Yes)
            if (ConsoleYesNo($"Edit {actionsDtlFile}?"))
                LaunchEditor(actionsDtlFile);

            // Prompt to compile into database (default: Yes)
            if (!ConsoleYesNo($"Compile actions into {profileName.ToUpper()}?"))
            {
                Console.WriteLine("Finished.");
                return 0;
            }

            // Compile
            Console.WriteLine("Compiling actions...");
            compile_actions_main.Run(cmdvars, profile, executor);
            return 0;
        }

        #endregion

        #region set_required_fields (ereq)

        /// <summary>
        /// Interactive menu for ereq/set_required_fields.
        /// Matches set_actions pattern:
        ///   1. Prompt to edit required_fields file (default: Yes)
        ///   2. Prompt to edit required_fields_dtl file (default: Yes)
        ///   3. Prompt to compile into database (default: Yes)
        ///   4. Run compile_required_fields
        /// </summary>
        public static int RunSetRequiredFields(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor)
        {
            if (CheckRawMode(profile)) return 1;

            var profileName = profile.IsProfile ? profile.ProfileName : cmdvars.ServerNameOnly;
            var setupDir = ibs_compiler_common.GetPath_Setup(profile);
            var rfFile = Path.Combine(setupDir, "css.required_fields");
            var rfDtlFile = Path.Combine(setupDir, "css.required_fields_dtl");

            // Check files exist
            if (!File.Exists(rfFile))
            {
                Console.WriteLine($"ERROR: required_fields file not found: {rfFile}");
                return 1;
            }
            if (!File.Exists(rfDtlFile))
            {
                Console.WriteLine($"ERROR: required_fields_dtl file not found: {rfDtlFile}");
                return 1;
            }

            // Prompt to edit required_fields file (default: Yes)
            if (ConsoleYesNo($"Edit {rfFile}?"))
                LaunchEditor(rfFile);

            // Prompt to edit required_fields_dtl file (default: Yes)
            if (ConsoleYesNo($"Edit {rfDtlFile}?"))
                LaunchEditor(rfDtlFile);

            // Prompt to compile into database (default: Yes)
            if (!ConsoleYesNo($"Compile required_fields into {profileName.ToUpper()}?"))
            {
                Console.WriteLine("Finished.");
                return 0;
            }

            // Compile
            Console.WriteLine("Compiling required fields...");
            compile_required_fields_main.Run(cmdvars, profile, executor);
            return 0;
        }

        #endregion

        #region set_table_locations (eloc)

        /// <summary>
        /// Interactive menu for eloc/set_table_locations.
        /// Matches Python set_table_locations.py main():
        ///   1. Prompt to edit table_locations file (default: Yes)
        ///   2. Prompt to compile into database (default: Yes)
        ///   3. Run compile_table_locations
        /// </summary>
        public static int RunSetTableLocations(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor)
        {
            if (CheckRawMode(profile)) return 1;

            var profileName = profile.IsProfile ? profile.ProfileName : cmdvars.ServerNameOnly;
            var locationsFile = ibs_compiler_common.GetPath_TableLocations(profile);

            if (!File.Exists(locationsFile))
            {
                Console.WriteLine($"ERROR: table_locations file not found: {locationsFile}");
                return 1;
            }

            // Prompt to edit the source file (default: Yes)
            if (ConsoleYesNo($"Edit {locationsFile}?"))
                LaunchEditor(locationsFile);

            // Prompt to compile into database (default: Yes)
            if (!ConsoleYesNo($"Compile table_locations into {profileName.ToUpper()}?"))
            {
                Console.WriteLine("Cancelled.");
                return 0;
            }

            // Compile
            Console.WriteLine("Compiling table_locations...");
            compile_table_locations_main.Run(cmdvars, profile, executor);
            return 0;
        }

        #endregion

        #region set_options (eopt)

        /// <summary>
        /// Interactive menu for eopt/set_options.
        /// Matches Python set_options.py main():
        ///   1. Mode selection: Add new options, Edit existing, or Exit
        ///   2. For Add mode: wizard to create options in options.def, merge into company/profile
        ///   3. For Edit mode: prompt to edit profile file, company file
        ///   4. Prompt to import into database (default: Yes)
        ///   5. Run compile_options
        /// </summary>
        public static int RunSetOptions(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor)
        {
            if (CheckRawMode(profile)) return 1;

            var profileName = profile.IsProfile ? profile.ProfileName : cmdvars.ServerNameOnly;
            var companyFile = ibs_compiler_common.GetPath_OptionsCompany(profile);
            var profileFile = ibs_compiler_common.GetPath_OptionsServer(cmdvars, profile);
            var defFile = ibs_compiler_common.GetPath_OptionsDefault(profile);
            var platformFile = ibs_compiler_common.GetPath_OptionsSQL(cmdvars, profile);

            // Mode selection
            Console.WriteLine();
            Console.WriteLine("What do you want to do?");
            Console.WriteLine("  1. Add new options (create in options.def)");
            Console.WriteLine("  2. Edit existing options");
            Console.WriteLine("  3. Import");
            Console.WriteLine("  99. Exit");

            Console.Write("\nChoose [1-3, 99]: ");
            var choice = Console.ReadLine()?.Trim() ?? "";

            if (choice == "1")
            {
                RunAddMode(defFile, companyFile, profileFile, platformFile);
            }
            else if (choice == "2")
            {
                RunEditMode(profileFile, companyFile);
            }
            else if (choice == "3")
            {
                // Skip straight to compile
            }
            else
            {
                Console.WriteLine("Finished.");
                return 0;
            }

            // Prompt to compile/import into database (default: Yes)
            if (choice != "3" && !ConsoleYesNo($"Import options into {profileName.ToUpper()}?"))
            {
                Console.WriteLine("Finished.");
                return 0;
            }

            // Compile
            Console.WriteLine("Compiling options...");
            compile_options_main.Run(cmdvars, profile, executor);
            return 0;
        }

        private static void RunEditMode(string profileFile, string companyFile)
        {
            // Prompt to edit profile options file FIRST (matches Python order)
            if (File.Exists(profileFile))
            {
                if (ConsoleYesNo($"Edit {profileFile}?", defaultYes: false))
                    LaunchEditor(profileFile);
            }
            else
            {
                Console.WriteLine($"Profile options file not found: {profileFile}");
            }

            // Prompt to edit company options file
            if (File.Exists(companyFile))
            {
                if (ConsoleYesNo($"Edit {companyFile}?", defaultYes: false))
                    LaunchEditor(companyFile);
            }
            else
            {
                Console.WriteLine($"Company options file not found: {companyFile}");
            }
        }

        private static void RunAddMode(string defFile, string companyFile, string profileFile, string platformFile)
        {
            if (!File.Exists(defFile))
            {
                Console.WriteLine($"ERROR: options.def not found: {defFile}");
                return;
            }

            var defLines = LoadOptionsFile(defFile);
            var newOptionsAdded = new List<string>();

            Console.WriteLine($"\nAdding new options to: {defFile}");

            // Prompt for modification information
            var modInfo = PromptModificationInfo();
            if (modInfo == null)
            {
                Console.WriteLine("Cancelled.");
                return;
            }

            // Interactive option creation loop
            while (true)
            {
                var optionLine = CreateNewOptionInteractive(defLines);
                if (optionLine == null)
                    break;

                newOptionsAdded.Add(optionLine);
                defLines.Add(optionLine);
                Console.WriteLine($"\nOption added: {optionLine}");

                if (!ConsoleYesNo("Add another option?"))
                    break;
            }

            // Save all new options with MOD markers
            if (newOptionsAdded.Count > 0)
            {
                defLines = LoadOptionsFile(defFile);
                defLines = AddChgToHeader(defLines, modInfo["chg_line"]);
                defLines.Add($"# {modInfo["mod_num"]} -->");
                foreach (var opt in newOptionsAdded)
                    defLines.Add(opt);
                defLines.Add($"# {modInfo["mod_num"]} <--");
                SaveOptionsFile(defFile, defLines);
                Console.WriteLine($"\n{newOptionsAdded.Count} option(s) added to options.def");
            }
            else
            {
                Console.WriteLine("No options added.");
            }

            // Merge into company file
            if (ConsoleYesNo($"\nMerge options from options.def into {companyFile}?"))
            {
                defLines = LoadOptionsFile(defFile);
                var companyLines = LoadOptionsFile(companyFile);

                var mergeOptions = new List<string>(defLines);
                if (File.Exists(platformFile))
                {
                    Console.WriteLine("Excluding platform-specific options...");
                    var platformLines = LoadOptionsFile(platformFile);
                    mergeOptions = RemoveOptions(mergeOptions, platformLines);
                }

                var optionsToAdd = FindNewOptions(mergeOptions, companyLines);
                if (optionsToAdd.Count == 0)
                {
                    Console.WriteLine("No new options to add. Company file is up to date.");
                }
                else
                {
                    Console.WriteLine($"\n{optionsToAdd.Count} new option(s) found. We will go through each one.");
                    var customized = PromptAndCustomizeOptions(optionsToAdd);
                    if (customized.Count > 0)
                    {
                        var merged = InsertNewOptions(companyLines, customized, modInfo["mod_num"]);
                        merged = AddChgToHeader(merged, modInfo["chg_line"]);
                        SaveOptionsFile(companyFile, merged);
                        Console.WriteLine($"\n{customized.Count} option(s) merged into {companyFile}");
                        if (ConsoleYesNo($"Edit {companyFile}?", defaultYes: false))
                            LaunchEditor(companyFile);
                    }
                    else
                    {
                        Console.WriteLine("\nNo options were added.");
                    }
                }
            }

            // Merge into profile file
            if (ConsoleYesNo($"\nMerge options from options.def into {profileFile}?", defaultYes: false))
            {
                defLines = LoadOptionsFile(defFile);
                var profileLines = LoadOptionsFile(profileFile);
                var optionsToAdd = FindNewOptions(defLines, profileLines);
                if (optionsToAdd.Count == 0)
                {
                    Console.WriteLine("No new options to add. Profile file is up to date.");
                }
                else
                {
                    Console.WriteLine($"\n{optionsToAdd.Count} new option(s) found. We will go through each one.");
                    var customized = PromptAndCustomizeOptions(optionsToAdd);
                    if (customized.Count > 0)
                    {
                        var merged = InsertNewOptions(profileLines, customized, modInfo["mod_num"]);
                        merged = AddChgToHeader(merged, modInfo["chg_line"]);
                        SaveOptionsFile(profileFile, merged);
                        Console.WriteLine($"\n{customized.Count} option(s) merged into {profileFile}");
                        if (ConsoleYesNo($"Edit {profileFile}?", defaultYes: false))
                            LaunchEditor(profileFile);
                    }
                    else
                    {
                        Console.WriteLine("\nNo options were added.");
                    }
                }
            }
        }

        #region Options File Helpers

        private static List<string> LoadOptionsFile(string path)
        {
            if (!File.Exists(path)) return new List<string>();
            return File.ReadAllLines(path).Select(l => l.TrimEnd('\r', '\n')).ToList();
        }

        private static void SaveOptionsFile(string path, List<string> lines)
        {
            using var writer = new StreamWriter(path, false);
            foreach (var line in lines)
                writer.WriteLine(line);
        }

        private static string ExtractOptionName(string line)
        {
            line = line.Trim();
            if (line.Length < 3) return "";
            var prefix = line.Substring(0, 2).ToLower();
            if (prefix != "v:" && prefix != "c:") return "";
            var content = line.Substring(2).Trim();
            var spaceIdx = content.IndexOf(' ');
            if (spaceIdx == -1) return content.ToLower();
            return content.Substring(0, spaceIdx).Trim().ToLower();
        }

        private static List<string> FindNewOptions(List<string> defLines, List<string> targetLines)
        {
            var existing = new HashSet<string>();
            foreach (var line in targetLines)
            {
                var name = ExtractOptionName(line);
                if (name != "") existing.Add(name);
            }
            var result = new List<string>();
            foreach (var line in defLines)
            {
                var name = ExtractOptionName(line);
                if (name != "" && !existing.Contains(name))
                    result.Add(line);
            }
            return result;
        }

        private static List<string> RemoveOptions(List<string> baseLines, List<string> linesToRemove)
        {
            var removeNames = new HashSet<string>();
            foreach (var line in linesToRemove)
            {
                var name = ExtractOptionName(line);
                if (name != "") removeNames.Add(name);
            }
            return baseLines.Where(line =>
            {
                var name = ExtractOptionName(line);
                return name == "" || !removeNames.Contains(name);
            }).ToList();
        }

        private static List<string> InsertNewOptions(List<string> targetLines, List<string> newOptions, string? modNum)
        {
            var result = new List<string>(targetLines);
            if (modNum != null)
            {
                result.Add($"# {modNum} -->");
                result.AddRange(newOptions);
                result.Add($"# {modNum} <--");
            }
            else
            {
                result.AddRange(newOptions);
            }
            return result;
        }

        private static List<string> AddChgToHeader(List<string> lines, string chgLine)
        {
            var result = new List<string>(lines);
            int lastChgIdx = -1;
            for (int i = 0; i < result.Count; i++)
            {
                if (result[i].Trim().StartsWith("CHG "))
                    lastChgIdx = i;
                if (result[i].Trim().StartsWith("v:") || result[i].Trim().StartsWith("V:") ||
                    result[i].Trim().StartsWith("c:") || result[i].Trim().StartsWith("C:"))
                    break;
            }
            if (lastChgIdx >= 0)
                result.Insert(lastChgIdx + 1, chgLine);
            else if (result.Count > 0)
                result.Insert(1, chgLine);
            else
                result.Add(chgLine);
            return result;
        }

        private static Dictionary<string, string>? PromptModificationInfo()
        {
            Console.WriteLine("\n--- Modification Information ---");
            Console.WriteLine("This will be added to the options.def header.");

            var dateStr = DateTime.Now.ToString("yyMMdd");
            Console.WriteLine($"\nDate: {dateStr} (auto-generated)");

            Console.WriteLine("\nEnter your name:");
            Console.Write("> ");
            var name = (Console.ReadLine()?.Trim() ?? "").ToUpper();
            if (name == "") { Console.WriteLine("Name is required."); return null; }

            Console.WriteLine("\nEnter MOD # (e.g., 07.95.27639):");
            Console.Write("> ");
            var modNum = Console.ReadLine()?.Trim() ?? "";
            if (modNum == "") { Console.WriteLine("MOD # is required."); return null; }

            Console.WriteLine("\nEnter reason/description:");
            Console.Write("> ");
            var reason = Console.ReadLine()?.Trim() ?? "";
            if (reason == "") { Console.WriteLine("Reason is required."); return null; }

            var chgLine = $"CHG {dateStr} {name}    {modNum}    {reason}";
            Console.WriteLine($"\nChange log entry: {chgLine}");

            if (!ConsoleYesNo("Is this correct?"))
                return null;

            return new Dictionary<string, string>
            {
                ["date"] = dateStr,
                ["name"] = name,
                ["mod_num"] = modNum,
                ["reason"] = reason,
                ["chg_line"] = chgLine
            };
        }

        private static string? CreateNewOptionInteractive(List<string> existingDef)
        {
            var existingNames = new HashSet<string>();
            foreach (var line in existingDef)
            {
                var n = ExtractOptionName(line);
                if (n != "") existingNames.Add(n);
            }

            while (true)
            {
                // Step 1: Option type
                Console.WriteLine("\n--- New Option ---");
                Console.WriteLine("What type of option?");
                Console.WriteLine("  1. Value option (stores a value like <<sbnmaster>>)");
                Console.WriteLine("  2. On/Off option (condition flag +/-)");
                Console.WriteLine("  3. Cancel");

                Console.Write("\nChoose [1-3]: ");
                var typeChoice = Console.ReadLine()?.Trim() ?? "";
                if (typeChoice == "3") return null;
                if (typeChoice != "1" && typeChoice != "2") { Console.WriteLine("Invalid choice."); continue; }
                bool isValueOption = typeChoice == "1";

                // Step 2: Static or dynamic
                Console.WriteLine("\nIs this option static or dynamic?");
                Console.WriteLine("  1. Static (lowercase v:/c: - cannot be changed at runtime)");
                Console.WriteLine("  2. Dynamic (uppercase V:/C: - can be changed by users)");
                Console.WriteLine("  3. Go back");

                Console.Write("\nChoose [1-3]: ");
                var dynChoice = Console.ReadLine()?.Trim() ?? "";
                if (dynChoice == "3") continue;
                if (dynChoice != "1" && dynChoice != "2") { Console.WriteLine("Invalid choice."); continue; }
                bool isDynamic = dynChoice == "2";

                // Step 3: Option name
                string? optName = null;
                while (optName == null)
                {
                    Console.WriteLine("\nEnter option name (max 8 characters, e.g., 'myopt'), or 'back' to go back:");
                    Console.Write("> ");
                    var nameInput = Console.ReadLine()?.Trim() ?? "";
                    if (nameInput.ToLower() == "back") break;
                    if (nameInput == "") { Console.WriteLine("Option name cannot be empty."); continue; }
                    if (nameInput.Length > 8) { Console.WriteLine("Option name must be 8 characters or less."); continue; }
                    if (existingNames.Contains(nameInput.ToLower())) { Console.WriteLine($"Option '{nameInput}' already exists in options.def."); continue; }
                    optName = nameInput;
                }
                if (optName == null) continue;

                // Step 4: Default value
                string defaultValue;
                if (isValueOption)
                {
                    Console.WriteLine("\nEnter default value (max 2000 characters, will be wrapped in <<>>):");
                    Console.Write("> ");
                    defaultValue = Console.ReadLine()?.Trim() ?? "";
                }
                else
                {
                    Console.WriteLine("\nDefault state:");
                    Console.WriteLine("  1. Off (-)");
                    Console.WriteLine("  2. On (+)");
                    Console.Write("\nChoose [1-2]: ");
                    var stateChoice = Console.ReadLine()?.Trim() ?? "";
                    if (stateChoice == "1") defaultValue = "-";
                    else if (stateChoice == "2") defaultValue = "+";
                    else { Console.WriteLine("Invalid choice."); continue; }
                }

                // Step 5: Description
                Console.WriteLine("\nEnter description:");
                Console.Write("> ");
                var description = Console.ReadLine()?.Trim() ?? "";
                if (description == "") description = "No description";

                // Build option line
                string prefix, optionLine;
                if (isValueOption)
                {
                    prefix = isDynamic ? "V:" : "v:";
                    optionLine = $"{prefix}{optName} <<{defaultValue}>> {description}";
                }
                else
                {
                    prefix = isDynamic ? "C:" : "c:";
                    optionLine = $"{prefix}{optName} {defaultValue} {description}";
                }

                // Step 6: Confirmation
                Console.WriteLine("\n--- Review ---");
                Console.WriteLine($"Option line: {optionLine}");
                Console.WriteLine($"  Type: {(isValueOption ? "Value" : "On/Off")}");
                Console.WriteLine($"  Dynamic: {(isDynamic ? "Yes" : "No")}");
                Console.WriteLine($"  Name: {optName}");
                if (isValueOption)
                    Console.WriteLine($"  Default value: <<{defaultValue}>>");
                else
                    Console.WriteLine($"  Default state: {(defaultValue == "+" ? "On (+)" : "Off (-)")}");
                Console.WriteLine($"  Description: {description}");

                if (ConsoleYesNo("\nIs this correct?"))
                    return optionLine;
            }
        }

        private static string PromptOptionValue(string optionLine)
        {
            var line = optionLine.Trim();
            if (line.Length < 3) return optionLine;
            var prefix = line.Substring(0, 2);
            var optType = prefix.ToLower();
            bool isDynamic = char.IsUpper(prefix[0]);
            if (optType != "v:" && optType != "c:") return optionLine;

            var content = line.Substring(2).Trim();
            var spaceIdx = content.IndexOf(' ');
            if (spaceIdx == -1) return optionLine;
            var optName = content.Substring(0, spaceIdx).Trim();
            var rest = content.Substring(spaceIdx).Trim();

            if (optType == "v:")
            {
                var startIdx = rest.IndexOf("<<");
                var endIdx = rest.IndexOf(">>");
                if (startIdx == -1 || endIdx == -1) return optionLine;
                var currentValue = rest.Substring(startIdx + 2, endIdx - startIdx - 2);
                var description = rest.Substring(endIdx + 2).Trim();

                Console.WriteLine($"\n  Option: {optName}");
                Console.WriteLine($"  Type: Value ({(isDynamic ? "dynamic" : "static")})");
                Console.WriteLine($"  Description: {description}");
                Console.WriteLine($"  Default value: <<{currentValue}>>");

                Console.Write($"  Enter value (or press Enter to keep <<{currentValue}>>): ");
                var newValue = Console.ReadLine()?.Trim() ?? "";
                if (newValue == "") newValue = currentValue;
                return $"{prefix}{optName} <<{newValue}>> {description}";
            }
            else
            {
                string currentState, description;
                if (rest.StartsWith("-")) { currentState = "-"; description = rest.Substring(1).Trim(); }
                else if (rest.StartsWith("+")) { currentState = "+"; description = rest.Substring(1).Trim(); }
                else { currentState = "+"; description = rest; }

                Console.WriteLine($"\n  Option: {optName}");
                Console.WriteLine($"  Type: On/Off ({(isDynamic ? "dynamic" : "static")})");
                Console.WriteLine($"  Description: {description}");
                Console.WriteLine($"  Default state: {(currentState == "+" ? "On (+)" : "Off (-)")}");
                Console.WriteLine("  1. Off (-)");
                Console.WriteLine("  2. On (+)");
                Console.WriteLine("  3. Keep current");

                while (true)
                {
                    Console.Write("  Choose [1-3]: ");
                    var c = Console.ReadLine()?.Trim() ?? "";
                    if (c == "1") return $"{prefix}{optName} - {description}";
                    if (c == "2") return $"{prefix}{optName} + {description}";
                    if (c == "3" || c == "") return $"{prefix}{optName} {currentState} {description}";
                    Console.WriteLine("  Please enter 1, 2, or 3");
                }
            }
        }

        private static List<string> PromptAndCustomizeOptions(List<string> optionsToAdd)
        {
            var customized = new List<string>();
            for (int i = 0; i < optionsToAdd.Count; i++)
            {
                var line = optionsToAdd[i].Trim();
                var prefix = line.Length >= 2 ? line.Substring(0, 2) : "";
                var optType = prefix.ToLower();
                bool isDynamic = prefix.Length > 0 && char.IsUpper(prefix[0]);
                var content = line.Length > 2 ? line.Substring(2).Trim() : "";
                var spaceIdx = content.IndexOf(' ');
                var optName = spaceIdx == -1 ? content : content.Substring(0, spaceIdx).Trim();
                var rest = spaceIdx == -1 ? "" : content.Substring(spaceIdx).Trim();

                string typeStr, valueStr, description;
                if (optType == "v:")
                {
                    var s = rest.IndexOf("<<"); var e = rest.IndexOf(">>");
                    if (s != -1 && e != -1)
                    { valueStr = $"<<{rest.Substring(s + 2, e - s - 2)}>>"; description = rest.Substring(e + 2).Trim(); }
                    else { valueStr = ""; description = rest; }
                    typeStr = "Value";
                }
                else if (optType == "c:")
                {
                    if (rest.StartsWith("-")) { valueStr = "Off (-)"; description = rest.Substring(1).Trim(); }
                    else if (rest.StartsWith("+")) { valueStr = "On (+)"; description = rest.Substring(1).Trim(); }
                    else { valueStr = "On (+)"; description = rest; }
                    typeStr = "On/Off";
                }
                else continue;

                Console.WriteLine($"\n--- Option {i + 1} of {optionsToAdd.Count} ---");
                Console.WriteLine($"  Name: {optName}");
                Console.WriteLine($"  Type: {typeStr} ({(isDynamic ? "dynamic" : "static")})");
                Console.WriteLine($"  Default: {valueStr}");
                Console.WriteLine($"  Description: {description}");

                if (ConsoleYesNo("Add this option?"))
                {
                    var customizedOpt = PromptOptionValue(optionsToAdd[i]);
                    customized.Add(customizedOpt);
                }
                else
                {
                    Console.WriteLine("  Skipped.");
                }
            }
            return customized;
        }

        #endregion

        #endregion

        #region set_messages (compile_msg)

        /// <summary>
        /// Interactive menu for compile_msg/set_messages.
        /// Matches Python set_messages.py main():
        ///   1. Mode selection: Import or Export
        ///   2. GONZO protection for imports
        ///   3. Run compile_msg or export
        /// </summary>
        public static int RunSetMessages(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor)
        {
            if (CheckRawMode(profile)) return 1;

            var profileName = profile.IsProfile ? profile.ProfileName : cmdvars.ServerNameOnly;
            var isGonzo = IsGonzoProfile(profileName);

            if (isGonzo)
            {
                // GONZO: export only — importing messages into GONZO is not allowed
                Console.WriteLine();
                Console.WriteLine("GONZO is the canonical message source.");
                Console.WriteLine("Only export is available for this server.");
                Console.WriteLine();

                Console.WriteLine("Exporting messages from GONZO...");
                RunMessageExport(cmdvars, profile, executor);
                Console.WriteLine("compile_msg DONE.");
                return 0;
            }

            // Non-GONZO: standard import/export menu
            Console.WriteLine();
            Console.WriteLine("Select operation:");
            Console.WriteLine("  1) Import messages from the files into the database");
            Console.WriteLine("  2) Export messages from the database into the files");
            Console.WriteLine("  99) Cancel");
            Console.WriteLine();

            string mode;
            while (true)
            {
                Console.Write("Enter choice (1, 2, or 99): ");
                var choice = Console.ReadLine()?.Trim() ?? "";
                if (choice == "1") { mode = "import"; break; }
                if (choice == "2") { mode = "export"; break; }
                if (choice == "99") { Console.WriteLine("Cancelled."); return 0; }
                Console.WriteLine("Invalid choice. Please enter 1, 2, or 99.");
            }

            if (mode == "export")
            {
                Console.WriteLine();
                Console.WriteLine("WARNING: Exporting from this server will override local message files.");
                if (!ConsoleYesNo("Are you sure?"))
                {
                    Console.WriteLine("Cancelled.");
                    return 0;
                }
                Console.WriteLine();
                Console.WriteLine("Exporting messages from database...");
                RunMessageExport(cmdvars, profile, executor);
                Console.WriteLine("compile_msg DONE.");
                return 0;
            }

            // Import mode (non-GONZO only)
            var mainDir = ibs_compiler_common.GetPath_Setup(profile);
            var mainMes = Path.Combine(mainDir, "css");
            if (!File.Exists(mainMes + ".ibs_msg"))
            {
                Console.WriteLine("ERROR: Message files not found at expected location.");
                Console.WriteLine($"Expected: {mainDir}");
                return 1;
            }

            Console.WriteLine("Compiling messages...");
            compile_msg_main.Run(cmdvars, profile, executor);
            return 0;
        }

        private static bool IsGonzoProfile(string profileName)
        {
            return profileName.ToUpper() == "GONZO" || profileName.ToUpper() == "G";
        }

        /// <summary>
        /// Export messages from database to flat files.
        /// Uses BCP OUT for each message table to the CSS/Setup directory.
        /// </summary>
        public static void RunMessageExport(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor)
        {
            var myOptions = new Options(cmdvars, profile, true);
            if (!myOptions.GenerateOptionFiles())
            {
                Console.WriteLine("ERROR: Could not generate option files.");
                return;
            }

            var mainDir = ibs_compiler_common.GetPath_Setup(profile);
            var mainMes = Path.Combine(mainDir, "css");

            // Export each message table type
            string[] tables = { "&ibs_messages&", "&ibs_message_groups&", "&jam_messages&", "&jam_message_groups&",
                                "&sqr_messages&", "&sqr_message_groups&", "&sql_messages&", "&sql_message_groups&",
                                "&gui_messages&", "&gui_message_groups&" };
            string[] extensions = { ".ibs_msg", ".ibs_msgrp", ".jam_msg", ".jam_msgrp",
                                    ".sqr_msg", ".sqr_msgrp", ".sql_msg", ".sql_msgrp",
                                    ".gui_msg", ".gui_msgrp" };
            string[] labels = { "IBS Messages", "IBS Message Groups", "JAM Messages", "JAM Message Groups",
                                "SQR Messages", "SQR Message Groups", "SQL Messages", "SQL Message Groups",
                                "GUI Messages", "GUI Message Groups" };

            var totalRows = 0;
            for (int i = 0; i < tables.Length; i++)
            {
                var resolvedTable = myOptions.ReplaceOptions(tables[i]);
                Console.WriteLine($"Exporting {labels[i]} ({resolvedTable})...");
                var result = executor.BulkCopy(resolvedTable, BcpDirection.OUT, mainMes + extensions[i]);
                if (!result.Returncode)
                {
                    Console.WriteLine($"ERROR: Failed to export {labels[i]}");
                    return;
                }
                int.TryParse(result.Output, out var rows);
                totalRows += rows;
                Console.WriteLine($"{rows} rows copied to {mainMes}{extensions[i]}");
                Console.WriteLine();
            }

            Console.WriteLine($"Export complete. {totalRows} total rows exported.");
        }

        #endregion
    }
}
