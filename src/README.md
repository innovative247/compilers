# IBS Compilers - Python Toolchain

**Cross-platform command-line tools for managing IBS databases (Sybase ASE and Microsoft SQL Server)**

---

## 🚀 5-Minute Quick Start

**For users with VS Code and `current.sql` open:**

### Step 1: Install Python (if not already installed)

**Check if installed:**
```bash
python --version   # or python3 --version
```

**If not installed:**
- **Windows**: [Download from python.org](https://www.python.org/downloads/) - **Check "Add Python to PATH"**
- **Linux**: `sudo apt install python3 python3-pip`

### Step 2: Install IBS Compilers

```bash
# Navigate to installation directory
cd C:\_innovative\_source\sbn-services\Ibs.Compilers\python-scripts\src  # Windows
cd ~/innovative/sbn-services/Ibs.Compilers/python-scripts/src            # Linux

# Run automated installer
python install.py
```

The installer will:
- ✓ Verify Python version
- ✓ Install dependencies (pyodbc)
- ✓ Check database drivers
- ✓ Create command-line tools
- ✓ Guide you through first profile setup

### Step 3: Install Database Drivers

#### For Microsoft SQL Server:

**Windows**: [Download ODBC Driver 17 for SQL Server](https://docs.microsoft.com/en-us/sql/connect/odbc/download-odbc-driver-for-sql-server)

**Linux (Debian/Ubuntu)**:
```bash
curl https://packages.microsoft.com/keys/microsoft.asc | sudo apt-key add -
curl https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/prod.list | sudo tee /etc/apt/sources.list.d/mssql-release.list
sudo apt-get update
sudo ACCEPT_EULA=Y apt-get install -y msodbcsql17
```

**Linux (RedHat/CentOS)**:
```bash
curl https://packages.microsoft.com/config/rhel/$(rpm -E %rhel)/prod.repo | sudo tee /etc/yum.repos.d/mssql-release.repo
sudo ACCEPT_EULA=Y yum install -y msodbcsql17
```

#### For Sybase ASE:

Install the SAP ASE ODBC driver from your Sybase client installation or SAP Support Portal. Contact your DBA if needed.

**Verify drivers:**
```bash
python test_connection.py --check-drivers
```

### Step 4: Create Your First Profile

```bash
python setup_profile.py
```

Follow the prompts to create a profile for your server (e.g., GONZO).

### Step 5: Compile Your First SQL File!

```bash
# From VS Code terminal (Ctrl+` or Cmd+`)
runsql CSS/SQL_Sources/Basics/pro_users.sql GONZO

# Preview mode (see SQL without executing)
runsql CSS/SQL_Sources/Basics/pro_users.sql GONZO --preview
```

**🎉 Done! You're now ready to compile SQL files.**

---

## 📖 Table of Contents

- [Quick Start](#-5-minute-quick-start)
- [Installation Details](#installation)
- [Configuration](#configuration)
- [Command Reference](#command-reference)
- [Common Workflows](#common-workflows)
- [VS Code Integration](#vs-code-integration)
- [Troubleshooting](#troubleshooting)
- [Cheat Sheet](#cheat-sheet)
- [For Developers](#for-developers)

---

## Disclaimer

**⚠️ This tool interacts directly with live databases and performs destructive operations** (truncating tables, dropping tables, bulk data insertion, running arbitrary SQL).

**Before using:**
- ✓ Always know which server and database you are targeting
- ✓ Always double-check the profile you are using
- ✓ Test against development/test databases before production
- ✓ Use `--preview` mode to see what will be executed

The development team is not responsible for data loss or corruption.

---

## Installation

### Prerequisites

1. **Python 3.8 or newer**
   - Windows: [python.org](https://www.python.org/downloads/) (**Check "Add Python to PATH"**)
   - Linux: `sudo apt install python3 python3-pip` or equivalent

2. **Database ODBC Drivers** (see Quick Start above)
   - MSSQL: Microsoft ODBC Driver 17 for SQL Server
   - Sybase: SAP ASE ODBC driver

3. **Database Command-Line Tools** (for BCP operations)
   - MSSQL: `bcp` and `sqlcmd`
   - Sybase: `bcp` and `isql`

### Installation Steps

#### Automated Installation (Recommended)

```bash
# Navigate to installation directory
cd python-scripts/src

# Run installer
python install.py
```

#### Manual Installation

```bash
# Navigate to src directory
cd python-scripts/src

# Install in editable mode
pip install -e .
# or on some systems:
python3 -m pip install -e .
```

**Verify installation:**
```bash
runsql --help
isqlline --help
```

If commands not found, close and reopen your terminal.

---

## Configuration

### Profile Setup

Configuration is managed through `settings.json` in your project root (where you opened VS Code).

**Create your first profile:**
```bash
python setup_profile.py
```

**Example `settings.json`:**
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
bcp_data out GONZO      # Export all data
bcp_data in GONZO       # Import all data
bcp_data in GONZO --truncate-tables  # Import with truncation
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
- Press `` Ctrl+` `` (backtick) or View → Terminal

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
4. Or: Press `Ctrl+Shift+P` → "Run Task" → Select task

---

## Troubleshooting

### Command Not Found

**Symptom:** `runsql: command not found` or `bash: runsql: command not found`

**Solutions:**
1. **Close and reopen your terminal** (most common fix)
2. Verify installation completed:
   ```bash
   pip show pyodbc
   ```
3. Check Python Scripts directory is in PATH:
   - Windows: `C:\Users\<YourName>\AppData\Local\Programs\Python\Python3XX\Scripts`
   - Linux: `/usr/local/bin` or `~/.local/bin`

### No Module Named 'pyodbc'

**Symptom:** `ModuleNotFoundError: No module named 'pyodbc'`

**Solution:**
```bash
pip install pyodbc
# or
python -m pip install pyodbc
```

### Data Source Name Not Found

**Symptom:** `Data source name not found and no default driver specified`

**Solutions:**
1. **Check drivers are installed:**
   ```bash
   python test_connection.py --check-drivers
   ```

2. **Windows - Check ODBC drivers:**
   ```powershell
   Get-OdbcDriver | Where-Object {$_.Name -like "*SQL*"}
   ```

3. **Linux - Check ODBC drivers:**
   ```bash
   odbcinst -q -d
   ```

4. **Reinstall drivers** (see Installation section)

### Company Option File Missing

**Symptom:** `Company option file missing: options.101`

**Solutions:**
1. **Check IR path in settings.json:**
   ```bash
   cat settings.json
   # Look for "IR" field
   ```

2. **Verify option files exist:**
   ```bash
   ls C:\_innovative\_source\current.sql\CSS\Setup/options.101
   # or
   ls ~/innovative/current.sql/CSS/Setup/options.101
   ```

3. **Verify CMPY number is correct** in settings.json

### Connection Timeout

**Symptom:** Connection timeout or "Unable to connect"

**Solutions:**
1. **Test connection:**
   ```bash
   python test_connection.py --platform SYBASE --host 10.10.123.4 --port 5000 --username sa
   ```

2. **Check network:**
   ```bash
   ping 10.10.123.4
   ```

3. **Check port is open:**
   ```bash
   # Windows
   Test-NetConnection -ComputerName 10.10.123.4 -Port 5000

   # Linux
   nc -zv 10.10.123.4 5000
   ```

4. **Check VPN** - May be required for remote servers

5. **Verify credentials** - Test with SSMS (MSSQL) or Sybase Central

### Placeholder Not Resolved

**Symptom:** SQL contains `&dbtbl&` or other placeholders after compilation

**Solutions:**
1. **Check option files exist:**
   ```bash
   ls CSS/Setup/options.*
   ls CSS/Setup/table_locations
   ```

2. **Clear cache files:**
   ```bash
   # Windows
   del %TEMP%\options.*.tmp

   # Linux
   rm /tmp/options.*.tmp
   ```

3. **Run in preview mode to diagnose:**
   ```bash
   runsql script.sql GONZO --preview
   ```

4. **Check for malformed option files** - Open in text editor

### Permission Denied

**Symptom:** Permission denied when writing to database

**Solutions:**
1. Verify user has appropriate database permissions
2. For stored procedures: Need CREATE PROCEDURE permission
3. For tables: Need CREATE TABLE, ALTER TABLE permissions
4. Contact DBA for elevated permissions

### Debug Mode

**Enable detailed logging:**
```bash
# Windows
set LOG_LEVEL=DEBUG
runsql script.sql GONZO

# Linux
export LOG_LEVEL=DEBUG
runsql script.sql GONZO
```

---

## Cheat Sheet

### Installation (One-Time)
```bash
cd python-scripts/src
python install.py
python setup_profile.py
```

### Basic Commands
```bash
runsql <file> <profile>                    # Compile SQL
runsql <file> <profile> --preview          # Preview only
runsql <file> <profile> --changelog        # With audit trail
runsql <file> <profile> -F 1 -L 10         # Sequences 1-10
isqlline "SELECT @@version" <profile>      # Quick query
eopt <profile>                             # Edit options
eloc <profile>                             # Edit table locations
bcp_data out <profile>                     # Export data
bcp_data in <profile>                      # Import data
```

### Profile Management
```bash
python setup_profile.py                    # Create/edit profiles
python test_connection.py --check-drivers  # Check drivers
python test_connection.py --platform SYBASE --host <ip> --port 5000  # Test connection
cat settings.json                          # View profiles (Linux)
type settings.json                         # View profiles (Windows)
```

### Troubleshooting
```bash
runsql --help                              # Verify installation
python test_connection.py --check-drivers  # Check drivers
rm %TEMP%\options.*.tmp                    # Clear cache (Windows)
rm /tmp/options.*.tmp                      # Clear cache (Linux)
set LOG_LEVEL=DEBUG                        # Enable debug (Windows)
export LOG_LEVEL=DEBUG                     # Enable debug (Linux)
```

### VS Code Shortcuts
- `` Ctrl+` `` - Open terminal
- `Ctrl+Shift+C` - Compile current file (after setup)
- `Ctrl+Shift+P` - Preview current file (after setup)

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

## Additional Resources

- **Complete Installation Guide**: `../GETTING_STARTED.md`
- **Migration Plan**: `../migration_plan.md`
- **Gap Analysis**: `../GAP_ANALYSIS.md`
- **Project Overview**: `../../CLAUDE.md`

---

## Support

**Having issues?**

1. Check this README's [Troubleshooting](#troubleshooting) section
2. Read `GETTING_STARTED.md` for detailed setup instructions
3. Run diagnostic: `python test_connection.py --check-drivers`
4. Enable debug logging: `set LOG_LEVEL=DEBUG` (Windows) or `export LOG_LEVEL=DEBUG` (Linux)
5. Contact: [team-email] or #ibs-compilers Slack channel

---

**Version:** 1.0.0
**Python:** 3.8+
**Platforms:** Windows, Linux, macOS
**Databases:** Sybase ASE 15.5+, Microsoft SQL Server 2012+

🚀 **Happy Compiling!**
