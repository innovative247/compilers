# IBS Compilers: C# to Python Migration Roadmap

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

### Symbolic Path Resolution

The compilers support shorthand directory paths that map to full directory names.
This is implemented in `convert_non_linked_paths()` (ibs_common.py) and mirrors
the C# `NonLinkedFilename()` function in common.cs.

**Mapping Table:**

| Symbolic | Real Path |
|----------|-----------|
| `/ss/ba/` | `/SQL_Sources/Basics/` |
| `/ss/bl/` | `/SQL_Sources/Billing/` |
| `/ss/mo/` | `/SQL_Sources/Monitoring/` |
| `/ss/sv/` | `/SQL_Sources/Service/` |
| `/ss/fe/` | `/SQL_Sources/Front_End/` |
| `/ss/ma/` | `/SQL_Sources/Co_Monitoring/` |
| `/ss/api/` | `/SQL_Sources/Application_Program_Interface/` |
| `/ss/api3/` | `/SQL_Sources/Application_Program_Interface_V3/` |
| `/ss/ub/` | `/SQL_Sources/US_Basics/` |
| `/ss/si/` | `/SQL_Sources/System_Init/` |
| `/ss/tm/` | `/SQL_Sources/Telemarketing/` |
| `/ss/mb/` | `/SQL_Sources/Mobile/` |
| `/ibs/ss/` | `/IBS/SQL_Sources/` |

See `ibs_common.py` for the complete list of mappings.

**Cross-Platform Implementation:**
- Normalizes all paths to forward slashes internally for matching
- Converts to OS-appropriate separator (`os.sep`) on output
- Works identically on Windows, macOS, and Linux

**Usage:**
- Called automatically by `find_file()` before searching for files
- Used by: `runsql.py`, `runcreate.py`, `i_run_upgrade.py`
- Not needed by: `isqlline.py` (takes inline SQL, not file paths)

**Example:**
```
Input:  css\ss\ba\pro_users.sql   (Windows)
Input:  css/ss/ba/pro_users.sql   (Unix)
Output: css/SQL_Sources/Basics/pro_users.sql  (normalized)
```

---

## Key File Locations

### Python Implementation (This Project)

| File | Purpose |
|------|---------|
| `src/commands/ibs_common.py` | Shared library (ALL reusable logic) |
| `src/commands/set_profile.py` | Profile wizard with connection/options testing |
| `src/commands/isqlline.py` | Execute single SQL command |
| `src/commands/runsql.py` | Execute SQL scripts with sequences |
| `src/commands/change_log.py` | Audit trail module |
| `src/commands/bcp_data.py` | Bulk copy data in/out |
| `src/commands/tail.py` | Tail log files |
| `src/commands/eloc.py` | Edit and compile table locations |
| `src/commands/eact.py` | Edit and compile actions |
| `src/commands/eopt.py` | Edit and compile options |
| `src/commands/compile_msg.py` | Compile messages |
| `src/commands/compile_required_fields.py` | Compile required fields |
| `src/commands/i_run_upgrade.py` | Run upgrade scripts |
| `src/commands/runcreate.py` | Orchestrate master build scripts |
| `src/commands/settings.json` | All connection and compile settings |
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

Tasks are ordered by implementation priority.

### Phase 1: Windows Installation
| Status | Task |
|--------|------|
| Done | `install/bootstrap.ps1` - Windows setup script |
| Done | `install/installer.py` - Windows installer |
| Done | FreeTDS (tsql, freebcp) installation via MSYS2 |

### Phase 2: set_profile
| Status | Task |
|--------|------|
| Done | Profile management in `ibs_common.py` (`load_profile`, `save_profile`, `list_profiles`) |
| Done | `set_profile.py` - Interactive profile wizard |
| Done | Test connection from within set_profile |
| Done | Test options from within set_profile |

### Phase 3: isqlline
| Status | Task |
|--------|------|
| Done | `execute_sql_native()` in `ibs_common.py` using tsql |
| Done | `isqlline.py` - Single SQL command execution |
| Done | Options class placeholder resolution |
| Done | `-e` echo, `-O` output file flags |

### Phase 4: runsql
| Status | Task |
|--------|------|
| Done | `runsql.py` - SQL script execution with sequences |
| Done | Options class placeholder resolution |
| Done | `-F`/`-L` sequence flags |
| Done | `-e` echo, `-O` output file flags |

### Phase 5: change_log
| Status | Task |
|--------|------|
| Pending | `change_log.py` module |
| Pending | Audit trail to `ba_gen_chg_log` table |
| Pending | `--changelog` flag in runsql |
| Pending | `--changelog` flag in isqlline |

### Phase 6: bcp_data
| Status | Task |
|--------|------|
| Done | `execute_bcp()` in `ibs_common.py` using freebcp |
| Done | `bcp_data.py` - Bulk copy data in/out |
| Pending | `--drop-indexes` flag |
| Pending | `--drop-triggers` flag |
| Pending | CRLF/LF normalization (`BuildTempFileForBcp`) |

