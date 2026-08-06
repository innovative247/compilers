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
        public static string DefaultErrFile { get; set; } = "";

        #region Platform parsing
        /// <summary>
        /// Canonicalizes a platform string to a SQLServerTypes value.
        /// "MSSQL" -> MSSQL, "POSTGRES" -> POSTGRES, anything else
        /// (including null) -> SYBASE (preserves the legacy unknown->SYBASE default).
        /// </summary>
        public static SQLServerTypes ParsePlatform(string? platform)
        {
            switch (platform?.Trim().ToUpperInvariant())
            {
                case "MSSQL": return SQLServerTypes.MSSQL;
                case "POSTGRES": return SQLServerTypes.POSTGRES;
                default: return SQLServerTypes.SYBASE;
            }
        }

        /// <summary>
        /// Default TCP port for a database platform: MSSQL=1433, POSTGRES=5432, else 5000.
        /// </summary>
        public static int DefaultPort(SQLServerTypes t)
        {
            switch (t)
            {
                case SQLServerTypes.MSSQL: return 1433;
                case SQLServerTypes.POSTGRES: return 5432;
                default: return 5000;
            }
        }

        /// <summary>
        /// The ONLY place the persisted/filename string form of a platform may be
        /// produced. Returns the enum name ("SYBASE"/"MSSQL"/"POSTGRES"). All code
        /// that writes a platform to a profile/settings file or builds a platform
        /// filename must route through here — never call ServerType.ToString() or
        /// concatenate a raw "POSTGRES" literal.
        /// </summary>
        public static string CanonicalName(SQLServerTypes t) => t.ToString();

        /// <summary>
        /// The ONLY ordered source of platform values. Numeric wizard menus, the
        /// editor platform cycle, and CLI flag-name lists must be built from this.
        /// There is no separate "display label" — every menu/label/value shows the
        /// canonical token (SYBASE / MSSQL / POSTGRES) via <see cref="CanonicalName"/>.
        /// </summary>
        public static readonly SQLServerTypes[] PlatformMenu =
        {
            SQLServerTypes.SYBASE,
            SQLServerTypes.MSSQL,
            SQLServerTypes.POSTGRES
        };

        /// <summary>
        /// Joins the canonical names over PlatformMenu, e.g. "SYBASE|MSSQL|POSTGRES".
        /// The ONLY place a joined list of platform tokens may be produced.
        /// </summary>
        public static string CanonicalNamesJoined(string sep = "|")
            => string.Join(sep, PlatformMenu.Select(t => CanonicalName(t)));

        /// <summary>
        /// The ONLY place a PostgreSQL identifier is double-quoted. Returns the
        /// identifier double-quoted iff it contains a character PostgreSQL will not
        /// accept in a bare identifier — notably the SBN work-table '#' marker
        /// (`w#ma_ins_services`, `s#olog`), which otherwise emits as a bare `#` and
        /// trips `syntax error at or near "#"`. A bare PG identifier may hold only
        /// letters, digits, '_' and '$' and must not start with a digit; uppercase
        /// is left bare (PG folds it, no error) so plain names stay byte-identical.
        /// Already-quoted input is returned unchanged; embedded quotes are doubled.
        /// Only ever used on the POSTGRES emission path — never on SYBASE/MSSQL.
        /// </summary>
        public static string PgQuoteIdentifierIfNeeded(string identifier)
        {
            if (string.IsNullOrEmpty(identifier)) return identifier;
            if (identifier.Length >= 2 && identifier[0] == '"' && identifier[^1] == '"')
                return identifier; // caller already hand-quoted it
            bool needsQuote = false;
            for (int i = 0; i < identifier.Length; i++)
            {
                char c = identifier[i];
                bool bare = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                            || (c >= '0' && c <= '9') || c == '_' || c == '$';
                if (i == 0 && c >= '0' && c <= '9') bare = false; // no leading digit
                if (!bare) { needsQuote = true; break; }
            }
            if (!needsQuote) return identifier;
            return "\"" + identifier.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>
        /// Builds a schema-qualified PostgreSQL object reference
        /// (`schema.object`), double-quoting the schema and/or object part only
        /// when each needs it (see <see cref="PgQuoteIdentifierIfNeeded"/>). A
        /// plain `sbnmaster.ma_ins_services` stays bare; a '#' work table becomes
        /// `sbnmaster."w#ma_ins_services"`. POSTGRES emission only.
        /// </summary>
        public static string PgQualifiedName(string schema, string obj)
            => PgQuoteIdentifierIfNeeded(schema) + "." + PgQuoteIdentifierIfNeeded(obj);

        /// <summary>
        /// At <paramref name="s"/>[<paramref name="i"/>] == '$', return the PostgreSQL
        /// dollar-quote tag ("$$", "$fn$", …) starting there, or null when it is not a tag
        /// (a digit immediately after '$' is a positional parameter like $1, not a tag).
        /// The single home for dollar-tag recognition — used by both the PG statement
        /// splitter (<c>PostgresExecutor.SplitStatements</c>) and the batch-level dollar-body
        /// tracker below, so both agree on what opens/closes a body.
        /// </summary>
        public static string? MatchDollarTag(string s, int i)
        {
            int j = i + 1;
            if (j < s.Length && char.IsDigit(s[j])) return null;
            while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
            if (j < s.Length && s[j] == '$') return s.Substring(i, j - i + 1);
            return null;
        }

        /// <summary>
        /// Stateful, line-at-a-time tracker for whether reading is currently inside an open
        /// PostgreSQL dollar-quoted body (<c>$$…$$</c> or <c>$tag$…$tag$</c>) that spans lines.
        /// SBN client directives (<c>go</c>, <c>exit</c>, <c>quit</c>) on their own line INSIDE
        /// such a body — e.g. a plpgsql <c>exit;</c> loop statement — must NOT be treated as
        /// batch terminators; doing so truncates the function and surfaces as
        /// "unterminated dollar-quoted string" (SR 52910). Dollar tags inside <c>'…'</c>/<c>"…"</c>
        /// string literals and <c>--</c> / <c>/* */</c> comments are ignored.
        /// POSTGRES-only by construction: callers gate on platform so SYBASE/MSSQL batch
        /// splitting stays byte-identical (those platforms never build dollar bodies).
        /// </summary>
        public sealed class PgDollarQuoteTracker
        {
            private string? _openTag;      // non-null: inside a dollar body closed by this tag
            private bool _inBlockComment;  // inside /* */ spanning lines
            private char _inString;        // '\0', '\'' or '"' — inside a string literal spanning lines

            /// <summary>True when currently inside an open dollar-quoted body.</summary>
            public bool InDollarBody => _openTag != null;

            /// <summary>Consume one source line, updating state; returns <see cref="InDollarBody"/> after it.</summary>
            public bool Consume(string line)
            {
                int i = 0, n = line.Length;
                while (i < n)
                {
                    char c = line[i];
                    char next = i + 1 < n ? line[i + 1] : '\0';

                    if (_openTag != null)
                    {
                        // Inside a body: only the matching close tag ends it; all else is literal.
                        if (c == '$' && line.AsSpan(i).StartsWith(_openTag.AsSpan()))
                        {
                            i += _openTag.Length;
                            _openTag = null;
                            continue;
                        }
                        i++;
                        continue;
                    }
                    if (_inBlockComment)
                    {
                        if (c == '*' && next == '/') { _inBlockComment = false; i += 2; continue; }
                        i++;
                        continue;
                    }
                    if (_inString != '\0')
                    {
                        if (c == _inString)
                        {
                            if (next == _inString) { i += 2; continue; } // '' or "" escape
                            _inString = '\0';
                        }
                        i++;
                        continue;
                    }
                    // Default state.
                    if (c == '-' && next == '-') break;                       // -- comment: rest of line
                    if (c == '/' && next == '*') { _inBlockComment = true; i += 2; continue; }
                    if (c == '\'' || c == '"') { _inString = c; i++; continue; }
                    if (c == '$')
                    {
                        var tag = MatchDollarTag(line, i);
                        if (tag != null) { _openTag = tag; i += tag.Length; continue; }
                    }
                    i++;
                }
                return InDollarBody;
            }
        }
        #endregion

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
            var normalized = line.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');
            using var fs = new FileStream(fileName, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(fs) { NewLine = "\n" };
            writer.WriteLine(normalized);
        }

        /// <summary>
        /// Opens a StreamWriter for any committed CSS source file (messages, options,
        /// actions, required_fields, table_locations, etc). Forces LF terminators so
        /// files are byte-identical regardless of host OS.
        /// </summary>
        public static StreamWriter OpenSourceWriter(string path, bool append = false)
            => new StreamWriter(path, append) { NewLine = "\n" };

        /// <summary>
        /// Seconds since the SBN epoch (1980-01-01), the int form used by
        /// chg_tm on message rows and end_tm on upgrades (see IRunUpgrade.cs).
        /// </summary>
        public static int SecondsSince1980()
            => (int)(DateTime.Now - new DateTime(1980, 1, 1)).TotalSeconds;

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

            // Case-insensitive walk: legacy Unix create-files use lowercase "css>ss>ba>..."
            // but the real directories on disk are mixed-case ("CSS/ss/ba/..."). On NTFS/DrvFs
            // this is harmless (case-insensitive match), but on ext4/macOS-Linux the lookups
            // above all fail. Walk the path component-by-component and resolve each segment
            // case-insensitively before giving up.
            if (TryResolveCaseInsensitive(fileName, out var resolved))
            {
                fileName = resolved;
                return true;
            }
            if (TryResolveCaseInsensitive(fileName + ".sql", out resolved))
            {
                fileName = resolved;
                return true;
            }
            if (TryResolveCaseInsensitive(fn, out resolved))
            {
                fileName = resolved;
                return true;
            }
            if (TryResolveCaseInsensitive(fn + ".sql", out resolved))
            {
                fileName = resolved;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Walk <paramref name="path"/> component-by-component, matching each directory and
        /// the file name case-insensitively against what is actually on disk. Returns true
        /// (and writes the on-disk path to <paramref name="resolved"/>) only when every
        /// segment resolves to a unique match.
        /// </summary>
        private static bool TryResolveCaseInsensitive(string path, out string resolved)
        {
            resolved = path;
            if (string.IsNullOrEmpty(path)) return false;
            if (File.Exists(path)) return true;

            var isAbsolute = Path.IsPathRooted(path);
            var root = isAbsolute ? Path.GetPathRoot(path) ?? string.Empty : string.Empty;
            var rel = isAbsolute ? path.Substring(root.Length) : path;
            var parts = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;

            var current = isAbsolute ? root : ".";
            // Trim trailing sep so Path.Combine gives us the segment join we want
            if (current.Length > 1 && (current.EndsWith(Path.DirectorySeparatorChar.ToString()) || current.EndsWith(Path.AltDirectorySeparatorChar.ToString())))
                current = current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrEmpty(current)) current = Path.DirectorySeparatorChar.ToString();

            for (int i = 0; i < parts.Length; i++)
            {
                if (!Directory.Exists(current)) return false;
                var wantFile = i == parts.Length - 1;
                string match = null;
                try
                {
                    if (wantFile)
                    {
                        foreach (var f in Directory.EnumerateFiles(current))
                        {
                            if (string.Equals(Path.GetFileName(f), parts[i], StringComparison.OrdinalIgnoreCase))
                            { match = f; break; }
                        }
                    }
                    if (match == null)
                    {
                        foreach (var d in Directory.EnumerateDirectories(current))
                        {
                            if (string.Equals(Path.GetFileName(d), parts[i], StringComparison.OrdinalIgnoreCase))
                            { match = d; break; }
                        }
                    }
                }
                catch { return false; }
                if (match == null) return false;
                current = match;
            }
            resolved = current;
            return File.Exists(resolved);
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

        /// <summary>
        /// Creates directory symlinks needed for SQL source short-path resolution.
        /// Idempotent — only creates links that don't already exist and whose targets exist.
        /// </summary>
        /// <summary>
        /// Symlink definitions: (link path relative to SqlSource, target name relative to link's parent).
        /// </summary>
        public static readonly (string Link, string Target)[] SymlinkDefinitions =
        {
            // Root level
            ("css", "CSS"),
            ("ibs", "IBS"),

            // Inside CSS/
            (Path.Combine("CSS", "ss"), "SQL_Sources"),
            (Path.Combine("CSS", "setup"), "Setup"),

            // Inside CSS/SQL_Sources/
            (Path.Combine("CSS", "SQL_Sources", "ba"), "Basics"),
            (Path.Combine("CSS", "SQL_Sources", "api3"), "Application_Program_Interface_V3"),
            (Path.Combine("CSS", "SQL_Sources", "at"), "Alarm_Treatment"),
            (Path.Combine("CSS", "SQL_Sources", "bl"), "Billing"),
            (Path.Combine("CSS", "SQL_Sources", "ct"), "Create_Temp"),
            (Path.Combine("CSS", "SQL_Sources", "dv"), "IBS_Development"),
            (Path.Combine("CSS", "SQL_Sources", "fe"), "Front_End"),
            (Path.Combine("CSS", "SQL_Sources", "in"), "Internal"),
            (Path.Combine("CSS", "SQL_Sources", "ma"), "Co_Monitoring"),
            (Path.Combine("CSS", "SQL_Sources", "mb"), "Mobile"),
            (Path.Combine("CSS", "SQL_Sources", "mo"), "Monitoring"),
            (Path.Combine("CSS", "SQL_Sources", "si"), "System_Init"),
            (Path.Combine("CSS", "SQL_Sources", "sv"), "Service"),
            (Path.Combine("CSS", "SQL_Sources", "tm"), "Telemarketing"),
            (Path.Combine("CSS", "SQL_Sources", "ub"), "US_Basics"),

            // Inside IBS/
            (Path.Combine("IBS", "ss"), "SQL_Sources"),
            (Path.Combine("IBS", "setup"), "Setup"),
        };

        /// <summary>
        /// Parse the SQL tree's own <c>create_links.sh</c> into (link, target)
        /// pairs. Each <c>ln -s &lt;target&gt; &lt;link&gt;</c> line becomes one
        /// pair, with both paths normalized (leading "./" stripped, "/" → the
        /// platform separator) so the same script the Unix host runs can be
        /// materialized on Windows, Linux, or macOS. This is the authoritative
        /// source of shortcuts: the renamed current.sql tree already has the
        /// short names on disk (every entry is a no-op), while legacy long-name
        /// trees (95.sql and earlier) get exactly the shortcuts the script
        /// defines. Returns an empty list when the script is absent.
        /// </summary>
        public static List<(string Link, string Target)> ParseCreateLinks(string sqlSource)
        {
            var result = new List<(string, string)>();
            if (string.IsNullOrEmpty(sqlSource)) return result;

            var script = Path.Combine(sqlSource, "create_links.sh");
            if (!File.Exists(script)) return result;

            foreach (var raw in File.ReadAllLines(script))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                // Expect exactly: ln -s <target> <link>
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4 || parts[0] != "ln" || parts[1] != "-s") continue;

                var target = NormalizeLinkPath(parts[2]);
                var link = NormalizeLinkPath(parts[3]);
                if (link.Length == 0 || target.Length == 0) continue;
                result.Add((link, target));
            }
            return result;
        }

        private static string NormalizeLinkPath(string p)
        {
            if (p.StartsWith("./")) p = p.Substring(2);
            return p.Replace('/', Path.DirectorySeparatorChar);
        }

        /// <summary>
        /// The shortcut set to materialize for a SQL tree: the tree's own
        /// <c>create_links.sh</c> when present (authoritative — matches the Unix
        /// host), otherwise the built-in curated <see cref="SymlinkDefinitions"/>.
        /// The create/skip guards downstream (short path or shortcut already
        /// present, target directory must exist) make the extra entries from the
        /// full script harmless — absolute Unix targets and runtime-only links
        /// simply skip because their targets aren't on disk in a SQL checkout.
        /// </summary>
        public static IReadOnlyList<(string Link, string Target)> ShortcutDefinitionsFor(string sqlSource)
        {
            var parsed = ParseCreateLinks(sqlSource);
            return parsed.Count > 0 ? parsed : SymlinkDefinitions;
        }

        /// <summary>
        /// Map of legacy long-form directory name -> short alias, derived from
        /// SymlinkDefinitions (last segment of each link is the short name for
        /// its target). Used by <see cref="ToShortPath"/> to recognize when a
        /// renamed SQL source tree has the short-name directories on disk
        /// directly and no symlinks are needed at all.
        /// </summary>
        private static Dictionary<string, string>? _legacyToShortMap;
        private static Dictionary<string, string> LegacyToShortMap
        {
            get
            {
                if (_legacyToShortMap != null) return _legacyToShortMap;
                var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (link, target) in SymlinkDefinitions)
                {
                    var shortName = Path.GetFileName(link);
                    if (!string.IsNullOrEmpty(shortName))
                        m[target] = shortName;
                }
                _legacyToShortMap = m;
                return m;
            }
        }

        /// <summary>
        /// Rewrite a path so every legacy long-form segment becomes its short
        /// alias (e.g. "CSS/SQL_Sources/ba" -> "css/ss/ba"). Segments not in the
        /// map are left untouched. Returns the original path if no segment
        /// changed.
        /// </summary>
        public static string ToShortPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var parts = path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                                   StringSplitOptions.None);
            var map = LegacyToShortMap;
            bool changed = false;
            for (int i = 0; i < parts.Length; i++)
            {
                if (map.TryGetValue(parts[i], out var s) && !string.Equals(parts[i], s, StringComparison.Ordinal))
                {
                    parts[i] = s;
                    changed = true;
                }
            }
            return changed ? string.Join(Path.DirectorySeparatorChar.ToString(), parts) : path;
        }

        /// <summary>
        /// True when either <paramref name="linkPath"/> exists on disk OR its
        /// all-short-name equivalent exists. Either form is sufficient for the
        /// compiler — the renamed SQL source trees (current.sql post-r57389)
        /// have the short-name directories on disk natively, so symlinks are
        /// not needed at all in that layout.
        /// </summary>
        public static bool SymlinkOrShortPathExists(string sqlSource, string link)
        {
            var linkPath = Path.Combine(sqlSource, link);
            if (Path.Exists(linkPath)) return true;
            var shortLink = ToShortPath(link);
            if (!string.Equals(shortLink, link, StringComparison.Ordinal))
            {
                var shortPath = Path.Combine(sqlSource, shortLink);
                if (Path.Exists(shortPath)) return true;
            }
            return false;
        }

        public static (int Created, int Existing, int TargetMissing, int PermissionDenied) EnsureSymbolicLinks(string sqlSource)
        {
            if (string.IsNullOrEmpty(sqlSource) || !Directory.Exists(sqlSource))
                return (0, 0, 0, 0);

            int created = 0, existing = 0, targetMissing = 0, permissionDenied = 0;
            bool windowsWarningShown = false;

            foreach (var (link, target) in ShortcutDefinitionsFor(sqlSource))
            {
                // Symlink already there, OR the short-name path is a real
                // directory on disk (renamed tree) — either way nothing to do.
                if (SymlinkOrShortPathExists(sqlSource, link))
                {
                    existing++;
                    continue;
                }

                var linkPath = Path.Combine(sqlSource, link);
                var linkParent = Path.GetDirectoryName(linkPath)!;
                var targetPath = Path.Combine(linkParent, target);

                // Skip if target directory doesn't exist (don't create dangling links)
                if (!Directory.Exists(targetPath))
                {
                    targetMissing++;
                    continue;
                }

                try
                {
                    Directory.CreateSymbolicLink(linkPath, target);
                    created++;
                }
                catch (UnauthorizedAccessException)
                {
                    if (!windowsWarningShown)
                    {
                        Console.WriteLine("  Note: Symbolic link creation requires elevated privileges on Windows.");
                        Console.WriteLine("  Run as Administrator, or enable Developer Mode in Windows Settings.");
                        Console.WriteLine("  The compilers will still work using path expansion fallback.");
                        windowsWarningShown = true;
                    }
                    permissionDenied++;
                }
                catch (IOException)
                {
                    // Link path became occupied between check and create, or other I/O issue
                    permissionDenied++;
                }
            }

            return (created, existing, targetMissing, permissionDenied);
        }

        public static void MergeTextFiles(string sourceFile, string destinationFile)
        {
            try
            {
                using var source = new StreamReader(sourceFile);
                using var dest = OpenSourceWriter(destinationFile, append: true);
                string? line;
                while ((line = source.ReadLine()) != null)
                    dest.WriteLine(line);
            }
            catch { }
        }

        public static bool SaveArrayToDisk(List<string> sourceFile, string destinationFile)
        {
            using var dest = OpenSourceWriter(destinationFile);
            foreach (var line in sourceFile)
                dest.WriteLine(line);
            return true;
        }

        /// <summary>
        /// Crash-safe, concurrency-safe write of an options/cache array to
        /// <paramref name="destinationFile"/>. Writes to a process-unique temp file first, then
        /// atomically replaces the destination (<c>File.Move(..., overwrite:true)</c> → MoveFileEx
        /// REPLACE_EXISTING on Windows, rename() on Unix). A concurrent reader therefore always
        /// sees either the old complete file or the new complete file — never a half-written one —
        /// and two writers never collide on the same target (each owns its own temp). This is the
        /// fix for SR 52910's parallel-compile-agent failure: the previous in-place truncating
        /// write let one agent read an empty/partial shared cache (unresolved <c>&amp;token&amp;</c>
        /// → raw <c>use …</c> → "syntax error at or near use") or crash with a sharing violation.
        /// The write is best-effort: any I/O failure is swallowed because the caller already holds
        /// the fully-built array in memory — the on-disk file is only a cache.
        /// </summary>
        public static bool SaveArrayToDiskAtomic(List<string> sourceFile, string destinationFile)
        {
            var dir = Path.GetDirectoryName(destinationFile);
            if (string.IsNullOrEmpty(dir)) dir = ".";
            var tmp = Path.Combine(dir,
                Path.GetFileName(destinationFile) + "." +
                Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var dest = OpenSourceWriter(tmp))
                    foreach (var line in sourceFile)
                        dest.WriteLine(line);
                File.Move(tmp, destinationFile, overwrite: true);
                return true;
            }
            catch (IOException)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
        }

        /// <summary>
        /// Atomic, best-effort replacement of a text file (temp write + <c>File.Move</c> overwrite).
        /// Same rationale as <see cref="SaveArrayToDiskAtomic"/> — a concurrent reader never sees a
        /// half-written file. Used for settings.json normalization so parallel compiler startups
        /// can't truncate the shared config out from under a peer (SR 52910). Returns false on any
        /// I/O failure; the caller treats the write as advisory.
        /// </summary>
        public static bool WriteAllTextAtomic(string path, string content)
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) dir = ".";
            var tmp = Path.Combine(dir,
                Path.GetFileName(path) + "." +
                Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(tmp, content);
                File.Move(tmp, path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
        }

        /// <summary>
        /// Resilient read of a shared text file: FileShare.ReadWrite|Delete plus a short retry so a
        /// concurrent atomic replace never trips a sharing violation, and a momentarily
        /// empty/whitespace read (peer mid-swap) is retried rather than parsed. Returns null on
        /// persistent failure (SR 52910).
        /// </summary>
        public static string? ReadAllTextResilient(string path)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs);
                    var text = sr.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(text) || attempt == 4) return text;
                }
                catch (IOException) { }
                System.Threading.Thread.Sleep(25);
            }
            return null;
        }

        public static List<string> BuildArrayFromDisk(string sourceFile)
        {
            // FileShare.ReadWrite|Delete so a concurrent atomic replace (SaveArrayToDiskAtomic)
            // never blocks this read and never trips a sharing violation; retry briefly to ride
            // out the instant a peer is swapping the file in. Returns an empty list on persistent
            // failure so callers can rebuild rather than crash (SR 52910).
            for (int attempt = 0; ; attempt++)
            {
                var arr = new List<string>();
                try
                {
                    using var fs = new FileStream(sourceFile, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var source = new StreamReader(fs);
                    string? line;
                    while ((line = source.ReadLine()) != null)
                        arr.Add(line);
                    return arr;
                }
                catch (IOException)
                {
                    if (attempt >= 4) return new List<string>();
                    System.Threading.Thread.Sleep(25);
                }
            }
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
            var serverSpecific = Path.Combine(profile.IRPath, "css", "setup", "actions." + serverName);
            if (File.Exists(serverSpecific)) return serverSpecific;
            return Path.Combine(profile.IRPath, "css", "setup", "actions");
        }

        public static string GetPath_ActionsDetail(ResolvedProfile profile)
        {
            return Path.Combine(profile.IRPath, "css", "setup", "actions_dtl");
        }

        public static string GetPath_OptionsDefault(ResolvedProfile profile)
        {
            return Path.Combine(profile.IRPath, "css", "setup", "options.def");
        }

        public static string GetPath_OptionsSQL(CommandVariables cmdvars, ResolvedProfile profile)
        {
            return Path.Combine(profile.IRPath, "css", "setup", "options." + CanonicalName(profile.ServerType));
        }

        public static string GetPath_OptionsCompany(ResolvedProfile profile)
        {
            return Path.Combine(profile.IRPath, "css", "setup", "options." + profile.Company);
        }

        public static string GetPath_OptionsServer(CommandVariables cmdvars, ResolvedProfile profile)
        {
            var serverName = (profile.IsProfile ? profile.ProfileName : cmdvars.Server)
                .Replace('\\', '_').Replace('.', '_');
            return Path.Combine(profile.IRPath, "css", "setup", "options." + profile.Company + "." + serverName);
        }

        public static string GetPath_TableLocations(ResolvedProfile profile)
        {
            return Path.Combine(profile.IRPath, "css", "setup", "table_locations");
        }

        public static string GetPath_TableLocationsCompany(ResolvedProfile profile)
        {
            return Path.Combine(profile.IRPath, "css", "setup", "table_locations." + profile.Company);
        }

        /// <summary>
        /// The fully-resolved options cache written by <see cref="Options.GenerateOptionFiles"/> —
        /// every option file plus table_locations merged into one flat token→value list.
        /// It lives in the temp directory, NOT css/setup: it is a derived file, safe to delete
        /// at any time, and the next compile rebuilds it from the source files. Reused for 60
        /// minutes from creation, so a source edit inside that window is invisible until the
        /// cache is cleared (see <see cref="ClearResolvedOptions"/>) or a compile forces a rebuild.
        /// </summary>
        public static string GetPath_ResolvedOptions(CommandVariables cmdvars, ResolvedProfile profile)
            => Path.Combine(GetPath_ResolvedOptionsDir(profile), GetName_ResolvedOptions(cmdvars, profile));

        /// <summary>
        /// Directory holding the resolved options cache: the common system temp directory, so
        /// the cache is never written into a SQL working copy (it would show up as untracked
        /// noise in every <c>svn status</c>). Every command that reads or writes it prints the
        /// full path — see <see cref="Options.ReportResolvedOptionsPath"/>.
        /// </summary>
        public static string GetPath_ResolvedOptionsDir(ResolvedProfile profile)
        {
            var tempPath = GetTempPath();
            return string.IsNullOrEmpty(tempPath) ? "." + Path.DirectorySeparatorChar : tempPath;
        }

        private static string GetName_ResolvedOptions(CommandVariables cmdvars, ResolvedProfile profile)
        {
            var serverName = (profile.IsProfile ? profile.ProfileName : cmdvars.Server ?? "")
                .Replace('\\', '_').Replace('.', '_');

            if (File.Exists(GetPath_OptionsSQL(cmdvars, profile)))
                return $"options.{CanonicalName(profile.ServerType)}.{profile.Company}.{serverName}.tmp";
            return $"options.{profile.Company}.{serverName}.tmp";
        }

        /// <summary>
        /// The 3.1.4-only location of the resolved options cache (inside the SQL working copy at
        /// <c>&lt;SQL_SOURCE&gt;/css/setup/temp</c>). Still cleared by
        /// <see cref="ClearResolvedOptions"/> so an install that ran 3.1.4 doesn't leave an
        /// orphan inside the source tree, but never read or written.
        /// </summary>
        private static string? GetPath_ResolvedOptionsLegacy(CommandVariables cmdvars, ResolvedProfile profile)
        {
            if (string.IsNullOrEmpty(profile.IRPath)) return null;
            return Path.Combine(profile.IRPath, "css", "setup", "temp",
                                GetName_ResolvedOptions(cmdvars, profile));
        }

        /// <summary>
        /// Deletes the resolved options cache. Returns true if a file was actually removed.
        /// </summary>
        public static bool ClearResolvedOptions(CommandVariables cmdvars, ResolvedProfile profile)
        {
            bool cleared = false;
            foreach (var path in new[] { GetPath_ResolvedOptions(cmdvars, profile),
                                         GetPath_ResolvedOptionsLegacy(cmdvars, profile) })
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                try { File.Delete(path); cleared = true; }
                catch (Exception ex) { WriteLine($"Could not delete {path}: {ex.Message}", cmdvars.OutFile); }
            }
            return cleared;
        }

        public static string GetPath_Setup(ResolvedProfile profile)
        {
            return Path.Combine(profile.IRPath, "css", "setup");
        }

        public static string GetPath_MessageBackup(ResolvedProfile profile)
        {
            return Path.Combine(profile.IRPath, "css", "setup", "backup");
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

                // Resolve outfile to full path; split into .out and .err
                if (!string.IsNullOrEmpty(myargs.OutFile))
                {
                    if (!Path.IsPathRooted(myargs.OutFile))
                        myargs.OutFile = Path.Combine(Environment.CurrentDirectory, myargs.OutFile);
                    myargs.ErrFile = myargs.OutFile + ".err";
                    myargs.OutFile = myargs.OutFile + ".out";
                    try { File.Delete(myargs.OutFile); } catch { }
                    try { File.Delete(myargs.ErrFile); } catch { }
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
            args.ErrFile = "";
            args.EchoInput = FindAndRemove_Flag("-E", ref arguments);
            args.SeqFirst = FindAndRemove_Int("-F", ref arguments);
            args.SeqLast = FindAndRemove_Int("-L", ref arguments);
            args.ChangeLog = FindAndRemove_BoolFlag("--changelog", ref arguments, defaultValue: true);
            args.Preview = FindAndRemove_BoolFlag("--preview", ref arguments, defaultValue: false);

            args.Command = "";
            args.Database = "";
            args.Server = "";
            args.Upgrade_no = "";
            // Leave User/Pass empty when -U/-P not passed. ProfileManager.Resolve treats
            // empty as "use profile creds"; a non-empty value is an explicit override —
            // including literal "sbn0"/"ibsibs". Legacy defaults are applied only in the
            // no-profile env-var fallback (ResolveFromEnvironment).
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
                if (!arguments[i].StartsWith("-"))
                    continue;
                var tok = arguments[i].TrimStart('-').ToUpperInvariant();
                if (tok is "MSSQL" or "SYBASE" or "POSTGRES")
                {
                    arguments.RemoveAt(i);
                    return ParsePlatform(tok);
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
