using System.Globalization;
using System.Text.RegularExpressions;

namespace ibsCompiler
{
    /// <summary>
    /// The actions file as ba_compile_actions loads it (SBN_IR css/ss/ba/pro_compile_actions.sql),
    /// and what the proc leaves for one action.
    /// </summary>
    public static class ActionModel
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // T-SQL substring (1-based). ASE returns NULL past the end or for a negative length; that
        // concatenates as '' there, and MSSQL returns '' past the end, so both map to "".
        private static string SqlSub(string s, int start, int len)
        {
            if (start < 1) { len += start - 1; start = 1; }
            if (len <= 0 || start > s.Length) return "";
            return s.Substring(start - 1, Math.Min(len, s.Length - start + 1));
        }

        private static int CharIndex(string what, string s) => s.IndexOf(what, StringComparison.Ordinal) + 1;

        // varchar(n) column: ASE drops trailing blanks; MSSQL (ANSI_PADDING on) keeps them.
        private static string VarCharCol(string s, int width, SQLServerTypes d)
        {
            var v = s.Length > width ? s.Substring(0, width) : s;
            return d == SQLServerTypes.SYBASE ? v.TrimEnd(' ') : v;
        }

        private static string Char5(string s) => s.Length >= 5 ? s.Substring(0, 5) : s.PadRight(5);

        private static bool Blank(string? s) => s == null || s.Trim(' ') == "";

        /// <summary>One ':>' line of the actions file, as ba_compile_actions loads it into #actions.</summary>
        public sealed class ActionLine
        {
            public int LineIx, Act, Type, MenuNo;
            public string Ctrl = "", MsgText = "", Flgs = "";
        }

        public sealed record ActionDtl(int Act, int Typ, int Ix, int MsgNo, int Shix);
        public sealed record ActionMenuRow(int LineIx, int MenuNo, string Ctrl, string Message, string Flgs);
        public sealed record ActionGuiRow(int Lang, int Cmpy, string Message);

        public sealed class ActionDef
        {
            public int Act;
            public List<ActionLine> Lines = new();
            public List<ActionDtl> Dtl = new();
            public int MsgNo => Act + 10000;
            public Dictionary<int, ActionLine> FirstByType() =>
                Lines.GroupBy(l => l.Type).ToDictionary(g => g.Key, g => g.First());
            public ActionLine? CtrlLine(int type) => Lines.FirstOrDefault(l => l.Type == type && !Blank(l.Ctrl));
        }

        /// <summary>What ba_compile_actions leaves for one action, given sysvalues company/language.</summary>
        public sealed class ActionExpect
        {
            public string? Message;
            public bool Present => !Blank(Message);
            public int ShixExisting, ShixNew;
            public List<ActionMenuRow> Menus = new();
            public List<ActionDtl> Dtl = new();
            public List<ActionGuiRow> Gui = new();
        }

        /// <summary>Same expressions as the proc's insert into #actions.</summary>
        public static ActionLine? ParseActionLine(int lineix, string m, SQLServerTypes d, out string error)
        {
            error = "";
            int g = CharIndex(">", m);
            if (!int.TryParse(SqlSub(m, g + 1, 4).Trim(), NumberStyles.AllowLeadingSign, Inv, out var act) ||
                !int.TryParse(SqlSub(m, g + 6, 3).Trim(), NumberStyles.AllowLeadingSign, Inv, out var type))
            { error = "action number / type is not numeric"; return null; }
            int lt = CharIndex("<<", m + "<<");
            var mm = m + "<<0>>";
            var menuS = SqlSub(mm, 2 + lt, CharIndex(">>", mm) - (lt + 2)).Trim();
            int menu = 0;
            if (menuS != "" && !int.TryParse(menuS, NumberStyles.AllowLeadingSign, Inv, out menu))
            { error = $"menu number '{menuS}' is not numeric"; return null; }
            var ctrl = SqlSub(m + "   ", CharIndex(">>", m + ">>") + 2, 100);
            var text = SqlSub(m, g + 9, lt - (g + 9)) + " ";
            var flgs = SqlSub(m, CharIndex("{", m + "{") + 1, CharIndex("}", m + "  }") - CharIndex("{", m) - 1) + " ";
            return new ActionLine
            {
                LineIx = lineix, Act = act, Type = type, MenuNo = menu,
                Ctrl = VarCharCol(ctrl, 255, d), MsgText = VarCharCol(text, 200, d), Flgs = Char5(flgs),
            };
        }

