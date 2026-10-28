# Compilers Architecture

## Database Connectivity

### ADO.NET Providers

| Platform | Provider | Package |
|----------|----------|---------|
| MSSQL | `Microsoft.Data.SqlClient` | NuGet package |
| Sybase ASE | `AdoNetCore.AseClient` | Local DLL (`src/AdoNetCore.AseClient.dll`) |
| PostgreSQL | `Npgsql` 8.0.5 | NuGet package |

All database operations go through the `ISqlExecutor` interface:

```csharp
public interface ISqlExecutor
{
    ExecReturn ExecuteSql(string sql, string database, bool captureOutput = false);
    void BulkCopy(string table, BcpDirection direction, string dataFile);
}
```

Factory creates the appropriate executor based on profile platform:

```csharp
var executor = SqlExecutorFactory.Create(profile);  // MssqlExecutor, SybaseExecutor, or PostgresExecutor
```

### BCP Implementation

| Platform | BulkCopyOut | BulkCopyIn |
|----------|-------------|------------|
| MSSQL | `SqlBulkCopy` + DataReader | `SqlBulkCopy` from tab-delimited file |
| Sybase | SELECT → tab-delimited file | Row-by-row INSERT |
| PostgreSQL | `BeginTextExport` (`COPY ... TO STDOUT (FORMAT text)`), unescaped to tab-delimited file | `BeginTextImport` (`COPY ... FROM STDIN (FORMAT text)`), PG-escaped straight from the tab-delimited file |

### isql output format

Everything an executor prints — result sets, row counts, return statuses, messages, errors — is what legacy `isqlline` / `runsql` (ksh wrappers around OCS `isql` 15.5 on Yoda) print against a Sybase server: byte for byte on Sybase except where noted below, and in the same layout on MSSQL and PostgreSQL, with each server's types mapped to the Sybase type that prints alike (mappings below). `IsqlFormatter` renders it for all three executors; it is platform-neutral (fed column widths/alignment and value strings) and also holds the value formats (`Money`, `Float`, `DateTimeValue` …). `SybaseExecutor` runs each batch with `AseCommand.ResultObserver` (IBS AseClient fork), which hands over row formats, rows, done tokens and return statuses in server order while `InfoMessage` delivers messages on the same thread — so everything interleaves as the server sent it. Fixtures and parity tests: `tests/fixtures/isql-parity` (the `.expected` files are legacy output captured on Yoda), suite cases `*_isql_parity*`.

