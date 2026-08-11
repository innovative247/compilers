using ibsCompiler;
using ibsCompiler.Configuration;
using ibsCompiler.Database;

const string Usage = "Usage: bcp_data <table...> <IN|OUT> [datafile] <server/profile> [-t field_terminator] [--truncate] [-U user] [-P pass] [-O outfile] [-MSSQL|-SYBASE|-POSTGRES]";
if (!VersionCheck.CheckForUpdates("bcp_data", args, Usage)) return 0;

var arguments = args.ToList();
var profileMgr = new ProfileManager();

var cmdvars = ibs_compiler_common.bcp_data_variables(arguments, profileMgr);
if (string.IsNullOrEmpty(cmdvars.Server))
{
    Console.Error.WriteLine("ERROR: <IN|OUT> and <server/profile> are both required.");
    Console.Error.WriteLine(Usage);
    return 1;
}

if (cmdvars.Bcp != "IN" && cmdvars.Bcp != "OUT")
{
    Console.Error.WriteLine($"ERROR: direction must be IN or OUT (got '{cmdvars.Bcp}').");
    Console.Error.WriteLine(Usage);
    return 1;
}

if (cmdvars.FieldTerminator.Length == 0)
{
    Console.Error.WriteLine("ERROR: -t requires a field terminator (e.g. -t\"|\" or -t\\t).");
    Console.Error.WriteLine(Usage);
    return 1;
}

// bcp_data_variables consumed the trailing positionals (direction + optional data
// file + server) and DefaultCommandVariables already stripped every flag — what is
// left in front is the table list.
var trailing = string.IsNullOrEmpty(cmdvars.DataFile) ? 2 : 3;
var tables = arguments.GetRange(0, arguments.Count - trailing);
if (tables.Count == 0)
{
    Console.Error.WriteLine("ERROR: at least one table name is required.");
    Console.Error.WriteLine(Usage);
    return 1;
}

// One data file cannot hold several tables' data, and native bcp is one table per
// call anyway — so naming a file only makes sense with a single table.
if (!string.IsNullOrEmpty(cmdvars.DataFile) && tables.Count != 1)
{
    Console.Error.WriteLine("ERROR: a data file requires exactly one table.");
    Console.Error.WriteLine(Usage);
    return 1;
}

if (!profileMgr.ValidateProfile(cmdvars.Server)) return 1;
var profile = profileMgr.Resolve(cmdvars);

// Production safety guard — never bulk-load into GONZO. Same name-based rule
// set_messages --import enforces (GONZO is the canonical source; export-only).
// Fires before the executor exists so nothing touches the server.
var profileName = profile.IsProfile ? profile.ProfileName : cmdvars.ServerNameOnly;
var direction = cmdvars.Bcp == "IN" ? BcpDirection.IN : BcpDirection.OUT;
if (direction == BcpDirection.IN &&
    (profileName.Equals("GONZO", StringComparison.OrdinalIgnoreCase) ||
     profileName.Equals("G", StringComparison.OrdinalIgnoreCase)))
{
    Console.Error.WriteLine($"ERROR: IN is not allowed against {profileName} (GONZO is the canonical source; OUT only).");
    return 1;
}

using var executor = SqlExecutorFactory.Create(profile);

// Bare table names resolve as &table& against the merged option set
// (options.<ServerType> + options.<company> + options.<company>.<server> +
// table_locations), exactly like set_profile --test --what options. That is what
// lets table_locations decide which database the table lives in. Names the caller
// already qualified (db..table) are used as-is; raw-mode profiles have no option
// files at all, so nothing is resolved there either.
Options? myOptions = null;
if (!profile.RawMode && tables.Exists(t => !t.Contains("..")))
{
    myOptions = new Options(cmdvars, profile, true);
    if (!myOptions.GenerateOptionFiles()) return 1;
}

int failed = 0;
foreach (var table in tables)
{
    if (!RunTable(table)) failed++;
}
return failed == 0 ? 0 : 1;

