# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This project contains Python scripts that replace the C# IBS Compilers (`Ibs.Compilers`). The tools compile and deploy SQL objects to Sybase ASE and MSSQL databases.

**Cross-Platform Requirement**: The compilers must work on **Windows, macOS, and Linux**. However, **Windows is the current focus** - macOS and Linux support will follow once Windows is complete and tested.

All connection and compile settings are stored in `src/settings.json`.

Do not read `CHEAT_SHEET.md` for context. This is only used by an end-user. Ignore this file unless explicitly told to update the file.


### Development Environment (Windows - Current Focus)

**Windows**: FreeTDS via MSYS2 (`tsql`, `freebcp` in `C:\msys64\ucrt64\bin`)

The python scripts use `src/settings.json` as the single-source-of-truth, syncing to `freetds.conf` as needed.

**Future platforms** (not yet implemented):
- macOS: FreeTDS via Homebrew
- Linux/WSL: FreeTDS via apt

### Project Structure
See `IMPLEMENTATION_ROADMAP.md`

### C# Reference Implementation

The original C# compilers are at: `C:\_innovative\_source\sbn-services\Ibs.Compilers`

Key reference files:
- `ibsCompilerCommon/options.cs` - Options class (soft-compiler logic)
- `ibsCompilerCommon/common.cs` - Core utilities

## Architecture

### Python Compiler Tools (src/)

| Command | Description |
|---------|-------------|
| `runsql` | Execute SQL scripts with placeholder resolution |
| `runcreate` | Orchestrate master build scripts |
| `eopt` | Edit and compile options |
| `eloc` | Edit and compile table locations |
| `eact` | Edit and compile actions |
| `bcp_data` | Bulk copy data in/out |
| `isqlline` | Execute single SQL commands |

### Database Connectivity Stack

**Unified across Windows, MAC, Linux:**
- **FreeTDS**: TDS protocol library for Sybase ASE and MSSQL connections
- **freebcp**: Bulk copy utility (replaces platform-specific `bcp` tools)
- **tsql**: Connection testing utility
- **pyodbc**: Python database driver (uses FreeTDS ODBC driver)

Configuration: `freetds.conf` (same format on all platforms)

### Connection Testing Tools
- `python3 test_connection.py` - Python connection testing
- `tsql`: Direct TDS connection testing (FreeTDS)
- `isql`: ODBC connection testing (unixODBC)

## Environment Setup (Windows)

Run `.\install\bootstrap.ps1` which:
1. Installs/verifies Python 3.8+
2. Launches `.\install\installer.py`

The installer handles MSYS2, FreeTDS, Python packages, and configuration. 


## Key Configuration Paths (Windows)

| File | Path |
|------|------|
| Python settings | `src/settings.json` |
| FreeTDS config | `C:\msys64\ucrt64\etc\freetds.conf` |