- **Line width (`W`).** Console: the window width. Redirected output, `-O` files and captured output: 80 — legacy's effective width, since `isqlline` passes `-w $COLUMNS` and `COLUMNS` is not exported (isql's default is 80) and `runsql` passes no `-w`.
- **Column widths:** `tinyint` 3, `smallint` 6, `int` 11, `bigint` 20, `unsigned smallint/int/bigint` 5/10/20, `bit` 1, `real`/`float` 27, `money`/`smallmoney` 24, `numeric(p,s)` p+3, `datetime`/`smalldatetime` 31, `date` 16, `time` 15, char types their byte length, `binary`/`varbinary` 2+2×length, `text`/`image` 512; every width capped at 512, then widened to the column name. `unichar`/`univarchar`: 2+2×length shown as hex in an `isqlline` session, length/2×3 as text in a `runsql` session (below).
- **Layout.** Each column is `" " + value` padded to its width; a row ends with one more space. A column moves to a new line starting with a tab when `pos + width + 2 > W` (a column alone on its line may overflow). Headings are always left-aligned and never cut. Numbers, money and dates are right-aligned; NULL prints `NULL` in the column's alignment. A left-aligned value longer than `W - pos - 2` is cut: `W - pos - 1` characters, then tab-indented pieces, the last padded to what remains of the column width; `text`/`image` values are never cut.
- **Values.** `datetime` `Jan  2 2026  3:04AM`; `date` `Jan  2 2026`; `time` ` 3:04AM`; money 2 places with thousands separators, rounded half away from zero; `float`/`real` 6 places, or `1.000000000000e+20` past 27 characters; numeric at its declared scale; `bit` 1/0; binary `0x` + lower-case hex.
- **Counts.** A heading prints with the first row, or at the end of an empty result set — not when the statement fails first. A top-level statement prints `(N rows affected)` / `(1 row affected)` unless `set nocount on`, after a blank line when it ended a result set. Inside a procedure only a statement that ends a result set prints a count, and it prints the latest count the server flagged in that procedure (isql's client library holds a count until it reports it). `(return status = N)` follows every procedure.
- **Messages.** Prints and informational messages print their text; errors print `Msg n, Level l, State s:` / `Server 'x', [Procedure 'p', ]Line n:` / text. Text longer than `W` breaks at its last space before `W`; text with no such space is not broken. Database, language and charset-change messages (5701/5703/5704) are not printed. A one-line `isqlline` command is sent after an empty line, as legacy's wrapper does, so errors report `Line 2`.
- **Echo (`-E`).** Each batch as `isql -e` prints it: `1> `, `2> ` … per line, `go` not echoed. `runsql` also echoes (and runs) `use <db>` and the changelog batch first, and a final `1> ` for the empty batch legacy appended to every script.
- **runsql.** `Running <full path> on <server> in <db> ` (trailing space; ` <n>` before it with -F/-L), on every platform; a missing script prints `<full path> not found.`; no per-batch status line; `&db&` and `&sv&` are replaced by the database and server as given (legacy substitute_options); a `!!` command (and a single-`!` `!runsql` line, which one upgrade script uses) runs with the install folder first on PATH, so a nested runsql is this one, and its output is followed by isql's `[sh:<command>]`; the changelog record is legacy's (`runsql [-F<n> -L<n> ]<path> <db> <server> <company>`, `-r` refno, an -F/-L run logged once); the changelog's output is printed (PostgreSQL: echoed, run silently, and `use <db>` echoed but not run — the connection already sits in that database/schema). On Sybase its session asks for the utf8 client charset (legacy runsql ran isql with `$BCPJ`, `-Jutf8`), so the server converts char data and sizes char columns ×3 — falling back to the server charset where utf8 is refused. `isqlline` (legacy `-J`) uses the server charset.
- **runcreate.** Legacy runcreate's frame: a line holding one space, `>>>>>>> RUNCREATE  <script>  <server>  ><out>.out <out>.err` (script, server and outfile as given; `> ` with no outfile), the date as Unix `date` prints it (`Wed Oct 21 15:28:41 CDT 2026`), a one-space line; the same four lines with `<<<<<<< END RUNCREATE` at the end. A missing script prints `Can't find create script: <script>`. The `.err` beside the `.out` always exists, empty when nothing failed. It logs nothing itself: each runsql it dispatches logs its script, as legacy's did, so each prints its changelog output.
- **i_run_upgrade.** Legacy's ksh flow: in master, a select of `&upgrades&` for `CONTROL` (missing: `Upgrade table does not exist or control record missing on server <server>`) and for the upgrade (found: `This upgrade has already been run !!! `), printing nothing; a missing script prints `<full path> not found.` before anything is written (legacy recorded the upgrade first, so every retry was refused); then the insert (its `(1 row affected)`), the whole script through runsql (with `-r` as its refno) and the `end_tm` update (its count). The exit code is non-zero when the script had errors.
- **compute.** Each compute row (the driver parses `TDS_ALTFMT`/`TDS_ALTROW` and reports `ComputeRow` events) prints as a heading line of its aggregates' blank names, `Compute Result:`, a separator and the values: the aggregates left to right, each at its own type's width (not under the column it aggregates), laid out like a row. A row after a compute row (the next group of `compute ... by`) repeats the result set's heading. The statement's count includes the compute rows (the server counts them).
- **Charset.** Output is the server's bytes in the session charset (isqlline: the server's, cp850 on GONZO; runsql: utf8), to the console and to `-O` files, as legacy wrote them. isql measures, pads and cuts in bytes, so `SybaseExecutor` feeds the formatter every server string as one character per byte and writes its lines as those bytes; in a utf8 session a value is never cut inside a character. On a Windows console the output code page is switched to the session charset's while output is written and restored after (Ctrl+C included). Profile `DATA_CHARSET` reinterpretation applies to bcp only, not to isql output. On the way in, a character the server charset cannot hold is sent as its UTF-8 bytes (`SybaseExecutor.KeepUnconvertible`), as legacy `isql -J` passed a script's bytes, so the server stores those bytes rather than refusing the batch (Msg 2402) or the driver swapping in a look-alike (`—` to `-`, `’` to `'`). On runsql's utf8 session only comments are rewritten: a literal goes as it is, so the server keeps it in unichar/univarchar and refuses it for char data, as legacy runsql did. U+FFFD (an unreadable source byte) and a charset that cannot carry the bytes are left for the server to refuse. MSSQL (UTF-16) and PostgreSQL (UTF-8) send every character unchanged. MSSQL and PostgreSQL output is unchanged (console encoding, UTF-8 files).

#### MSSQL mapping

`MssqlExecutor` opens every session with `FireInfoMessageEventOnUserErrors`, so errors arrive through `InfoMessage` in server order with prints and rows (severity 20+ still throws), and takes row counts from `SqlCommand.StatementCompleted` (one per DONE token that carries a count; `set nocount on` sends none). The error header names `@@servername`.

| SQL Server type | prints as Sybase | width |
|---|---|---|
| `tinyint` `smallint` `int` `bigint` | same | 3 / 6 / 11 / 20 |
| `bit` | `bit` (1/0) | 1 |
| `real` `float` | `float` | 27 |
| `money` `smallmoney` | `money` | 24 |
| `decimal(p,s)` `numeric(p,s)` | `numeric(p,s)` | p+3 |
| `datetime` `smalldatetime` `datetime2` `datetimeoffset` | `datetime` (`datetimeoffset` at its own offset) | 31 |
| `date` / `time` | `date` / `time` | 16 / 15 |
| `char` `varchar` `nchar` `nvarchar` (n) | `char`/`varchar` (n characters) | n |
| `varchar(max)` `nvarchar(max)` `text` `ntext` `xml` | `text` | 512, never cut |
| `binary` `varbinary` (n), `timestamp`/`rowversion` | `binary` | 2+2n |
| `varbinary(max)` `image` | `image` | 512, never cut |
| `uniqueidentifier` | `char(36)` | 36 |

Not matched on MSSQL: `(return status = N)` — SqlClient does not surface the return status of an `exec` inside a text batch (only of an RPC call, which would change the batch's scope); accepted, as the F4.8 compilers' `sqlcmd` never printed it either; a counted statement inside a procedure prints its count, because `StatementCompleted` does not say whether a DONE came from inside a procedure; error and message texts, numbers and line numbers are SQL Server's own.

#### PostgreSQL mapping

`PostgresExecutor` runs a chunk statement by statement (D3), synchronously, so notices interleave with rows. A notice prints like a print. `set nocount on|off` (T-SQL) is taken as a client setting and suppresses the counts. A statement's count is the rows it returned, or `RecordsAffected` for one that returned none; DDL, `DO` and `SET` print none. An error prints as `Msg <SQLSTATE>, Level 16, State 1:` (FATAL 20, PANIC 21) / `Line n:` / text: no `Server` part (PostgreSQL has no `@@servername`), the line is the batch line the error position points at (a whole-statement error: the statement's first line); an error raised inside a PL/pgSQL function shows `Procedure '<function>', Line <its line>:`, one in a `DO` block the batch line.

| PostgreSQL type | prints as Sybase | width |
|---|---|---|
| `smallint` `integer` `bigint` (`oid`) | `smallint` `int` `bigint` | 6 / 11 / 20 |
| `boolean` | `bit` (1/0) | 1 |
| `real` `double precision` | `float` | 27 |
| `money` | `money` | 24 |
| `numeric(p,s)` | `numeric(p,s)` | p+3 |
| `timestamp` `timestamptz` (local time) | `datetime` | 31 |
| `date` / `time` `timetz` | `date` / `time` | 16 / 15 |
| `char(n)` `varchar(n)` | `char`/`varchar` | n |
| `uuid` | `char(36)` | 36 |
| `bytea` | `varbinary` (0x hex) | sized to the longest value |
| `text`, `varchar`, `numeric` without a modifier, and every other type (json, arrays, interval …) | `varchar` (numeric right-aligned) | sized to the longest value, capped at 512 |

A result set with a column sized to its values is read whole before it prints; the others stream row by row. Not matched on PostgreSQL: a column without an alias is headed by PostgreSQL's own name (`?column?` prints as isql's blank heading, but `int4`, `repeat`, `now` … print); there is no `(return status = N)` and no `print` (use `RAISE NOTICE`); error texts are PostgreSQL's.

