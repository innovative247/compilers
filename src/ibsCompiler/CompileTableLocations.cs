using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace ibsCompiler
{
    /// <summary>
    /// Port of F4.8 compile_table_locations.cs.
    /// Table locations compilation via managed bulk copy.
    /// </summary>
    public static class compile_table_locations_main
    {
        public static ExecReturn Run(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor)
        {
            var result = new ExecReturn { Returncode = false, Output = "" };

            var myOptions = new Options(cmdvars, profile, true);
            if (!myOptions.GenerateOptionFiles()) return result;

            var tabLoc = ibs_compiler_common.GetPath_TableLocations(profile);
            if (!File.Exists(tabLoc))
            {
                ibs_compiler_common.WriteLine("table_location file missing (" + tabLoc + ")", cmdvars.OutFile);
                return result;
            }

            ibs_compiler_common.WriteLine("compile_table_locations started at " + DateTime.Now, cmdvars.OutFile);

            cmdvars.Database = "ibs";
            cmdvars.Command = "truncate table ibs..table_locations";
            ibs_compiler_common.WriteLine("Executing " + cmdvars.Command, cmdvars.OutFile);
            isqlline_main.Run(cmdvars, profile, executor, myOptions);

            // Create temp file with filtered data
            var tempFile = Path.Combine(ibs_compiler_common.GetTempPath(), "table_locations.tmp");
            ibs_compiler_common.WriteLine("creating temp file: " + tempFile, cmdvars.OutFile);

            using (var source = new StreamReader(tabLoc))
            using (var dest = new StreamWriter(tempFile, false))
            {
                string? line;
                while ((line = source.ReadLine()) != null)
                {
                    if (line.Length > 2 && line.Substring(0, 2) == "->")
                    {
                        var tblName = line.Substring(2, line.IndexOf("&") - 2).Replace('\t', ' ').Trim();
                        int iStart = 0;
                        int i = line.IndexOf("&", iStart);
                        iStart = i + 1;
                        int j = line.IndexOf("&", iStart);
                        var optName = line.Substring(i, j - i + 1);
                        var dbName = myOptions.ReplaceWord(optName);
                        var fullName = dbName + ".." + tblName;
                        dest.WriteLine($"{tblName}\t{optName.Replace("&", "")}\t{dbName}\t{fullName}");
                    }
                }
            }

            ibs_compiler_common.WriteLine("Starting table_locations insert...", cmdvars.OutFile);
            result = executor.BulkCopy("ibs..table_locations", BcpDirection.IN, tempFile);

            ibs_compiler_common.WriteLine("deleting temp file: " + tempFile, cmdvars.OutFile);
            try { File.Delete(tempFile); } catch { }
            ibs_compiler_common.WriteLine("compile_table_locations completed at " + DateTime.Now, cmdvars.OutFile);

            return result;
        }
    }
}
