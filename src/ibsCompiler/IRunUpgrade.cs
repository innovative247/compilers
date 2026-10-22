using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace ibsCompiler
{
    /// <summary>
    /// Port of legacy i_run_upgrade (ksh): skip an upgrade already recorded in &amp;upgrades&amp;,
    /// record it, run its script with runsql, then stamp its end time. It prints what legacy
    /// printed: the insert's and the update's row counts around runsql's own output.
    /// </summary>
    public class i_run_upgrade_main
    {
        public bool Run(CommandVariables cmdvars, ResolvedProfile profile, ISqlExecutor executor, Options? existingOptions = null)
        {
            if (profile.RawMode && existingOptions == null)
            {
                ibs_compiler_common.WriteLine("ERROR: i_run_upgrade requires option file processing and is not available in raw mode.", cmdvars.OutFile);
                return false;
            }

            var myOptions = existingOptions ?? new Options(cmdvars, profile);
            if (existingOptions == null && !myOptions.GenerateOptionFiles()) return false;

            bool isPostgres = profile.ServerType == SQLServerTypes.POSTGRES;
            var upgradeNo = cmdvars.Upgrade_no.Replace("'", "''");

            // Legacy's isqlline calls: in master, never echoed, no changelog.
            var sqlVars = cmdvars;
            sqlVars.EchoInput = false;
            sqlVars.Database = profile.AdminDatabase;

            // Legacy grepped the select's output for the value.
            bool Recorded(string value)
            {
                sqlVars.Command = $"select upgrade_no from &upgrades& where upgrade_no = '{value.Replace("'", "''")}'";
                var r = isqlline_main.Run(sqlVars, profile, executor, myOptions, redirectOutput: true);
                return (r.Output ?? "").Split('\n').Any(l => l.Contains(value));
            }

            if (!Recorded("CONTROL"))
            {
                ibs_compiler_common.WriteLine($"Upgrade table does not exist or control record missing on server {cmdvars.ServerNameOnly}", cmdvars.OutFile);
                return false;
            }
            if (Recorded(cmdvars.Upgrade_no))
            {
                ibs_compiler_common.WriteLine("This upgrade has already been run !!! ", cmdvars.OutFile);
                return false;
            }

            sqlVars.Command = (isPostgres ? "insert into" : "insert") +
                $" &upgrades& (upgrade_no,version,ix,opc,filename) values('{upgradeNo}','',0,1,'')";
            isqlline_main.Run(sqlVars, profile, executor, myOptions);

            // The whole script through runsql, which logs it; legacy's runsql failed only on a
            // missing script, and only then was the end time left unset.
            var script = cmdvars.Command;
            var found = ibs_compiler_common.FindFile(ref script);
            var runVars = cmdvars;
            runVars.CommandName = "runsql";
            new runsql_main().Run(runVars, profile, executor, true, myOptions);
            if (!found) return false;

            sqlVars.Command = isPostgres
                ? $"update &upgrades& set end_tm=EXTRACT(EPOCH FROM (now() - TIMESTAMP '1980-01-01'))::int where upgrade_no = '{upgradeNo}' and ix = 0 and opc = 1"
                : $"update &upgrades& set end_tm=datediff(ss,'800101',getdate()) where upgrade_no = '{upgradeNo}' and ix = 0 and opc = 1";
            isqlline_main.Run(sqlVars, profile, executor, myOptions);
            return true;
        }
    }
}
