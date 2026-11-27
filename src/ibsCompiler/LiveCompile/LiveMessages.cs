using System.Globalization;

namespace ibsCompiler
{
    /// <summary>Message tables on the live path: the target is what compile_msg's reload leaves
    /// (i_compile_messages, the per-type procs and the gui_messages save/restore; SBN_IR ibs/ss).</summary>
    public static class LiveMessages
    {
        public const int MsgNo = 0, Lang = 1, Cmpy = 2, Grp = 3, UpdFlg = 4, ChgTm = 5, Message = 6;

        // ASE reads '' back as ' '.
        private static LiveTable MsgTable(string name) => new()
        {
            Label = name, Placeholder = $"&{name}&",
            Columns = new LiveColumn[]
            {
                new("s#msgno", LiveColType.Int), new("lang", LiveColType.Int), new("cmpy", LiveColType.Int),
                new("grp", LiveColType.Char, LiveNullCompare.Blank), new("upd_flg", LiveColType.Char, LiveNullCompare.Blank),
                new("chg_tm", LiveColType.Int), new("message", LiveColType.VarChar, LiveNullCompare.Blank),
            },
            Key = new[] { 0, 1, 2 },
        };

        private static LiveTable GroupTable(string name) => new()
        {
            Label = name, Placeholder = $"&{name}&",
            Columns = new LiveColumn[]
            {
                new("grp", LiveColType.Char), new("s#minmsg", LiveColType.Int),
                new("description", LiveColType.VarChar, LiveNullCompare.Blank),
            },
            Key = new[] { 0 },
        };

        public static readonly LiveTable IbsMessages = MsgTable("ibs_messages"), JamMessages = MsgTable("jam_messages"),
            SqrMessages = MsgTable("sqr_messages"), SqlMessages = MsgTable("sql_messages"),
            GuiMessages = MsgTable("gui_messages"), GuiMessagesSave = MsgTable("gui_messages_save");

        // i_compile_messages copies only these two group tables; jam/sqr/sql groups stop in their w# tables.
        public static readonly LiveTable GuiMessageGroups = GroupTable("gui_message_groups"),
            IbsMessageGroups = GroupTable("ibs_message_groups");

        public static readonly LiveTable Messages = new()
        {
            Label = "messages", Placeholder = "&messages&",
            Columns = new LiveColumn[]
            {
                new("s#msgno", LiveColType.Int), new("grp", LiveColType.Char, LiveNullCompare.Blank),
                new("message", LiveColType.VarChar, LiveNullCompare.Blank),
            },
            Key = new[] { 0 },
        };

        /// <summary>One message type and the &amp;messages&amp; range its proc rebuilds.</summary>
        /// <param name="BaseSkipsQuoted">The base insert has "substring(message,1,2) != '&gt;&gt;'".</param>
        /// <param name="DeletesQuoted">The final delete also drops '&gt;&gt;' messages (ibs only).</param>
        /// <param name="Procs">Procs re-run after a change for what they derive beyond &amp;messages&amp; (w# files, ba_ tables).</param>
        public sealed record MsgType(string Ext, LiveTable Table, int Offset, int Lo, int Hi,
                                     bool BaseSkipsQuoted, bool DeletesQuoted, string[] Procs)
        {
            public int First => Lo + Offset;
            public int Last => Hi + Offset;
        }

        // sqr's proc reads 1..10000, but 30000 lies outside the range it deletes; 9999 keeps the target keyed inside it.
        public static readonly MsgType[] Types =
        {
            new("ibs_msg", IbsMessages, 15000, 1, 4999, false, true, new[] { "i_compile_ibs_messages" }),
            new("jam_msg", JamMessages, 0, 1, 9999, true, false, new[] { "i_compile_jam_messages" }),
            new("sqr_msg", SqrMessages, 20000, 1, 9999, true, false, new[] { "i_compile_sqr_messages", "i_compile_jrw_messages" }),
            new("sql_msg", SqlMessages, 30000, 1, 1999, true, false, Array.Empty<string>()),
        };

        public static string MessagesWhere =>
            string.Join(" or ", Types.Select(t => $"s#msgno between {t.First} and {t.Last}"));

