# IBS Compilers: C# to Python Migration Roadmap

**Last Updated**: 2025-11-29

## Current Status

**Installation working.** Python 3.11.9 installed via winget, all commands available.

**Completed (2025-11-29):**
- [x] Fixed syntax error in `ibs_common.py:828` (nested quotes in f-string)
- [x] Replaced deprecated `IR` setting with `PATH_APPEND` throughout codebase
- [x] `test_options` - Options class placeholder resolution working
- [x] `isqlline` - Single SQL command execution with Options resolution, `-e` echo, `-O` output file
- [x] `runsql` - SQL script execution with Options resolution, `-e` echo, `-O` output file, sequence support
- [x] Added `create_symbolic_links()` function to `ibs_common.py`
- [x] Improved stderr filtering for Sybase informational messages
- [x] Documented runcreate architecture and line processing

**Next session: Implement runcreate.py**
- runcreate is an orchestrator that reads create scripts and calls runsql/others
- See Phase 3 section below for detailed implementation plan
- Sample create script: `C:\_innovative\_source\current.sql\CSS\SQL_Sources\Basics\create_test`
- C# reference: `C:\_innovative\_source\sbn-services\Ibs.Compilers\ibsCompilerCommon\runcreate.cs`

## Overview

Python replacement for the C# IBS Compilers (`Ibs.Compilers`). Tools compile and deploy SQL objects to Sybase ASE and MSSQL databases.

**Current Focus: Windows only.** macOS and Linux support will be added after Windows is complete and tested.

---

## Architecture Principles

### Single Source of Truth

**`ibs_common.py`** is the shared library containing ALL reusable logic:
- SQL execution via tsql (`execute_sql_native()`)
- Connection testing via tsql (`test_connection()`)
- Database connections via pyodbc (`get_db_connection()`)
- BCP operations via freebcp (`execute_bcp()`)
- Profile management (`load_profile()`, `save_profile()`, `list_profiles()`)
- File utilities (`find_file()`, `convert_non_linked_paths()`)
- Configuration management (`get_config()`, `replace_placeholders()`)
- UI utilities (`console_yes_no()`, `launch_editor()`)

**Commands in `src/commands/`** are thin wrappers that:
- Parse command-line arguments
- Call functions from `ibs_common.py`
- Handle user interaction specific to that command

### Why This Matters

If connection logic exists in multiple places, it becomes a maintenance nightmare. **ALL database/SQL execution logic MUST be in `ibs_common.py`.**

### SQL Execution Architecture

**Single execution path:** All SQL execution flows through `execute_sql_native()`:

```
isqlline.py  ──┐
               ├──► ibs_common.execute_sql_native() ──► tsql ──► Database
runsql.py   ──┘
```

**FreeTDS tsql** is used for both Sybase ASE and MSSQL:
- `-H {host} -p {port}` for direct connection (no server aliases)
- `-o q` for quiet mode (suppress prompts)
- `-v` for verbose mode (echo input, maps to `-e` flag in commands)
- `-D {database}` for database selection

### Output File Behavior (`-O` flag)

| Command | Mode | Description |
|---------|------|-------------|
| `isqlline` | Overwrite | `-O` always creates/overwrites the output file |
| `runsql` | Overwrite | `-O` always creates/overwrites the output file |
| `runcreate` | Overwrite then Append | `-O` creates/overwrites file at start, then appends as it orchestrates multiple runsql calls |

When `-e` (echo) is combined with `-O`:
- Echo output goes to file only (no console output)
- Resolved SQL is written before query results

### Connection Architecture

**settings.json** stores connection profiles with:
- `HOST` - Server IP/hostname
- `PORT` - Server port
- `USERNAME` - Database user
- `PASSWORD` - Database password
- `PLATFORM` - SYBASE or MSSQL

**Direct connections only** - No freetds.conf server aliases:
- tsql: `-H {host} -p {port}` (not `-S servername`)
- freebcp: `-H {host} -p {port}` (not `-S servername`)
- pyodbc: `SERVER={host},{port}` or FreeTDS ODBC driver

### Command-Line Flag Standards

All commands that execute SQL should support these flags:

| Flag | Description |
|------|-------------|
| `-S` | Profile/server name (takes precedence over positional) |
| `-H` | Database host (direct connection) |
| `-p` | Database port |
| `-U` | Username |
| `-P` | Password |
| `-D` | Database name override |
| `-O` | Output file |
| `-e` | Echo input (maps to tsql `-v`) |
| `--platform` | SYBASE or MSSQL |
| `--debug` | Enable debug output |
| `--test-connection` | Test connection before executing |
| `--changelog` | Enable audit trail logging |

---

## Key File Locations

### Python Implementation (This Project)

| File | Purpose |
|------|---------|
| `src/commands/ibs_common.py` | Shared library (ALL reusable logic) |
| `src/commands/isqlline.py` | Execute single SQL command |
| `src/commands/runsql.py` | Execute SQL scripts with sequences |
| `src/commands/runcreate.py` | Orchestrate master build scripts |
| `src/commands/eopt.py` | Edit and compile options |
| `src/commands/eloc.py` | Edit and compile table locations |
| `src/commands/eact.py` | Edit and compile actions |
| `src/commands/bcp_data.py` | Bulk copy data in/out |
| `src/commands/i_run_upgrade.py` | Run upgrade scripts |
| `src/commands/compile_msg.py` | Compile messages |
| `src/commands/compile_required_fields.py` | Compile required fields |
| `src/commands/test_connection.py` | Test database connectivity |
| `src/commands/test_options.py` | Test Options class placeholder resolution |
| `src/commands/change_log.py` | Audit trail module |
| `src/commands/setup_profile.py` | Profile wizard |
| `src/settings.json` | All connection and compile settings |
| `install/bootstrap.ps1` | Windows setup script |
| `install/installer.py` | Windows installer |

### C# Reference Implementation

Location: `C:\_innovative\_source\sbn-services\Ibs.Compilers`

| File | Purpose |
|------|---------|
| `ibsCompilerCommon/options.cs` | **Primary reference** - Options class with v:/c: parsing |
| `ibsCompilerCommon/common.cs` | Core utilities, path mappings, file handling |
| `ibsCompilerCommon/isqlline.cs` | Single SQL command execution |

---

## Task List

Tasks are ordered by priority. Later tasks depend on earlier ones.

### Phase 0: Installation & Connectivity (Windows)

| # | Task | Status |
|---|------|--------|
| 0.1 | Complete `install/bootstrap.ps1` for Windows | Completed |
| 0.2 | Complete `install/installer.py` for Windows | Completed |
| 0.3 | Complete `test_connection.py` CLI interface | Completed |
| 0.4 | Consolidate connection logic in `ibs_common.py` | Completed |
| 0.5 | Update `test_connection.py` to use `ibs_common.py` | Completed |
| 0.6 | Complete `isqlline.py` for single SQL command execution | Completed |
| 0.7 | Complete `runsql.py` using shared `execute_sql_native()` | Completed |
| 0.8 | Verify all commands use `ibs_common.py` for connections | Completed |

### Phase 1: Core Infrastructure

| # | Task | Status |
|---|------|--------|
| 1.1 | Add `execute_sql_native()` to `ibs_common.py` using tsql | Completed |
| 1.2 | Add `test_connection()` function to `ibs_common.py` using tsql | Completed |
| 1.3 | Add `load_profile()` / `save_profile()` to `ibs_common.py` | Completed |
| 1.4 | Implement `convert_non_linked_paths()` - symbolic path conversion | Completed |
| 1.5 | Update `execute_bcp()` to use `-H host -p port` | Completed |
| 1.6 | Add wildcard matching to `find_file()` | Pending |
| 1.7 | Implement `BuildTempFileForBcp()` for CRLF/LF normalization | Pending |

### Phase 2: Soft-Compiler Options Class

The Options class is the heart of the soft-compiler system. It performs multi-layered option file merging and placeholder resolution.
A single resolved options file is a compilation of multiple files, all found in <root>\CSS\Setup

**Option File Hierarchy** (in precedence order):
1. `options.{def}` (all available options with defaults) If this file does not exist prompt user then exit all processing.
2. `options.{Company}` (e.g., options.101) This overrides above options. If this file does not exist prompt user then exit all processing.
3. `options.{Company}.{Server}` (e.g., options.101.GONZO) This overrides above options. It is okay if this file does not exist. Do not prompt user.
4. `table_locations` This is amended to the above, and uses the resolution of above to generate the correct table location. If this file does not exist prompt user then exit all processing.


