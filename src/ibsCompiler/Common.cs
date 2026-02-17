using System.Text.RegularExpressions;
using ibsCompiler.Configuration;

namespace ibsCompiler
{
    /// <summary>
    /// Shared utilities ported from F4.8 common.cs.
    /// exec_process() and exec_bcp() are replaced by ISqlExecutor - the remaining
    /// file I/O, option generation, and argument parsing utilities live here.
    /// </summary>
    public static class ibs_compiler_common
    {
        public static bool OutputToStdErr { get; set; } = false;
        public static string DefaultOutFile { get; set; } = "";

        #region Console output
        public static void WriteLine(string text, string outputFile = "")
        {
            var target = !string.IsNullOrWhiteSpace(outputFile) ? outputFile : DefaultOutFile;
            if (!string.IsNullOrWhiteSpace(target))
                WriteLineToDisk(target, text);
            else if (OutputToStdErr)
                Console.Error.WriteLine(text);
            else
                Console.WriteLine(text);
        }

        public static void WriteLineToDisk(string fileName, string line)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return;
            using var fs = new FileStream(fileName, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(fs);
            writer.WriteLine(line);
        }

        public static bool ConsoleYesNo(string question)
        {
            while (true)
            {
                Console.WriteLine(question);
                var response = Console.ReadLine()?.ToUpper();
                if (response == "Y") return true;
                if (response == "N") return false;
            }
        }
        #endregion

