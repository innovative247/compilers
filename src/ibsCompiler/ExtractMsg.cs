using ibsCompiler.Configuration;
using ibsCompiler.Database;

namespace ibsCompiler
{
    /// <summary>
    /// Port of Python extract_msg.py.
    /// Automated export-then-import with no prompts.
    /// Always exports from database first (preserving current messages),
    /// then imports from files back into database.
    /// </summary>
    public static class extract_msg_main
    {
        public static int Run(string[] args)
        {
            var arguments = args.ToList();
            var profileMgr = new ProfileManager();

            var cmdvars = ibs_compiler_common.compile_variables(arguments, profileMgr);
            if (string.IsNullOrEmpty(cmdvars.Server))
            {
                Console.Error.WriteLine("Usage: extract_msg <server/profile> [-U user] [-P pass] [-O outfile] [-MSSQL|-SYBASE]");
                return 1;
            }

            var profile = profileMgr.Resolve(cmdvars);
            using var executor = SqlExecutorFactory.Create(profile);

            // Step 1: Export messages from database to files
            Console.WriteLine("Step 1: Exporting messages from database...");
            InteractiveMenus.RunMessageExport(cmdvars, profile, executor);

            // Step 2: Import messages from files into database
            Console.WriteLine();
            Console.WriteLine("Step 2: Compiling messages...");
            compile_msg_main.Run(cmdvars, profile, executor);

            Console.WriteLine("extract_msg DONE.");
            return 0;
        }
    }
}