---

## Source File I/O — LF terminators required

Any committed CSS source file the compilers write — `css.*_msg`, `css.*_msgrp`, `css.options*`, `css.actions*`, `css.required_fields*`, `css.table_locations`, etc. — **must** be written with LF (`\n`) line endings, never CRLF. These files are shared across Windows, WSL, and Unix; mixing terminators churns diffs and breaks downstream tooling.

Use the helper, never raw `StreamWriter`:

```csharp
using var writer = ibs_compiler_common.OpenSourceWriter(path);   // overwrite
using var writer = ibs_compiler_common.OpenSourceWriter(path, append: true);
```

Default `new StreamWriter(path, …)` uses `Environment.NewLine` (CRLF on Windows) and is the wrong default for any file that gets committed. Reserve plain `StreamWriter` for transient files under `GetTempPath()` and for diagnostic output (trace dumps, logs).

**`File.Copy` is also banned for setup files** — it clones bytes verbatim, so copying a CRLF source births a CRLF file and reintroduces `^M`. Route copies through load+save (e.g. `InteractiveMenus.CopyOptionsFileLf` → `LoadOptionsFile` + `SaveOptionsFile`), never `File.Copy`.

The hard rule, restated: **the compilers must NEVER write `^M` (CRLF) into a committed setup file, on any OS.** Every write path — overwrite, append, copy — goes through `OpenSourceWriter`/load+save. This is the canonical, cross-repo team rule; the SQL-source side (file must be pure LF and pinned with `svn:eol-style=LF`) is documented in the handbook at `.docs/requirements/sql.md` § "css/setup source files are LF-only". Keep both in sync.

