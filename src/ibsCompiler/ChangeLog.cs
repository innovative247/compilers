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

        public static IEnumerable<string> lines(CommandVariables cmdvars, ResolvedProfile profile)
        {
            if (cmdvars.ChangeLog)
            {
                if (string.IsNullOrEmpty(_whoAmI))
                    _whoAmI = Environment.UserName;

                var sc = cmdvars.Command.Replace("'", "''");
                var db = cmdvars.Database;
                var sv = profile.IsProfile ? profile.ProfileName : cmdvars.ServerNameOnly;
                var cmpy = profile.Company;
                var refno = cmdvars.Upgrade_no;

                yield return "if exists (select * from &options& where id = 'gclog12' and act_flg = '+') ";
                yield return "if exists (select * from &dbpro&..sysobjects where name = 'ba_gen_chg_log_new') ";
                yield return $"exec &dbpro&..ba_gen_chg_log_new '', 'User `{_whoAmI}` recompiled sproc or ran sql', 'RUNSQL', '',  'runsql {sc} {db} {sv} {cmpy}', '{refno}','X'";
                yield return "go";
                yield return "";
            }
        }
    }
}
