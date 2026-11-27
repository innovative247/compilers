using System.Globalization;

namespace ibsCompiler
{
    /// <summary>
    /// Action tables on the live path: the target is what ba_compile_actions leaves
    /// (SBN_IR css/ss/ba/pro_compile_actions.sql), for the whole actions and actions_dtl files.
    /// </summary>
    public static class LiveActions
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private const string MsgWhere = "s#msgno between 10000 and 12999";

        // chg_tm is the compile time, so comparing it would update every row on every run.
        public static readonly LiveTable GuiMessages = new()
        {
            Label = LiveMessages.GuiMessages.Label, Placeholder = LiveMessages.GuiMessages.Placeholder,
            Columns = LiveMessages.GuiMessages.Columns.Select(c => c.Name == "chg_tm" ? c with { Compare = false } : c).ToArray(),
            Key = LiveMessages.GuiMessages.Key,
        };

        public static readonly LiveTable Actions = new()
        {
            Label = "actions", Placeholder = "&actions&",
            Columns = new LiveColumn[]
            {
                new("s#act", LiveColType.Int), new("ix", LiveColType.Int),
                new("s#msgno", LiveColType.Int), new("shix", LiveColType.Int),
            },
            Key = new[] { 0, 1 },
        };

        public static readonly LiveTable Menus = new()
        {
            Label = "menus", Placeholder = "&menus&",
            Columns = new LiveColumn[]
            {
                new("lineix", LiveColType.Int), new("menuno", LiveColType.Int), new("s#act", LiveColType.Int),
                new("field_ctrlstr", LiveColType.VarChar, LiveNullCompare.Blank),
                new("message", LiveColType.VarChar, LiveNullCompare.Blank),
                new("flgs", LiveColType.Char, LiveNullCompare.Blank),
            },
            Key = new[] { 0 },
        };

        public static readonly LiveTable ProfileDetails = new()
        {
            Label = "action_profile_details", Placeholder = "&action_profile_details&",
            Columns = new LiveColumn[]
            {
                new("s#aprof", LiveColType.Int), new("s#act", LiveColType.Int), new("ix", LiveColType.Int),
            },
            Key = new[] { 0, 1, 2 },
        };

        // The current state BuildTargets takes; chg_tm from the server clock, as the proc stamps it.
        public const string SysValuesSql = "select company, language, datediff(ss,'800101',getdate()) from &sysvalues&";
        public const string ActionKeysSql = "select s#act, ix, s#msgno from &actions&";
        public const string ProfilesSql = "select s#aprof, name from &action_profiles&";

        /// <summary>The s#aprof of the profiles named <paramref name="allActName"/> (option allact, resolved).</summary>
        public static List<int> AllActProfiles(IEnumerable<(int Aprof, string Name)> profiles, string allActName) =>
            profiles.Where(p => p.Name.Trim() == allActName.Trim()).Select(p => p.Aprof).ToList();

        private static string S(int v) => v.ToString(Inv);

        /// <summary>One ActionDef per action number in either file. Non-numeric fields go to <paramref name="errors"/>.</summary>
        public static List<ActionModel.ActionDef> ParseAll(IList<string> actLines, IList<string> dtlLines,
            Func<string, string> resolve, SQLServerTypes d, List<string> errors,
            string actSource = "actions", string dtlSource = "actions_dtl")
        {
            var defs = new SortedDictionary<int, ActionModel.ActionDef>();
            ActionModel.ActionDef Def(int k) =>
                defs.TryGetValue(k, out var x) ? x : defs[k] = new ActionModel.ActionDef { Act = k };
            foreach (var (i, line) in action_files.HeaderLines(actLines, resolve, actSource))
            {
                var l = ActionModel.ParseActionLine(i, line, d, out var err);
                if (l == null) errors.Add($"{actSource} lineix {i}: {err}");
                else Def(l.Act).Lines.Add(l);
            }
            foreach (var f in action_files.DetailFields(dtlLines, resolve, dtlSource))
            {
                if (int.TryParse(f[0].Trim(), NumberStyles.AllowLeadingSign, Inv, out var k) &&
                    int.TryParse(f[1].Trim(), NumberStyles.AllowLeadingSign, Inv, out var typ) &&
                    int.TryParse(f[2].Trim(), NumberStyles.AllowLeadingSign, Inv, out var ix) &&
                    int.TryParse(f[3].Trim(), NumberStyles.AllowLeadingSign, Inv, out var no) &&
                    int.TryParse(f[4].Trim(), NumberStyles.AllowLeadingSign, Inv, out var shix))
                    Def(k).Dtl.Add(new ActionModel.ActionDtl(k, typ, ix, no, shix));
                else errors.Add($"{dtlSource} '{string.Join(" ", f)}': a field is not numeric");
            }
            return defs.Values.ToList();
        }