---

## Profile System

### settings.json

Connection profiles stored alongside executables:

```json
{
  "Profiles": {
    "PRODUCTION": {
      "ALIASES": ["PROD"],
      "COMPANY": 999,
      "DEFAULT_LANGUAGE": 1,
      "PLATFORM": "SYBASE",
      "HOST": "10.10.10.10",
      "PORT": 5000,
      "USERNAME": "user",
      "PASSWORD": "password",
      "SQL_SOURCE": "/path/to/current.sql"
    }
  }
}
```

A PostgreSQL profile sets `"PLATFORM": "POSTGRES"` and `"PORT": 5432` (the default when `--port` is omitted at create); an optional `"DATABASE"` key names the physical PG database to connect to (see PG platform model below) — when absent, the executor connects to `postgres`.

### Profile Resolution

1. Exact name match (case-insensitive)
2. Alias match (from `ALIASES` array)
3. Error if not found

### ProfileManager

- `FindSettingsFile()` — searches exe directory, then parent directories
- `LoadProfile(name)` — returns `ResolvedProfile` with all connection details
- Settings file is cached based on mtime

---

## Symbolic Links

### Purpose

Allow short paths in SQL source files:
- `/ss/ba/` instead of `/SQL_Sources/Basics/`

### Path Mappings