        // As the reload's loader stores it: SybaseExecutor sends "" and ASE stores ' ' (never NULL; NULL is rejected by
        // NOT NULL gui_messages.message), MssqlExecutor/PostgresExecutor keep "". An empty number loads as 0 (NULL on ASE).
        private static string? Field(string s, SQLServerTypes d) => s == "" && d == SQLServerTypes.SYBASE ? " " : s;
        private static string? Number(string s, SQLServerTypes d) => s == "" ? (d == SQLServerTypes.SYBASE ? null : "0") : s.Trim();
        private static bool IsNumber(string s) =>
            s == "" || long.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);

        /// <summary>A css.*_msg line (tab-separated, as bcp loads it). Null for a blank line.</summary>
        public static string?[]? ParseMsgLine(string line, SQLServerTypes d, out string error)
        {
            error = "";
            if (line.Trim() == "") return null;
            var f = line.Split('\t');
            if (f.Length < 7) { error = $"expected 7 tab-separated columns, found {f.Length}"; return null; }
            foreach (var i in new[] { 0, 1, 2, 5 })
                if (!IsNumber(f[i])) { error = $"column {i + 1} '{f[i]}' is not a number"; return null; }
            for (int i = 0; i < 3; i++)
                if (f[i] == "") { error = $"column {i + 1} is empty"; return null; }
            // A tab inside the message is data, not a column break.
            return new[] { f[0].Trim(), f[1].Trim(), f[2].Trim(), Field(f[3], d), Field(f[4], d),
                           Number(f[5], d), Field(string.Join('\t', f.Skip(6)), d) };
        }

        public static string?[]? ParseGroupLine(string line, SQLServerTypes d, out string error)
        {
            error = "";
            if (line.Trim() == "") return null;
            var f = line.Split('\t');
            if (f.Length < 3) { error = $"expected 3 tab-separated columns, found {f.Length}"; return null; }
            if (f[0].Trim() == "") { error = "grp is empty"; return null; }
            if (!IsNumber(f[1])) { error = $"s#minmsg '{f[1]}' is not a number"; return null; }
            return new[] { f[0], Number(f[1], d), Field(string.Join('\t', f.Skip(2)), d) };
        }

        public static List<string?[]> ParseFile(string name, IList<string> lines, bool groups, SQLServerTypes d, List<string> errors)
        {
            var rows = new List<string?[]>();
            for (int i = 0; i < lines.Count; i++)
            {
                var r = groups ? ParseGroupLine(lines[i], d, out var err) : ParseMsgLine(lines[i], d, out err);
                if (err != "") errors.Add($"{name}:{i + 1}: {err}");
                else if (r != null) rows.Add(r);
            }
            return rows;
        }

        /// <summary>sqr_msg rows numbered 10000: the live target stops at 9999, see <see cref="Types"/>.</summary>
        public static List<string> SqrCapWarnings(IEnumerable<string?[]> sqrRows) =>
            sqrRows.Where(r => I(r[MsgNo]) == 10000).Select(r =>
                $"sqr_msg 10000 (lang {r[Lang]}, cmpy {r[Cmpy]}): i_compile_sqr_messages copies it to &messages& 30000, " +
                "outside the range it deletes, so that proc fails with a duplicate key on its next run; " +
                "the live &messages& target leaves 30000 alone").ToList();

        private static int I(string? v) => v == null ? int.MinValue : int.Parse(v, CultureInfo.InvariantCulture);
        private static bool Quoted(string? m) => m != null && m.StartsWith(">>", StringComparison.Ordinal);
        // ASE/MSSQL "message = ''" ignores trailing blanks and is unknown for NULL.
        private static bool Empty(string? m) => m != null && m.TrimEnd(' ') == "";

        /// <summary>The proc's ibs_messages etc. content: the file rows, upd_flg cleared ("where upd_flg != ''").</summary>
        public static List<string?[]> SourceRows(IEnumerable<string?[]> fileRows) =>
            fileRows.Select(r =>
            {
                var c = (string?[])r.Clone();
                if (c[UpdFlg] != null && c[UpdFlg]!.Trim(' ') != "") c[UpdFlg] = "";
                return c;
            }).ToList();

