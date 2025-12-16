# Project Overview: IBS Compilers

## Executive Summary

The IBS Compilers project is a cross-platform Python-based toolchain for managing and deploying SQL databases to Sybase ASE and Microsoft SQL Server. This project replaces a legacy C# implementation (Ibs.Compilers) with a modern, portable Python solution that works across Windows, macOS, and Linux environments.

The toolchain serves as the database deployment infrastructure for the SmartSBN enterprise system, providing SQL script compilation, metadata-driven configuration management, and database orchestration capabilities. It handles complex compile-time placeholder resolution, hierarchical options management, and changelog auditing for all database operations.

At its core, the toolchain uses FreeTDS (tsql) for database connectivity, ensuring consistent behavior across different platforms and database vendors. The entire suite consists of 12 Python command-line tools distributed as console scripts, with all shared logic consolidated in a 4,302-line common library module.

## Technology Stack

- **Primary Language**: Python 3.11+
- **Frameworks & Libraries**:
  - pyodbc (database connectivity fallback)
  - FreeTDS tsql (primary database execution engine)
  - argparse (CLI argument parsing)
  - pathlib (cross-platform file path handling)
- **Runtime/Platform**: Python 3.11+ on Windows, macOS, Linux (including WSL)
- **Database(s)**:
  - Sybase ASE 15.0+ (primary target)
  - Microsoft SQL Server 2016+ (secondary target)
- **Build Tools**: setuptools, pip (editable install via `pip install -e .`)
- **Testing Framework(s)**: pytest (minimal coverage currently)
- **Other Notable Technologies**:
  - MSYS2 (Windows FreeTDS installation)
  - Homebrew (macOS FreeTDS installation)
  - apt (Linux FreeTDS installation)
  - Symbolic links for path aliasing

## Architecture Overview

The project follows a **shared library architecture** where all reusable logic resides in a single common module (`ibs_common.py`), and individual command scripts serve as thin CLI wrappers. This design ensures consistency across all tools and minimizes code duplication.

**Key Architectural Decisions**:

1. **FreeTDS Native Execution**: Uses subprocess calls to `tsql` rather than ODBC/pyodbc to match legacy C# behavior exactly. This ensures consistent SQL handling, especially for stored procedures and batch scripts.

2. **Hierarchical Options System**: Implements a sophisticated compile-time placeholder replacement system with three-level precedence (defaults → company → profile) and 24-hour caching.

3. **Module-Level Caching**: Settings.json and database connection state are cached at module import time to avoid repeated I/O when commands call each other recursively (e.g., `runcreate` calling `runsql` hundreds of times).

4. **Symbolic Path Expansion**: Supports both real symbolic links (when administrator privileges available) and runtime path string expansion for shorthand paths like `/ss/ba/` → `/SQL_Sources/Basics/`.

5. **Character Encoding Strategy**: Files are read as UTF-8, but all communication with FreeTDS tsql uses CP1252 encoding to avoid multi-byte character issues.

6. **Raw Mode Support**: Provides RAW_MODE flag to bypass all soft-compiler features for simple SQL execution scenarios.

## Project Structure

```
compilers/
├── install/                          # Bootstrap installers for all platforms
│   ├── bootstrap.ps1                 # Windows PowerShell bootstrap
│   ├── bootstrap.sh                  # macOS/Linux/WSL bash bootstrap
│   ├── installer_windows.py          # Windows installer (MSYS2 + FreeTDS)
│   ├── installer_linux.py            # Linux installer (apt + FreeTDS)
│   └── installer_macos.py            # macOS installer (Homebrew + FreeTDS)
│
├── src/                              # Python package source
│   ├── pyproject.toml                # Package metadata and entry points
│   ├── commands/                     # All command modules
│   │   ├── __init__.py
│   │   ├── ibs_common.py             # Shared library (4,302 lines)
│   │   ├── runsql.py                 # Execute SQL scripts (393 lines)
│   │   ├── isqlline.py               # Execute single SQL command (319 lines)
│   │   ├── runcreate.py              # Orchestrate database builds (750 lines)
│   │   ├── i_run_upgrade.py          # Database upgrade management (293 lines)
│   │   ├── set_profile.py            # Interactive profile wizard (1,324 lines)
│   │   ├── set_options.py            # Options editor/compiler (1,215 lines)
│   │   ├── set_table_locations.py    # Table locations editor (146 lines)
│   │   ├── set_actions.py            # Actions compiler (161 lines)
│   │   ├── set_required_fields.py    # Required fields compiler (105 lines)
│   │   └── set_messages.py           # Messages compiler (268 lines)
│   │
│   └── ibs_compilers.egg-info/       # Auto-generated package metadata
│       ├── entry_points.txt          # Console script definitions
│       ├── requires.txt              # Python dependencies
│       └── ...
│
├── settings.json                     # Database connection profiles (gitignored)
├── settings.json.example             # Template for settings.json
├── readme.md                         # Comprehensive user documentation
├── PROJECT_STATUS.md                 # Development status and notes
├── CLAUDE.md                         # AI assistant guidelines
└── .gitignore                        # Git ignore rules
```