| Symbolic | Full Path |
|----------|-----------|
| `/ss/api/` | `/SQL_Sources/Application_Program_Interface/` |
| `/ss/ba/` | `/SQL_Sources/Basics/` |
| `/ss/bl/` | `/SQL_Sources/Billing/` |
| `/ss/ct/` | `/SQL_Sources/Create_Temp/` |
| `/ss/cv/` | `/SQL_Sources/Conversions/` |
| `/ss/dv/` | `/SQL_Sources/IBS_Development/` |
| `/ss/fe/` | `/SQL_Sources/Front_End/` |
| `/ss/in/` | `/SQL_Sources/Internal/` |
| `/ss/ma/` | `/SQL_Sources/Co_Monitoring/` |
| `/ss/mb/` | `/SQL_Sources/Mobile/` |
| `/ss/mo/` | `/SQL_Sources/Monitoring/` |
| `/ss/sdi/` | `/SQL_Sources/SDI_App/` |
| `/ss/si/` | `/SQL_Sources/System_Init/` |
| `/ss/sv/` | `/SQL_Sources/Service/` |
| `/ss/test/` | `/SQL_Sources/Test/` |
| `/ss/tm/` | `/SQL_Sources/Telemarketing/` |
| `/ss/ub/` | `/SQL_Sources/US_Basics/` |

See `Common.cs` for the built-in fallback list.

### Source of truth — `create_links.sh`

The authoritative shortcut set is the SQL tree's own `create_links.sh` (at the
SQL_SOURCE root), parsed by `ibs_compiler_common.ParseCreateLinks`. Each
`ln -s <target> <link>` line becomes one `(link, target)` pair. This is the same
script the Unix host runs, so the compilers create exactly the shortcuts that
branch expects — nothing hardcoded. `ShortcutDefinitionsFor(sqlSource)` returns
the parsed entries when the script is present, else the built-in
`SymlinkDefinitions` as a fallback.

- **Renamed trees** (`current.sql` post-r57389) already have the short-name
  directories on disk natively — every entry is a no-op, 0 created.
- **Legacy long-name trees** (`95.sql` and earlier) have only the long names, so
  `css/ss`, `css/sba`, … get created from the script.

### Creation

- Triggered (non-raw profiles only) by `set_profile` create/edit
  (`EnsureSymbolicLinks`) and the **Test → Symbolic links** check
  (`TestSymbolicLinks`, interactive option 6 and headless `--test --what symlinks`).
- **Create only when missing**: an entry is skipped if the short path / shortcut
  already exists (`SymlinkOrShortPathExists`) OR its target directory is absent
  (so absolute Unix targets and runtime-only links in `create_links.sh` skip
  harmlessly on a SQL checkout).
- On Windows: requires Administrator privileges or Developer Mode (otherwise
  reports per-entry "permission denied"; compiler still works via path expansion).
- On Unix/macOS: standard permissions, created directly.
- Checked once per session via `IBS_SYMLINKS_CHECKED` env var.

---

## Raw Mode

Skip all SBN-specific preprocessing for simple SQL execution.

In `settings.json`:
```json
{
  "RAW_MODE": true
}
```

Skips: options loading, placeholder resolution, symbolic links, changelog logging, sequence processing.

---

## Caching

### Options Cache

Location (`ibs_compiler_common.GetPath_ResolvedOptions`): the common system temp directory —
`%TEMP%/options.{platform}.{company}.{profile}.tmp`, or `options.{company}.{profile}.tmp`
when no `options.{platform}` source file exists. If `%TEMP%` contains a space the CLI falls
back to `<install-dir>/temp/`. It is deliberately NOT written inside the SQL working copy —
that would surface as untracked noise in every `svn status`.

> Because the file is out of sight, **every command that works with options or
> table_locations prints its full path**: `set_options`, `set_table_locations`,
> `compile_options`, `compile_table_locations`, and `set_profile --test --what
> options|table-locations` (which also prints the age). The other setup compiles print
> their bulk-copy temp files the same way.

- **TTL: 60 minutes** from file creation, then rebuilt on next use.
- Contains the whole option hierarchy merged into one flat `token→value` list:
  `options.{platform}` + `options.{company}` + `options.{company}.{profile}` +
  `table_locations` (layered last, so table placeholders resolve to `db..table`).
- Every compile path builds it with `forceRebuild: true`, so `compile_options` /
  `compile_table_locations` always re-merge from source.
- 3.1.4 briefly stored the cache in `{SQL_SOURCE}/css/setup/temp`; `--rebuild` deletes that
  orphan too, so an install that ran 3.1.4 leaves nothing behind in the source tree.
