namespace ibsCompiler
{
    /// <summary>Change-log rows the live path writes in place of the ones the reload procs write.</summary>
    public static class LiveChangeLog
    {
        // Cut so "User <who>: " + it fits varchar(255).
        public static string Description(string what, string? mod, IList<string> ids)
        {
            var d = what + (mod != null ? $" MOD {mod}" : "") + (ids.Count > 0 ? ": " + string.Join(",", ids) : "");
            return d.Length > 200 ? d.Substring(0, 200) : d;
        }

        // The row i_compile_gui_messages (MSG_GUI) or i_compile_sql_messages (MSG_SQL) writes when gclog2 is on.
        public static IEnumerable<string> MsgGuiLines(string description, string subject = "MSG_GUI")
        {
            yield return "if exists (select * from &options& where id = 'gclog2' and act_flg = '+')";
            yield return "begin";
            yield return "  declare @chg_s#us smallint, @userid varchar(10), @msg varchar(255)";
            yield return "  exec u_get_user_info @s#us = @chg_s#us out";
            yield return "  if @chg_s#us is null select @chg_s#us = 0";
            yield return "  select @userid = isnull(userid, '') from &users& where s#us = @chg_s#us";
            yield return $"  select @msg = substring('user: ' + isnull(@userid, '') + ' {description.Replace("'", "''")}', 1, 255)";
            yield return $"  exec &dbpro&..ba_gen_chg_log_new '0', @msg, '{subject}', '', '', ''";
            yield return "end";
            yield return "go";
            yield return "";
        }
    }
}