### Phase 7: tail
| Status | Task |
|--------|------|
| Done | `tail.py` - Tail log files |

### Phase 8: table_locations / eloc
| Status | Task |
|--------|------|
| Done | `eloc.py` - Edit table locations |
| Pending | Compile table locations into database |

### Phase 9: actions / eact
| Status | Task |
|--------|------|
| Done | `eact.py` - Edit actions |
| Pending | `compile_actions()` - Fixed-width parsing |

### Phase 10: options / eopt
| Status | Task |
|--------|------|
| Done | Options class in `ibs_common.py` |
| Done | `_parse_v_option()`, `_parse_c_option()`, `_parse_table_option()` |
| Done | `generate_option_files()` with caching (24-hour expiry) |
| Done | `replace_options()` with @sequence@ support |
| Done | `eopt.py` - Edit options |
| Pending | `GenerateImportOptionFile()` - `:>` format conversion |

### Phase 11: messages / compile_msg
| Status | Task |
|--------|------|
| Done | `compile_msg.py` - Compile messages |
| Pending | CRLF/LF normalization |

### Phase 12: required_fields / compile_required_fields
| Status | Task |
|--------|------|
| Done | `compile_required_fields.py` - Basic structure |
| Pending | Full implementation |

### Phase 13: upgrade / i_run_upgrade
| Status | Task |
|--------|------|
| Done | `i_run_upgrade.py` - Run upgrade scripts |
| Pending | `GetSeq()` extraction |
| Pending | `--changelog` flag |

### Phase 14: runcreate
- runcreate is an orchestrator that reads create scripts and calls runsql/others
- Sample create script: `C:\_innovative\_source\current.sql\CSS\SQL_Sources\Basics\create_test`
- C# reference: `C:\_innovative\_source\sbn-services\Ibs.Compilers\ibsCompilerCommon\runcreate.cs`

| Status | Task |
|--------|------|
| Done | `runcreate.py` - Basic orchestrator |
| Pending | Line parser (extract command type, clean line) |
| Pending | `$ir>path` conversion to `{SQL_SOURCE}\path` |
| Pending | Leading `&option&` resolution (conditional lines) |
| Pending | `-F`/`-L` sequence flag extraction |
| Pending | `#NT` Windows-specific line handling |
| Pending | Multiple `-D` database handling |
| Pending | Recursive runcreate calls |
| Pending | Output file append mode |

---

## Completed Features

- Windows installer (bootstrap.ps1, installer.py)
- Profile management with testing (set_profile.py)
- Single SQL command execution (isqlline.py) via tsql
- SQL script execution (runsql.py) via tsql with sequence support
- Soft-compiler Options class (v:, c:, -> placeholders) with 24-hour caching
- Non-linked path conversion (convert_non_linked_paths)
- Database connectivity (Sybase ASE and MSSQL via FreeTDS tsql)

---

## Unit Testing

### Test Framework

The project uses **pytest** for unit testing. Tests are located in `src/tests/`.

**Install dev dependencies:**
```bash
cd src
pip install -e ".[dev]"
```

**Run tests:**
```bash
pytest                      # Run all tests
pytest -v                   # Verbose output
pytest --cov=commands       # With coverage report
pytest tests/test_options_parsing.py  # Run specific file
```

### Test Files

| File | Tests | Coverage |
|------|-------|----------|
| `conftest.py` | - | Shared fixtures (temp_dir, sample_config, etc.) |
| `test_convert_non_linked_paths.py` | ~25 | All 23 path mappings, cross-platform, case-insensitivity |
| `test_options_parsing.py` | ~35 | `_parse_v_option`, `_parse_c_option`, `_parse_table_option`, `replace_options` |
| `test_profile_management.py` | ~15 | `load_profile`, `save_profile`, `list_profiles` |
| `test_find_file.py` | ~20 | Current dir, PATH_APPEND, auto .sql extension, symbolic paths |

### Test Categories

**Pure Unit Tests** (no external dependencies):
- `convert_non_linked_paths()` - Path normalization
- `Options._parse_v_option()` - Parse v: lines
- `Options._parse_c_option()` - Parse c: lines
- `Options._parse_table_option()` - Parse -> lines
- `Options.replace_options()` - Placeholder substitution

**Unit Tests with Temp Files:**
- `find_file()` - File searching
- `load_profile()` / `save_profile()` - Profile management

**Integration Tests** (require database - not yet implemented):
- `execute_sql_native()` - SQL execution
- `test_connection()` - Connection testing

### Adding New Tests

When completing a feature, add corresponding tests:

1. Create test file in `src/tests/test_<feature>.py`
2. Use fixtures from `conftest.py` for common setup
3. Follow naming: `class Test<Feature>:` and `def test_<scenario>():`
4. Run tests before committing: `pytest -v`

---

## Known Limitations (Acceptable)

- Uses Python logging module instead of C# approach
- Editor detection uses $EDITOR, code, notepad, or vim
- Assumes tsql/freebcp in PATH (MSYS2 on Windows)