        #region File utilities
        public static bool FindFile(ref string fileName)
        {
            // Normalize path separators for current platform
            fileName = fileName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

            if (File.Exists(fileName)) return true;
            if (File.Exists(fileName + ".sql")) { fileName += ".sql"; return true; }

            var fn = NonLinkedFilename(fileName);
            if (File.Exists(fn)) { fileName = fn; return true; }
            if (File.Exists(fn + ".sql")) { fileName = fn + ".sql"; return true; }

            // Wildcard lookup
            var dir = Path.GetDirectoryName(fileName);
            var file = Path.GetFileName(fileName);
            if (string.IsNullOrEmpty(dir)) dir = ".";
            try
            {
                var files = Directory.GetFiles(dir, file);
                if (files.Length == 1)
                {
                    fileName = files[0];
                    if (fileName.StartsWith("." + Path.DirectorySeparatorChar))
                        fileName = fileName.Substring(2);
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static string NonLinkedFilename(string argFilename)
        {
            string[,] ConvertPaths =
            {
                {@"[\\/]ss[\\/]api[\\/]",    Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar + "Application_Program_Interface" + Path.DirectorySeparatorChar},
                {@"[\\/]ss[\\/]api2[\\/]",   Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar + "Application_Program_Interface_V2" + Path.DirectorySeparatorChar},
                {@"[\\/]ss[\\/]api3[\\/]",   Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar + "Application_Program_Interface_V3" + Path.DirectorySeparatorChar},
                {@"[\\/]ss[\\/]at[\\/]",     Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar + "Alarm_Treatment" + Path.DirectorySeparatorChar},
                {@"[\\/]ss[\\/]ba[\\/]",     Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar + "Basics" + Path.DirectorySeparatorChar},
                {@"[\\/]ss[\\/]bl[\\/]",     Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar + "Billing" + Path.DirectorySeparatorChar},
                {@"[\\/]ss[\\/]ct[\\/]",     Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar + "Create_Temp" + Path.DirectorySeparatorChar},
                {@"[\\/]ss[\\/]cv[\\/]",     Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar + "Conversions" + Path.DirectorySeparatorChar},
                {@"[\\/]ss[\\/]da[\\/]",     Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar + "da" + Path.DirectorySeparatorChar},
                {@"[\\/]ss[\\/]dv[\\/]",     Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar + "IBS_Development" + Path.DirectorySeparatorChar},
                {@"[\\/]ss[\\/]fe[\\/]",     Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar + "Front_End" + Path.DirectorySeparatorChar},
                {@"[\\/]ss[\\/]in[\\/]",     Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar + "Internal" + Path.DirectorySeparatorChar},
                {@"[\\/]ss[\\/]ma[\\/]",     Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar + "Co_Monitoring" + Path.DirectorySeparatorChar},
                {@"[\\/]ss[\\/]mb[\\/]",     Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar + "Mobile" + Path.DirectorySeparatorChar},
                {@"[\\/]ss[\\/]mo[\\/]",     Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar + "Monitoring" + Path.DirectorySeparatorChar},
                {@"[\\/]ss[\\/]mobile[\\/]", Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar + "Mobile" + Path.DirectorySeparatorChar},
                {@"[\\/]ss[\\/]sdi[\\/]",    Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar + "SDI_App" + Path.DirectorySeparatorChar},
                {@"[\\/]ss[\\/]si[\\/]",     Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar + "System_Init" + Path.DirectorySeparatorChar},
                {@"[\\/]ss[\\/]sv[\\/]",     Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar + "Service" + Path.DirectorySeparatorChar},
                {@"[\\/]ss[\\/]tm[\\/]",     Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar + "Telemarketing" + Path.DirectorySeparatorChar},
                {@"[\\/]ss[\\/]test[\\/]",   Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar + "Test" + Path.DirectorySeparatorChar},
                {@"[\\/]ss[\\/]ub[\\/]",     Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar + "US_Basics" + Path.DirectorySeparatorChar},
                {@"[\\/]ibs[\\/]ss[\\/]",    Path.DirectorySeparatorChar + "IBS" + Path.DirectorySeparatorChar + "SQL_Sources" + Path.DirectorySeparatorChar}
            };

            int orgLength = argFilename.Length;
            if (Regex.IsMatch(argFilename, @"([\\/])(css|ibs)([\\/])", RegexOptions.IgnoreCase))
            {
                for (int i = 0; i < ConvertPaths.GetLength(0); ++i)
                {
                    argFilename = Regex.Replace(argFilename, ConvertPaths[i, 0], ConvertPaths[i, 1], RegexOptions.IgnoreCase);
                    if (argFilename.Length != orgLength)
                        return argFilename;
                }
            }
            return argFilename;
        }

        public static void MergeTextFiles(string sourceFile, string destinationFile)
        {
            try
            {
                using var source = new StreamReader(sourceFile);
                using var dest = new StreamWriter(destinationFile, true);
                string? line;
                while ((line = source.ReadLine()) != null)
                    dest.WriteLine(line);
            }
            catch { }
        }

        public static bool SaveArrayToDisk(List<string> sourceFile, string destinationFile)
        {
            using var dest = new StreamWriter(destinationFile, false);
            foreach (var line in sourceFile)
                dest.WriteLine(line);
            return true;
        }

        public static List<string> BuildArrayFromDisk(string sourceFile)
        {
            var arr = new List<string>();
            using var source = new StreamReader(sourceFile);
            string? line;
            while ((line = source.ReadLine()) != null)
                arr.Add(line);
            return arr;
        }
        #endregion

        #region Temp files
        public static string GetTempPath()
        {
            var mypath = Path.GetTempPath();
            if (mypath.Contains(' '))
            {
                mypath = Path.Combine(AppContext.BaseDirectory, "temp");
                if (!Directory.Exists(mypath)) Directory.CreateDirectory(mypath);
                if (!Directory.Exists(mypath)) mypath = "";
                else mypath += Path.DirectorySeparatorChar;
            }
            return mypath;
        }

        public static string GetTempFile()
        {
            return Path.Combine(GetTempPath(), Path.GetRandomFileName());
        }
        #endregion

        #region Validation
        public static bool ValidateSeqFirstLast(CommandVariables cmdvars)
        {
            return cmdvars.SeqFirst <= cmdvars.SeqLast;
        }
        #endregion

        #region File paths (using ResolvedProfile instead of WindowsVariables)
        public static string GetPath_Actions(CommandVariables cmdvars, ResolvedProfile profile)
        {
            var serverName = profile.IsProfile ? profile.ProfileName : cmdvars.ServerNameOnly;
            var serverSpecific = Path.Combine(profile.IRPath, "CSS", "Setup", "actions." + serverName);
            if (File.Exists(serverSpecific)) return serverSpecific;
            return Path.Combine(profile.IRPath, "CSS", "Setup", "actions");
        }

        public static string GetPath_ActionsDetail(ResolvedProfile profile)
        {
            return Path.Combine(profile.IRPath, "CSS", "Setup", "actions_dtl");
        }

        public static string GetPath_OptionsDefault(ResolvedProfile profile)
        {
            return Path.Combine(profile.IRPath, "CSS", "Setup", "options.def");
        }

        public static string GetPath_OptionsSQL(CommandVariables cmdvars, ResolvedProfile profile)
        {
            return Path.Combine(profile.IRPath, "CSS", "Setup", "options." + profile.ServerType);
        }

        public static string GetPath_OptionsCompany(ResolvedProfile profile)
        {
            return Path.Combine(profile.IRPath, "CSS", "Setup", "options." + profile.Company);
        }

        public static string GetPath_OptionsServer(CommandVariables cmdvars, ResolvedProfile profile)
        {
            var serverName = (profile.IsProfile ? profile.ProfileName : cmdvars.Server)
                .Replace('\\', '_').Replace('.', '_');
            return Path.Combine(profile.IRPath, "CSS", "Setup", "options." + profile.Company + "." + serverName);
        }

        public static string GetPath_TableLocations(ResolvedProfile profile)
        {
            return Path.Combine(profile.IRPath, "CSS", "Setup", "table_locations");
        }

        public static string GetPath_TableLocationsCompany(ResolvedProfile profile)
        {
            return Path.Combine(profile.IRPath, "CSS", "Setup", "table_locations." + profile.Company);
        }

        public static string GetPath_Setup(ResolvedProfile profile)
        {
            return Path.Combine(profile.IRPath, "CSS", "Setup");
        }

        public static string GetPath_MessageBackup(ResolvedProfile profile)
        {
            return Path.Combine(profile.IRPath, "CSS", "Setup", "backup");
        }
        #endregion

        #region Argument parsing
        /// <summary>
        /// Used by isqlline, runsql where database and command are necessary.
        /// </summary>
        public static CommandVariables isql_variables(List<string> arguments, ProfileManager profileMgr)
        {
            var myargs = DefaultCommandVariables(ref arguments);
            if (arguments.Count >= 3)
            {
                foreach (var arg in arguments)
                {
                    if (arg.Length > 1)
                    {
                        switch (arg.Substring(0, 2).ToUpper())
                        {
                            case "-D": myargs.Database = arg.Substring(2); break;
                            case "-S": myargs.Server = arg.Substring(2); break;
                        }
                    }
                }
                if (myargs.Server == "") myargs.Server = arguments[arguments.Count - 1];
                if (myargs.Database == "") myargs.Database = arguments[arguments.Count - 2];
                if (myargs.Command == "") myargs.Command = arguments[arguments.Count - 3];
                if (myargs.OutFile != "") try { File.Delete(myargs.OutFile); } catch { }
            }
            return myargs;
        }

        /// <summary>
        /// Used by compile_xxx, import_options - only Server required.
        /// </summary>
        public static CommandVariables compile_variables(List<string> arguments, ProfileManager profileMgr)
        {
            var myargs = DefaultCommandVariables(ref arguments);
            if (arguments.Count >= 1)
            {
                foreach (var arg in arguments)
                {
                    if (arg.Length > 1 && arg.Substring(0, 2).ToUpper() == "-S")
                        myargs.Server = arg.Substring(2);
                }
                if (myargs.Server == "") myargs.Server = arguments[arguments.Count - 1];
                if (myargs.OutFile != "") try { File.Delete(myargs.OutFile); } catch { }
            }
            return myargs;
        }

        /// <summary>
        /// Used by runcreate - Server and Command required.
        /// </summary>
        public static CommandVariables runcreate_variables(List<string> arguments, ProfileManager profileMgr)
        {
            var myargs = DefaultCommandVariables(ref arguments);
            if (arguments.Count >= 2)
            {
                // Positional: <script> <server/profile> [outfile]
                myargs.Command = arguments[0];
                myargs.Server = arguments[1];

                // Third positional arg is outfile (alternative to -O flag)
                if (arguments.Count >= 3 && string.IsNullOrEmpty(myargs.OutFile))
                    myargs.OutFile = arguments[2];

                // Resolve outfile to full path
                if (!string.IsNullOrEmpty(myargs.OutFile))
                {
                    if (!Path.IsPathRooted(myargs.OutFile))
                        myargs.OutFile = Path.Combine(Environment.CurrentDirectory, myargs.OutFile);
                    try { File.Delete(myargs.OutFile); } catch { }
                }
            }
            return myargs;
        }

        /// <summary>
        /// Used by i_run_upgrade - Server, Upgrade_No, Command required.
        /// </summary>
        public static CommandVariables i_run_upgrade_variables(List<string> arguments, ProfileManager profileMgr)
        {
            var myargs = DefaultCommandVariables(ref arguments);
            int count = arguments.Count;
            if (count >= 3)
            {
                foreach (var arg in arguments)
                {
                    if (arg.Length > 1)
                    {
                        switch (arg.Substring(0, 2).ToUpper())
                        {
                            case "-D": myargs.Database = arg.Substring(2); break;
                            case "-S": myargs.Server = arg.Substring(2); break;
                        }
                    }
                }
                if (myargs.Command == "") myargs.Command = arguments[count - 1];
                if (myargs.Upgrade_no == "") myargs.Upgrade_no = arguments[count - 2];
                if (myargs.Server == "") myargs.Server = arguments[count - 3];
                if (count > 3 && myargs.Database == "") myargs.Database = arguments[count - 4];
                if (myargs.OutFile != "") try { File.Delete(myargs.OutFile); } catch { }
            }
            return myargs;
        }

        /// <summary>
        /// Used by bcp_data - Server and Bcp direction required.
        /// </summary>
        public static CommandVariables bcp_data_variables(List<string> arguments, ProfileManager profileMgr)
        {
            var myargs = DefaultCommandVariables(ref arguments);
            if (arguments.Count >= 2)
            {
                foreach (var arg in arguments)
                {
                    if (arg.Length > 1 && arg.Substring(0, 2).ToUpper() == "-S")
                        myargs.Server = arg.Substring(2);
                }
                if (myargs.Server == "") myargs.Server = arguments[arguments.Count - 1];
                if (myargs.Bcp == "") myargs.Bcp = arguments[arguments.Count - 2];
                if (myargs.OutFile != "") try { File.Delete(myargs.OutFile); } catch { }
                myargs.Bcp = myargs.Bcp.ToUpper();
            }
            return myargs;
        }

        private static CommandVariables DefaultCommandVariables(ref List<string> arguments)
        {
            var args = new CommandVariables();
            args.ServerType = FindAndRemove_SQLServerType(ref arguments);
            args.User = FindAndRemove("-U", ref arguments);
            args.Pass = FindAndRemove("-P", ref arguments);
            args.OutFile = FindAndRemove("-O", ref arguments);
            args.EchoInput = FindAndRemove_Flag("-E", ref arguments);
            args.SeqFirst = FindAndRemove_Int("-F", ref arguments);
            args.SeqLast = FindAndRemove_Int("-L", ref arguments);
            args.ChangeLog = FindAndRemove_BoolFlag("--changelog", ref arguments, defaultValue: true);
            args.Preview = FindAndRemove_BoolFlag("--preview", ref arguments, defaultValue: false);

            args.Command = "";
            args.Database = "";
            args.Server = "";
            args.Upgrade_no = "";
            args.User = string.IsNullOrEmpty(args.User) ? "sbn0" : args.User;
            args.Pass = string.IsNullOrEmpty(args.Pass) ? "ibsibs" : args.Pass;
            args.Bcp = "";
            return args;
        }

        private static string FindAndRemove(string flag, ref List<string> arguments)
        {
            for (int i = 0; i < arguments.Count; i++)
            {
                if (arguments[i].Length > 1 && arguments[i].Substring(0, 2).ToUpper() == flag.ToUpper())
                {
                    var value = arguments[i].Substring(2);
                    arguments.RemoveAt(i);
                    // If flag was provided without attached value (e.g. -O out.txt), take next argument
                    if (string.IsNullOrEmpty(value) && i < arguments.Count)
                    {
                        value = arguments[i];
                        arguments.RemoveAt(i);
                    }
                    return value;
                }
            }
            return "";
        }

        private static bool FindAndRemove_Flag(string flag, ref List<string> arguments)
        {
            for (int i = 0; i < arguments.Count; i++)
            {
                if (arguments[i].Length > 1 && arguments[i].Substring(0, 2).ToUpper() == flag.ToUpper())
                {
                    arguments.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        private static int FindAndRemove_Int(string flag, ref List<string> arguments)
        {
            for (int i = 0; i < arguments.Count; i++)
            {
                if (arguments[i].Length > 1 && arguments[i].Substring(0, 2).ToUpper() == flag.ToUpper())
                {
                    var str = arguments[i].Substring(2).Trim();
                    arguments.RemoveAt(i);
                    if (string.IsNullOrEmpty(str) && i < arguments.Count)
                    {
                        str = arguments[i].Trim();
                        arguments.RemoveAt(i);
                    }
                    if (string.IsNullOrEmpty(str)) str = "1";
                    return int.TryParse(str, out var val) ? val : 0;
                }
            }
            return 0;
        }

        private static bool FindAndRemove_BoolFlag(string flag, ref List<string> arguments, bool defaultValue)
        {
            var rx = new Regex(@"^" + Regex.Escape(flag) + @"(:[yn])?$", RegexOptions.IgnoreCase);
            for (int i = 0; i < arguments.Count; i++)
            {
                var m = rx.Match(arguments[i]);
                if (m.Success)
                {
                    arguments.RemoveAt(i);
                    return m.Groups[1].Value.ToLower() != ":n";
                }
            }
            return defaultValue;
        }

        private static SQLServerTypes FindAndRemove_SQLServerType(ref List<string> arguments)
        {
            for (int i = 0; i < arguments.Count; i++)
            {
                if (arguments[i].ToUpper() == "-MSSQL")
                {
                    arguments.RemoveAt(i);
                    return SQLServerTypes.MSSQL;
                }
                else if (arguments[i].ToUpper() == "-SYBASE")
                {
                    arguments.RemoveAt(i);
                    return SQLServerTypes.SYBASE;
                }
            }
            return default;
        }
        #endregion

        #region Option file generation
        public static List<string> CombineOptionFiles(List<string> source1File, List<string> source2File)
        {
            // Combine company (source1) and server/profile (source2) options.
            // Profile overrides company for same option name.
            // All unique options from BOTH sources are included.
            var optionsDict = new Dictionary<string, string>();
            foreach (var line in source1File)
            {
                var key = line.Split(' ').First();
                optionsDict[key] = line;
            }
            foreach (var line in source2File)
            {
                var key = line.Split(' ').First();
                optionsDict[key] = line; // Override company with profile
            }
            return optionsDict.Values.ToList();
        }

        public static List<string> CombineSQLSrvOptionFiles(List<string> source1File, List<string> source2File, List<string> source3File)
        {
            var newarr = new List<string>();
            var srcDictionary = new Dictionary<string, string>();
            foreach (var line in source3File)
            {
                var key = line.Split(' ').First();
                srcDictionary[key] = line;
                newarr.Add(line);
            }
            foreach (var line in source2File)
            {
                var key = line.Split(' ').First();
                if (!srcDictionary.ContainsKey(key))
                {
                    srcDictionary[key] = line;
                    newarr.Add(line);
                }
            }
            foreach (var line in source1File)
            {
                var key = line.Split(' ').First();
                if (!srcDictionary.ContainsKey(key))
                {
                    srcDictionary[key] = line;
                    newarr.Add(line);
                }
            }
            return newarr;
        }

        public static List<string> GenerateCompileOptionFile(string sourceFile)
        {
            var dest = new List<string>();
            using var source = new StreamReader(sourceFile);
            string? line;
            while ((line = source.ReadLine()) != null)
            {
                if (line.Length > 1)
                {
                    switch (line.Substring(0, 2))
                    {
                        case "v:":
                        {
                            var opt_name = "&" + line.Substring(2, line.IndexOf(' ') - 1).Trim() + "&";
                            var opt_value = line.Substring(line.IndexOf("<<") + 2, line.IndexOf(">>") - line.IndexOf("<<") - 2).Trim();
                            dest.Add(opt_name.PadRight(40) + opt_value.PadRight(200));
                            break;
                        }
                        case "c:":
                        {
                            var opt_name = line.Substring(2, line.IndexOf(' ') - 1).Trim();
                            var opt_value = line.Substring(11, 1).Trim();
                            string if_, endif_, ifn_, endifn_;
                            if (opt_value == "+")
                            {
                                if_ = ""; endif_ = ""; ifn_ = "/*"; endifn_ = "*/";
                            }
                            else
                            {
                                if_ = "/*"; endif_ = "*/"; ifn_ = ""; endifn_ = "";
                            }
                            dest.Add(("&if_" + opt_name.Trim() + "&").PadRight(40) + if_.PadRight(200));
                            dest.Add(("&endif_" + opt_name.Trim() + "&").PadRight(40) + endif_.PadRight(200));
                            dest.Add(("&ifn_" + opt_name.Trim() + "&").PadRight(40) + ifn_.PadRight(200));
                            dest.Add(("&endifn_" + opt_name.Trim() + "&").PadRight(40) + endifn_.PadRight(200));
                            break;
                        }
                    }
                }
            }
            return dest;
        }

        public static List<string> GenerateImportOptionFile(string sourceFile)
        {
            var dest = new List<string>();
            if (!File.Exists(sourceFile)) return dest;
            using var source = new StreamReader(sourceFile);
            string? line;
            while ((line = source.ReadLine()) != null)
            {
                if (line.Length > 1 && !line.StartsWith("#"))
                {
                    var opt_type = line.Substring(0, 2);
                    if (opt_type == "v:" || opt_type == "V:" || opt_type == "c:" || opt_type == "C:")
                    {
                        line = line.Substring(2).Trim();
                        var opt_name = line.Substring(0, line.IndexOf(" ")).Trim();
                        string mystr = "";

                        if (opt_type == "v:" || opt_type == "V:")
                        {
                            line = line.Substring(line.IndexOf("<<")).Trim();
                            var opt_value = line.Substring(line.IndexOf("<<"), line.IndexOf(">>") + 2);
                            var opt_desc = line.Replace(opt_value, "").Trim();
                            mystr = ":>" + opt_name.PadRight(8) + " - - + " + (opt_type == "V:" ? "+" : "-") + " " + opt_value + " " + opt_desc.PadRight(200);
                        }
                        else if (opt_type == "c:" || opt_type == "C:")
                        {
                            line = line.Replace(opt_name, "").Trim();
                            var opt_value = line.StartsWith("-") ? "-" : "+";
                            var opt_desc = line.Replace(opt_value, "").Trim();
                            mystr = ":>" + opt_name.PadRight(8) + " " + opt_value + " + - " + (opt_type == "C:" ? "+" : "-") + " " + opt_desc.PadRight(200);
                        }

                        if (mystr != "")
                        {
                            if (mystr.Length > 254) mystr = mystr.Substring(0, 254);
                            dest.Add(mystr);
                        }
                    }
                }
            }
            return dest;
        }

        public static List<string> FindNewOptions(List<string> options_def, List<string> options)
        {
            var newOptions = new List<string>();
            foreach (var defLine in options_def)
            {
                if (defLine.ToUpper().StartsWith("C:") || defLine.ToUpper().StartsWith("V:"))
                {
                    bool found = false;
                    foreach (var optLine in options)
                    {
                        if (optLine.Length > 9 &&
                            defLine.Replace("\t", " ").Substring(0, 9) == optLine.Replace("\t", " ").Substring(0, 9))
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!found) newOptions.Add(defLine);
                }
            }
            return newOptions;
        }

        public static List<string> InsertNewOptions(List<string> baseOptions, List<string> optionsToInsert)
        {
            foreach (var line in optionsToInsert)
                baseOptions.Add("NEW->" + line);
            return baseOptions;
        }

        public static List<string> RemoveOptions(List<string> baseOptions, List<string> optionsToRemove)
        {
            var removeDict = new Dictionary<string, string>();
            foreach (var line in optionsToRemove)
            {
                if (line.Trim().Length > 1 && !line.StartsWith("#"))
                {
                    var key = line.Split(' ').First();
                    removeDict[key] = line;
                }
            }
            return baseOptions.Where(line => !removeDict.ContainsKey(line.Split(' ').First())).ToList();
        }
        #endregion
    }
}
