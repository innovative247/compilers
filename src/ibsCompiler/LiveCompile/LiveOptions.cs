namespace ibsCompiler
{
    /// <summary>
    /// &amp;options&amp; on the live path: the target is what i_import_options
    /// (SBN_IR ibs/ss/pro_import_options.sql) leaves after a reload from the merged option lines.
    /// </summary>
    public static class LiveOptions
    {
        public static readonly LiveTable Table = new()
        {
            Label = "options", Placeholder = "&options&",
            Columns = new LiveColumn[]
            {
                new("id", LiveColType.VarChar), new("act_flg", LiveColType.Char),
                new("if_flg", LiveColType.Char), new("val_flg", LiveColType.Char),
                new("value", LiveColType.NVarChar), new("dyn_flg", LiveColType.Char),
                new("description", LiveColType.VarChar),
            },
            Key = new[] { 0 },
        };

        public const int Id = 0, DynFlg = 5;

        /// <summary>
        /// Parses a merged ':>' line exactly as i_import_options does: fixed offsets from the
        /// first '>', value between the first '&lt;&lt;' and '&gt;&gt;'. Null for a line without an id.
        /// </summary>
        public static string?[]? ParseLine(string line, SQLServerTypes dialect)
        {
            int g = line.IndexOf('>');
            if (g < 0 || line.Length < g + 2) return null;
            string At(int off, int len) => g + off >= line.Length ? "" :
                line.Substring(g + off, Math.Min(len, line.Length - g - off));
            var id = At(1, 8).TrimEnd();
            if (id == "") return null;
            var ifFlg = At(12, 1);
            var dyn = At(16, 1);
            if (dyn != "+" && dyn != "-") dyn = " ";

            string value;
            int lt = line.IndexOf("<<", StringComparison.Ordinal);
            if (lt < 0) value = "";
            else
            {
                int gt2 = line.IndexOf(">>", StringComparison.Ordinal);
                value = gt2 < 0 ? line.Substring(lt + 2) : gt2 > lt + 2 ? line.Substring(lt + 2, gt2 - lt - 2) : "";
            }

            string desc;
            if (ifFlg == "+") desc = At(18, 255);
            else
            {
                int gt2 = line.IndexOf(">>", StringComparison.Ordinal);
                desc = gt2 < 0 ? "" : line.Substring(gt2 + 2);
            }
            if (desc.Length > 255) desc = desc.Substring(0, 255);
            desc = desc.Trim();

            // ASE substring of length 0 is NULL; MSSQL gives ''.
            return new[] { id, At(10, 1), ifFlg, At(14, 1),
                           value == "" && dialect == SQLServerTypes.SYBASE ? null : value, dyn, desc };
        }

        /// <summary>
        /// Target rows from the merged option lines. The proc's own de-duplication never fires (the
        /// lines carry no ix), so two lines with the same 8-character id are reported and the last wins.
        /// </summary>
        public static List<string?[]> TargetRows(IEnumerable<string> lines, SQLServerTypes d, List<string> warnings)
        {
            var byKey = new Dictionary<string, string?[]>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var l in lines)
            {
                if (ParseLine(l, d) is not { } r) continue;
                var k = Table.KeyOf(r, d);
                if (byKey.ContainsKey(k)) warnings.Add($"option id '{r[Id]}' appears more than once; the last line is used");
                else order.Add(k);
                byKey[k] = r;
            }
            return order.Select(k => byKey[k]).ToList();
        }

        /// <summary>
        /// i_import_options keeps an existing dynamic row only when the file row is dynamic too
        /// ("insert ... where dyn_flg = '+' and not exists"). A dynamic row turned static is replaced,
        /// and a dynamic row missing from the file is deleted (the proc's last delete).
        /// </summary>
        public static bool Keep(string?[] current, string?[]? target) =>
            current[DynFlg]?.Trim() == "+" && target != null && target[DynFlg]?.Trim() == "+";

        public static LiveTarget Target(List<string?[]> rows) => new() { Table = Table, Rows = rows, Keep = Keep };
    }
}
