# IBS Compilers: C# to Python Migration Roadmap

## Overview

Python replacement for the C# IBS Compilers (`Ibs.Compilers`). Tools compile and deploy SQL objects to Sybase ASE and MSSQL databases.

**Current Focus: Windows complete, now testing Ubuntu/Linux.**

---

## Phase 15: Ubuntu/Linux Testing

### Status: In Progress

Windows implementation is complete. Now testing on Ubuntu to validate cross-platform compatibility.

### Known Areas Requiring Attention

| Area | Windows | Ubuntu | Notes |
|------|---------|--------|-------|
| FreeTDS installation | MSYS2 | `apt install freetds-bin freetds-dev` | Different package manager |
| Python installation | Python.org installer | `apt install python3 python3-pip` | System package |
| Path separators | `\` | `/` | Code uses `os.sep` - should work |
| Symbolic links | Requires Admin | Native support | `create_symbolic_links()` in ibs_common.py |
| settings.json location | `src/commands/` | Same | Verify path resolution |
| Line endings | CRLF | LF | SQL files, options files |
| Editor detection | notepad, code | vim, nano, code | `launch_editor()` in ibs_common.py |

### Ubuntu Installation Steps (To Be Tested)

```bash
# Install FreeTDS
sudo apt update
sudo apt install freetds-bin freetds-dev

# Verify tsql is available
which tsql
tsql -C

# Install Python package
cd /path/to/compilers/src
pip install -e .

