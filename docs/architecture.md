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

### Result-Set Display Widths

Column widths in streamed result-sets (`isqlline`, `runsql`, anything that hits `StreamOneResultSetAsync`) are computed from three inputs, taking the max:

1. `SchemaTable.ColumnSize` (capped at `MaxColumnWidth = 256`)
2. The column header text length
3. **`MinDisplayWidthForType(t)`** — a per-.NET-type display floor

Input #3 exists because `SchemaTable.ColumnSize` reports the **byte width** for fixed-length types — `datetime`=8, `int`=4, `bigint`=8, `bit`=1, `uniqueidentifier`=16. Without a type-aware floor, those byte counts collapse the column to ~10 chars (the hardcoded baseline) and `Substring(0, width)` in the row loop chops the rendered string. The original symptom: `dateadd(ss, min(int_col), "800101")` rendered as `MM/dd/yyyy` with the time silently dropped, while a non-aggregate `dateadd(ss, literal, ...)` in the same SELECT kept its time because that column's name/text happened to push the width higher.

Floors (kept symmetric in `SybaseExecutor.cs`, `MssqlExecutor.cs`, and `PostgresExecutor.cs`):

| .NET type | Floor | Rationale |
|---|---|---|
| `DateTime`, `DateTimeOffset` | 26 | `MM/dd/yyyy h:mm:ss tt` and Sybase isql's `Mon DD YYYY HH:MM:SS.fffAM` |
| `Guid` | 36 | `xxxxxxxx-xxxx-…` canonical form |
| `long`, `decimal`, `double` | 24 | room for `-9223372036854775808` and scaled decimal |
| `int` | 11 | `-2147483648` |
| `short` | 6 | `-32768` |
| `byte` | 3 | `255` |
| `float` | 14 | scientific notation |
| `TimeSpan` | 16 | `-d.hh:mm:ss.fffffff` |
| `bool` | 5 | `False` |
| anything else (e.g. `string`, `byte[]`) | 0 | fall back to `ColumnSize`, which IS the printed width for these |

When extending: if the AseClient or SqlClient starts returning a new fixed-length .NET type, add its row here — don't reach for `MaxColumnWidth` (that's the *cap*, not a default).

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

Location: `{SQL_SOURCE}/CSS/Setup/temp/{profile}.options.tmp`
- TTL: 24 hours
- Contains merged placeholders from option hierarchy
- Force rebuild by deleting the cache file

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
