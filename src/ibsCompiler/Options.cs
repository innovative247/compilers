using ibsCompiler.Configuration;

namespace ibsCompiler
{
    /// <summary>
    /// Port of F4.8 Options class.
    /// Generates merged option files from SQL-type, company, and server option sources.
    /// Handles &placeholder& token replacement and @sequence@ substitution.
    /// </summary>
    public class Options
    {
        private readonly ResolvedProfile _profile;
        private readonly CommandVariables _cmdvars;
        private readonly bool _forceRebuild;
        private List<string> _arrOptions = new();

        public Options(CommandVariables cmdvars, ResolvedProfile profile, bool forceRebuild = false)
        {
            _profile = profile;
            _cmdvars = cmdvars;
            _forceRebuild = forceRebuild;
        }

        public bool GenerateOptionFiles()
        {
            var tempPath = ibs_compiler_common.GetTempPath();
            if (string.IsNullOrEmpty(tempPath))
            {
                ibs_compiler_common.WriteLine("Variable TEMP not set. Using current directory for temp file storage.", _cmdvars.OutFile);
                tempPath = "." + Path.DirectorySeparatorChar;
            }

            var optFileSQL = ibs_compiler_common.GetPath_OptionsSQL(_cmdvars, _profile);
            var optFileCompany = ibs_compiler_common.GetPath_OptionsCompany(_profile);
            var optFileServer = ibs_compiler_common.GetPath_OptionsServer(_cmdvars, _profile);
            var tblFileServer = ibs_compiler_common.GetPath_TableLocations(_profile);
            var tblFileCompany = ibs_compiler_common.GetPath_TableLocationsCompany(_profile);

            string optFileFinal;
            var serverName = (_profile.IsProfile ? _profile.ProfileName : _cmdvars.Server)
                .Replace('\\', '_').Replace('.', '_');
            if (File.Exists(optFileSQL))
                optFileFinal = Path.Combine(tempPath, $"options.{_profile.ServerType}.{_profile.Company}.{serverName}.tmp");
            else
                optFileFinal = Path.Combine(tempPath, $"options.{_profile.Company}.{serverName}.tmp");

            bool forceRebuild = _forceRebuild;
            if (!File.Exists(optFileFinal))
            {
                forceRebuild = true;
            }
            else
            {
                var fi = new FileInfo(optFileFinal);
                if (DateTime.Now.Subtract(fi.CreationTime).TotalMinutes > 60)
                    forceRebuild = true;
            }

            if (forceRebuild)
            {
                if (!File.Exists(optFileCompany))
                {
                    ibs_compiler_common.WriteLine("Company Option File Missing! " + optFileCompany, _cmdvars.OutFile);
                    return false;
                }
                if (!File.Exists(optFileServer))
                {
                    ibs_compiler_common.WriteLine("Warning! Server Option File Missing! " + optFileServer, _cmdvars.OutFile);
                }
                if (!File.Exists(tblFileServer))
                {
                    ibs_compiler_common.WriteLine("Table Locations File Missing! " + tblFileServer, _cmdvars.OutFile);
                    return false;
                }

                try { File.Delete(optFileFinal); } catch { }

                List<string> tmpOptFileSQL = new();
                List<string> tmpOptFileCompany;
                List<string> tmpOptFileServer = new();

                if (File.Exists(optFileSQL))
                {
                    tmpOptFileSQL = ibs_compiler_common.GenerateCompileOptionFile(optFileSQL);
                }

                tmpOptFileCompany = ibs_compiler_common.GenerateCompileOptionFile(optFileCompany);

                // Add &cmpy& and &lang& from profile
                if (!string.IsNullOrEmpty(_profile.Company))
                    tmpOptFileCompany.Add("&cmpy&".PadRight(40) + _profile.Company.PadRight(200));
                if (!string.IsNullOrEmpty(_profile.Language))
                    tmpOptFileCompany.Add("&lang&".PadRight(40) + _profile.Language.PadRight(200));

                if (File.Exists(optFileServer))
                {
                    tmpOptFileServer = ibs_compiler_common.GenerateCompileOptionFile(optFileServer);
                }

                if (tmpOptFileSQL.Count > 0)
                    _arrOptions = ibs_compiler_common.CombineSQLSrvOptionFiles(tmpOptFileSQL, tmpOptFileCompany, tmpOptFileServer);
                else
                    _arrOptions = ibs_compiler_common.CombineOptionFiles(tmpOptFileCompany, tmpOptFileServer);

                MergeTableFileIntoOptionFile(tblFileServer);

                ibs_compiler_common.SaveArrayToDisk(_arrOptions, optFileFinal);
            }
            else
            {
                _arrOptions = ibs_compiler_common.BuildArrayFromDisk(optFileFinal);
            }
            return true;
        }

        private bool MergeTableFileIntoOptionFile(string sourceFile)
        {
            if (_arrOptions.Count == 0) return false;
            int lineNo = 0;
            string? line = "";
            using var source = new StreamReader(sourceFile);
            try
            {
                while ((line = source.ReadLine()) != null)
                {
                    lineNo++;
                    if (line.Trim().Length > 0 && line.Substring(0, 2).Trim() == "->")
                    {
                        var dbName = line.Substring(2, line.IndexOf("&") - 2).Trim();
                        int iStart = 0;
                        int i = line.IndexOf("&", iStart);
                        iStart = i + 1;
                        int j = line.IndexOf("&", iStart);
                        var optValue = line.Substring(i, j - i + 1);
                        var dbLocation = ReplaceWord(optValue);
                        _arrOptions.Add(("&" + dbName + "&").PadRight(40) + dbLocation + ".." + dbName);
                        _arrOptions.Add(("&db-" + dbName + "&").PadRight(40) + dbLocation);
                    }
                }
            }
            catch (Exception)
            {
                ibs_compiler_common.WriteLine($"Error merging line no {lineNo}: {line}");
                throw;
            }
            return true;
        }

        public string ReplaceWord(string myText)
        {
            if (_arrOptions.Count == 0) return myText;
            foreach (var line in _arrOptions)
            {
                if (!myText.Contains("&")) return myText;
                if (line.Length >= 40)
                    myText = myText.Replace(line.Substring(0, 40).Trim(), line.Substring(40).Trim());
            }
            return myText;
        }

        public string ReplaceOptions(string sourceString, int sequence = -1)
        {
            if (sequence > -1)
                sourceString = sourceString.Replace("@sequence@", sequence.ToString());
            return ReplaceWord(sourceString);
        }

        public List<string> ReplaceOptions(List<string> sourceStrings)
        {
            for (int i = 0; i < sourceStrings.Count; i++)
                sourceStrings[i] = ReplaceOptions(sourceStrings[i]);
            return sourceStrings;
        }
    }
}