# Test commands are available
set_profile --help
isqlline --help
runsql --help
runcreate --help
```

### Potential Issues to Watch

1. **Symbolic links** - Windows requires Administrator; Linux should work natively
2. **Case sensitivity** - Linux filesystem is case-sensitive; Windows is not
3. **Path handling** - `os.path.isabs()` behavior differs slightly
4. **Temp file locations** - `/tmp` vs `%TEMP%`
5. **ODBC drivers** - pyodbc may need unixODBC and FreeTDS ODBC driver
6. **Line ending handling** - Options files and SQL scripts may have CRLF from Windows

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

### Options System (Soft-Compiler)

The options system resolves `&placeholder&` values in SQL files. There are four types:

| Prefix | Type | Resolution | Example |
|--------|------|------------|---------|
| `v:` | Static value | Compiled into SQL | `v:af <<2>>` → `&af&` = `2` |
| `V:` | Dynamic value | Queried from `&options&` table at runtime | `V:attpath <<>>` |
| `c:` | Static on/off | Compiled as `&if_/&endif_` comment blocks | `c:adspl +` → `&if_adspl&` = `` |
| `C:` | Dynamic on/off | Queried from `&options&.act_flg` at runtime | `C:gclog12 -` |

**Static options** (`v:`, `c:`) are resolved at compile time and baked into the SQL.

**Dynamic options** (`V:`, `C:`) are NOT resolved by the compiler. The SQL contains runtime checks like:
```sql
if exists (select * from &options& where id = 'gclog12' and act_flg = '+')
```

**Option File Hierarchy** (later files override earlier):
1. `options.def` - Default values (REQUIRED)
2. `options.{company}` - Company-specific (REQUIRED)
3. `options.{company}.{profile}` - Profile-specific (OPTIONAL)
4. `table_locations` - Table location mappings (REQUIRED)

All files are in `{SQL_SOURCE}/CSS/Setup/`.

---

## Key File Locations

### Python Implementation (This Project)

| File | Purpose |
|------|---------|
| `src/commands/ibs_common.py` | Shared library (ALL reusable logic) |
| `src/commands/set_profile.py` | Profile wizard with connection/options testing |
| `src/commands/isqlline.py` | Execute single SQL command |
| `src/commands/runsql.py` | Execute SQL scripts with sequences |
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

### Phase 5: tail
| Status | Task |
|--------|------|
| Done | `tail.py` - Tail log files |

### Phase 6: change_log
| Status | Task |
|--------|------|
| Done | `is_changelog_enabled()` in `ibs_common.py` |
| Done | `insert_changelog_entry()` in `ibs_common.py` |
| Done | `generate_changelog_sql()` in `ibs_common.py` |
| Done | `test_changelog()` in `set_profile.py` |
| Done | runsql: changelog ON by default, `--no-changelog` to disable |
| Done | isqlline: never logs to changelog |
| Note | runcreate will pass `--no-changelog` to runsql |
| Note | i_run_upgrade uses runsql default (changelog ON) |

### Phase 8: table_locations / eloc
| Status | Task |
|--------|------|
| Done | `eloc.py` - Edit and compile table locations |
| Done | `compile_table_locations()` - Insert via SQL INSERT (BCP has 255 char limit) |

### Phase 9: actions / eact
| Status | Task |
|--------|------|
| Done | `eact.py` - Edit actions |
| Done | `compile_actions()` - Fixed-width parsing and SQL INSERT |

### Phase 10: options / eopt
| Status | Task |
|--------|------|
| Done | Options class in `ibs_common.py` |
| Done | `_parse_v_option()`, `_parse_c_option()`, `_parse_table_option()` |
| Done | `generate_option_files()` with caching (24-hour expiry) |
| Done | `replace_options()` with @sequence@ support |
| Done | `eopt.py` - Edit options (add, edit, merge modes) |
| Done | `compile_options()` - Insert options into database via SQL INSERT |
| Done | `compile_table_locations()` - Insert table_locations via SQL INSERT |
| Done | Interactive option creation wizard with MOD # tracking |
| Done | Merge options from options.def into company/profile files |

### Phase 11: messages / compile_msg
| Status | Task |
|--------|------|
| Done | `compile_msg.py` - Compile messages (import mode) |
| Done | `export_messages()` - Export messages from database to flat files |
| Note | CRLF/LF normalization - may need testing |

### Phase 12: required_fields / ereq
| Status | Task |
|--------|------|
| Done | `ereq.py` - Edit and compile required fields via SQL INSERT |

### Phase 13: upgrade / i_run_upgrade
| Status | Task |
|--------|------|
| Done | `i_run_upgrade.py` - Run upgrade scripts (thin wrapper around runsql) |
| Done | Changelog - inherits runsql default behavior (ON) |
| Done | `-e` echo, `-O` output file flags |
| Note | GetSeq() extraction handled by runsql `-F`/`-L` flags |

### Phase 14: runcreate
- runcreate is an orchestrator that reads create scripts and calls runsql/others
- Sample create script: `C:\_innovative\_source\current.sql\CSS\SQL_Sources\Basics\create_test`
- C# reference: `C:\_innovative\_source\sbn-services\Ibs.Compilers\ibsCompilerCommon\runcreate.cs`

| Status | Task |
|--------|------|
| Done | `runcreate.py` - Full rewrite matching C# functionality |
| Done | Line parser (extract command type, clean line) |
| Done | `$ir>path` conversion to `{SQL_SOURCE}/path` |
| Done | Leading `&option&` resolution (conditional lines) |
| Done | `-F`/`-L` sequence flag extraction |
| Done | `#NT` Windows-specific line handling |
| Done | Multiple `-D` database handling |
| Done | Positional `&db*&` placeholder handling |
| Done | Recursive runcreate calls |
| Done | Ctrl-C signal handler (shared in ibs_common.py) |
| Done | Output file append mode (`-O` flag) |
| Done | `-e` echo flag (propagates to all child calls) |

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

---

## Testing Checklist

All phases are complete. Use this checklist for manual testing.

### Prerequisites
1. Ensure MSYS2 is installed with FreeTDS (`tsql`, `freebcp` in PATH)
2. Have a working Sybase ASE or MSSQL server available
3. Have a valid `SQL_SOURCE` directory with options files

