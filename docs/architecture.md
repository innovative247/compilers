# Compilers Architecture

## Database Connectivity

### ADO.NET Providers

| Platform | Provider | Package |
|----------|----------|---------|
| MSSQL | `Microsoft.Data.SqlClient` | NuGet package |
| Sybase ASE | `AdoNetCore.AseClient` | Local DLL (`src/AdoNetCore.AseClient.dll`) |

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
var executor = SqlExecutorFactory.Create(profile);  // MssqlExecutor or SybaseExecutor
```

### BCP Implementation

| Platform | BulkCopyOut | BulkCopyIn |
|----------|-------------|------------|
| MSSQL | `SqlBulkCopy` + DataReader | `SqlBulkCopy` from tab-delimited file |
| Sybase | SELECT → tab-delimited file | Row-by-row INSERT |

---

## Source File I/O — LF terminators required

Any committed CSS source file the compilers write — `css.*_msg`, `css.*_msgrp`, `css.options*`, `css.actions*`, `css.required_fields*`, `css.table_locations`, etc. — **must** be written with LF (`\n`) line endings, never CRLF. These files are shared across Windows, WSL, and Unix; mixing terminators churns diffs and breaks downstream tooling.

Use the helper, never raw `StreamWriter`:

```csharp
using var writer = ibs_compiler_common.OpenSourceWriter(path);   // overwrite
using var writer = ibs_compiler_common.OpenSourceWriter(path, append: true);
```

Default `new StreamWriter(path, …)` uses `Environment.NewLine` (CRLF on Windows) and is the wrong default for any file that gets committed. Reserve plain `StreamWriter` for transient files under `GetTempPath()` and for diagnostic output (trace dumps, logs).

---

## Profile System

### settings.json

Connection profiles stored alongside executables:

```json
{
  "Profiles": {
    "GONZO": {
      "ALIASES": ["G"],
      "COMPANY": 101,
      "DEFAULT_LANGUAGE": 1,
      "PLATFORM": "SYBASE",
      "HOST": "10.10.123.4",
      "PORT": 5000,
      "USERNAME": "sbn0",
      "PASSWORD": "password",
      "SQL_SOURCE": "/path/to/current.sql"
    }
  }
}
```

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

See `Common.cs` for the full list.

### Creation

- On Windows: Requires Administrator privileges (falls back to path expansion)
- On Unix: Standard permissions
- Checked once per session via `IBS_SYMLINKS_CHECKED` env var

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

### Version Checking

Every command calls `VersionCheck.CheckForUpdates()` at startup:
1. Handles subcommands: `version`, `update`, `configure`
2. Once per day, checks GitHub Releases API for a newer version
3. If newer: prompts user interactively to update now (y/N)
4. State file: `%LOCALAPPDATA%\ibs-compilers\version_state.json` (Windows) or `~/ibs-compilers/version_state.json` (Linux/macOS)