        /// <summary>Every target in insert order (deletes run reversed): gui_messages, messages, actions,
        /// menus, action_profile_details. No database access: the current state comes in.</summary>
        public static List<LiveTarget> BuildTargets(IList<string> actLines, IList<string> dtlLines, Func<string, string> resolve,
            SQLServerTypes d, int company, int language, IEnumerable<(int Act, int Ix, int MsgNo)> currentActionKeys,
            IEnumerable<int> profiles, IReadOnlyCollection<int> allAct, int chgTm, List<string> errors, List<string> warnings)
        {
            var defs = ParseAll(actLines, dtlLines, resolve, d, errors);
            var cur = currentActionKeys.ToList();
            var current = cur.Select(k => (k.Act, k.Ix)).ToHashSet();
            var currentIx0 = cur.Where(k => k.Ix == 0).GroupBy(k => k.Act).ToDictionary(g => g.Key, g => g.First().MsgNo);

            var gui = new List<string?[]>();
            var msgs = new List<string?[]>();
            var acts = new Dictionary<(int Act, int Ix), string?[]>();
            var actOrder = new List<(int, int)>();
            var menus = new List<string?[]>();
            var present = new HashSet<int>();

            void Act(int act, int ix, int no, int shix)
            {
                if (!acts.ContainsKey((act, ix))) actOrder.Add((act, ix));
                acts[(act, ix)] = new[] { S(act), S(ix), S(no), S(shix) };
            }

            foreach (var a in defs)
            {
                foreach (var g in a.Lines.GroupBy(l => l.Type)
                             .Where(g => g.Select(l => ActionModel.MessageText(l, d)).Distinct().Count() > 1))
                    warnings.Add($"action {a.Act} type {g.Key} has different texts; lineix {g.First().LineIx} is used.");

                var e = ActionModel.Expect(a, company, language, d);
                int no = a.MsgNo;
                bool inRange = a.Act >= 0 && a.Act <= 2999;
                if (a.Lines.Count > 0 && !inRange)
                    warnings.Add($"action {a.Act} is outside 0..2999, so it gets no gui_messages or messages rows.");
                if (inRange)
                {
                    foreach (var r in e.Gui)
                        gui.Add(new[] { S(no), S(r.Lang), S(r.Cmpy), "MENU", "", S(chgTm), r.Message });
                    if (a.Lines.Any(l => l.Type == 1) && (a.Act == 0 || e.Present))
                        msgs.Add(new[] { S(no), " ", e.Message });
                }
                if (a.Act >= 1 && a.Act <= 2999 && e.Present)
                {
                    present.Add(a.Act);
                    // The proc inserts s#act+10000 only for a new row and never rewrites an existing one's s#msgno.
                    if (currentIx0.TryGetValue(a.Act, out var curNo)) Act(a.Act, 0, curNo, e.ShixExisting);
                    else Act(a.Act, 0, no, e.ShixNew);
                    foreach (var m in e.Menus)
                        menus.Add(new[] { S(m.LineIx), S(m.MenuNo), S(a.Act), m.Ctrl, m.Message, m.Flgs });
                }
            }
            // actions_dtl applies to every action, after the ix 0 rows (proc :416-449).
            foreach (var a in defs)
                foreach (var x in ActionModel.Expect(a, company, language, d).Dtl)
                    Act(a.Act, x.Ix, x.MsgNo, x.Shix);

            WarnOverlappingMenus(defs, company, language, warnings);

            var actRows = actOrder.Select(k => acts[k]).ToList();
            var actKeys = acts.Keys.ToHashSet();
            // The proc deletes a messageless action's rows and re-inserts its dtl rows; the delete
            // trigger drops them from every profile, which an in-place keep would not.
            var reset = current.Where(k => k.Act > 0 && !present.Contains(k.Act) && actKeys.Contains(k)).ToHashSet();

            var prof = new List<string?[]>();
            foreach (var p in allAct)
                foreach (var k in actOrder.Where(k => k.Item1 >= 1 && k.Item1 <= 3000))
                    prof.Add(new[] { S(p), S(k.Item1), S(k.Item2) });
            foreach (var p in profiles.Distinct())
                prof.Add(new[] { S(p), "0", "0" });
            var where = new List<string> { "(s#act = 0 and ix = 0)" };
            if (allAct.Count > 0) where.Insert(0, $"s#aprof in ({string.Join(", ", allAct.Select(S))})");
            var resetActs = reset.Select(k => k.Act).Distinct().OrderBy(x => x).ToList();
            if (resetActs.Count > 0) where.Add($"s#act in ({string.Join(", ", resetActs.Select(S))})");

            static int I(string? v) => int.Parse(v!.Trim(), Inv);
            return new List<LiveTarget>
            {
                new() { Table = GuiMessages, Rows = gui, Where = MsgWhere, Keep = (cur, _) => I(cur[LiveMessages.Cmpy]) != 0 },
                new() { Table = LiveMessages.Messages, Rows = msgs, Where = MsgWhere },
                new() { Table = Actions, Rows = actRows, Keep = (cur, tgt) =>
                        tgt == null && I(cur[1]) <= 0 && (I(cur[0]) <= 0 || present.Contains(I(cur[0]))) },
                new() { Table = Menus, Rows = menus },
                new() { Table = ProfileDetails, Rows = prof, Where = string.Join(" or ", where),
                        Keep = (cur, _) => !reset.Contains((I(cur[1]), I(cur[2]))) },
            };
        }

        /// <summary>The proc's "Menus intermingled with other menus" check.</summary>
        private static void WarnOverlappingMenus(List<ActionModel.ActionDef> defs, int company, int language, List<string> warnings)
        {
            var info = defs.SelectMany(a => a.Lines)
                .Where(l => l.MenuNo > 0 && (l.Type == 1 || l.Type == company || l.Type == language))
                .GroupBy(l => l.MenuNo)
                .Select(g => (Menu: g.Key, Min: g.Min(l => l.LineIx), Max: g.Max(l => l.LineIx)))
                .OrderBy(m => m.Menu).ToList();
            foreach (var m1 in info)
                foreach (var m2 in info.Where(m2 => m1.Menu < m2.Menu && m1.Min <= m2.Max && m1.Max >= m2.Min))
                    warnings.Add($"menu {m1.Menu} (lineix {m1.Min}-{m1.Max}) is intermingled with menu {m2.Menu} (lineix {m2.Min}-{m2.Max}).");
        }
    }
}