**Placeholder Types**:
- `v:` (Value): `v: dbsta <<sbnstatic>>` → `&dbsta&` resolves to `sbnstatic`
- `c:` (Conditional): `c: mssql -` → generates `&if_mssql&`, `&endif_mssql&`, `&ifn_mssql&`, `&endifn_mssql&`
- `->` (Table): `-> users &dbtbl&` → `&users&` resolves to `sbnmaster..users`, `&db-users&` resolves to `sbnmaster`

The resolved file is stored on disk in a common location as options.{Company}.{Server} in a format that is both user-friendly and provides a quick lookup for the compiler. This file can be used hundreds of times for a single command. The file on disk should be automatically treated as 'stale' after 24 hours, forcing the compiler to rebuild the file.
The file is automatically rebuilt after 24 hours or if an input forceRebuild boolean (defaults to false) is passed in as true.


| # | Task | Status |
|---|------|--------|
| 2.1 | Create `Options` class in `ibs_common.py` | Completed |
| 2.2 | Implement `_parse_v_option()` - parse v: lines | Completed |
| 2.3 | Implement `_parse_c_option()` - parse c: lines, generate 4 conditional placeholders | Completed |
| 2.4 | Implement `generate_option_files()` - merge with Server > Company > def precedence | Completed |
| 2.5 | Implement `_parse_table_option()` - parse table_locations, generate &table& and &db-table& | Completed |
| 2.6 | Implement `replace_options()` with @sequence@ support | Completed |
| 2.7 | Add caching mechanism (temp file, 24-hour expiry) | Completed |
| 2.8 | Create `test_options.py` CLI for testing placeholder resolution | Completed |

### Phase 3: Command Integration - runcreate

#### What is runcreate?

**runcreate is an orchestrator** - it does NOT execute SQL directly. Instead, it reads a "create script" file containing a list of commands and executes them in sequence by calling other tools (runsql, i_run_upgrade, etc.).

**Key difference from runsql/isqlline:**
- `isqlline` - executes a single SQL command (inline)
- `runsql` - executes a single SQL file
- `runcreate` - reads a script of commands and calls runsql/others for each line

#### Sample Create Script (`create_test`)

```
# Create tables - test
#
# CHG 151215 FRANK    07.84.24462    tbl_import_wo_installer
# CHG 160127 JAKE     07.84.24565    tbl_communication_log_arc

runsql $1 $ir>css>ss>ba>tbl_ba_agent_activity &dbtbl& &sv&
runsql $1 $ir>css>ss>ba>tbl_vru_intest &dbtbl& &sv&
runsql $1 $ir>css>ss>ba>tbl_ba_vip_prio_type &dbtbl& &sv&
```

#### Line Processing Steps

| Step | Description | Example |
|------|-------------|---------|
| 1 | Skip comment lines (`#`) | `# CHG 151215...` → skipped |
| 2 | Handle `#NT` prefix (Windows-specific) | `#NT runsql...` → `runsql...` |
| 3 | Resolve leading `&option&` (conditional) | `&if_mssql& runsql...` → may blank line |
| 4 | Extract command type | `runsql $1 $ir>...` → type=`runsql` |
| 5 | Remove `$1`, `-o`, tabs | Cleanup legacy placeholders |
| 6 | Extract `-F`/`-L` sequence flags | `-F1 -L5` → SeqFirst=1, SeqLast=5 |
| 7 | Convert `$ir>path>to>file` | `$ir>css>ss>ba>file` → `{PATH_APPEND}\css\ss\ba\file` |
| 8 | Remove `&sv&` (server variable) | Not needed, already have profile |
| 9 | Resolve `-D` databases via Options | `-D &dbtbl&` → `-D sbnmaster` |

#### Supported Command Types

| Command | Action |
|---------|--------|
| `runsql` | Execute SQL file (can have multiple `-D` databases per line) |
| `runcreate` | Recursive call for nested create scripts |
| `i_run_upgrade` | Run upgrade scripts |
| `import_options` | Compile options into database |
| `create_tbl_locations` | Compile table locations |
| `install_msg` | Compile messages |
| `compile_actions` | Compile actions |
| `install_required_fields` | Compile required fields |

