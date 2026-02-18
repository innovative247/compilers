using ibsCompiler.Configuration;

namespace ibsCompiler
{
    /// <summary>
    /// Port of F4.8 change_log.cs.
    /// Generates changelog SQL using ba_gen_chg_log_new stored procedure.
    /// Uses Environment.UserName instead of WindowsIdentity (cross-platform).
    /// </summary>
    static class change_log
    {
        private static string _whoAmI = "";

        private static string WhoAmI
        {
            get
            {
                if (string.IsNullOrEmpty(_whoAmI))
                    _whoAmI = Environment.UserName;
                return _whoAmI;
            }
        }

        /// <summary>
        /// Mask any password values in a string with ****.
        /// Handles common patterns: -P password, -P=password, PASSWORD=value
        /// </summary>
        private static string MaskPasswords(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            // -P password (space-separated)
            value = System.Text.RegularExpressions.Regex.Replace(
                value, @"(-P\s+)\S+", "$1****", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // -P=password or --password=value
            value = System.Text.RegularExpressions.Regex.Replace(
                value, @"(-P=|--password=)\S+", "$1****", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // PASSWORD=value (e.g. in connection strings)
            value = System.Text.RegularExpressions.Regex.Replace(
                value, @"(PASSWORD\s*=\s*)\S+", "$1****", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // pwd=value (e.g. in connection strings)
            value = System.Text.RegularExpressions.Regex.Replace(
                value, @"(pwd\s*=\s*)\S+", "$1****", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return value;
        }

        /// <summary>
        /// Generate changelog SQL lines for runsql, runcreate, and i_run_upgrade.
        /// </summary>
        public static IEnumerable<string> lines(CommandVariables cmdvars, ResolvedProfile profile)
        {
            if (cmdvars.ChangeLog)
            {
                var cmdName = string.IsNullOrEmpty(cmdvars.CommandName) ? "runsql" : cmdvars.CommandName;

                // PRGNO mapping
                var prgno = cmdName.ToUpper() switch
                {
                    "I_RUN_UPGRADE" => "UPGRADE",
                    _ => cmdName.ToUpper()
                };

                var fullPath = MaskPasswords(Path.GetFullPath(cmdvars.Command).Replace("'", "''"));
                var descr = $"User {WhoAmI}: {cmdName}".Replace("'", "''");
                var refno = cmdvars.Upgrade_no;

                yield return "if exists (select * from &options& where id = 'gclog12' and act_flg = '+') ";
                yield return "if exists (select * from &dbpro&..sysobjects where name = 'ba_gen_chg_log_new') ";
                yield return $"exec &dbpro&..ba_gen_chg_log_new '', '{descr}', '{prgno}', '', '{fullPath}', '{refno}','X'";
                yield return "go";
                yield return "";
            }
        }

        /// <summary>
        /// Generate changelog SQL for compile commands (actions, options, table_locations, messages).
        /// Called directly by the compile methods — does not check cmdvars.ChangeLog since these
        /// always log when gclog12 is active.
        /// </summary>
        public static IEnumerable<string> compileLines(string prgno, string description)
        {
            var descr = $"User {WhoAmI}: {description}".Replace("'", "''");

            yield return "if exists (select * from &options& where id = 'gclog12' and act_flg = '+') ";
            yield return "if exists (select * from &dbpro&..sysobjects where name = 'ba_gen_chg_log_new') ";
            yield return $"exec &dbpro&..ba_gen_chg_log_new '', '{descr}', '{prgno}', '', '', '','X'";
            yield return "go";
            yield return "";
        }
    }
}
