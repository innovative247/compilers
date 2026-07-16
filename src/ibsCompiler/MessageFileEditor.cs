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
                var min = minMsgByGroup[groupKey];
                if (min <= 0)
                    return AddMessageResult.Fail(
                        $"group '{groupKey}' has no messages and its css.{t}_msgrp s#minmsg is {min} (need > 0 to seed a block)");
                next = min;
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
            using var fs = File.OpenRead(path);
            if (fs.Length == 0) return false;
            fs.Seek(-1, SeekOrigin.End);
            return fs.ReadByte() != '\n';
        }
    }
}
