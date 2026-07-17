using ibsCompiler.Configuration;

namespace ibsCompiler
{
    /// <summary>
    /// Appends a single message row directly into the flat CSS message files
    /// (<c>css.&lt;type&gt;_msg</c>) under a profile's SQL source, picking the next
    /// free s#msgno for the target group. No database round-trip and no
    /// <c>ISqlExecutor</c> dependency — this is a pure file operation, which is
    /// why it is allowed even against the canonical GONZO profile.
    ///
    /// File shape (7 tab-delimited columns, no header, LF line endings, UTF-8
    /// no BOM, trailing newline):
    ///   s#msgno \t lang \t cmpy \t grp \t upd_flg \t chg_tm \t message
    /// The same msgno legitimately appears on multiple rows (key is
    /// msgno+cmpy+lang), so numbering is scoped to the group. Reserved
    /// placeholder rows (message = ">> Reserved for group: X") count as used
    /// numbers and are never overwritten — we always append.
    /// </summary>
    public static class MessageFileEditor
    {
        /// <summary>Message types that map to a live css.&lt;type&gt;_msg file.</summary>
        private static readonly string[] ValidTypes = { "ibs", "gui", "sql", "sqr", "jam" };

        public sealed class AddMessageResult
        {
            public bool Success { get; set; }
            public int Msgno { get; set; }
            public string Row { get; set; } = "";
            public string? Error { get; set; }
            public string? Warning { get; set; }
            public bool DryRun { get; set; }

            public static AddMessageResult Fail(string error) => new() { Success = false, Error = error };
        }

        /// <summary>
        /// Compute the next free message number for <paramref name="group"/> in the
        /// <paramref name="type"/> file and (unless <paramref name="dryRun"/>) append
        /// the row. Returns a result carrying the msgno, the exact tab row, and any
        /// error/warning. Never throws for validation problems — those come back as
        /// <see cref="AddMessageResult.Error"/>.
        /// </summary>
        public static AddMessageResult AddMessage(
            ResolvedProfile profile, string type, string group, string text,
            int lang = 1, int cmpy = 0, char? updFlg = null, bool dryRun = false)
        {
            // ---- input validation (before touching the filesystem) ----
            var t = (type ?? "").Trim().ToLowerInvariant();
            if (t == "rpt")
                return AddMessageResult.Fail("rpt messages are not part of the live pipeline");
            if (Array.IndexOf(ValidTypes, t) < 0)
                return AddMessageResult.Fail($"unknown message type '{type}' (expected one of {string.Join("|", ValidTypes)})");

            if (group == null || group.Trim().Length == 0)
                return AddMessageResult.Fail("group is required");
            var groupKey = group.Trim().ToUpperInvariant();
            if (groupKey.Length > 6)
                return AddMessageResult.Fail($"group '{groupKey}' exceeds 6 characters");

            if (text == null)
                return AddMessageResult.Fail("message text is required");
            var textBytes = System.Text.Encoding.UTF8.GetByteCount(text);
            if (textBytes > 255)
                return AddMessageResult.Fail($"message text exceeds 255 bytes (got {textBytes})");
            if (text.IndexOf('\r') >= 0 || text.IndexOf('\n') >= 0)
                return AddMessageResult.Fail("message text may not contain a carriage return or newline");

            if (lang < 0)
                return AddMessageResult.Fail("lang must be >= 0");
            if (cmpy < 0)
                return AddMessageResult.Fail("cmpy must be >= 0");

            var setupDir = ibs_compiler_common.GetPath_Setup(profile);
            var msgPath = Path.Combine(setupDir, $"css.{t}_msg");
            var msgrpPath = Path.Combine(setupDir, $"css.{t}_msgrp");
            if (!File.Exists(msgPath))
                return AddMessageResult.Fail($"message file not found: {msgPath}");

            // ---- parse css.<type>_msg ----
            var allMsgnos = new HashSet<int>();
            var groupMsgnos = new List<int>();
            string? collisionOwner = null;
            var msgnoOwner = new Dictionary<int, string>();
            int duplicateInGroup = 0;

            foreach (var line in File.ReadAllLines(msgPath))
            {
                if (line.Length == 0) continue;
                var cols = line.Split('\t');
                if (cols.Length < 7) continue;
                if (!int.TryParse(cols[0].Trim(), out var msgno)) continue;

                allMsgnos.Add(msgno);
                var rowGroup = cols[3].Trim().ToUpperInvariant();
                if (!msgnoOwner.ContainsKey(msgno)) msgnoOwner[msgno] = rowGroup;

                if (rowGroup == groupKey)
                {
                    groupMsgnos.Add(msgno);
                    // cols[6..] rejoined preserves any tabs a bcp-in would fold into
                    // the last column; compare against the incoming verbatim text.
                    var rowText = string.Join("\t", cols.Skip(6));
                    if (string.Equals(rowText, text, StringComparison.Ordinal))
                        duplicateInGroup++;
                }
            }

            // ---- parse css.<type>_msgrp (grp \t s#minmsg \t description) ----
            var minMsgByGroup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(msgrpPath))
            {
                foreach (var line in File.ReadAllLines(msgrpPath))
                {
                    if (line.Length == 0) continue;
                    var cols = line.Split('\t');
                    if (cols.Length < 2) continue;
                    var g = cols[0].Trim().ToUpperInvariant();
                    if (g.Length == 0) continue;
                    if (!int.TryParse(cols[1].Trim(), out var min)) continue;
                    minMsgByGroup[g] = min;
                }
            }

