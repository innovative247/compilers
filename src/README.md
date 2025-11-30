# IBS Compilers - Python Toolchain

**Cross-platform command-line tools for managing IBS databases (Sybase ASE and Microsoft SQL Server)**

---

## Table of Contents

**Setup**
- [Quick Start](#-5-minute-quick-start)
- [Getting Started - Windows](#getting-started---windows)
- [Getting Started - Linux/WSL](#getting-started---linuxwsl)
- [Configuration](#configuration)

**Usage**
- [Command Reference](#command-reference)
- [Common Workflows](#common-workflows)
- [VS Code Integration](#vs-code-integration)

**Reference**
- [FreeTDS Reference](#freetds-reference)
- [WSL Management](#wsl-management)
- [Troubleshooting](#troubleshooting)
- [Quick Reference](#quick-reference)
- [For Developers](#for-developers)

---

## Overview

Python scripts that replace the C# `Ibs.Compilers`. Works identically on **Windows** and **Linux/WSL**:
- **FreeTDS** provides unified database connectivity (Sybase ASE + MSSQL)
- **settings.json** stores all connection and compile settings
- Same commands (`runsql`, `freebcp`, `tsql`) work on both platforms

---

## Disclaimer

**This tool interacts directly with live databases and performs destructive operations** (truncating tables, dropping tables, bulk data insertion, running arbitrary SQL).

**Before using:**
- Always know which server and database you are targeting
- Always double-check the profile you are using
- Test against development/test databases before production
- Use `--preview` mode to see what will be executed

The development team is not responsible for data loss or corruption.

---

## 5-Minute Quick Start

**For users with VS Code and `current.sql` open:**

### Step 1: Install Python (if not already installed)

**Check if installed:**
```bash
python --version   # or python3 --version
```

**If not installed:**
- **Windows**: [Download from python.org](https://www.python.org/downloads/) - **Check "Add Python to PATH"**
- **Linux**: `sudo apt install python3 python3-pip`

### Step 2: Run Bootstrap Script (Windows)

```powershell
.\bootstrap.ps1
```

The bootstrap script will walk you through installing all dependencies, and is safe to re-run if needed.

**Options:**
- `.\bootstrap.ps1 -Force` - Reinstall/reconfigure everything
- `.\bootstrap.ps1 -SkipPython` - Skip Python if already installed
- `.\bootstrap.ps1 -Help` - Show all options

### Step 3: Create Your First Profile

```bash
python setup_profile.py
```

Follow the prompts to create a profile for your server (e.g., GONZO).

### Step 4: Compile Your First SQL File

```bash
# From VS Code terminal (Ctrl+` or Cmd+`)
runsql CSS/SQL_Sources/Basics/pro_users.sql GONZO

# Preview mode (see SQL without executing)
runsql CSS/SQL_Sources/Basics/pro_users.sql GONZO --preview
```

**Done! You're now ready to compile SQL files.**

---

## Getting Started - Windows

### Automated Setup (Recommended)

Run the bootstrap script in PowerShell:

```powershell
.\bootstrap.ps1
```

### Manual Setup

If you prefer manual installation, follow these steps:

#### Step 1: Install MSYS2 (for FreeTDS)

```powershell
# Option A: Via winget
winget install -i MSYS2.MSYS2

# Option B: Download from https://www.msys2.org/
```

#### Step 2: Install FreeTDS

Open **MSYS2 UCRT64** terminal (not regular MSYS2):

```bash
pacman -S mingw-w64-ucrt-x86_64-freetds
```

#### Step 3: Add to Windows PATH

Add this to your system PATH:
```
C:\msys64\ucrt64\bin
```

#### Step 4: Install Python Scripts

```powershell
# Requires Python 3.8+
python3 --version

cd C:\Users\JakeWilliams\Projects\compilers\src
pip3 install -e .
```

**Scripts Location**: Commands are installed to your user scripts directory:
```
C:\Users\<YourName>\AppData\Roaming\Python\Python314\Scripts
```

If commands aren't found after install, add this directory to your PATH:
```powershell
[Environment]::SetEnvironmentVariable(
    "PATH",
    [Environment]::GetEnvironmentVariable("PATH", "User") + ";C:\Users\<YourName>\AppData\Roaming\Python\Python314\Scripts",
    "User"
)
```
Then restart PowerShell.

**Note**: If `python3` doesn't work on Windows, use `py -3` or create an alias:
```powershell
# Option A: Use py launcher
py -3 --version
py -3 -m pip install -e .

# Option B: Create alias (run once in admin PowerShell)
Set-Alias -Name python3 -Value "${env:LocalAppData}\Programs\Python\Python311\python.exe"
```

#### Step 5: Configure FreeTDS

Create/edit `C:\msys64\ucrt64\etc\freetds.conf`:
```ini
[sybase_dev]
    host = 54.235.236.130
    port = 5000
    tds version = 5.0

[mssql_dev]
    host = 172.21.80.1
    port = 49694
    tds version = 7.4
```

#### Step 6: Verify Installation

```powershell
tsql -C              # FreeTDS version
freebcp -v           # BCP utility
runsql --help        # Python scripts

# Test database connection
tsql -S sybase_dev -U username -P password
```

---

## Getting Started - Linux/WSL

### Step 1: Install FreeTDS and Dependencies

```bash
sudo apt update
sudo apt install -y freetds-dev freetds-bin unixodbc unixodbc-dev tdsodbc python3-pip
```

### Step 2: Install Python Scripts

```bash
# Requires Python 3.8+
python3 --version

cd /mnt/c/Users/JakeWilliams/Projects/compilers/src
pip3 install -e .
```

### Step 3: Configure FreeTDS

Edit `/etc/freetds/freetds.conf`:
```ini
[sybase_dev]
    host = 54.235.236.130
    port = 5000
    tds version = 5.0

[mssql_dev]
    host = 172.21.80.1
    port = 49694
    tds version = 7.4
```

### Step 4: Verify Installation

```bash
tsql -C              # FreeTDS version
freebcp -v           # BCP utility
runsql --help        # Python scripts

# Test database connection
tsql -S sybase_dev -U username -P password
```

---

## Configuration

### Profile Setup

Configuration is managed through `settings.json` in your project root.

**Create/edit profiles:**
```bash
# Interactive wizard
python setup_profile.py
```

### settings.json Structure

```json
{
  "Profiles": {
    "GONZO": {
      "CMPY": 101,
      "IBSLANG": 1,
      "IR": "C:\\_innovative\\_source\\current.sql",
      "BCPJ": null,
      "PLATFORM": "SYBASE",
      "HOST": "10.10.123.4",
      "PORT": 5000,
      "USERNAME": "sa",
      "PASSWORD": "your_password",
      "PATH_APPEND": "C:\\_innovative\\_source\\current.sql"
    },
    "TEST_MSSQL": {
      "CMPY": 101,
      "IBSLANG": 1,
      "IR": "C:\\_innovative\\_source\\current.sql",
      "BCPJ": null,
      "PLATFORM": "MSSQL",
      "HOST": "127.0.0.1",
      "PORT": 1433,
      "USERNAME": "sa",
      "PASSWORD": "your_password",
      "PATH_APPEND": "C:\\_innovative\\_source\\current.sql"
    }
  }
}
```

### Profile Fields Explained

| Field | Description | Example |
|-------|-------------|---------|
| `CMPY` | Company number | `101` |
| `IBSLANG` | Language ID | `1` |
| `IR` | Installation Root - path to SQL source code | `C:\_innovative\_source\current.sql` |
| `PLATFORM` | Database type | `SYBASE` or `MSSQL` |
| `HOST` | Database server hostname or IP | `10.10.123.4` or `localhost` |
| `PORT` | Database port | `5000` (Sybase) or `1433` (MSSQL) |
| `USERNAME` | Database username | `sa` |
| `PASSWORD` | Database password | `your_password` |
| `PATH_APPEND` | Additional paths for file lookup | Same as IR typically |

### Multi-Version Support

**One installation can work with multiple SQL versions:**

```json
{
  "Profiles": {
    "GONZO_CURRENT": {
      "IR": "C:\\_innovative\\_source\\current.sql",
      ...
    },
    "GONZO_V94": {
      "IR": "C:\\_innovative\\_source\\sql_v94",
      ...
    },
    "GONZO_V93": {
      "IR": "C:\\_innovative\\_source\\sql_v93",
      ...
    }
  }
}
```

Use different profiles to compile different SQL versions to the same or different servers.

---

## Command Reference

### Available Commands

| Command | Description |
|---------|-------------|
| `runsql` | Execute SQL scripts with placeholder replacement and sequences |
| `isqlline` | Execute a single SQL command |
| `eopt` | Interactively edit and compile database options |
| `eloc` | Interactively edit and compile table locations |
| `eact` | Interactively edit and compile database actions |
| `bcp_data` | Perform bulk copy operations |
| `check_tables` | Check for and manage 'old' tables |
| `compile_msg` | Compile and install messages |
| `compile_required_fields` | Compile and install required field definitions |
| `i_run_upgrade` | Execute database upgrade scripts |
| `runcreate` | Orchestrate multi-step database builds |
| `tail` | Continuously display new lines from a file |

### runsql - Main SQL Compiler

**Basic syntax:**
```bash
runsql <script> <profile> [options]
```

**Options:**

| Option | Description | Example |
|--------|-------------|---------|
| `--preview` | Show SQL without executing | `runsql script.sql GONZO --preview` |
| `--changelog` | Enable audit logging | `runsql script.sql GONZO --changelog` |
| `-F N` | First sequence number | `runsql script.sql GONZO -F 1` |
| `-L N` | Last sequence number | `runsql script.sql GONZO -L 10` |
| `-D db` | Override database | `runsql script.sql GONZO -D sbnwork` |
| `-S server` | Override server/profile | `runsql script.sql -S GONZO` |
| `-U user` | Override username | `runsql script.sql GONZO -U admin` |
| `-P pass` | Override password | `runsql script.sql GONZO -P secret` |

**Examples:**
```bash
# Basic compilation
runsql pro_users.sql GONZO

# Preview without executing
runsql pro_users.sql GONZO --preview

# With audit trail (production compilations)
runsql pro_users.sql GONZO --changelog

# Compile sequences 1-10
runsql create_tables.sql GONZO -F 1 -L 10

# Compile to specific database
runsql script.sql GONZO -D sbnwork

# Using symbolic paths (auto-resolved)
runsql css/ss/ba/pro_users.sql GONZO
runsql \ss\bl\pro_invoices.sql GONZO
```

### isqlline - Quick SQL Queries

**Basic syntax:**
```bash
isqlline "<query>" <profile> [options]
```

**Examples:**
```bash
# Check server version
isqlline "SELECT @@version" GONZO

# Quick data query
isqlline "SELECT TOP 10 * FROM sbnmaster..users" GONZO

# With output to file
isqlline "SELECT * FROM sbnmaster..users" GONZO -O users.txt

# Specific database
isqlline "SELECT DB_NAME()" GONZO -D master
```

### Other Commands

**eopt - Edit Options:**
```bash
eopt GONZO              # Interactive option editor
eopt GONZO -d           # Developer mode (merge with options.def)
```

**eloc - Edit Table Locations:**
```bash
eloc GONZO              # Interactive table locations editor
```

**eact - Edit Actions:**
```bash
eact GONZO              # Interactive actions compiler
```

**bcp_data - Bulk Copy:**
```bash
bcp_data out GONZO                     # Export all data
bcp_data in GONZO                      # Import all data
bcp_data in GONZO --truncate-tables    # Import with truncation
```

**runcreate - Run Build Scripts:**
```bash
runcreate CSS/Setup/create_tbl GONZO    # Create all tables
runcreate CSS/Setup/create_pro GONZO    # Create all procedures
```

---

## Common Workflows

### Daily Compilation Workflow

**1. Open SQL file in VS Code**
- Navigate to file: `CSS/SQL_Sources/Basics/pro_users.sql`

**2. Open integrated terminal**
- Press `` Ctrl+` `` (backtick) or View -> Terminal

**3. Compile the file**
```bash
runsql CSS/SQL_Sources/Basics/pro_users.sql GONZO
```

### Safe Compilation with Preview

**Always preview changes to production:**
```bash
# 1. Preview the compiled SQL
runsql pro_users.sql PROD --preview

# 2. Review output carefully

# 3. Execute with audit logging
runsql pro_users.sql PROD --changelog
```

### Building from Scratch

```bash
# 1. Create tables
runcreate CSS/Setup/create_tbl GONZO

# 2. Create procedures
runcreate CSS/Setup/create_pro GONZO

# 3. Create views
runcreate CSS/Setup/create_viw GONZO

# 4. Create triggers
runcreate CSS/Setup/create_tri GONZO

# 5. Compile messages
compile_msg GONZO

# 6. Compile options
eopt GONZO

# 7. Compile table locations
eloc GONZO
```

### Working with Multiple SQL Versions

```bash
# Morning: Work on current release
runsql script.sql GONZO_CURRENT

# Afternoon: Test patch on release 94
runsql hotfix.sql GONZO_V94

# Next day: Deploy to production (release 93)
runsql hotfix.sql PROD_V93 --changelog
```

### Symbolic Path Resolution

These paths are automatically expanded:

| Symbolic | Expands To |
|----------|------------|
| `\ss\ba\` | `\SQL_Sources\Basics\` |
| `\ss\bl\` | `\SQL_Sources\Billing\` |
| `\ss\mo\` | `\SQL_Sources\Monitoring\` |
| `\ss\sv\` | `\SQL_Sources\Service\` |
| `\ss\api\` | `\SQL_Sources\Application_Program_Interface\` |
| `\ss\api3\` | `\SQL_Sources\Application_Program_Interface_V3\` |

**Example:**
```bash
# These are equivalent:
runsql CSS/SQL_Sources/Basics/pro_users.sql GONZO
runsql css/ss/ba/pro_users.sql GONZO
```

---

## VS Code Integration

### Setup Task Runner

Create `.vscode/tasks.json` in your workspace root:

```json
{
  "version": "2.0.0",
  "tasks": [
    {
      "label": "Compile SQL to GONZO",
      "type": "shell",
      "command": "runsql",
      "args": ["${file}", "GONZO"],
      "problemMatcher": [],
      "presentation": {
        "reveal": "always",
        "panel": "new"
      }
    },
    {
      "label": "Preview SQL",
      "type": "shell",
      "command": "runsql",
      "args": ["${file}", "GONZO", "--preview"],
      "problemMatcher": []
    },
    {
      "label": "Compile SQL (Choose Profile)",
      "type": "shell",
      "command": "runsql",
      "args": ["${file}", "${input:profileName}"],
      "problemMatcher": []
    },
    {
      "label": "Compile SQL with Audit",
      "type": "shell",
      "command": "runsql",
      "args": ["${file}", "${input:profileName}", "--changelog"],
      "problemMatcher": []
    }
  ],
  "inputs": [
    {
      "id": "profileName",
      "type": "pickString",
      "description": "Select profile",
      "options": ["GONZO", "TEST", "PROD"],
      "default": "GONZO"
    }
  ]
}
```

### Setup Keyboard Shortcuts

Create `.vscode/keybindings.json`:

```json
[
  {
    "key": "ctrl+shift+c",
    "command": "workbench.action.tasks.runTask",
    "args": "Compile SQL to GONZO",
    "when": "editorLangId == sql"
  },
  {
    "key": "ctrl+shift+p",
    "command": "workbench.action.tasks.runTask",
    "args": "Preview SQL",
    "when": "editorLangId == sql"
  }
]
```

**Usage:**
1. Open any `.sql` file
2. Press `Ctrl+Shift+C` to compile to GONZO
3. Press `Ctrl+Shift+P` to preview
4. Or: Press `Ctrl+Shift+P` -> "Run Task" -> Select task

---

## FreeTDS Reference

FreeTDS provides unified database connectivity for both Sybase ASE and MSSQL on **Windows and Linux**.

### Config File Locations

| Platform | Config Location |
|----------|-----------------|
| Windows (MSYS2) | `C:\msys64\ucrt64\etc\freetds.conf` or set `FREETDS` env var |
| Linux/WSL | `/etc/freetds/freetds.conf` |

### Test Connections with tsql

```bash
# Direct connection
tsql -H <host> -p <port> -U <user> -P <password>

# Using server name from freetds.conf
tsql -S sybase_dev -U JAKE -P ibsibs2
tsql -S mssql_dev -U sa -P innsoft247
```

### BCP (Bulk Copy)

`freebcp` works identically on **Windows and Linux** (requires server defined in freetds.conf):

```bash
# Export data
freebcp database..table out output.txt -S server_name -U user -P password -c

# Import data
freebcp database..table in input.txt -S server_name -U user -P password -c

# Examples:
freebcp sbnmaster..users out sybase_users.txt -S sybase_dev -U JAKE -P ibsibs2 -c
freebcp sbnmaster..users out mssql_users.txt -S mssql_dev -U sa -P innsoft247 -c
```

**Note**: The Python scripts use `freebcp` on both platforms for true cross-platform parity.

### Common freebcp Options

| Flag | Meaning |
|------|---------|
| `-c` | Character mode (tab-delimited text) |
| `-t,` | Use comma as delimiter |
| `-r\n` | Row terminator (newline) |
| `-b 1000` | Batch size for imports |

---

## WSL Management

A WSL Ubuntu instance is configured for Linux testing of the Python compilers.

### List Installed Distros

```powershell
wsl --list --verbose
```

### Find Distro Locations

```powershell
Get-ChildItem "HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss" | ForEach-Object { Get-ItemProperty $_.PSPath } | Select-Object DistributionName, BasePath
```

Default Microsoft Store location:
```
C:\Users\<username>\AppData\Local\Packages\CanonicalGroupLimited.Ubuntu_<id>\LocalState\
```

### Backup (Export)

```powershell
# Shutdown WSL first
wsl --shutdown

# Export to tar file
wsl --export Ubuntu C:\Users\JakeWilliams\wsl_backup\ubuntu-backup.tar
```

### Restore (Import)

```powershell
# Create parent directory if needed
mkdir C:\WSL

# Import as new distro
wsl --import <NewName> <InstallPath> <TarFilePath>

# Example:
wsl --import Ubuntu-Dev C:\WSL\Ubuntu-Dev "C:\Users\JakeWilliams\wsl_backup\test.tar"
```

### Run a Specific Distro

```powershell
wsl -d Ubuntu-Dev
```

### Set Default Distro

```powershell
wsl --set-default Ubuntu-Dev
```

### Remove a Distro

```powershell
wsl --unregister Ubuntu-Dev
```

**Warning**: This permanently deletes the distro and its files.

### Change Default User

From PowerShell:
```powershell
ubuntu config --default-user admin
```

Or edit `/etc/wsl.conf` inside WSL (as root):
```bash
[user]
default=admin
```

Then restart:
```powershell
wsl --shutdown
wsl
```

### Windows Firewall (for MSSQL from WSL)

```powershell
# Allow MSSQL port
New-NetFirewallRule -DisplayName "MSSQL DEV01" -Direction Inbound -Protocol TCP -LocalPort 49694 -Action Allow
```

### Find Windows IP from WSL

```bash
cat /etc/resolv.conf | grep nameserver
```

### Test Port Connectivity

```bash
nc -zv <ip> <port>
```

---

## Troubleshooting

### "runsql: command not found"

1. Close and reopen terminal
2. Check pip install succeeded: `pip show ibs_compilers`
3. Add Python user scripts to PATH (Windows):
   ```powershell
   [Environment]::SetEnvironmentVariable(
       "PATH",
       [Environment]::GetEnvironmentVariable("PATH", "User") + ";C:\Users\<YourName>\AppData\Roaming\Python\Python314\Scripts",
       "User"
   )
   ```
4. Restart PowerShell after PATH changes

### "freebcp: command not found" or "tsql: command not found"

**Windows:**
1. Verify MSYS2 FreeTDS is installed: Open UCRT64 terminal, run `pacman -Q mingw-w64-ucrt-x86_64-freetds`
2. Verify PATH includes: `C:\msys64\ucrt64\bin`
3. Restart terminal after PATH changes

**Linux:**
```bash
sudo apt install freetds-bin
```

### "No module named 'pyodbc'"

```bash
pip install pyodbc
# Or reinstall
cd C:\Users\JakeWilliams\Projects\compilers\src
pip install -e .
```

### "Data source name not found"

Check FreeTDS configuration:
```bash
# Verify freetds.conf has your server defined
tsql -C    # Shows config file location
```

### "Company option file missing: options.101"

1. Verify `IR` path in settings.json
2. Check file exists: `ls C:\_innovative\_source\current.sql\CSS\Setup\options.101`
3. Verify CMPY number is correct

### Connection timeout / "Unable to connect"

```bash
# Test connectivity
python test_connection.py --platform SYBASE --host <ip> --port <port> --username <user> --password <pass>

# Ping server
ping <ip>

# Check port (Windows)
Test-NetConnection -ComputerName <ip> -Port <port>

# Check port (Linux)
nc -zv <ip> <port>
```

### "Placeholder &dbtbl& not resolved"

1. Check option files exist in `CSS\Setup\`
2. Clear cache:
   ```bash
   # Windows
   del %TEMP%\options.*.tmp

   # Linux
   rm /tmp/options.*.tmp
   ```
3. Run with preview: `runsql script.sql GONZO --preview`

### Permission Denied

1. Verify user has appropriate database permissions
2. For stored procedures: Need CREATE PROCEDURE permission
3. For tables: Need CREATE TABLE, ALTER TABLE permissions
4. Contact DBA for elevated permissions

### Debug Mode

```bash
# Windows
set LOG_LEVEL=DEBUG
runsql <file> <profile>

# Linux
export LOG_LEVEL=DEBUG
runsql <file> <profile>
```

---

## Quick Reference

| Task | Command |
|------|---------|
| **Installation** | |
| Run installer | `.\bootstrap.ps1` (Windows) |
| Create profile | `python setup_profile.py` |
| **Python Scripts** | |
| Compile SQL | `runsql <file> <profile>` |
| Preview SQL | `runsql <file> <profile> --preview` |
| With audit trail | `runsql <file> <profile> --changelog` |
| Sequences | `runsql <file> <profile> -F 1 -L 10` |
| Quick query | `isqlline "SELECT ..." <profile>` |
| Edit options | `eopt <profile>` |
| Edit table locations | `eloc <profile>` |
| BCP export | `bcp_data out <profile>` |
| BCP import | `bcp_data in <profile>` |
| **FreeTDS** | |
| Check version | `tsql -C` |
| Test connection | `tsql -S <server> -U <user> -P <pass>` |
| BCP export | `freebcp db..table out file.txt -S server -U user -P pass -c` |
| BCP import | `freebcp db..table in file.txt -S server -U user -P pass -c` |
| **WSL** | |
| Backup distro | `wsl --export Ubuntu backup.tar` |
| Restore distro | `wsl --import Name Path backup.tar` |
| List distros | `wsl --list --verbose` |
| **Troubleshooting** | |
| Verify installation | `runsql --help` |
| Check FreeTDS config | `tsql -C` |
| Clear cache (Windows) | `del %TEMP%\options.*.tmp` |
| Clear cache (Linux) | `rm /tmp/options.*.tmp` |
| Enable debug | `set LOG_LEVEL=DEBUG` (Win) / `export LOG_LEVEL=DEBUG` (Linux) |
| **VS Code Shortcuts** | |
| Open terminal | `` Ctrl+` `` |
| Compile current file | `Ctrl+Shift+C` (after setup) |
| Preview current file | `Ctrl+Shift+P` (after setup) |

---

## For Developers

### Development Installation

```bash
cd python-scripts/src
pip install -e .
```

Changes to Python files are immediately reflected without reinstalling.

### Running Scripts Directly

```bash
# Run without installation
python commands/runsql.py script.sql GONZO

# With Python 3
python3 commands/runsql.py script.sql GONZO
```

### Project Structure

```
python-scripts/
├── src/
│   ├── pyproject.toml           # Package configuration
│   ├── settings.json            # User profiles (created at runtime)
│   ├── ibs_common.py            # Shared library
│   ├── options.py               # Soft-compiler (placeholder resolution)
│   ├── change_log.py            # Audit trail logging
│   ├── install.py               # Automated installer
│   ├── setup_profile.py         # Profile wizard
│   ├── test_connection.py       # Connection tester
│   └── commands/
│       ├── runsql.py            # Main SQL compiler
│       ├── isqlline.py          # Single query executor
│       ├── eopt.py              # Options editor
│       ├── eloc.py              # Table locations editor
│       ├── eact.py              # Actions editor
│       ├── bcp_data.py          # Bulk copy utility
│       └── [other commands...]
├── GETTING_STARTED.md           # Complete installation guide
└── migration_plan.md            # Technical migration details
```

### Key Modules

**`ibs_common.py`** - Core utilities:
- `get_config()` - Load settings.json and parse args
- `get_db_connection()` - Create database connection
- `execute_sql()` - Execute SQL with error handling
- `find_file()` - Locate files with path conversion
- `convert_non_linked_paths()` - Expand symbolic paths

**`options.py`** - Soft-compiler:
- `Options` class - Main placeholder resolution
- `generate_compile_option_file()` - Parse v: and c: options
- `combine_option_files()` - Merge option files
- Supports: v: values, c: conditionals, -> table locations, @sequence@

**`change_log.py`** - Audit logging:
- `ChangeLog` class - Generate audit SQL
- `inject_change_log()` - Prepend audit statements
- Writes to ba_gen_chg_log table

### The `ibs_compilers.egg-info/` Directory

The `src/ibs_compilers.egg-info/` directory is **automatically generated by pip** when you install the package in editable/development mode (`pip install -e src/`).

#### Contents

| File | Purpose |
|------|---------|
| `PKG-INFO` | Package metadata (name, version, author, description) |
| `entry_points.txt` | Console scripts defined in `pyproject.toml` (runsql, eopt, etc.) |
| `requires.txt` | Package dependencies (pyodbc, etc.) |
| `SOURCES.txt` | List of all source files in the package |
| `top_level.txt` | Top-level Python modules/packages |
| `dependency_links.txt` | Legacy field (usually empty) |

#### Key Points

- **Auto-generated** - Don't edit these files manually
- **Recreated** on each `pip install -e`
- **Can be deleted** - Will regenerate on next install
- **Should be in `.gitignore`** - Not committed to version control

This is how pip tracks that the package is installed and where to find the console script entry points (like `runsql`, `eopt`, `isqlline`).

### Testing

```bash
# Test connection
python test_connection.py --platform SYBASE --host localhost --port 5000

# Test with preview mode
runsql test_script.sql GONZO --preview

# Test options resolution
python -c "from options import Options; opts = Options({'CMPY': 101, 'IR': '.', 'PLATFORM': 'SYBASE', 'PROFILE_NAME': 'TEST'}); opts.generate_option_files()"
```

---

## Support

**Having issues?**

1. Check this README's [Troubleshooting](#troubleshooting) section
2. Read `GETTING_STARTED.md` for detailed setup instructions
3. Run diagnostic: `python test_connection.py --check-drivers`
4. Enable debug logging: `set LOG_LEVEL=DEBUG` (Windows) or `export LOG_LEVEL=DEBUG` (Linux)

---

**Version:** 1.0.0
**Python:** 3.8+
**Platforms:** Windows, Linux, macOS
**Databases:** Sybase ASE 15.5+, Microsoft SQL Server 2012+