#### Output File Behavior

- `-O` flag: **Overwrites** file at start, then **appends** as each command runs
- Each runsql call appends its output to the same file

#### C# Reference

See: `C:\_innovative\_source\sbn-services\Ibs.Compilers\ibsCompilerCommon\runcreate.cs`

#### Implementation Tasks

| # | Task | Status |
|---|------|--------|
| 3.1 | Update runsql.py to use `Options.replace_options()` | Completed |
| 3.1b | Update isqlline.py to use `Options.replace_options()` | Completed |
| 3.2 | Implement runcreate line parser (extract command type, clean line) | Pending |
| 3.3 | Implement `$ir>path` conversion to `{PATH_APPEND}\path` | Pending |
| 3.4 | Implement leading `&option&` resolution (conditional lines) | Pending |
| 3.5 | Implement `-F`/`-L` sequence flag extraction | Pending |
| 3.6 | Implement `#NT` Windows-specific line handling | Pending |
| 3.7 | Implement multiple `-D` database handling for runsql calls | Pending |
| 3.8 | Implement recursive runcreate calls | Pending |
| 3.9 | Implement output file append mode | Pending |
| 3.10 | Update i_run_upgrade.py - implement GetSeq() extraction | Pending |
| 3.11 | Update eloc.py to use `Options.ReplaceWord()` | Pending |

### Phase 4: Change Logging

| # | Task | Status |
|---|------|--------|
| 4.1 | Create `change_log.py` module | Completed |
| 4.2 | Implement audit trail to ba_gen_chg_log table | Completed |
| 4.3 | Add `--changelog` flag to runsql | Completed |
| 4.4 | Add `--changelog` flag to isqlline | Completed |
| 4.5 | Add `--changelog` flag to i_run_upgrade | Pending |

### Phase 5: Missing Modules

| # | Task | Status |
|---|------|--------|
| 5.1 | Create compile_required_fields.py (full module) | Pending |
| 5.2 | Implement compile_actions() in eact.py (fixed-width parsing) | Pending |
| 5.3 | Implement GenerateImportOptionFile() in eopt.py (`:>` format conversion) | Pending |
| 5.4 | Implement --drop-indexes in bcp_data.py | Pending |
| 5.5 | Implement --drop-triggers in bcp_data.py | Pending |
| 5.6 | Implement CRLF/LF normalization in compile_msg.py | Pending |

### Phase 6: Polish

| # | Task | Status |
|---|------|--------|
| 6.1 | Add `-e` echo flag to isqlline and runsql | Completed |
| 6.2 | Add `-O` output file flag to isqlline and runsql | Completed |
| 6.3 | Add `-S` profile flag to isqlline and runsql | Completed |
| 6.4 | Add `--test-connection` flag to isqlline and runsql | Completed |
| 6.5 | Show help when commands called with no arguments | Completed |
| 6.6 | Add BCP format file support (-f flag) | Pending |

---

## Completed Features

- Windows installer (bootstrap.ps1, installer.py)
- Connection testing CLI (test_connection.py)
- Single SQL command execution (isqlline.py) via tsql
- SQL script execution (runsql.py) via tsql with sequence support
- Master script orchestration (runcreate with simple commands)
- Interactive editors (eopt, eloc, eact)
- Database connectivity (Sybase ASE and MSSQL via FreeTDS tsql)
- BCP operations (basic character mode via freebcp)
- Configuration management (profiles in settings.json)
- Upgrade script execution (i_run_upgrade)
- Message compilation (compile_msg)
- Change logging / audit trail (change_log.py)
- Non-linked path conversion (convert_non_linked_paths)
- Profile management (setup_profile.py)
- Tail command for log files
- Soft-compiler Options class (v:, c:, -> placeholders) with 24-hour caching
- test_options.py CLI for testing placeholder resolution

---

## Known Limitations (Acceptable)

- Uses Python logging module instead of C# approach
- Editor detection uses $EDITOR, code, notepad, or vim
- Assumes tsql/freebcp in PATH (MSYS2 on Windows)
