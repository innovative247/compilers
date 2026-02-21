# IBS Compilers

Cross-platform .NET 8 tools for compiling and deploying SQL to Sybase ASE and Microsoft SQL Server databases.

Self-contained executables — no runtime or dependencies required. Managed ADO.NET drivers handle all database connectivity.

- **SQL compilation** - Execute SQL scripts with placeholder resolution and sequence support
- **Database builds** - Orchestrate multi-file builds via create scripts
- **Configuration management** - Edit and compile options, table locations, actions, messages
- **Data transfer** - Bulk transfer between databases using managed BCP

---

## Installation

### Online Install

**Windows (PowerShell):**
```powershell
irm https://raw.githubusercontent.com/innovative247/compilers/main/install.ps1 | iex
```

**Linux / macOS / WSL:**
```bash
curl -fsSL https://raw.githubusercontent.com/innovative247/compilers/main/install.sh | bash
```

The installer downloads the latest release, extracts it, and walks you through setup.

### Offline / Manual Install

If the target machine has no internet access:

1. Download the archive for your platform from [GitHub Releases](https://github.com/innovative247/compilers/releases/latest):
   - `compilers-net8-win-x64.zip` (Windows)
   - `compilers-net8-linux-x64.tar.gz` (Linux / WSL)
   - `compilers-net8-osx-x64.tar.gz` (macOS)

2. Transfer the archive to the target machine.

3. Extract to your preferred location:

   **Windows:**
   ```powershell
   Expand-Archive compilers-net8-win-x64.zip -DestinationPath "$env:LOCALAPPDATA\ibs-compilers"
   ```

   **Linux / macOS:**
   ```bash
   mkdir -p ~/ibs-compilers && tar -xzf compilers-net8-linux-x64.tar.gz -C ~/ibs-compilers
   ```

4. Run configure to add to PATH and set up settings.json:
   ```bash
   set_profile configure
   ```
   On first run, use the full path to the executable (e.g., `~/ibs-compilers/set_profile configure`).

### After Install

Every command supports the subcommands `configure`, `version`, `update`, and `install`. You can use any command interchangeably for these operations (e.g., `runsql version`, `set_profile update`, `isqlline configure` all work).

```bash
set_profile configure    # Add to PATH, verify environment
set_profile              # Configure database connections
set_profile version      # Verify installation
set_profile update       # Download and install latest release
```

On machines without internet, download the new archive and extract over the existing installation.

---

## Environment Troubleshooting

Having trouble with your development environment (terminal, Node.js, Git, SVN, WSL, etc.)?

- [Windows Server 2022](windows-setup.md)
- [WSL Ubuntu](ubuntu-setup.md)

---

## Commands

| Command | Aliases | Description |
|---------|---------|-------------|
| `set_profile` | | Interactive profile configuration wizard |
| `isqlline` | | Execute a single SQL command |
| `runsql` | | Execute SQL scripts with placeholder resolution and sequences |
| `runcreate` | | Orchestrate multi-file database builds (supports `-bg` to run in background) |
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
| `iwatch` | | Follow a log file live (`tail -f` on Linux/macOS, `Get-Content -Wait` on Windows) |

### Common Subcommands

Every command supports these subcommands:

| Subcommand | Description |
|------------|-------------|
| `version` | Print version and exit |
| `update` / `install` | Download and install latest release |
| `configure` | Show configuration status, add to PATH |

### Background Builds + Live Log (runcreate + iwatch)

Run a database build in the background and watch the log live in the same terminal session:

```bash
runcreate create_all G gonzo.log -bg    # launches in background, prints PID
iwatch gonzo.log.out                    # follows the log live; waits if file not yet created
```

When a log file is specified, `runcreate` automatically creates two files:
- `gonzo.log.out` — all output (every script start, result, and elapsed time)
- `gonzo.log.err` — only the sections for scripts that failed (quick failure summary)

`iwatch` waits up to 5 seconds for the file to appear, then tails it until the process exits. On Windows it uses `Get-Content -Wait`; on Linux/macOS it uses `tail -f`.

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

## MSSQL Initialization Script (SQLCMDINI)

When connecting to Microsoft SQL Server, the compilers check for the `SQLCMDINI` environment variable. If it points to a SQL file, that file is executed at the start of every connection — before any compilation runs. This matches `sqlcmd.exe` behavior.

This is useful for setting session options that make MSSQL behave consistently with Sybase:

**Example file** (`mssql_setup.sql`):
```sql
SET ARITHABORT               ON
SET ANSI_NULL_DFLT_ON        OFF
SET ANSI_NULL_DFLT_OFF       OFF
SET CONCAT_NULL_YIELDS_NULL  OFF
SET ANSI_WARNINGS            ON
SET QUOTED_IDENTIFIER        OFF
```

**Set the environment variable:**

Windows:
```powershell
[Environment]::SetEnvironmentVariable("SQLCMDINI", "C:\path\to\mssql_setup.sql", "User")
```

Linux / macOS:
```bash
echo 'export SQLCMDINI="$HOME/mssql_setup.sql"' >> ~/.bashrc
source ~/.bashrc
```

---

## Troubleshooting

### Command not found

Run `set_profile configure` to automatically add the install directory to your PATH. If that doesn't work, add it manually:

**Windows (PowerShell):**
```powershell
# Add to user PATH permanently
[Environment]::SetEnvironmentVariable("PATH", "$env:LOCALAPPDATA\ibs-compilers;" + [Environment]::GetEnvironmentVariable("PATH", "User"), "User")
```
Restart your terminal for the change to take effect.

**Linux/macOS:**
```bash
echo 'export PATH="$HOME/ibs-compilers:$PATH"' >> ~/.bashrc
source ~/.bashrc && hash -r
```
For zsh, use `~/.zshrc` instead of `~/.bashrc`.

If commands were previously found but now fail, bash may have cached an old location. Run `hash -r` to clear it.

### Command not found after self-update

On Linux/macOS, if commands fail with "Permission denied" after running `update`, the new files may not be executable. Fix with:
```bash
chmod +x ~/ibs-compilers/*
```

### Connection failed

Check your profile settings with `set_profile`, then test connectivity:
```bash
isqlline "SELECT 1" master PROFILE_NAME
```

### MSSQL: QUOTED_IDENTIFIER or SET option errors

Some MSSQL databases require specific session settings. Set the `SQLCMDINI` environment variable to point to a SQL file that runs at the start of every connection:

**Windows:**
```powershell
[Environment]::SetEnvironmentVariable("SQLCMDINI", "C:\path\to\mssql_setup.sql", "User")
```

**Linux/macOS:**
```bash
echo 'export SQLCMDINI="$HOME/mssql_setup.sql"' >> ~/.bashrc
source ~/.bashrc
```

See the [MSSQL Initialization Script](#mssql-initialization-script-sqlcmdini) section for example SQL settings.

### Clipboard not working over RDP (Windows Server)

```cmd
taskkill /f /im rdpclip.exe && rdpclip.exe
```

### Removing Python compilers (v1.x)

The installer detects and offers to remove the old Python `ibs_compilers` package automatically. If you need to clean up manually:

**1. Uninstall the pip package:**
```bash
python -m pip uninstall ibs_compilers -y
```

**2. Remove leftover entry points** (executables like `runsql`, `set_profile`, etc.) from Python Scripts directories:

**Windows:** Check `%APPDATA%\Python\Python*\Scripts\` and `%LOCALAPPDATA%\Programs\Python\Python*\Scripts\`

**Linux/macOS:** Check `~/.local/bin/`

**3. Remove editable install leftovers** that `pip uninstall` misses:
```bash
# From your Python site-packages directory:
rm -f __editable__.ibs_compilers-*.pth
rm -f __editable___ibs_compilers_*_finder.py
rm -rf ibs_compilers-*.dist-info
```

**4. Clean PATH:** Remove any Python Scripts directories from your PATH that were only there for the compilers.

**5. Clear bash hash cache** (Linux/macOS):
```bash
hash -r
```

---

## Project Structure

```
compilers/
├── install.ps1              # Windows installer (irm | iex)
├── install.sh               # Linux/macOS installer (curl | bash)
├── publish.ps1              # Build and package all platforms
├── readme.md
├── settings.json.example    # Template configuration
├── ubuntu-setup.md          # WSL/Ubuntu environment setup guide
├── windows-setup.md         # Windows Server environment setup guide
└── src/
    ├── Directory.Build.props    # Shared version (all projects)
    ├── Compilers.sln
    ├── AdoNetCore.AseClient.dll # Sybase managed ADO.NET driver
    ├── ibsCompiler/             # Shared library
    │   ├── Configuration/       # ProfileManager, ProfileData
    │   ├── Database/            # ISqlExecutor, MssqlExecutor, SybaseExecutor
    │   ├── TransferData/        # Bulk transfer engine (menu, wizard, runner)
    │   ├── ConfigureCommand.cs  # PATH setup, environment status
    │   ├── VersionCheck.cs      # Daily update check, self-update
    │   ├── VersionInfo.cs       # Runtime version accessor
    │   └── ...                  # Command logic (Runsql, Isqlline, etc.)
    ├── runsql/                  # Entry point projects (21 total)
    ├── isqlline/
    ├── set_profile/
    ├── transfer_data/
    ├── bcp_data/
    └── ...
```