        /// <summary>The &amp;messages&amp; rows one per-type proc leaves in its range.</summary>
        public static List<string?[]> MessagesRows(MsgType t, IReadOnlyList<string?[]> src, int language, int company)
        {
            var order = new List<int>();
            var byNo = new Dictionary<int, string?[]>();
            foreach (var r in src)
            {
                int no = I(r[MsgNo]);
                if (I(r[Lang]) != 1 || I(r[Cmpy]) != 0 || no < t.Lo || no > t.Hi) continue;
                // NULL != '>>' is unknown, so the filtered insert skips a NULL message too.
                if (t.BaseSkipsQuoted && (r[Message] == null || Quoted(r[Message]))) continue;
                if (byNo.TryAdd(no + t.Offset, new[] { (no + t.Offset).ToString(CultureInfo.InvariantCulture), r[Grp], r[Message] }))
                    order.Add(no + t.Offset);
            }
            void Override(Func<string?[], bool> match)
            {
                foreach (var r in src)
                    if (match(r) && byNo.TryGetValue(I(r[MsgNo]) + t.Offset, out var m)) m[2] = r[Message];
            }
            if (language > 1) Override(r => I(r[Lang]) == language && I(r[Cmpy]) == 0);
            if (company > 1) Override(r => I(r[Cmpy]) == company && (I(r[Lang]) == 0 || I(r[Lang]) == 1));
            return order.Select(n => byNo[n])
                .Where(m => !Empty(m[2]) && !(t.DeletesQuoted && Quoted(m[2]))).ToList();
        }

        /// <summary>i_compile_gui_messages restores only for a translating site company.</summary>
        public static bool TranslatingCompany(int company) => company != 0 && company != 101;

        /// <summary>gui_messages target. For a translating company saved own-company rows (or, with none saved,
        /// the database's own rows) replace the file's own-company rows; with neither, the file's stay.</summary>
        /// <param name="saved">Own-company rows kept from a prior failed compile (keep), or null.</param>
        /// <param name="dbHasOwn">The database has own-company gui_messages rows (what the save step would copy).</param>
        public static LiveTarget GuiTarget(IReadOnlyList<string?[]> fileRows, int company, IReadOnlyList<string?[]>? saved, bool dbHasOwn)
        {
            var rows = fileRows.ToList();
            LiveKeep? keep = null;
            if (TranslatingCompany(company))
            {
                bool Own(string?[] r) => I(r[Cmpy]) == company;
                var savedOwn = saved?.Where(Own).ToList() ?? new List<string?[]>();
                if (savedOwn.Count > 0)
                    rows = rows.Where(r => !Own(r)).Concat(savedOwn).ToList();
                else if (dbHasOwn)
                {
                    rows = rows.Where(r => !Own(r)).ToList();
                    keep = (cur, _) => Own(cur);
                }
            }
            return new LiveTarget { Table = GuiMessages, Rows = rows, Keep = keep };
        }

        /// <summary>Every target, in the order the engine inserts (and reverse-deletes); the save table is
        /// <see cref="SaveTarget"/>, emptied in a later run once gui_messages read back.</summary>
        public static List<LiveTarget> Targets(IReadOnlyDictionary<string, List<string?[]>> files, int language, int company,
                                               IReadOnlyList<string?[]>? saved, bool dbHasOwn)
        {
            var list = new List<LiveTarget>
            {
                new() { Table = GuiMessageGroups, Rows = files["gui_msgrp"] },
                new() { Table = IbsMessageGroups, Rows = files["ibs_msgrp"] },
                GuiTarget(files["gui_msg"], company, saved, dbHasOwn),
            };
            var msgs = new List<string?[]>();
            foreach (var t in Types)
            {
                var src = SourceRows(files[t.Ext]);
                list.Add(new LiveTarget { Table = t.Table, Rows = src });
                msgs.AddRange(MessagesRows(t, src, language, company));
            }
            list.Add(new LiveTarget { Table = Messages, Rows = msgs, Where = MessagesWhere });
            return list;
        }

        /// <summary>The save table emptied, as i_compile_gui_messages does, by keyed deletes.</summary>
        public static LiveTarget SaveTarget() => new() { Table = GuiMessagesSave, Rows = new() };

        /// <summary>Procs to re-run for the types whose source table or &amp;messages&amp; range changed.</summary>
        public static List<string> ProcsFor(IReadOnlyList<LiveDiff> diffs)
        {
            var procs = new List<string>();
            foreach (var t in Types)
            {
                bool changed = diffs.Any(x => x.Table == t.Table && !x.IsEmpty) ||
                    diffs.Where(x => x.Table == Messages).SelectMany(x => x.Deletes.Concat(x.Updates).Concat(x.Inserts))
                         .Any(c => { int n = I((c.Kind == LiveChangeKind.Insert ? c.Target! : c.Current)[0]); return n >= t.First && n <= t.Last; });
                if (changed) procs.AddRange(t.Procs);
            }
            return procs;
        }
    }
}