        // ' (K)' unless ctrl matches the proc's case-insensitive patindex '%i_menu%' / '#MENU%' ('_' is any one character).
        public static string MenuSuffix(string ctrl, int act) =>
            MenuPattern.IsMatch(ctrl) ? "" : $" ({act.ToString(Inv)})";

        private static readonly Regex MenuPattern = new(@"i.menu|^#menu", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

        /// <summary>gui_messages MENU text: after '} ' trimmed, else left-trimmed, plus the suffix (both dialects).</summary>
        public static string GuiText(ActionLine l)
        {
            int p = CharIndex("} ", l.MsgText);
            var body = p > 0 ? SqlSub(l.MsgText, p + 2, 255).Trim(' ') : l.MsgText.TrimStart(' ');
            return body + MenuSuffix(l.Ctrl, l.Act);
        }

        /// <summary>messages text: the '{flags}' prefix removed when the line has flags.</summary>
        public static string MessageText(ActionLine l, SQLServerTypes d) =>
            Blank(l.Flgs) ? l.MsgText : VarCharCol(SqlSub(l.MsgText, CharIndex("}", l.MsgText) + 1, 255), 200, d);

        public static string FinalFlgs(ActionLine l)
        {
            var f = Blank(l.Flgs) ? Char5("X") : l.Flgs;
            if (l.Act >= 300 && l.Act <= 500) f = Char5("XXX" + SqlSub(f, 4, 2));
            return f.TrimEnd(' ');
        }

        private static string MenuMessage(string msg, ActionLine l, SQLServerTypes d)
        {
            // ASE branch appends the suffix; the &if_mssql& branch takes messages.message as is.
            var v = d == SQLServerTypes.SYBASE ? msg + MenuSuffix(l.Ctrl, l.Act) : msg;
            return v.Length > 64 ? v.Substring(0, 64) : v;
        }

        public static ActionExpect Expect(ActionDef a, int company, int language, SQLServerTypes d)
        {
            var e = new ActionExpect();
            var byType = a.FirstByType();
            if (byType.TryGetValue(1, out var b))
            {
                e.Message = MessageText(b, d);
                if (language > 1 && byType.TryGetValue(language, out var ll)) e.Message = MessageText(ll, d);
                if (company > 1 && byType.TryGetValue(company, out var cl)) e.Message = MessageText(cl, d);
                e.ShixExisting = a.Lines.Where(l => l.Type == 1).Min(l => l.LineIx);
                e.ShixNew = a.Lines.Where(l => l.Type == 1 || l.Type == company || l.Type == language).Min(l => l.LineIx);
            }
            if (e.Present)
            {
                ActionLine? Over(int t, bool on) => on ? a.CtrlLine(t) : null;
                var lo = Over(language, language > 1);
                var co = Over(company, company > 1);
                foreach (var l in a.Lines.Where(l => l.Type == 1 && l.MenuNo != 0))
                {
                    string ctrl = l.Ctrl, flgs = FinalFlgs(l);
                    if (lo != null) { ctrl = lo.Ctrl; flgs = FinalFlgs(lo); }
                    if (co != null) { ctrl = co.Ctrl; flgs = FinalFlgs(co); }
                    if (!Blank(ctrl)) e.Menus.Add(new ActionMenuRow(l.LineIx, l.MenuNo, ctrl, MenuMessage(e.Message!, l, d), flgs));
                }
            }
            foreach (var x in a.Dtl.Where(x => x.Typ == 1).GroupBy(x => x.Ix).Select(g => g.First()))
            {
                int no = x.MsgNo;
                if (language > 1 && a.Dtl.FirstOrDefault(y => y.Ix == x.Ix && y.Typ == language) is { } yl) no = yl.MsgNo;
                if (company > 1 && a.Dtl.FirstOrDefault(y => y.Ix == x.Ix && y.Typ == company) is { } yc) no = yc.MsgNo;
                e.Dtl.Add(x with { MsgNo = no });
            }
            foreach (var l in byType.Values.Where(l => l.Type >= 0).OrderBy(l => l.Type))
                e.Gui.Add(l.Type < 100 ? new ActionGuiRow(l.Type, 0, GuiText(l)) : new ActionGuiRow(0, l.Type, GuiText(l)));
            return e;
        }
    }
}