bool RunTable(string table)
{
    var label = BareName(table).ToUpper();
    // Without the datafile positional the file is named after the table, in the
    // current directory — same convention as the Unix export_sql_table_data /
    // import_sql_table_data pair, with transfer_data's .bcp extension.
    var dataFile = Path.Combine(Environment.CurrentDirectory,
        string.IsNullOrEmpty(cmdvars.DataFile) ? BareName(table) + ".bcp" : cmdvars.DataFile);

    // Checked before anything else so a missing file never opens a connection.
    if (direction == BcpDirection.IN && !File.Exists(dataFile))
    {
        ibs_compiler_common.WriteLine($"ERROR! Data file not found: {dataFile}", cmdvars.OutFile);
        return false;
    }

    // Bare token -> &token&, same normalization set_profile --test --what options
    // applies; a token that already carries its own delimiters is left alone.
    var resolved = table.Contains("..") || myOptions == null
        ? table
        : myOptions.ReplaceOptions(table.Contains('&') ? table.Trim() : "&" + table.Trim() + "&");
    if (resolved.Contains('&'))
    {
        ibs_compiler_common.WriteLine($"ERROR! Cannot resolve table '{table}' — no table_locations entry for &{table}&.", cmdvars.OutFile);
        return false;
    }

    // BulkCopy takes the qualified name and each executor splits it itself (on
    // POSTGRES the db part is a schema). Plain SQL, though, has no portable
    // db..table form — so statements go out with the bare name and the database
    // handed to the executor, which is what makes them work on all three
    // platforms (initial catalog on MSSQL/Sybase, search_path on POSTGRES).
    // The Unix scripts did exactly this: isqlline "truncate table $tbl" $db $sv.
    var tableOnly = BareName(resolved);
    var database = resolved.Contains("..")
        ? resolved.Split(new[] { ".." }, 2, StringSplitOptions.None)[0]
        : cmdvars.Database;
    var verb = direction == BcpDirection.IN ? "Import" : "Export";
    // Same shape as the Unix scripts: "<verb> of <TBL> from <source> to <target> <phase> at <time>".
    string StatusLine(string phase) => direction == BcpDirection.IN
        ? $"Import of {label} from {dataFile} to {resolved} on {profileName} {phase} at {Timestamp()}"
        : $"Export of {label} from {resolved} on {profileName} to {dataFile} {phase} at {Timestamp()}";

    ibs_compiler_common.WriteLine(StatusLine("started"), cmdvars.OutFile);

    // Native bcp appends; --truncate is the opt-in for the legacy
    // import_sql_table_data behavior of emptying the table first.
    if (direction == BcpDirection.IN && cmdvars.Truncate)
    {
        var truncate = executor.ExecuteSql($"truncate table {tableOnly}", database, false, cmdvars.OutFile);
        if (!truncate.Returncode)
        {
            ibs_compiler_common.WriteLine($"ERROR! Truncate of {resolved} failed.", cmdvars.OutFile);
            return false;
        }
    }

    // Sybase stores char data in the server's charset and SybaseExecutor reinterprets
    // against it (DATA_CHARSET); say which one an import is being written through so a
    // mojibake result is diagnosable from the log alone.
    if (direction == BcpDirection.IN && executor is SybaseExecutor sybase)
        ibs_compiler_common.WriteLine($"server charset: {sybase.ServerCharset}", cmdvars.OutFile);

    var result = executor.BulkCopy(resolved, direction, dataFile, fieldTerminator: cmdvars.FieldTerminator);
    if (!result.Returncode)
    {
        ibs_compiler_common.WriteLine($"ERROR! {verb} of {label} failed. {result.Output}", cmdvars.OutFile);
        return false;
    }

    // BulkCopy OUT returns its row count but only prints "rows successfully
    // extracted", so the count line is ours to write. BulkCopyIn prints its own
    // "N rows copied." — printing a second one here would just duplicate it.
    if (direction == BcpDirection.OUT && long.TryParse(result.Output?.Trim(), out var copied))
        ibs_compiler_common.WriteLine($"{copied} rows copied.", cmdvars.OutFile);

    if (direction == BcpDirection.IN)
    {
        // Legacy import_sql_table_data ran this after every table. "update
        // statistics" is Sybase/MSSQL syntax; ANALYZE is the POSTGRES equivalent.
        // Either way the rows are already in, so a refusal is a warning, not a
        // failed import.
        var statsSql = profile.ServerType == SQLServerTypes.POSTGRES
            ? $"analyze {tableOnly}"
            : $"update statistics {tableOnly}";
        var stats = executor.ExecuteSql(statsSql, database, false, cmdvars.OutFile);
        if (!stats.Returncode)
            ibs_compiler_common.WriteLine($"Warning: {statsSql} on {resolved} failed, continuing...", cmdvars.OutFile);
    }

    ibs_compiler_common.WriteLine(StatusLine("ended"), cmdvars.OutFile);
    return true;
}

static string BareName(string table)
    => table.Contains("..") ? table.Split(new[] { ".." }, 2, StringSplitOptions.None)[1] : table.Trim();

static string Timestamp() => DateTime.Now.ToString("yy-MM-dd_HH:mm:ss");