- Force a rebuild explicitly: `set_options <profile> --rebuild` or
  `set_table_locations <profile> --rebuild` (alias `--rebuild-cache`), or delete the file.
- `set_profile --test --what options|table-locations` prints the path and age, and
  accepts `--rebuild` (interactively it prompts to clear and rebuild).

### Symlink Check Cache

Environment variable `IBS_SYMLINKS_CHECKED` — prevents repeated creation during nested calls (e.g., `runcreate` calling `runsql` 100+ times).

### Changelog Status Cache

Environment variable `IBS_CHANGELOG_STATUS` — caches whether changelog logging is enabled on the target database.

---

## MSSQL Connection Initialization (SQLCMDINI)

Set `SQLCMDINI` env var to point to a SQL file that runs before every MSSQL command:

```sql
SET ARITHABORT               ON
SET ANSI_NULL_DFLT_ON        OFF
SET CONCAT_NULL_YIELDS_NULL  OFF
SET ANSI_WARNINGS            ON
SET QUOTED_IDENTIFIER        OFF
```

- Only applies to MSSQL (Sybase unaffected)
- Prepended as its own batch before user SQL
- If file missing: warning logged, execution continues

## PostgreSQL Connection Initialization (PGSQLINI)

Same mechanism, PG-side: set `PGSQLINI` env var to point to a SQL file that runs after every PG connect (including bulk-copy connects). If unset, `PostgresExecutor` looks for `PGSQLINI.sql` next to `settings.json` (falling back to the binaries directory). Missing file: no error, just skipped.

After the optional init script, the executor always runs `SET datestyle TO 'ISO, MDY'` on every connection — this is not conditional on PGSQLINI being present, it pins PG's date rendering to match the compiler's date-parsing assumptions.

`use <db>` inside a batch (and a `database` argument that names a schema, not the connection database) maps to `SET search_path TO <schema>, public` rather than a Sybase-style `USE` — see the PG platform model below.

---

## PostgreSQL Platform Model

SBN "databases" are PostgreSQL *schemas* inside one physical database — there is no per-database isolation the way MSSQL/Sybase have it. `PostgresExecutor.ResolveTarget` decides, per call, whether the `database` argument names the connection's own database (plain connect) or a schema (connect to the profile's admin database, then `SET search_path TO <schema>, public`). The same mapping applies to `use <db>` inside a batch — the only statement-level rewrite the executor performs.

For BCP, `db..table` follows suit: the `db` part becomes a schema-qualified target (`schema."table"`), not a separate database connection.

**User SQL is never translated.** PG source is authored natively — no `create proc` → `create function` rewriting, no automatic DDL translation. The compiler passes user-authored batches through untouched except for the `use`/search_path substitution and the diagnostic mapping below. Generated SQL emitted *by* the compiler itself (not user-authored) is the only place `db..table` → `schema.table` rewriting happens.

Diagnostic toggles (`set showplan on`, `set statistics io on`, `set noexec on`) have no PG session-level equivalent, so the executor tracks them as local state and prefixes subsequent DML with an `EXPLAIN` variant instead of forwarding the (invalid) Sybase syntax: `noexec` → `EXPLAIN`, `statistics` → `EXPLAIN (ANALYZE, BUFFERS)`, `showplan` → `EXPLAIN`. `noexec` wins if more than one is on.

Each `GO`-delimited chunk is split into individual statements (a PG-aware splitter that respects quotes, comments, and dollar-quoted function bodies) and executed one at a time, matching Sybase/MSSQL's per-statement autocommit — a chunk is not one implicit multi-statement transaction.

### refcursor auto-dereference (D6)