### Key Components

**Core Library (ibs_common.py - 4,302 lines)**:
- Configuration management (settings.json loading, profile resolution, caching)
- Options class for soft-compiler placeholder resolution
- Database connectivity (FreeTDS tsql wrapper, pyodbc fallback)
- SQL execution engines (native mode, interleaved mode)
- File system utilities (file finding, symbolic links, path conversion)
- Compilation functions (options, table_locations, actions, messages, required_fields)
- Changelog management (ba_gen_chg_log integration)
- User interface utilities (console prompts, editor launching)

**Primary Command Tools**:

1. **runsql** - SQL script executor with full soft-compiler support
   - Placeholder replacement (&options&, &dbtbl&, etc.)
   - Sequence processing (-F/-L flags for multi-step scripts)
   - Changelog logging (default ON, can disable with --no-changelog)
   - Preview mode for debugging

2. **isqlline** - Single SQL command executor
   - Lighter weight than runsql
   - No changelog logging
   - Useful for ad-hoc queries

3. **runcreate** - Build orchestrator
   - Reads create files (create_all, create_tbl, create_pro, etc.)
   - Dispatches to appropriate commands
   - Conditional execution (&if_mssql&, &ifn_sybase&)
   - Platform-specific lines (#NT, #UNIX)
   - Output file aggregation (-O flag)

4. **i_run_upgrade** - Database upgrade manager
   - Checks ba_upgrades_check before execution
   - Extracts upgrade numbers from filenames (xx.yy.zzzzz pattern)
   - Updates upgrade end time after completion

5. **set_profile** - Interactive profile creation wizard
   - Database connection testing
   - Alias management
   - Profile validation (duplicate detection, alias conflicts)
   - Editor integration for manual editing

**Configuration Management Tools**:

6. **set_options** (aliases: eopt, import_options) - Options editor and compiler
   - Edit mode: Modify existing options
   - Add mode: Merge new options from options.def
   - Compiles to w#options work table → options table via i_import_options

7. **set_table_locations** (aliases: eloc, create_tbl_locations) - Table location mapping
   - Maps logical table names (&users&) to physical databases
   - Syntax: `-> tablename &database& description`

8. **set_actions** (aliases: eact, compile_actions) - Application actions compiler
   - Compiles actions and actions_dtl files to database
   - Used by IBS framework for menu/permission system

9. **set_required_fields** (aliases: ereq, install_required_fields) - Required fields compiler
   - Compiles required fields definitions to database

10. **set_messages** (aliases: compile_msg, install_msg, extract_msg) - Message management
    - compile/install: Load messages into database
    - extract: Export messages from GONZO (canonical source) for other servers

## Entrypoint Analysis

**Primary Entrypoint**: pyproject.toml defines 21 console script entry points

The package uses setuptools entry points to create executable console scripts. When installed via `pip install -e .`, setuptools creates wrapper executables in the Python Scripts directory that call the main() function of each command module.

**Initialization Sequence**:

1. User invokes command (e.g., `runsql script.sql sbnmaster GONZO`)
2. Console script wrapper calls `commands.runsql:main()`
3. Command parses arguments using argparse
4. Command imports functions from ibs_common
5. On first import, ibs_common:
   - Sets up Ctrl-C handler
   - Pre-compiles regex patterns for performance
   - Initializes module-level caches (settings, options)
6. Command calls get_config() or load_profile() to resolve connection parameters
7. If profile used, Options class loads and caches hierarchical option files
8. Symbolic links created if needed (one-time per session via environment flag)
9. SQL content read, placeholders replaced using Options.replace_options()
10. SQL executed via execute_sql_native() subprocess call to tsql
11. Results returned to user (stdout or output file)

**Bootstrap Process** (install/bootstrap.sh or bootstrap.ps1):

1. Detect platform (Windows/macOS/Linux)
2. Check Python 3.11+ availability
3. Install FreeTDS via platform package manager
4. Run platform-specific Python installer
5. Installer verifies dependencies
6. Installer runs `pip install -e .` from project root
7. Creates settings.json from template if not exists
8. Verifies tsql command availability
9. Prompts user to run set_profile for first profile

## Data Flow & Integration Points

**Typical Data Flow**:

```
User Command
    ↓
Console Script Entry Point (main)
    ↓
Argument Parsing (argparse)
    ↓
Load Profile (settings.json → profile dict)
    ↓
Load Options (options.def + options.{company} + options.{company}.{profile} → cached)
    ↓
Read SQL File (UTF-8)
    ↓
Replace Placeholders (&dbtbl& → sbnmaster, &dbpro& → sbnpro)
    ↓
Process Sequences (@1@, @2@ blocks if -F/-L specified)
    ↓
Changelog Entry (if enabled, insert to ba_gen_chg_log)
    ↓
Execute via FreeTDS tsql (CP1252 encoding)
    ↓
Parse Output
    ↓
Return Results (stdout or file)
```

**External Integration Points**:

1. **FreeTDS tsql**: Primary database execution engine
   - Invoked via subprocess.run()
   - Stdin: SQL script content (CP1252 encoded)
   - Args: -H host -p port -U user -P password -D database
   - Output: Captured from stdout/stderr

2. **settings.json**: Connection profile storage
   - Read at module load time
   - Cached based on file modification time
   - Contains: Profiles with HOST, PORT, USERNAME, PASSWORD, SQL_SOURCE, PLATFORM, COMPANY

3. **SQL Source Directory** (config['SQL_SOURCE']):
   - Expected structure: {SQL_SOURCE}/CSS/Setup/ for options files
   - {SQL_SOURCE}/CSS/SQL_Sources/ for SQL scripts
   - Symbolic links created: CSS/ss → CSS/SQL_Sources, CSS/ss/ba → CSS/SQL_Sources/Basics, etc.

4. **Database Tables**:
   - **ba_gen_chg_log**: Audit log for all runsql operations
   - **options**: Runtime configuration values (compiled from source files)
   - **w#options**: Work table for options import
   - **table_locations**: Logical to physical table mappings
   - **actions / actions_dtl**: Application menu actions
   - **required_fields / required_fields_dtl**: Field validation rules
   - **messages**: Multi-language UI text
   - **ba_upgrades_check**: Upgrade control and status

5. **Editor Integration**:
   - Windows: os.startfile()
   - macOS: `open` command
   - Linux: Falls back through code → vim → nano → vi

## Code Quality Assessment

### Strengths

- **Excellent documentation**: Comprehensive docstrings, inline comments explaining complex logic, detailed README
- **Performance optimization**: Pre-compiled regex patterns, module-level caching, settings.json caching based on mtime
- **Cross-platform design**: Consistent behavior across Windows, macOS, Linux using pathlib and os.path
- **Error handling**: Graceful failures with informative error messages, connection testing before operations
- **Security**: Passwords never logged, credentials in gitignored settings.json
- **Consistency**: Shared ibs_common.py library ensures uniform behavior across all 12 commands
- **Ctrl-C handling**: Clean interrupt handling in all commands
- **Detailed logging**: Debug mode available, clear progress messages during operations

### Areas for Improvement

- **Test coverage**: Minimal automated testing (pytest infrastructure present but ~0% coverage)
  - Severity: MEDIUM - No unit tests for critical functions like Options.replace_options()
  - Files: src/commands/*.py - All commands lack test files

- **Error recovery**: Some operations lack rollback mechanisms
  - Severity: MEDIUM - Options compilation doesn't rollback if i_import_options fails
  - File: src/commands/ibs_common.py:compile_options()

- **Hardcoded paths**: Some paths assume specific directory structures
  - Severity: LOW - CSS/Setup paths are hardcoded, may break with different layouts
  - File: src/commands/ibs_common.py:get_options_*_path()

- **Limited validation**: Profile creation accepts invalid hostnames/ports
  - Severity: LOW - set_profile doesn't validate host format or port ranges
  - File: src/commands/set_profile.py:prompt_for_new_profile()

- **Windows-specific code**: Elevated privilege code only works on Windows
  - Severity: LOW - Symbolic link creation elevation is Windows-only
  - File: src/commands/ibs_common.py:_run_elevated_symlink_creation()

- **Magic numbers**: Some constants lack named definitions
  - Severity: LOW - 24-hour cache TTL hardcoded as 86400
  - File: src/commands/ibs_common.py:Options class

- **Large functions**: Some functions exceed 100 lines
  - Severity: LOW - execute_sql_native() is 150+ lines, could be refactored
  - File: src/commands/ibs_common.py

## Recommended Enhancements

### High Priority

1. **Add automated testing infrastructure**
   - Create test suite for Options class (placeholder replacement, file merging, caching)
   - Add integration tests for FreeTDS execution with mock database
   - Test symbolic link creation on all platforms
   - Justification: Currently zero test coverage for critical database operations

2. **Implement transaction safety for metadata compilation**
   - Wrap compile_options, compile_actions, etc. in database transactions
   - Rollback w#options on failure
   - Delete cache files only after successful compilation
   - Justification: Currently partial failures leave database in inconsistent state

3. **Add connection pooling/reuse**
   - Cache database connections for runcreate operations
   - Reduce overhead when executing hundreds of scripts
   - Justification: runcreate currently creates new tsql subprocess for every file

### Medium Priority

1. **Enhance input validation**
   - Validate hostname format (IP or FQDN)
   - Validate port ranges (1-65535)
   - Validate SQL_SOURCE directory exists
   - Check FreeTDS installation before operations
   - Justification: Prevents confusing errors from invalid configuration

2. **Improve error messages**
   - Distinguish between connection failures, SQL errors, file not found
   - Provide suggestions for common problems
   - Justification: Current error messages sometimes don't indicate root cause

3. **Add progress indicators**
   - Show progress bar for runcreate operations
   - Display "X of Y files" during builds
   - Justification: Large builds (1000+ files) currently give no progress feedback

4. **Configuration validation on startup**
   - Check settings.json schema
   - Warn about deprecated fields
   - Validate profile aliases don't conflict
   - Justification: set_profile does this, but other commands silently fail with bad config

5. **Add dry-run mode to all commands**
   - Extend --preview to all compilation commands
   - Show what would be executed without doing it
   - Justification: Helps prevent mistakes in production environments

### Low Priority / Future Considerations

1. **Python type hints**
   - Add type annotations to all functions
   - Enable mypy checking
   - Justification: Improves IDE support and catches type errors early

2. **Configuration file versioning**
   - Add version field to settings.json
   - Migrate old formats automatically
   - Justification: Future-proofs against breaking changes

3. **Parallel execution**
   - Run independent scripts in parallel during runcreate
   - Justification: Could speed up large builds, but adds complexity

4. **Plugin architecture**
   - Allow custom commands via entry points
   - Justification: Enables extension without modifying core code

5. **GUI installer**
   - Provide graphical installer for Windows users
   - Justification: Command-line bootstrap works well, but GUI may help adoption

6. **Database connection encryption**
   - Support TLS/SSL connections to database
   - Justification: FreeTDS supports it, but requires additional configuration

## Security Considerations

**Current Security Measures**:
- Passwords stored in gitignored settings.json
- Passwords never logged to console or files
- No credentials in command history (passed via stdin to tsql)
- Input sanitization for SQL injection (though limited - relies on stored procedures)

**Potential Vulnerabilities**:
- **settings.json plaintext passwords**: Credentials stored unencrypted on disk
  - Mitigation: File permissions should be restricted (600 on Unix)
  - Future: Consider encrypted credential storage or OS keychain integration

- **Command-line password exposure**: -P flag shows password in process list
  - Mitigation: Commands prompt for password if not provided
  - Best practice: Never use -P flag, always prompt

- **SQL injection in placeholder replacement**: User-provided values could contain SQL
  - Current state: Limited risk - most values from options files, not user input
  - Mitigation: Placeholders are compile-time only, not runtime

- **Symbolic link attacks**: Malicious links could redirect file operations
  - Mitigation: Symbolic links only created in controlled SQL_SOURCE directory
  - Requires admin/root privileges on most systems

**Audit Trail**:
- All runsql operations logged to ba_gen_chg_log by default
- Logs include: username, timestamp, database, script path, command type
- isqlline does NOT log (by design, for ad-hoc queries)
- runcreate logs start/end of build with elapsed time

## Testing & Quality Assurance

**Current Testing Approach**:
- Manual testing during development
- Real database testing against Sybase ASE and MSSQL
- Cross-platform verification on Windows, Ubuntu, macOS
- pytest infrastructure configured but no tests written

**Test Coverage Assessment**: ~0%

**Testing Infrastructure**:
- pytest configured in pyproject.toml
- pytest-cov available for coverage reporting
- Test directory: src/tests/ (would need to be created)
- No CI/CD pipeline configured

**Manual Testing Checklist** (based on git history):
- ✓ Windows installation via bootstrap.ps1
- ✓ Ubuntu installation via bootstrap.sh
- ✓ macOS installation via bootstrap.sh
- ✓ Profile creation and validation
- ✓ Symbolic link creation (Windows admin, Unix standard)
- ✓ Options compilation (Sybase and MSSQL)
- ✓ Full database build via runcreate
- ✓ Upgrade script execution
- ✓ Message export/import
- Partial: FreeTDS encoding edge cases (CP1252 vs UTF-8)

## Deployment & Operations

**Build Process**:

Development (editable install):
```bash
cd /path/to/compilers
pip install -e .
```

Production (standard install):
```bash
pip install /path/to/compilers
```

**Deployment Strategy**:

1. **Developer Workstations**: Editable install for active development
2. **Build Servers**: Standard install from repository
3. **Deployment**: No deployment needed - runs locally against remote databases

**Installation Requirements**:
- Python 3.11+ with pip
- FreeTDS 1.0+ (installed via platform package manager)
- Administrator/root for symbolic link creation (optional, will fall back)
- Network access to database servers

**Runtime Configuration**:
- settings.json in project root (created from settings.json.example)
- Environment variable IBS_SYMLINKS_CHECKED (internal, auto-managed)
- No other external configuration required

**Monitoring**:
- No built-in monitoring
- Operations logged to console/output files
- Database operations audited in ba_gen_chg_log
- Installer creates install/installer.log

**Operational Commands**:

Create first profile:
```bash
set_profile
```

Test database connection:
```bash
isqlline 'select @@version' sbnmaster PROFILE_NAME
```

Execute SQL script:
```bash
runsql script.sql sbnmaster PROFILE_NAME
```

Run full database build:
```bash
runcreate create_all PROFILE_NAME build.log
```

Compile options:
```bash
eopt PROFILE_NAME
```

Execute database upgrade:
```bash
i_run_upgrade sbnmaster PROFILE_NAME upgrade_07.95.12345.sql
```

## Dependencies Analysis

**Direct Dependencies**:

1. **pyodbc** (1.4.x+)
   - Purpose: Database connectivity (fallback, not actively used)
   - Risk: LOW - Currently unused, FreeTDS tsql is primary
   - Status: Current stable version

**External Tool Dependencies**:

1. **FreeTDS tsql** (1.0+)
   - Purpose: Primary database execution engine
   - Risk: HIGH - Critical dependency, entire toolchain depends on it
   - Status: Mature project, actively maintained
   - Installation: Via MSYS2 (Windows), Homebrew (macOS), apt (Linux)

2. **Python 3.11+**
   - Purpose: Runtime environment
   - Risk: MEDIUM - Requires modern Python
   - Status: Python 3.11 released Oct 2022, widely available
   - Note: Uses pathlib, argparse (stdlib), no exotic features

**Platform-Specific Dependencies**:

- **Windows**: MSYS2 for FreeTDS, Windows-specific editor launching
- **macOS**: Homebrew for FreeTDS, `open` command for editor
- **Linux**: apt/yum for FreeTDS, various editor fallbacks

**Dependency Risks**:
- FreeTDS bugs or changes could break all database operations
- pyodbc unused but still required (could be made optional)
- No version pinning in pyproject.toml (accepts any pyodbc version)

**Dependency Currency**:
- All dependencies are current as of December 2024
- No deprecated packages
- No known security vulnerabilities

## Documentation Status

**Quality**: EXCELLENT

**Completeness**: VERY GOOD

**Existing Documentation**:

1. **readme.md** (8,324 bytes)
   - Comprehensive overview
   - Installation instructions for all platforms
   - Command reference table
   - Configuration guide
   - Troubleshooting section
   - FreeTDS explanation
   - WSL Ubuntu setup guide
   - Options system documentation
   - Symbolic path reference

2. **PROJECT_STATUS.md** (1,925 bytes)
   - Development status tracking
   - Platform testing notes
   - Architecture decisions
   - C# reference implementation locations
   - FreeTDS encoding notes

3. **CLAUDE.md** (1,782 bytes)
   - AI assistant guidelines
   - Development workflow rules
   - Documentation philosophy (avoid over-documentation)

4. **In-Code Documentation**:
   - Comprehensive docstrings in all modules
   - Detailed function headers with Args/Returns/Examples
   - Inline comments for complex logic
   - CHG entries tracking changes (matches database convention)

5. **settings.json.example** (345 bytes)
   - Template for configuration
   - Example profile structure

**Documentation Gaps**:
- No API reference documentation
- No architecture diagrams (data flow, component interaction)
- No contribution guidelines
- No changelog file (changes tracked in git commits and CHG comments)
- Limited troubleshooting examples

**Documentation Format**: Markdown (portable, readable)

## Scalability & Performance Notes

**Current Performance Characteristics**:

- **Settings loading**: O(1) with mtime-based caching - only reads file when modified
- **Options resolution**: O(1) lookup after initial load - 24-hour cache prevents repeated file I/O
- **Placeholder replacement**: O(n*m) where n=text length, m=number of placeholders - uses pre-compiled regex
- **SQL execution**: O(1) subprocess overhead per script - could benefit from connection pooling
- **runcreate builds**: O(n) where n=number of scripts - sequential execution, no parallelization

**Performance Optimizations Implemented**:

1. **Module-level caching**: Settings and options cached at import time
2. **Pre-compiled regex patterns**: Placeholder and SQL pattern matching compiled once
3. **Options cache files**: Merged options saved to temp files for 24-hour reuse
4. **Symbolic link check**: Only checks once per session via environment variable
5. **Lazy loading**: Options only loaded when needed (profile-based commands)

**Scalability Considerations**:

- **Large builds**: runcreate tested with 1000+ file builds - works but slow (sequential)
- **Concurrent users**: Each user has separate settings.json and cache files - no contention
- **Large SQL files**: FreeTDS handles files of any size via stdin streaming
- **Memory usage**: Options cache kept in memory - grows with number of placeholders (typically <1MB)

**Bottlenecks**:

1. **Sequential execution**: runcreate runs scripts one at a time
   - Impact: 1000-file build takes 30+ minutes
   - Mitigation: Could parallelize independent scripts, but adds complexity

2. **Subprocess overhead**: Each runsql call spawns new tsql process
   - Impact: ~100ms overhead per script
   - Mitigation: Connection pooling would help but requires persistent tsql session

3. **File I/O**: Reading large SQL files from disk
   - Impact: Minimal - disk caching handles this well
   - Mitigation: None needed currently

**Performance Recommendations**:

- For large builds, use runcreate output file (-O flag) to reduce console I/O
- Use RAW_MODE for simple SQL execution to bypass options processing
- Consider parallel execution for independent database builds (future enhancement)

## Next Steps for New Developers

1. **Initial Setup**:
   ```bash
   # Clone repository
   git clone <repository_url>
   cd compilers

   # Run bootstrap installer for your platform
   # Windows:
   .\install\bootstrap.ps1

   # macOS/Linux:
   chmod +x ./install/bootstrap.sh
   ./install/bootstrap.sh

   # Create first profile
   set_profile
   ```

2. **Understand the Architecture**:
   - Read readme.md for user-facing documentation
   - Review PROJECT_STATUS.md for development context
   - Examine ibs_common.py to understand shared library
   - Study runsql.py as example of how commands use ibs_common

3. **Test Basic Functionality**:
   ```bash
   # Test database connection
   isqlline 'select @@version' sbnmaster YOUR_PROFILE

   # Test placeholder replacement
   runsql --preview simple_script.sql sbnmaster YOUR_PROFILE

   # Test options system
   eopt YOUR_PROFILE
   ```

4. **Explore Key Concepts**:
   - **Options hierarchy**: Review CSS/Setup/options.* files in SQL source directory
   - **Symbolic paths**: Check CSS/ss/* symbolic links after running any command
   - **Changelog auditing**: Query ba_gen_chg_log table to see operation history
   - **Create files**: Look at CSS/Setup/create_* files to understand build orchestration

5. **Make First Code Change**:
   - Pick a simple enhancement (e.g., add validation, improve error message)
   - Modify code in src/commands/
   - Test with `runsql --debug` or appropriate command
   - No rebuild needed (editable install detects changes immediately)

6. **Reference the Legacy Implementation**:
   - C# code location: C:\_innovative\_source\sbn-services\Ibs.Compilers
   - Compare behavior when unsure about edge cases
   - Python implementation aims to match C# behavior exactly

7. **Understand the Data Flow**:
   - Trace a command from entry point → argument parsing → config loading → options merging → SQL execution → result output
   - Use --debug flag to see detailed execution logging
   - Check output files to understand FreeTDS communication

8. **Study the Options System** (most complex component):
   - Read Options class in ibs_common.py
   - Understand v:/V: (values) vs c:/C: (conditionals)
   - Review how compile_options() converts source files to database tables
   - Test with different option files to see precedence in action

9. **Familiarize with Database Schema**:
   - Review tables: options, table_locations, actions, messages, ba_gen_chg_log
   - Understand stored procedures: i_import_options, ba_upgrades_check
   - See how soft-compiler placeholders map to physical databases

10. **Contributing Guidelines**:
    - Follow existing code style (docstrings, type hints where present, clear comments)
    - Test on multiple platforms if possible (Windows, Linux at minimum)
    - Update readme.md for user-facing changes
    - Add CHG comments in code for significant changes
    - Keep commands as thin wrappers - put logic in ibs_common.py
    - Avoid creating excessive documentation files per CLAUDE.md guidelines

## Appendix: Soft-Compiler Placeholder Syntax

The Options system supports several types of compile-time placeholders:

**Value Placeholders** (v:/V:):
```
v:dbtbl <<sbnmaster>> Main tables database
V:timeout <<30>> Connection timeout (dynamic)
```
- `&dbtbl&` resolves to `sbnmaster` at compile time
- v: = static, V: = dynamic (can be changed by users at runtime)

**Conditional Placeholders** (c:/C:):
```
c:mssql - SQL Server platform
c:sybase + Sybase platform
```
- `&if_mssql&` = execute if mssql is enabled (+)
- `&ifn_sybase&` = execute if sybase is NOT enabled

**Table Location Mappings** (->):
```
-> users &dbtbl& User master table
-> invoices &dbtbl& Invoice header table
```
- `&users&` resolves to `sbnmaster..users`
- Allows logical table names to map to physical locations

**Database Shortcuts**:
```
&dbtbl& → sbnmaster (main tables)
&dbpro& → sbnpro (stored procedures)
&dbwrk& → sbnwork (work tables)
&dbsta& → sbnstatic (static reference data)
&dbibs& → ibsmaster (IBS framework)
```

**Symbolic Path Shortcuts**:
```
/ss/ba/ → /SQL_Sources/Basics/
/ss/bl/ → /SQL_Sources/Billing/
/ss/ma/ → /SQL_Sources/Co_Monitoring/
```

This placeholder system enables a single SQL codebase to work across multiple database servers, platforms (Sybase/MSSQL), and environments (dev/test/prod) through configuration files rather than code changes.