### Test 1: Profile Setup (`set_profile`)
```bash
set_profile
```
- [ ] Create a new profile (e.g., `TEST`)
- [ ] Enter connection details (HOST, PORT, USERNAME, PASSWORD)
- [ ] Test connection succeeds
- [ ] Test options loading succeeds
- [ ] Profile saved to `settings.json`

### Test 2: Single SQL Command (`isqlline`)
```bash
# Basic query
isqlline "select @@version" master TEST

# With output file
isqlline "select @@version" master TEST -O test_output.txt

# With echo
isqlline "select @@version" master TEST -e

# Both flags
isqlline "select @@version" master TEST -O test_output.txt -e
```
- [ ] Query executes and returns results
- [ ] `-O` creates output file with results
- [ ] `-e` shows SQL before execution
- [ ] Combined flags work correctly

### Test 3: SQL Script Execution (`runsql`)
```bash
# Basic script
runsql script.sql sbnmaster TEST

# With output file
runsql script.sql sbnmaster TEST -O runsql_output.txt

# With echo
runsql script.sql sbnmaster TEST -e

# With sequences
runsql script.sql sbnmaster TEST -F1 -L3

# Preview mode (no execution)
runsql script.sql sbnmaster TEST --preview
```
- [ ] Script executes with placeholder resolution
- [ ] `-O` creates output file
- [ ] `-e` echoes resolved SQL
- [ ] `-F`/`-L` loops through sequences
- [ ] `--preview` shows SQL without executing

### Test 4: Upgrade Scripts (`i_run_upgrade`)
```bash
# Script without upgrade number
i_run_upgrade sbnmaster TEST script.sql

# Script with upgrade number in filename
i_run_upgrade sbnmaster TEST sct_07.95.12345_bef.sql

# With output file
i_run_upgrade sbnmaster TEST script.sql -O upgrade_output.txt

# With echo
i_run_upgrade sbnmaster TEST script.sql -e
```
- [ ] Script executes correctly
- [ ] Upgrade number extracted from filename
- [ ] `-O` creates output file
- [ ] `-e` echoes resolved SQL

### Test 5: Build Orchestration (`runcreate`)
```bash
# Basic create script
runcreate create_test TEST

# With output file (overwrites then appends)
runcreate create_test TEST -O build.log

# With echo (propagates to all child calls)
runcreate create_test TEST -e

# Both flags
runcreate create_test TEST -O build.log -e
```
- [ ] Create script parses and executes all lines
- [ ] Nested `runcreate` calls work
- [ ] `-O` creates file at start, appends for each child
- [ ] `-e` propagates to all runsql/isqlline calls

### Test 6: Output File Behavior
| Command | Expected Behavior |
|---------|-------------------|
| `isqlline ... -O out.txt` | Creates/overwrites `out.txt` |
| `runsql ... -O out.txt` | Creates/overwrites `out.txt` |
| `i_run_upgrade ... -O out.txt` | Creates/overwrites `out.txt` |
| `runcreate ... -O out.txt` | Creates/overwrites at start, appends for each child |

- [ ] Each command creates output file correctly
- [ ] runcreate appends child output to same file
- [ ] Output contains all expected content

### Test 7: Echo Flag Behavior (`-e`)
All commands should:
- [ ] Print resolved SQL before execution
- [ ] Pass `-v` flag to tsql for verbose mode
- [ ] When combined with `-O`, echo goes to file only

### Test 8: Editor Commands
```bash
# Edit table locations
eloc TEST

# Edit actions
eact TEST

# Edit options
eopt TEST

# Edit required fields
ereq TEST
```
- [ ] Each opens editor with correct file
- [ ] Saves compile to database on exit

### Test 9: Compile Commands
```bash
# Compile messages
compile_msg TEST

# Tail log file
tail /path/to/logfile
```
- [ ] Messages import correctly
- [ ] Tail follows file updates
