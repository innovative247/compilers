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

        /// <summary>
        /// Where the fully-resolved token→value set is cached on disk. Delete it (or use
        /// the <c>--rebuild</c> flag on set_options / set_table_locations) to force the
        /// next run to re-merge from the source option files.
        /// </summary>
        public string ResolvedOptionsPath => ibs_compiler_common.GetPath_ResolvedOptions(_cmdvars, _profile);

        /// <summary>
        /// Prints the resolved cache path (with its age) to the normal log channel. The cache
        /// lives in the shared system temp directory, so every command that builds or reads it
        /// says exactly where it is — otherwise a stale-cache symptom is undiagnosable without
        /// reading source. Deliberately NOT called from GenerateOptionFiles: that runs on every
        /// isqlline/runsql invocation and would drown ordinary query output.
        /// </summary>
        public void ReportResolvedOptionsPath()
        {
            var path = ResolvedOptionsPath;
            if (File.Exists(path))
            {
                var age = (int)DateTime.Now.Subtract(new FileInfo(path).CreationTime).TotalMinutes;
                ibs_compiler_common.WriteLine($"resolved options file: {path} ({age} min old, rebuilt after 60)", _cmdvars.OutFile);
            }
            else
            {
                ibs_compiler_common.WriteLine($"resolved options file: {path} (not built yet)", _cmdvars.OutFile);
            }
        }

        public bool GenerateOptionFiles()
        {
            // Only reached when SQL_SOURCE is unset and the system temp fallback is also
            // unusable — the cache normally lives under <SQL_SOURCE>/css/setup/temp.
            if (string.IsNullOrEmpty(_profile.IRPath) && string.IsNullOrEmpty(ibs_compiler_common.GetTempPath()))
                ibs_compiler_common.WriteLine("Variable TEMP not set. Using current directory for temp file storage.", _cmdvars.OutFile);

            var optFileSQL = ibs_compiler_common.GetPath_OptionsSQL(_cmdvars, _profile);
            var optFileCompany = ibs_compiler_common.GetPath_OptionsCompany(_profile);
            var optFileServer = ibs_compiler_common.GetPath_OptionsServer(_cmdvars, _profile);
            var tblFileServer = ibs_compiler_common.GetPath_TableLocations(_profile);
            var tblFileCompany = ibs_compiler_common.GetPath_TableLocationsCompany(_profile);

            var optFileFinal = ResolvedOptionsPath;

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

            if (!forceRebuild)
            {
                _arrOptions = ibs_compiler_common.BuildArrayFromDisk(optFileFinal);
                // An empty read means the shared cache was mid-rewrite by a parallel compile
                // agent (or briefly unreadable). Rebuild from source rather than run with no
                // options — running empty would leave &tokens& unresolved (SR 52910).
                if (_arrOptions.Count == 0) forceRebuild = true;
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

                // Atomic replace: a parallel agent reading this cache never sees a partial file.
                ibs_compiler_common.SaveArrayToDiskAtomic(_arrOptions, optFileFinal);

                // The 60-minute TTL above is measured from CreationTime, but an atomic
                // replace (and NTFS file tunneling on a delete/recreate) carries the ORIGINAL
                // creation time onto the new file — so a just-rebuilt cache can read as
                // hours old and be rebuilt again on the very next call. Stamp it so the age
                // reported by set_profile, and the TTL itself, mean what they say.
                try { File.SetCreationTime(optFileFinal, DateTime.Now); } catch { }
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
                        if (_profile.ServerType == SQLServerTypes.POSTGRES)
                            _arrOptions.Add(("&" + dbName + "&").PadRight(40)
                                + ibs_compiler_common.PgQualifiedName(dbLocation, dbName));
                        else
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