            // ---- group must be known (has rows in _msg OR an entry in _msgrp) ----
            bool knownInMsg = groupMsgnos.Count > 0;
            bool knownInMsgrp = minMsgByGroup.ContainsKey(groupKey);
            if (!knownInMsg && !knownInMsgrp)
                return AddMessageResult.Fail(
                    $"unknown group '{groupKey}' (no rows in css.{t}_msg and no entry in css.{t}_msgrp)");

            // ---- next number ----
            int next;
            if (knownInMsg)
            {
                next = groupMsgnos.Max() + 1;
            }
            else
            {
                // Group is known only via css.<type>_msgrp (no live rows yet).
                // A positive s#minmsg seeds the block at that floor. A zero
                // (or absent) floor means "no reserved block" — fall back to the
                // global next-free number so the group can still be populated.
                var min = minMsgByGroup[groupKey];
                if (min > 0)
                    next = min;
                else
                    next = allMsgnos.Count > 0 ? allMsgnos.Max() + 1 : 1;
            }

            // ---- hard collision guard: number must be free everywhere ----
            if (allMsgnos.Contains(next))
            {
                msgnoOwner.TryGetValue(next, out collisionOwner);
                return AddMessageResult.Fail(
                    $"message number {next} is already in use (block exhausted / overlaps group {collisionOwner ?? "?"})");
            }

            // ---- build the row ----
            char flag = updFlg ?? (t == "gui" ? 'X' : ' ');
            var grpPadded = groupKey.PadRight(6);
            var chgTm = ibs_compiler_common.SecondsSince1980();
            var row = string.Join("\t",
                next.ToString(),
                lang.ToString(),
                cmpy.ToString(),
                grpPadded,
                flag.ToString(),
                chgTm.ToString(),
                text);

            var result = new AddMessageResult
            {
                Success = true,
                Msgno = next,
                Row = row,
                DryRun = dryRun,
            };
            if (duplicateInGroup > 0)
                result.Warning = $"an identical message already exists in group {groupKey}; appending anyway";

            // ---- append (unless dry-run) ----
            if (!dryRun)
            {
                bool needsLeadingNewline = LastByteIsNotNewline(msgPath);
                using var w = ibs_compiler_common.OpenSourceWriter(msgPath, append: true);
                if (needsLeadingNewline) w.Write("\n");
                w.Write(row);
                w.Write("\n");
            }

            return result;
        }

        /// <summary>
        /// True when the file is non-empty and its final byte is not LF — in which
        /// case a leading newline must precede the appended row so we never glue it
        /// onto a partial last line.
        /// </summary>
        private static bool LastByteIsNotNewline(string path)
        {
            if (!File.Exists(path)) return false;
            using var fs = File.OpenRead(path);
            if (fs.Length == 0) return false;
            fs.Seek(-1, SeekOrigin.End);
            return fs.ReadByte() != '\n';
        }

