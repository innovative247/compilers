# IBS Compilers

Cross-platform Python tools for compiling and deploying SQL to Sybase ASE and Microsoft SQL Server databases.

## Overview

This toolset replaces the legacy C# `Ibs.Compilers`. It provides command-line utilities for:

- **SQL compilation** - Execute SQL scripts with placeholder resolution and sequence support
- **Database builds** - Orchestrate multi-file builds via create scripts
- **Configuration management** - Edit and compile options, table locations, actions, messages

---

## FreeTDS

All tools use **FreeTDS** for database connectivity, providing unified support for both Sybase ASE and MSSQL on Windows, macOS, and Linux.

FreeTDS provides `tsql` for executing SQL commands and `freebcp` for bulk data operations. Be aware that FreeTDS has some limitations and reserved keywords that may affect certain SQL syntax. See the FreeTDS documentation for details:

**Note:** The `transfer_data` command uses `freebcp` for high-performance bulk transfers between Sybase and MSSQL. FreeTDS 1.0+ is required for reliable long column support. Be aware that source data containing Ctrl-Z (0x1a) bytes will cause import failures on Windows - clean source data before transfer.

- [FreeTDS User Guide](https://www.freetds.org/userguide/)
- [tsql Reference](https://www.freetds.org/userguide/tsql.html)

### Configuration

FreeTDS typically uses a configuration file (`freetds.conf`) to define server aliases. **Our compilers do NOT use this configuration file.** Instead, all connection parameters (host, port, username, password) are stored in `settings.json` and passed directly to `tsql` using command-line flags (`-H`, `-p`, `-U`, `-P`). This ensures portable, self-contained configuration without external dependencies.

### Installation

The bootstrap installers will install FreeTDS automatically. For manual installation:

- **Windows:** Install MSYS2, then run `pacman -S mingw-w64-ucrt-x86_64-freetds`
- **macOS:** `brew install freetds`
- **Linux:** `sudo apt install freetds-bin freetds-dev`

Test FreeTDS installation:
```bash
tsql -C
```

---

## Installation

### Windows

```powershell
.\install\bootstrap.ps1
```

### macOS / Linux / WSL

```bash
chmod +x ./install/bootstrap.sh
./install/bootstrap.sh
```

### Verify Installation

```bash
runsql --help
tsql -C
```

### Create Your First Profile

```bash
set_profile
```

---

## Commands

Run any command with `--help` to see full usage and options.

| Command | Aliases | Description |
|---------|---------|-------------|
| `set_profile` | | Interactive profile configuration wizard (includes VSCode integration) |
| `isqlline` | | Execute a single SQL command |
| `runsql` | | Execute SQL scripts with placeholder resolution and sequences |
| `runcreate` | | Orchestrate multi-file database builds |
| `i_run_upgrade` | | Execute database upgrade scripts |
| `set_options` | `eopt`, `import_options` | Edit and compile database options |
| `set_table_locations` | `eloc`, `create_tbl_locations` | Edit and compile table locations |
| `set_actions` | `eact`, `compile_actions` | Edit and compile actions |
| `set_required_fields` | `ereq`, `install_required_fields` | Edit and compile required fields |
| `set_messages` | `compile_msg`, `install_msg`, `extract_msg` | Compile messages to the database |
| `transfer_data` | | Bulk data transfer between Sybase and MSSQL using freebcp |

---

## Configuration

### settings.json

Connection profiles are stored in `settings.json` at the project root:

```json
{
  "Profiles": {
    "GONZO": {
      "CMPY": 101,
      "IR": "C:\\_innovative\\_source\\current.sql",
      "PLATFORM": "SYBASE",
      "HOST": "10.10.123.4",
      "PORT": 5000,
      "USERNAME": "sa",
      "PASSWORD": "your_password"
    }
  }
}
```

### Options System

SQL files use `&placeholder&` syntax for compile-time substitution:

| Prefix | Type | Description |
|--------|------|-------------|
| `v:` | Static value | Replaced at compile time |
| `c:` | Static conditional | Enables/disables code blocks |
| `V:` | Dynamic value | Resolved at runtime from database |
| `C:` | Dynamic conditional | Checked at runtime |

Option files are loaded from `CSS/Setup/` in this order:
1. `options.def` - Defaults
2. `options.{company}` - Company-specific
3. `options.{company}.{profile}` - Profile-specific

### Symbolic Paths

Short paths are automatically expanded in SQL file references:

| Symbolic | Expands To |
|----------|------------|
| `/ss/api/` | `/SQL_Sources/Application_Program_Interface/` |
| `/ss/api2/` | `/SQL_Sources/Application_Program_Interface_V2/` |
| `/ss/api3/` | `/SQL_Sources/Application_Program_Interface_V3/` |
| `/ss/at/` | `/SQL_Sources/Alarm_Treatment/` |
| `/ss/ba/` | `/SQL_Sources/Basics/` |
| `/ss/bl/` | `/SQL_Sources/Billing/` |
| `/ss/ct/` | `/SQL_Sources/Create_Temp/` |
| `/ss/cv/` | `/SQL_Sources/Conversions/` |
| `/ss/da/` | `/SQL_Sources/da/` |
| `/ss/dv/` | `/SQL_Sources/IBS_Development/` |
| `/ss/fe/` | `/SQL_Sources/Front_End/` |
| `/ss/in/` | `/SQL_Sources/Internal/` |
| `/ss/ma/` | `/SQL_Sources/Co_Monitoring/` |
| `/ss/mb/` | `/SQL_Sources/Mobile/` |
| `/ss/mo/` | `/SQL_Sources/Monitoring/` |
| `/ss/mobile/` | `/SQL_Sources/Mobile/` |
| `/ss/sdi/` | `/SQL_Sources/SDI_App/` |
| `/ss/si/` | `/SQL_Sources/System_Init/` |
| `/ss/sv/` | `/SQL_Sources/Service/` |
| `/ss/test/` | `/SQL_Sources/Test/` |
| `/ss/tm/` | `/SQL_Sources/Telemarketing/` |
| `/ss/ub/` | `/SQL_Sources/US_Basics/` |
| `/ibs/ss/` | `/IBS/SQL_Sources/` |

**Symbolic Link Creation:** When any command runs (`runsql`, `isqlline`, `runcreate`, etc.), it attempts to create actual symbolic links (e.g., `CSS/ss/ba` → `CSS/SQL_Sources/Basics`) in the SQL source directory. This check only happens once per session using an environment variable (`IBS_SYMLINKS_CHECKED`), so nested command calls (e.g., `runcreate` calling `runsql`) do not repeat the check. On Windows, this requires Administrator privileges. If symbolic links cannot be created, the compilers fall back to path string expansion.

---

## Troubleshooting

### Command not found

Add the Python scripts directory to PATH:

- **Windows:** `%APPDATA%\Python\Python3XX\Scripts`
- **macOS/Linux:** `~/.local/bin`

### Connection failed

Test connectivity directly:
```bash
tsql -H <host> -p <port> -U <user> -P <password>
```

---

## Project Structure

```
compilers/
├── install/
│   ├── bootstrap.ps1          # Windows
│   ├── bootstrap.sh           # macOS/Linux
│   ├── installer_windows.py
│   ├── installer_linux.py
│   └── installer_macos.py
├── src/
│   ├── pyproject.toml         # Defines entry points that create executables from Python files
│   └── commands/
│       ├── ibs_common.py      # Shared library
│       ├── runsql.py
│       ├── isqlline.py
│       ├── runcreate.py
│       ├── transfer_data.py   # Bulk data transfer (Sybase <-> MSSQL)
│       └── ...
├── settings.json
└── README.md
```