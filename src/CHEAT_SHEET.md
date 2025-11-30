# IBS Compilers & Database Connectivity Cheatsheet

## Table of Contents

**Setup**
- [Getting Started - Windows](#getting-started---windows)
- [Getting Started - Linux/WSL](#getting-started---linuxwsl)
- [Configuration](#configuration)

**Usage**
- [Daily Commands](#daily-commands)
- [FreeTDS Reference](#freetds-reference)

**Reference**
- [WSL Management](#wsl-management)
- [Troubleshooting](#troubleshooting)
- [Quick Reference](#quick-reference)
- [Developer Notes](#developer-notes)

---

## Overview

Python scripts that replace the C# `Ibs.Compilers`. Works identically on **Windows** and **Linux/WSL**:
- **FreeTDS** provides unified database connectivity (Sybase ASE + MSSQL)
- **settings.json** stores all connection and compile settings
- Same commands (`runsql`, `freebcp`, `tsql`) work on both platforms

---

## Getting Started - Windows

### Automated Setup (Recommended)

Run the bootstrap script in PowerShell. The bootstrap script will walk you through installing all dependencies, and is safe to re-run if needed.

```powershell
.\bootstrap.ps1
```
**Options:**
- `.\bootstrap.ps1 -Force` - Reinstall/reconfigure everything
- `.\bootstrap.ps1 -SkipPython` - Skip Python if already installed
- `.\bootstrap.ps1 -Help` - Show all options
---

### Manual Setup

If you prefer manual installation, follow these steps:

### Step 1: Install MSYS2 (for FreeTDS)

```powershell
# Option A: Via winget
winget install -i MSYS2.MSYS2

# Option B: Download from https://www.msys2.org/
```

### Step 2: Install FreeTDS

Open **MSYS2 UCRT64** terminal (not regular MSYS2):

```bash
pacman -S mingw-w64-ucrt-x86_64-freetds
```

### Step 3: Add to Windows PATH

Add this to your system PATH:
```
C:\msys64\ucrt64\bin
```

### Step 4: Install Python Scripts

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

### Step 5: Configure FreeTDS

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

### Step 6: Verify Installation

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

```bash
# Interactive wizard
python3 setup_profile.py
```

#### settings.json Structure

```json
{
  "Profiles": {
    "GONZO": {
      "CMPY": 101,
      "IBSLANG": 1,
      "IR": "C:\\_innovative\\_source\\current.sql",
      "PLATFORM": "SYBASE",
      "HOST": "10.10.123.4",
      "PORT": 5000,
      "USERNAME": "sa",
      "PASSWORD": "your_encrypted_password"
    }
  }
}
```

---

## Daily Commands

```bash
# Compile SQL file
runsql <file> <profile>

# Preview without executing
runsql <file> <profile> --preview

# With change logging
runsql <file> <profile> --changelog

# With sequences
runsql <file> <profile> -F 1 -L 10

# Compile to specific database
runsql script.sql GONZO -D sbnwork

# Quick query
isqlline "SELECT @@version" <profile>
isqlline "SELECT TOP 10 * FROM sbnmaster..users" GONZO
isqlline "SELECT * FROM sbnmaster..users" GONZO -O users.txt
```

### All Commands

| Command | Description |
|---------|-------------|
| `eopt <profile>` | Interactive option editor |
| `eopt <profile> -d` | Developer mode (merge with options.def) |
| `eloc <profile>` | Edit table locations |
| `eact <profile>` | Edit actions compiler |
| `bcp_data out <profile>` | BCP data export |
| `bcp_data in <profile> --truncate-tables` | BCP data import |
| `runcreate CSS/Setup/create_tbl <profile>` | Run master build script |

### VS Code Integration

#### .vscode/tasks.json

```json
{
  "version": "2.0.0",
  "tasks": [
    {
      "label": "Compile SQL (GONZO)",
      "type": "shell",
      "command": "runsql",
      "args": ["${file}", "GONZO"],
      "problemMatcher": []
    },
    {
      "label": "Compile SQL (Preview)",
      "type": "shell",
      "command": "runsql",
      "args": ["${file}", "GONZO", "--preview"],
      "problemMatcher": []
    }
  ]
}
```

#### Keyboard Shortcuts (.vscode/keybindings.json)

```json
[
  {"key": "ctrl+shift+c", "command": "workbench.action.tasks.runTask", "args": "Compile SQL (GONZO)"},
  {"key": "ctrl+shift+p", "command": "workbench.action.tasks.runTask", "args": "Compile SQL (Preview)"}
]
```

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

## Windows Firewall (for MSSQL from WSL)

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
python3 test_connection.py --platform SYBASE --host <ip> --port <port> --username <user> --password <pass>

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
| **Python Scripts** | |
| Compile SQL | `runsql <file> <profile>` |
| Preview SQL | `runsql <file> <profile> --preview` |
| Quick query | `isqlline "SELECT ..." <profile>` |
| **FreeTDS** | |
| Check version | `tsql -C` |
| Test connection | `tsql -S <server> -U <user> -P <pass>` |
| BCP export | `freebcp db..table out file.txt -S server -U user -P pass -c` |
| BCP import | `freebcp db..table in file.txt -S server -U user -P pass -c` |
| **WSL** | |
| Backup distro | `wsl --export Ubuntu backup.tar` |
| Restore distro | `wsl --import Name Path backup.tar` |
| List distros | `wsl --list --verbose` |

---

## Developer Notes

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