        // ================================================================
        // File-first model + query/mutation surface (wave 1).
        //
        // Everything below is pure file I/O built on the same byte contract
        // AddMessage uses (7 tab-delimited columns, LF line endings, UTF-8 no
        // BOM). It never throws for validation — callers get a result struct —
        // and it never touches a database. Untouched physical lines are always
        // preserved byte-verbatim across edit/delete rewrites, so bytes that are
        // not valid UTF-8 (legacy code-page rows) survive round trips intact.
        // ================================================================

        /// <summary>One decoded message row plus the exact source bytes of its physical line.</summary>
        public sealed class MsgRow
        {
            /// <summary>Index of this row's physical line within <see cref="MsgFile.RawLines"/>.</summary>
            public int LineIndex { get; set; }
            public int Msgno { get; set; }
            public int Lang { get; set; }
            public int Cmpy { get; set; }
            /// <summary>Group, trimmed and upper-cased (matches the AddMessage key convention).</summary>
            public string Group { get; set; } = "";
            public char UpdFlg { get; set; }
            public int ChgTm { get; set; }
            public string Text { get; set; } = "";
            /// <summary>The physical line's bytes, verbatim, with the trailing LF stripped.</summary>
            public byte[] RawBytes { get; set; } = Array.Empty<byte>();
        }

        /// <summary>A whole message file parsed once: every physical line byte-verbatim plus the decoded rows.</summary>
        public sealed class MsgFile
        {
            public string Path { get; set; } = "";
            public string Type { get; set; } = "";
            /// <summary>Every physical line, byte-verbatim, LF stripped. A malformed line still appears here.</summary>
            public List<byte[]> RawLines { get; } = new();
            /// <summary>Only well-formed (≥7 column, numeric-msgno) lines, in file order.</summary>
            public List<MsgRow> Rows { get; } = new();
            /// <summary>True when the file's final byte is LF (the canonical shape).</summary>
            public bool TrailingNewline { get; set; }
        }

        /// <summary>A message group as seen in css.&lt;type&gt;_msgrp, with its live row count from css.&lt;type&gt;_msg.</summary>
        public sealed class MsgGroup
        {
            public string Group { get; set; } = "";
            public int MinMsg { get; set; }
            public string Description { get; set; } = "";
            public int RowCount { get; set; }
        }

        /// <summary>One live message type and the file paths that back it.</summary>
        public sealed class LiveType
        {
            public string Type { get; set; } = "";
            public string Label { get; set; } = "";
            public string MsgPath { get; set; } = "";
            public string MsgrpPath { get; set; } = "";
        }

        public sealed class AddGroupResult
        {
            public bool Success { get; set; }
            public string Group { get; set; } = "";
            public int Start { get; set; }
            public string Row { get; set; } = "";
            public string? Error { get; set; }
            public bool DryRun { get; set; }
            public static AddGroupResult Fail(string error) => new() { Success = false, Error = error };
        }

        public sealed class UpdateMessageResult
        {
            public bool Success { get; set; }
            public int Msgno { get; set; }
            public string Row { get; set; } = "";
            public string? Error { get; set; }
            public bool DryRun { get; set; }
            public static UpdateMessageResult Fail(string error) => new() { Success = false, Error = error };
        }

        public sealed class DeleteMessageResult
        {
            public bool Success { get; set; }
            public int Msgno { get; set; }
            public string? Error { get; set; }
            public bool DryRun { get; set; }
            public static DeleteMessageResult Fail(string error) => new() { Success = false, Error = error };
        }

