# IBS Compilers

Cross-platform .NET 8 tools for compiling and deploying SQL to Sybase ASE and Microsoft SQL Server databases.

## Overview

Self-contained executables — no runtime or FreeTDS required. Managed ADO.NET drivers handle all database connectivity.

- **SQL compilation** - Execute SQL scripts with placeholder resolution and sequence support
- **Database builds** - Orchestrate multi-file builds via create scripts
- **Configuration management** - Edit and compile options, table locations, actions, messages
- **Data transfer** - Bulk transfer between databases using managed BCP

---

## Installation

### Windows

```powershell
irm https://raw.githubusercontent.com/innovative247/compilers/main/install.ps1 | iex
```

### macOS / Linux / WSL

```bash
curl -fsSL https://raw.githubusercontent.com/innovative247/compilers/main/install.sh | bash
```

### After Install

```bash
set_profile configure    # Add to PATH
set_profile              # Configure database connections
```

### Verify

```bash
runsql version
```

### Update

```bash
runsql update
```

---

## Commands

| Command | Aliases | Description |
|---------|---------|-------------|
| `set_profile` | | Interactive profile configuration wizard |
| `isqlline` | | Execute a single SQL command |
| `runsql` | | Execute SQL scripts with placeholder resolution and sequences |
| `runcreate` | | Orchestrate multi-file database builds |
| `i_run_upgrade` | | Execute database upgrade scripts |
| `set_options` | `eopt` | Edit and compile database options |
| `set_table_locations` | `eloc` | Edit and compile table locations |
| `set_actions` | `eact` | Edit and compile actions |
| `set_required_fields` | | Edit and compile required fields |
| `set_messages` | `compile_msg`, `extract_msg` | Compile messages to the database |
| `transfer_data` | | Bulk data transfer between databases |
| `bcp_data` | | Bulk copy in/out for individual tables |
| `iplan` | | Interactive plan viewer |
| `iplanext` | | Extended plan viewer |
| `iwho` | | Show connected users |

### Common Subcommands

Every command supports these subcommands:

| Subcommand | Description |
|------------|-------------|
| `version` | Print version and exit |
| `update` | Download and install latest release |
| `configure` | Show configuration status, add to PATH |

---

## Configuration

### settings.json

Connection profiles are stored in `settings.json` alongside the executables:

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
      "PASSWORD": "your_password",
      "SQL_SOURCE": "/path/to/current.sql"
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
| `/ss/ba/` | `/SQL_Sources/Basics/` |
| `/ss/bl/` | `/SQL_Sources/Billing/` |
| `/ss/fe/` | `/SQL_Sources/Front_End/` |
| `/ss/in/` | `/SQL_Sources/Internal/` |
| `/ss/sv/` | `/SQL_Sources/Service/` |
| ... | (see Common.cs for full list) |

---

## Troubleshooting

### Command not found

Run `set_profile configure` to add the install directory to your PATH.

### Connection failed

Check your profile settings with `set_profile`, then test connectivity:
```bash
isqlline "SELECT 1" master PROFILE_NAME
```

---

## Project Structure

```
compilers/
├── install.ps1              # Windows installer
├── install.sh               # Linux/macOS installer
├── publish.ps1              # Build and package all platforms
├── settings.json.example    # Template configuration
└── src/
    ├── Directory.Build.props    # Shared version (all projects)
    ├── Compilers.sln
    ├── ibsCompiler/             # Shared library
    │   ├── Configuration/       # Profile management
    │   ├── Database/            # ISqlExecutor, MSSQL/Sybase executors
    │   └── TransferData/        # Bulk transfer engine
    ├── runsql/                  # Entry point projects
    ├── isqlline/
    ├── set_profile/
    └── ...                      # 21 total executables
```