SBN procs ported to PG are authored as `returns setof refcursor` — one cursor per Sybase result set — so `select * from ibs.proc(...)` yields a result set of cursor *names* (`<unnamed portal 1>`…), and the non-holdable cursors die at end of the producing statement's transaction. Left as-is, `runsql`/`isqlline` would print the portal names and the caller would never see the data. Instead, when a streamed result set's columns are **all** the PG `refcursor` type (detected via the reader's column schema, `PostgresType.Name == "refcursor"`), `PostgresExecutor` collects the cursor names, then runs `FETCH ALL FROM "<name>"` for each **in order**, streaming those result sets through the normal formatter (header / rows / `(N rows affected)`) — mirroring Sybase isql's multi-resultset output. The cursor-name result itself is not printed. **Mixed columns** (refcursor + other) print the raw result unchanged; non-refcursor results are untouched.

Because a non-holdable cursor only lives to the end of the transaction that opened it, the producing statement and its FETCHes must share one transaction, so each statement is executed inside an explicit `BEGIN`/`COMMIT`. A single statement wrapped in `BEGIN`/`COMMIT` is autocommit-equivalent (same result, same durability, locks released at commit), so this is invisible to the non-refcursor path and preserves the per-statement autocommit parity above. The wrapper is unconditional only because a statement can't be known to be refcursor-typed until after it executes.

---

## Command Architecture

All commands follow the same pattern: thin `Program.cs` wrapper → shared library logic.

```csharp
// runsql/Program.cs
if (!VersionCheck.CheckForUpdates("runsql", args)) return 0;
var profile = ProfileManager.LoadProfile(args);
var executor = SqlExecutorFactory.Create(profile);
Runsql.Run(executor, profile, args);
```

### Subcommands (all commands)

| Subcommand | Action |
|------------|--------|
| `version` | Print version and exit |
| `update` | Download latest from GitHub Releases |
| `configure` | Show configuration status, add to PATH |

### Full-screen console widgets — caret rule

Hide the caret before any multi-row redraw; show it last, after positioning it on the input line (`ConsoleMenu.HideCaret` / `ConsoleMenu.ShowCaretAt`). A visible caret is seen jumping across the screen on every keystroke. Applies to `ProfilePicker`, `ProfileEditor`, `MessageForm`, `MessageBrowser`, `InteractiveCheckbox` and `ConsoleMenu.DrawChoiceBuffer`.

---

## Build & Distribution

### Version

Version is stored in `src/Directory.Build.props` (single source of truth). All projects inherit the same version.

### Publishing

**Always use `--self-contained` when publishing.** Target machines do not have .NET installed system-wide — the runtime is bundled in the install directory.

```powershell
.\release.ps1 -Version X.Y.Z -Notes "..."
```

Produces:
- `bin/compilers-net8-win-x64.zip`
- `bin/compilers-net8-linux-x64.tar.gz`
- `bin/compilers-net8-osx-x64.tar.gz`

`settings.json` is excluded from all archives so `runsql update` never overwrites a user's credentials.

`src/sql-test/` and `src/bcp_data/` are Sybase-only internal utilities — not part of the cross-platform command set, not PG-ported.

### Version Checking

Every command calls `VersionCheck.CheckForUpdates()` at startup:
1. Handles subcommands: `version`, `update`, `configure`
2. Once per day, checks GitHub Releases API for a newer version
3. If newer: prompts user interactively to update now (y/N)
4. State file: `%LOCALAPPDATA%\ibs-compilers\version_state.json` (Windows) or `~/ibs-compilers/version_state.json` (Linux/macOS)

Operational guarantees (`DailyCheck()`):
- **First-use-per-day cadence.** The check fires on the first compiler invocation of each **UTC** calendar day, gated by `last_check_date`. The state file is **shared across the whole suite** (one file, not per-binary), so it's once/day total — running `runsql` then `set_options` the same day checks only once.
- **Non-blocking.** 5-second GitHub timeout; any network error or non-2xx is swallowed and the real command proceeds. The check never fails a command.
- **No re-nag.** Today's date is stamped **before** the prompt, so declining (`N`) does not prompt again until the next UTC day. Forcing it any time: `<cmd> update`.
- **Headless-safe.** The prompt uses `Console.ReadLine()`; under a closed/redirected stdin it returns null → treated as `N` (prints "run update later"), so automation never hangs.
- **A git push is not enough** — the check reads GitHub *Releases*, not commits/tags. A new version only reaches machines once `release.ps1` has cut the release with platform assets attached (see `.docs/workflows/compilers-release.md`).