        private static readonly Dictionary<string, string> TypeLabels = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ibs"] = "IBS", ["gui"] = "GUI", ["sql"] = "SQL", ["sqr"] = "SQR", ["jam"] = "JAM",
        };

        /// <summary>Normalize a type token; returns null when it is not one of the five live types.</summary>
        private static string? NormalizeType(string? type)
        {
            var t = (type ?? "").Trim().ToLowerInvariant();
            if (t == "rpt") return null;
            return Array.IndexOf(ValidTypes, t) >= 0 ? t : null;
        }

        /// <summary>
        /// Enumerate the live message types that actually have a css.&lt;type&gt;_msgrp
        /// under the profile's Setup dir. rpt is never live even if css.rpt_msgrp exists.
        /// </summary>
        public static List<LiveType> ListLiveTypes(ResolvedProfile profile)
        {
            var setupDir = ibs_compiler_common.GetPath_Setup(profile);
            var result = new List<LiveType>();
            foreach (var t in ValidTypes) // ValidTypes already excludes rpt
            {
                var msgrp = Path.Combine(setupDir, $"css.{t}_msgrp");
                if (!File.Exists(msgrp)) continue;
                result.Add(new LiveType
                {
                    Type = t,
                    Label = TypeLabels.TryGetValue(t, out var l) ? l : t.ToUpperInvariant(),
                    MsgPath = Path.Combine(setupDir, $"css.{t}_msg"),
                    MsgrpPath = msgrp,
                });
            }
            return result;
        }

        /// <summary>
        /// Parse css.&lt;type&gt;_msgrp (grp / s#minmsg / description) and attach the live
        /// row count from css.&lt;type&gt;_msg. Returns an empty list for an unknown type.
        /// </summary>
        public static List<MsgGroup> ListGroups(ResolvedProfile profile, string type)
        {
            var t = NormalizeType(type);
            var groups = new List<MsgGroup>();
            if (t == null) return groups;

            var setupDir = ibs_compiler_common.GetPath_Setup(profile);
            var msgPath = Path.Combine(setupDir, $"css.{t}_msg");
            var msgrpPath = Path.Combine(setupDir, $"css.{t}_msgrp");

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(msgPath))
            {
                foreach (var line in File.ReadAllLines(msgPath))
                {
                    if (line.Length == 0) continue;
                    var cols = line.Split('\t');
                    if (cols.Length < 7) continue;
                    if (!int.TryParse(cols[0].Trim(), out _)) continue;
                    var g = cols[3].Trim().ToUpperInvariant();
                    counts[g] = counts.TryGetValue(g, out var c) ? c + 1 : 1;
                }
            }

            if (File.Exists(msgrpPath))
            {
                foreach (var line in File.ReadAllLines(msgrpPath))
                {
                    if (line.Length == 0) continue;
                    var cols = line.Split('\t');
                    if (cols.Length < 2) continue;
                    var g = cols[0].Trim().ToUpperInvariant();
                    if (g.Length == 0) continue;
                    if (!int.TryParse(cols[1].Trim(), out var min)) continue;
                    groups.Add(new MsgGroup
                    {
                        Group = g,
                        MinMsg = min,
                        Description = cols.Length >= 3 ? string.Join("\t", cols.Skip(2)) : "",
                        RowCount = counts.TryGetValue(g, out var c) ? c : 0,
                    });
                }
            }
            return groups;
        }

        /// <summary>
        /// Read css.&lt;type&gt;_msg once into an in-memory model. Physical lines are split on
        /// 0x0A and kept byte-verbatim; each line is decoded best-effort as UTF-8. Lines with
        /// fewer than 7 columns or a non-numeric msgno are retained as raw bytes only (not in Rows).
        /// A missing file yields an empty model (Path still set).
        /// </summary>
        public static MsgFile LoadFile(ResolvedProfile profile, string type)
        {
            var t = NormalizeType(type) ?? (type ?? "").Trim().ToLowerInvariant();
            var setupDir = ibs_compiler_common.GetPath_Setup(profile);
            var msgPath = Path.Combine(setupDir, $"css.{t}_msg");
            var file = new MsgFile { Path = msgPath, Type = t };
            if (!File.Exists(msgPath)) return file;

            var bytes = File.ReadAllBytes(msgPath);
            SplitLines(bytes, file.RawLines, out var trailing);
            file.TrailingNewline = trailing;
            for (int i = 0; i < file.RawLines.Count; i++)
            {
                var row = TryParseRow(file.RawLines[i], i);
                if (row != null) file.Rows.Add(row);
            }
            return file;
        }

        /// <summary>
        /// Search a type's rows: an empty term matches everything, otherwise the term must
        /// be a substring of the msgno (ordinal) OR a case-insensitive substring of the text.
        /// Exact cmpy/lang equality filters apply only when supplied.
        /// </summary>
        public static List<MsgRow> FindMessages(ResolvedProfile profile, string type, string term, int? cmpy = null, int? lang = null)
            => FindMessages(LoadFile(profile, type), type, term, cmpy, lang);

        /// <summary>Overload that searches an already-loaded <see cref="MsgFile"/>.</summary>
        public static List<MsgRow> FindMessages(MsgFile file, string type, string term, int? cmpy = null, int? lang = null)
        {
            term ??= "";
            var results = new List<MsgRow>();
            foreach (var row in file.Rows)
            {
                bool match = term.Length == 0
                    || row.Msgno.ToString().Contains(term, StringComparison.Ordinal)
                    || row.Text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!match) continue;
                if (cmpy.HasValue && row.Cmpy != cmpy.Value) continue;
                if (lang.HasValue && row.Lang != lang.Value) continue;
                results.Add(row);
            }
            return results;
        }

        /// <summary>
        /// Append a new group definition row to css.&lt;type&gt;_msgrp. Group is padded to 6 on
        /// write; the description may not contain a CR, LF, or tab; the group must not already
        /// exist. Append path mirrors AddMessage (OpenSourceWriter + LastByteIsNotNewline guard).
        /// </summary>
        public static AddGroupResult AddGroup(ResolvedProfile profile, string type, string group, int start, string desc, bool dryRun = false)
        {
            var t = NormalizeType(type);
            if ((type ?? "").Trim().Equals("rpt", StringComparison.OrdinalIgnoreCase))
                return AddGroupResult.Fail("rpt messages are not part of the live pipeline");
            if (t == null)
                return AddGroupResult.Fail($"unknown message type '{type}' (expected one of {string.Join("|", ValidTypes)})");

            if (string.IsNullOrWhiteSpace(group))
                return AddGroupResult.Fail("group is required");
            var groupKey = group.Trim().ToUpperInvariant();
            if (groupKey.Length > 6)
                return AddGroupResult.Fail($"group '{groupKey}' exceeds 6 characters");

            if (start < 0)
                return AddGroupResult.Fail("start must be >= 0");

            if (string.IsNullOrEmpty(desc) || desc.Trim().Length == 0)
                return AddGroupResult.Fail("description is required");
            if (desc.IndexOf('\r') >= 0 || desc.IndexOf('\n') >= 0 || desc.IndexOf('\t') >= 0)
                return AddGroupResult.Fail("description may not contain a carriage return, newline, or tab");

            var setupDir = ibs_compiler_common.GetPath_Setup(profile);
            var msgrpPath = Path.Combine(setupDir, $"css.{t}_msgrp");

            if (File.Exists(msgrpPath))
            {
                foreach (var line in File.ReadAllLines(msgrpPath))
                {
                    if (line.Length == 0) continue;
                    var cols = line.Split('\t');
                    if (cols.Length < 1) continue;
                    if (cols[0].Trim().ToUpperInvariant() == groupKey)
                        return AddGroupResult.Fail($"group '{groupKey}' already exists in css.{t}_msgrp");
                }
            }

            var row = string.Join("\t", groupKey.PadRight(6), start.ToString(), desc);
            var result = new AddGroupResult
            {
                Success = true,
                Group = groupKey,
                Start = start,
                Row = row,
                DryRun = dryRun,
            };

            if (!dryRun)
            {
                bool needsLeadingNewline = LastByteIsNotNewline(msgrpPath);
                using var w = ibs_compiler_common.OpenSourceWriter(msgrpPath, append: true);
                if (needsLeadingNewline) w.Write("\n");
                w.Write(row);
                w.Write("\n");
            }
            return result;
        }

        /// <summary>
        /// Update a single message row located by (msgno, cmpy, lang). When
        /// <paramref name="lineIndex"/> is supplied (TUI disambiguation) it is used directly
        /// after verifying it matches the key; otherwise two or more matching rows are an
        /// ambiguity error. At least one of newText/newUpdFlg is required. The row's bytes are
        /// rebuilt (7 cols, refreshed chg_tm) and the whole file is rewritten byte-for-byte
        /// except for that one line.
        /// </summary>
        public static UpdateMessageResult UpdateMessage(
            ResolvedProfile profile, string type, int msgno, int cmpy, int lang,
            string? newText = null, char? newUpdFlg = null, bool dryRun = false, int? lineIndex = null)
        {
            var t = NormalizeType(type);
            if ((type ?? "").Trim().Equals("rpt", StringComparison.OrdinalIgnoreCase))
                return UpdateMessageResult.Fail("rpt messages are not part of the live pipeline");
            if (t == null)
                return UpdateMessageResult.Fail($"unknown message type '{type}' (expected one of {string.Join("|", ValidTypes)})");

            if (newText == null && newUpdFlg == null)
                return UpdateMessageResult.Fail("nothing to update (supply new text and/or an update flag)");

            if (newText != null)
            {
                var bytes = System.Text.Encoding.UTF8.GetByteCount(newText);
                if (bytes > 255)
                    return UpdateMessageResult.Fail($"message text exceeds 255 bytes (got {bytes})");
                if (newText.IndexOf('\r') >= 0 || newText.IndexOf('\n') >= 0)
                    return UpdateMessageResult.Fail("message text may not contain a carriage return or newline");
            }

            var file = LoadFile(profile, t);
            if (!File.Exists(file.Path))
                return UpdateMessageResult.Fail($"message file not found: {file.Path}");

            var target = LocateRow(file, msgno, cmpy, lang, lineIndex, out var locateError);
            if (target == null)
                return UpdateMessageResult.Fail(locateError!);

            var text = newText ?? target.Text;
            var flag = newUpdFlg ?? target.UpdFlg;
            var chgTm = ibs_compiler_common.SecondsSince1980();
            var newRow = string.Join("\t",
                msgno.ToString(), lang.ToString(), cmpy.ToString(),
                target.Group.PadRight(6), flag.ToString(), chgTm.ToString(), text);

            if (!dryRun)
                RewriteFile(file, target.LineIndex, System.Text.Encoding.UTF8.GetBytes(newRow), delete: false);

            return new UpdateMessageResult { Success = true, Msgno = msgno, Row = newRow, DryRun = dryRun };
        }

        /// <summary>
        /// Delete a single message row located by (msgno, cmpy, lang), with the same keying and
        /// ambiguity rules as <see cref="UpdateMessage"/>. Drops the physical line and rewrites
        /// the file byte-for-byte apart from the removed line.
        /// </summary>
        public static DeleteMessageResult DeleteMessage(
            ResolvedProfile profile, string type, int msgno, int cmpy, int lang,
            bool dryRun = false, int? lineIndex = null)
        {
            var t = NormalizeType(type);
            if ((type ?? "").Trim().Equals("rpt", StringComparison.OrdinalIgnoreCase))
                return DeleteMessageResult.Fail("rpt messages are not part of the live pipeline");
            if (t == null)
                return DeleteMessageResult.Fail($"unknown message type '{type}' (expected one of {string.Join("|", ValidTypes)})");

            var file = LoadFile(profile, t);
            if (!File.Exists(file.Path))
                return DeleteMessageResult.Fail($"message file not found: {file.Path}");

            var target = LocateRow(file, msgno, cmpy, lang, lineIndex, out var locateError);
            if (target == null)
                return DeleteMessageResult.Fail(locateError!);

            if (!dryRun)
                RewriteFile(file, target.LineIndex, null, delete: true);

            return new DeleteMessageResult { Success = true, Msgno = msgno, DryRun = dryRun };
        }

        /// <summary>
        /// Resolve the single target row for an update/delete. With an explicit line index the
        /// row at that index must match the key; without one, exactly one row must match — zero
        /// is "not found" and two or more is "ambiguous". Returns null and sets
        /// <paramref name="error"/> on any failure.
        /// </summary>
        private static MsgRow? LocateRow(MsgFile file, int msgno, int cmpy, int lang, int? lineIndex, out string? error)
        {
            error = null;
            if (lineIndex.HasValue)
            {
                var byIndex = file.Rows.FirstOrDefault(r => r.LineIndex == lineIndex.Value);
                if (byIndex == null)
                {
                    error = $"no message row at line index {lineIndex.Value}";
                    return null;
                }
                if (byIndex.Msgno != msgno || byIndex.Cmpy != cmpy || byIndex.Lang != lang)
                {
                    error = $"line index {lineIndex.Value} does not match key (msgno={msgno} cmpy={cmpy} lang={lang})";
                    return null;
                }
                return byIndex;
            }

            var matches = file.Rows.Where(r => r.Msgno == msgno && r.Cmpy == cmpy && r.Lang == lang).ToList();
            if (matches.Count == 0)
            {
                error = $"no message matches msgno={msgno} cmpy={cmpy} lang={lang}";
                return null;
            }
            if (matches.Count >= 2)
            {
                error = $"ambiguous ({matches.Count} rows match msgno={msgno} cmpy={cmpy} lang={lang}); pass a line index to disambiguate";
                return null;
            }
            return matches[0];
        }

        /// <summary>
        /// Rewrite a file replacing or dropping exactly one physical line, keeping every other
        /// line byte-verbatim. Writes to "&lt;path&gt;.tmp" then atomically File.Replace()s it in
        /// (same directory/volume). Never decodes/re-encodes untouched lines.
        /// </summary>
        private static void RewriteFile(MsgFile file, int targetIndex, byte[]? newLine, bool delete)
        {
            var segments = new List<byte[]>(file.RawLines);
            if (delete) segments.RemoveAt(targetIndex);
            else segments[targetIndex] = newLine!;

            using var ms = new MemoryStream();
            for (int i = 0; i < segments.Count; i++)
            {
                ms.Write(segments[i], 0, segments[i].Length);
                bool isLast = i == segments.Count - 1;
                if (!isLast || file.TrailingNewline) ms.WriteByte(0x0A);
            }

            var tmp = file.Path + ".tmp";
            File.WriteAllBytes(tmp, ms.ToArray());
            File.Replace(tmp, file.Path, null);
        }

        /// <summary>Split raw file bytes on LF into byte-verbatim segments (LF stripped); reports whether the final byte was LF.</summary>
        private static void SplitLines(byte[] bytes, List<byte[]> outLines, out bool trailingNewline)
        {
            outLines.Clear();
            trailingNewline = bytes.Length > 0 && bytes[bytes.Length - 1] == 0x0A;
            int start = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != 0x0A) continue;
                var seg = new byte[i - start];
                Array.Copy(bytes, start, seg, 0, seg.Length);
                outLines.Add(seg);
                start = i + 1;
            }
            if (start < bytes.Length) // trailing partial line with no final LF
            {
                var seg = new byte[bytes.Length - start];
                Array.Copy(bytes, start, seg, 0, seg.Length);
                outLines.Add(seg);
            }
        }

        /// <summary>Decode one physical line best-effort as UTF-8 into a MsgRow; returns null for a malformed / &lt;7-column line.</summary>
        private static MsgRow? TryParseRow(byte[] raw, int lineIndex)
        {
            if (raw.Length == 0) return null;
            var line = System.Text.Encoding.UTF8.GetString(raw); // replacement fallback for invalid bytes
            var cols = line.Split('\t');
            if (cols.Length < 7) return null;
            if (!int.TryParse(cols[0].Trim(), out var msgno)) return null;
            int.TryParse(cols[1].Trim(), out var lang);
            int.TryParse(cols[2].Trim(), out var cmpy);
            int.TryParse(cols[5].Trim(), out var chgTm);
            return new MsgRow
            {
                LineIndex = lineIndex,
                Msgno = msgno,
                Lang = lang,
                Cmpy = cmpy,
                Group = cols[3].Trim().ToUpperInvariant(),
                UpdFlg = cols[4].Length > 0 ? cols[4][0] : ' ',
                ChgTm = chgTm,
                Text = string.Join("\t", cols.Skip(6)),
                RawBytes = raw,
            };
        }
    }
}
