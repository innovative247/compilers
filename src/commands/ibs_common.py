"""
ibs_common.py: Shared library for the IBS Compiler Python toolchain.

This module consolidates core, reusable functions and classes for:
- Configuration management (loading settings.json, profile selection, placeholder replacement).
- Database interactions (pyodbc connections, SQL execution, stored procedures).
- External process wrappers (BCP utility).
- File system utilities (finding files, temporary file management).
- User interface interactions (console prompts, logging).
- System health checks (verifying external tool dependencies).
"""

import argparse
import os
import sys
import json
import subprocess
import pyodbc
import shutil
import logging
from pathlib import Path
import tempfile
import datetime
import re
import getpass
import signal
import platform

# =============================================================================
# TERMINAL STYLING (Cross-platform colors and icons)
# =============================================================================
# Uses colorama for Windows compatibility. Works on Windows 10+, Mac, Ubuntu.

# Fix Windows console encoding for UTF-8 output
if sys.platform == 'win32':
    # Try to set UTF-8 mode for Windows console
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
        sys.stderr.reconfigure(encoding='utf-8', errors='replace')
    except (AttributeError, OSError):
        pass  # Older Python or non-reconfigurable stream

try:
    from colorama import init as colorama_init, Fore, Style
    colorama_init()  # Required for Windows ANSI support
    _HAS_COLOR = True
except ImportError:
    _HAS_COLOR = False
    # Fallback: define empty color codes
    class Fore:
        GREEN = RED = YELLOW = CYAN = BLUE = MAGENTA = WHITE = ""
        LIGHTGREEN_EX = LIGHTRED_EX = LIGHTYELLOW_EX = LIGHTCYAN_EX = ""
        LIGHTBLUE_EX = LIGHTMAGENTA_EX = LIGHTWHITE_EX = ""
    class Style:
        RESET_ALL = BRIGHT = DIM = ""


def _supports_unicode() -> bool:
    """Check if the terminal supports Unicode output."""
    try:
        # Try to encode a test character
        encoding = getattr(sys.stdout, 'encoding', 'ascii') or 'ascii'
        '✓'.encode(encoding)
        return True
    except (UnicodeEncodeError, LookupError):
        return False


# Icons - use Unicode if supported, ASCII fallbacks otherwise
class Icons:
    """Terminal icons with automatic ASCII fallback for legacy consoles."""
    _USE_UNICODE = _supports_unicode()

    # Unicode icons with ASCII fallbacks
    SUCCESS = "✓" if _USE_UNICODE else "[OK]"
    ERROR = "✗" if _USE_UNICODE else "[X]"
    WARNING = "⚠" if _USE_UNICODE else "[!]"
    INFO = "ℹ" if _USE_UNICODE else "[i]"
    ARROW = "→" if _USE_UNICODE else "->"
    BULLET = "•" if _USE_UNICODE else "*"
    RUNNING = "⚡" if _USE_UNICODE else "[~]"
    FOLDER = "📁" if _USE_UNICODE else "[D]"
    FILE = "📄" if _USE_UNICODE else "[F]"
    DATABASE = "🗄" if _USE_UNICODE else "[DB]"
    GEAR = "⚙" if _USE_UNICODE else "[*]"
    CLOCK = "⏱" if _USE_UNICODE else "[T]"
    CHECK = "☑" if _USE_UNICODE else "[x]"
    UNCHECK = "☐" if _USE_UNICODE else "[ ]"
    STAR = "★" if _USE_UNICODE else "[*]"


def style_success(text: str) -> str:
    """Format text as success (green with checkmark)."""
    return f"{Fore.GREEN}{Icons.SUCCESS} {text}{Style.RESET_ALL}"


def style_error(text: str) -> str:
    """Format text as error (red with X)."""
    return f"{Fore.RED}{Icons.ERROR} {text}{Style.RESET_ALL}"


def style_warning(text: str) -> str:
    """Format text as warning (yellow with warning icon)."""
    return f"{Fore.YELLOW}{Icons.WARNING} {text}{Style.RESET_ALL}"


def style_info(text: str) -> str:
    """Format text as info (cyan with info icon)."""
    return f"{Fore.CYAN}{Icons.INFO} {text}{Style.RESET_ALL}"


def style_running(text: str) -> str:
    """Format text as running/in-progress (yellow with lightning)."""
    return f"{Fore.YELLOW}{Icons.RUNNING} {text}{Style.RESET_ALL}"


def style_header(text: str, width: int = 70) -> str:
    """Format text as a header with decorative borders."""
    line = "=" * width
    return f"{Fore.CYAN}{line}\n  {text}\n{line}{Style.RESET_ALL}"


def style_subheader(text: str, width: int = 70) -> str:
    """Format text as a subheader with dashed borders."""
    line = "-" * width
    return f"{Fore.CYAN}{line}\n  {text}\n{line}{Style.RESET_ALL}"


def style_step(step_num: int, text: str) -> str:
    """Format text as a numbered step."""
    return f"{Fore.CYAN}{Style.BRIGHT}STEP {step_num}:{Style.RESET_ALL} {text}"


def style_path(path: str) -> str:
    """Format a file path with folder icon."""
    return f"{Icons.FOLDER} {Fore.LIGHTBLUE_EX}{path}{Style.RESET_ALL}"


def style_database(db_name: str) -> str:
    """Format a database name."""
    return f"{Fore.MAGENTA}{db_name}{Style.RESET_ALL}"


def style_command(cmd: str) -> str:
    """Format a command for display."""
    return f"{Fore.LIGHTWHITE_EX}{Style.BRIGHT}{cmd}{Style.RESET_ALL}"


def style_dim(text: str) -> str:
    """Format text as dimmed/subtle."""
    return f"{Style.DIM}{text}{Style.RESET_ALL}"


def print_success(text: str):
    """Print a success message."""
    print(style_success(text))


def print_error(text: str):
    """Print an error message."""
    print(style_error(text))


def print_warning(text: str):
    """Print a warning message."""
    print(style_warning(text))


def print_info(text: str):
    """Print an info message."""
    print(style_info(text))


def print_header(text: str, width: int = 70):
    """Print a header."""
    print(style_header(text, width))


def print_subheader(text: str, width: int = 70):
    """Print a subheader."""
    print(style_subheader(text, width))


def print_step(step_num: int, text: str):
    """Print a numbered step."""
    print(style_step(step_num, text))


def _handle_interrupt(sig, frame):
    """Handle Ctrl-C gracefully."""
    print("\nCtrl-C")
    sys.exit(1)

signal.signal(signal.SIGINT, _handle_interrupt)

# =============================================================================
# PRE-COMPILED REGEX PATTERNS (Performance optimization)
# =============================================================================
# These patterns are compiled once at module load time instead of on every call

# Pattern for &placeholder& replacement (matches valid names: letters, numbers, _, #, -)
_PLACEHOLDER_PATTERN = re.compile(r'&[a-zA-Z_][a-zA-Z0-9_#-]*&')

# Pattern for 'use <database>' statement detection
_USE_DB_PATTERN = re.compile(r'^\s*use\s+(\w+)\s*$', re.MULTILINE | re.IGNORECASE)

# =============================================================================
# SETTINGS CACHE (Performance optimization)
# =============================================================================
# Cache settings.json at module level to avoid re-reading for every call

_SETTINGS_CACHE = None
_SETTINGS_MTIME = None

# =============================================================================
# CONFIGURATION MANAGEMENT
# =============================================================================

def is_raw_mode(config: dict) -> bool:
    """Check if raw mode is enabled - skips options, symlinks, changelog."""
    return bool(config.get('RAW_MODE', False))

def find_settings_file() -> Path:
    """
    Find settings.json in the project root directory.

    The project structure is:
        compilers/                  <- project root (settings.json goes here)
        compilers/src/
        compilers/src/commands/     <- this script's directory

    Returns:
        Path to settings.json (compilers/settings.json)
    """
    script_dir = Path(__file__).parent.resolve()  # src/commands/
    project_root = script_dir.parent.parent        # compilers/
    return project_root / "settings.json"


def open_settings_in_editor():
    """
    Open settings.json using the system's default application.

    Uses native OS commands: start (Windows), open (Mac), xdg-open (Linux)
    """
    settings_path = find_settings_file()

    if not settings_path.exists():
        print_error(f"Settings file not found: {settings_path}")
        return

    print(f"Opening: {settings_path}")

    system = platform.system()
    if system == 'Windows':
        os.startfile(str(settings_path))
    elif system == 'Darwin':  # Mac
        subprocess.run(['open', str(settings_path)])
    else:  # Linux
        subprocess.run(['xdg-open', str(settings_path)])


def load_settings() -> dict:
    """
    Load settings.json and return as dict (cached at module level).

    The file is cached based on modification time to avoid re-reading
    when called multiple times (e.g., runcreate calling runsql repeatedly).

    Returns:
        Dictionary containing settings data with 'Profiles' section

    Raises:
        FileNotFoundError: If settings.json cannot be found
        json.JSONDecodeError: If settings.json is invalid JSON
    """
    global _SETTINGS_CACHE, _SETTINGS_MTIME

    settings_file = find_settings_file()

    if not settings_file.exists():
        logging.warning(f"settings.json not found at {settings_file}. Creating empty settings.")
        return {"Profiles": {}}

    try:
        # Check if cache is still valid (file hasn't changed)
        current_mtime = os.path.getmtime(settings_file)
        if _SETTINGS_CACHE is not None and _SETTINGS_MTIME == current_mtime:
            return _SETTINGS_CACHE

        # Cache miss or file changed - load fresh
        with open(settings_file, 'r', encoding='utf-8') as f:
            settings_data = json.load(f)

        if "Profiles" not in settings_data:
            logging.warning("settings.json missing 'Profiles' section. Adding empty Profiles.")
            settings_data["Profiles"] = {}

        # Update cache
        _SETTINGS_CACHE = settings_data
        _SETTINGS_MTIME = current_mtime

        return settings_data

    except json.JSONDecodeError as e:
        logging.error(f"Invalid JSON in settings.json: {e}")
        raise

    except Exception as e:
        logging.error(f"Could not read settings.json: {e}")
        raise


def save_settings(settings_data: dict) -> bool:
    """
    Save settings dictionary to settings.json with pretty printing.

    Args:
        settings_data: Dictionary to save

    Returns:
        True if successful, False otherwise
    """
    try:
        settings_file = find_settings_file()
        with open(settings_file, 'w', encoding='utf-8') as f:
            json.dump(settings_data, f, indent=2)
        logging.info(f"Successfully updated {settings_file}.")
        return True
    except IOError as e:
        logging.error(f"Failed to write to settings file: {e}")
        return False


def load_profile(profile_name: str) -> dict:
    """
    Load a specific profile from settings.json by name or alias.

    Args:
        profile_name: Name or alias of the profile to load (case-insensitive)

    Returns:
        Dictionary with HOST, PORT, USERNAME, PASSWORD, PLATFORM, etc.

    Raises:
        KeyError: If profile not found in settings.json
        ValueError: If alias conflicts detected or required fields missing
        FileNotFoundError: If settings.json cannot be found
    """
    settings = load_settings()

    # Validate aliases first
    alias_errors = validate_profile_aliases(settings)
    if alias_errors:
        raise ValueError(f"Alias conflicts in settings.json:\n  " + "\n  ".join(alias_errors))

    # Find profile by name or alias
    found_name, profile_data = find_profile_by_name_or_alias(settings, profile_name)

    if found_name is None:
        raise KeyError(f"Profile '{profile_name}' not found in settings.json")

    profile = profile_data.copy()

    # Validate required fields
    required_fields = ["HOST", "PORT", "USERNAME", "PASSWORD", "PLATFORM"]
    missing = [field for field in required_fields if field not in profile]

    if missing:
        raise ValueError(f"Profile '{found_name}' missing required fields: {', '.join(missing)}")

    # Store the canonical profile name (resolves aliases to real name)
    profile['PROFILE_NAME'] = found_name.upper()

    return profile


def save_profile(profile_name: str, profile_data: dict) -> bool:
    """
    Save a profile to settings.json.

    Args:
        profile_name: Name of the profile (will be stored uppercase)
        profile_data: Dictionary containing profile settings

    Returns:
        True if successfully saved, False otherwise
    """
    try:
        settings = load_settings()

        if "Profiles" not in settings:
            settings["Profiles"] = {}

        # Normalize profile name to uppercase
        profile_name = profile_name.upper()

        settings["Profiles"][profile_name] = profile_data

        return save_settings(settings)

    except Exception as e:
        logging.error(f"Failed to save profile '{profile_name}': {e}")
        return False


def list_profiles() -> list:
    """
    Return list of profile names from settings.json.

    Returns:
        List of profile name strings
    """
    try:
        settings = load_settings()
        return list(settings.get("Profiles", {}).keys())
    except Exception as e:
        logging.error(f"Failed to list profiles: {e}")
        return []


def validate_profile_aliases(settings_data: dict) -> list:
    """
    Validate that profile aliases don't conflict with profile names or other aliases.

    Args:
        settings_data: Full settings dictionary with "Profiles" key

    Returns:
        List of error messages (empty list = valid)
    """
    errors = []
    profiles = settings_data.get("Profiles", {})

    # Collect all profile names (uppercase)
    profile_names = {name.upper() for name in profiles.keys()}

    # Collect all aliases with their source profile
    alias_to_profile = {}  # alias -> profile_name

    for profile_name, profile_data in profiles.items():
        profile_upper = profile_name.upper()
        aliases = profile_data.get("ALIASES", [])

        for alias in aliases:
            alias_upper = alias.upper()

            # Check: alias matches own profile name (redundant)
            if alias_upper == profile_upper:
                errors.append(f"Alias '{alias}' is redundant - it matches profile name '{profile_name}'")
                continue

            # Check: alias matches another profile name
            if alias_upper in profile_names:
                errors.append(f"Alias '{alias}' in profile '{profile_name}' conflicts with existing profile name '{alias_upper}'")
                continue

            # Check: alias already used by another profile
            if alias_upper in alias_to_profile:
                other_profile = alias_to_profile[alias_upper]
                errors.append(f"Alias '{alias}' is used by both '{other_profile}' and '{profile_name}'")
                continue

            alias_to_profile[alias_upper] = profile_name

    return errors


def find_profile_by_name_or_alias(settings_data: dict, name: str) -> tuple:
    """
    Find a profile by name or alias.

    Args:
        settings_data: Full settings dictionary with "Profiles" key
        name: Profile name or alias to look up (case-insensitive)

    Returns:
        Tuple of (profile_name, profile_data) if found, or (None, None) if not found
    """
    profiles = settings_data.get("Profiles", {})
    name_upper = name.upper()

    # First try exact profile name match (case-insensitive)
    for profile_name, profile_data in profiles.items():
        if profile_name.upper() == name_upper:
            return profile_name, profile_data

    # Then try alias match
    for profile_name, profile_data in profiles.items():
        aliases = profile_data.get("ALIASES", [])
        for alias in aliases:
            if alias.upper() == name_upper:
                return profile_name, profile_data

    return None, None


def prompt_for_new_profile(profile_name: str) -> dict:
    """
    Interactively prompts the user to create a new profile with HOST/PORT.

    Args:
        profile_name: Suggested name for the profile

    Returns:
        Dictionary with profile configuration
    """
    print(f"\nProfile '{profile_name}' not found. Let's create it.\n")

    new_profile = {}

    # Prompt for PLATFORM
    while True:
        platform = input("Enter PLATFORM (MSSQL or SYBASE): ").upper()
        if platform in ["MSSQL", "SYBASE"]:
            new_profile["PLATFORM"] = platform
            break
        else:
            print("Invalid input. Please enter 'MSSQL' or 'SYBASE'.")

    # Prompt for HOST and PORT (new direct connection approach)
    new_profile["HOST"] = input("Enter database server HOST (IP address or hostname): ").strip()
    if not new_profile["HOST"]:
        print("Error: HOST is required.")
        return {}

    default_port = 1433 if platform == "MSSQL" else 5000
    port_input = input(f"Enter database PORT (default: {default_port}): ").strip()
    new_profile["PORT"] = int(port_input) if port_input else default_port

    # Credentials
    new_profile["USERNAME"] = input("Enter database username: ")
    new_profile["PASSWORD"] = getpass.getpass("Enter database password: ")

    # Company and language
    new_profile["COMPANY"] = input("Enter Company ID (e.g., 101): ")
    new_profile["DEFAULT_LANGUAGE"] = input("Enter Language ID (default: 1): ") or "1"

    # Set sensible defaults for other fields
    new_profile["SQL_SOURCE"] = None

    logging.info(f"New profile '{profile_name}' created.")
    return new_profile


# =============================================================================
# DATA TRANSFER PROJECT MANAGEMENT
# =============================================================================
# These functions manage the "data_transfer" section of settings.json.
# They are completely separate from the "Profiles" section used by set_profile,
# runsql, and other compiler tools.

def load_data_transfer_projects() -> dict:
    """
    Load all data transfer projects from settings.json.

    Returns only the "data_transfer" section, never touching "Profiles".

    Returns:
        Dictionary of project_name -> project_config
    """
    try:
        settings = load_settings()
        return settings.get("data_transfer", {})
    except Exception as e:
        logging.error(f"Failed to load data transfer projects: {e}")
        return {}


def save_data_transfer_project(project_name: str, project_data: dict) -> bool:
    """
    Save a data transfer project to settings.json.

    Only modifies the "data_transfer" section, never touching "Profiles".

    Args:
        project_name: Name of the project (stored as-is, case-sensitive)
        project_data: Project configuration dictionary

    Returns:
        True if successfully saved, False otherwise
    """
    try:
        settings = load_settings()

        if "data_transfer" not in settings:
            settings["data_transfer"] = {}

        settings["data_transfer"][project_name] = project_data

        return save_settings(settings)

    except Exception as e:
        logging.error(f"Failed to save data transfer project '{project_name}': {e}")
        return False


def delete_data_transfer_project(project_name: str) -> bool:
    """
    Delete a data transfer project from settings.json.

    Only modifies the "data_transfer" section, never touching "Profiles".

    Args:
        project_name: Name of the project to delete

    Returns:
        True if successfully deleted, False otherwise
    """
    try:
        settings = load_settings()

        if "data_transfer" not in settings:
            return False

        if project_name not in settings["data_transfer"]:
            logging.warning(f"Data transfer project '{project_name}' not found")
            return False

        del settings["data_transfer"][project_name]

        return save_settings(settings)

    except Exception as e:
        logging.error(f"Failed to delete data transfer project '{project_name}': {e}")
        return False


def list_data_transfer_projects() -> list:
    """
    Return list of data transfer project names from settings.json.

    Returns:
        List of project name strings
    """
    try:
        projects = load_data_transfer_projects()
        return list(projects.keys())
    except Exception as e:
        logging.error(f"Failed to list data transfer projects: {e}")
        return []


def load_data_transfer_project(project_name: str) -> dict:
    """
    Load a specific data transfer project by name.

    Args:
        project_name: Name of the project to load

    Returns:
        Project configuration dictionary, or empty dict if not found
    """
    try:
        projects = load_data_transfer_projects()
        return projects.get(project_name, {})
    except Exception as e:
        logging.error(f"Failed to load data transfer project '{project_name}': {e}")
        return {}


def match_wildcard_pattern(name: str, patterns: list) -> bool:
    """
    Check if a name matches any of the given wildcard patterns.

    Supports simple wildcards:
    - '*' matches any sequence of characters
    - Matching is case-insensitive

    Args:
        name: The name to check (e.g., "sbnmaster", "w#temp")
        patterns: List of patterns (e.g., ["sbn*", "ibs"])

    Returns:
        True if name matches any pattern, False otherwise

    Examples:
        match_wildcard_pattern("sbnmaster", ["sbn*"]) -> True
        match_wildcard_pattern("w#temp", ["w#*"]) -> True
        match_wildcard_pattern("users", ["w#*", "srm_*"]) -> False
    """
    import fnmatch

    name_lower = name.lower()

    for pattern in patterns:
        pattern_lower = pattern.lower()
        if fnmatch.fnmatch(name_lower, pattern_lower):
            return True

    return False


def get_databases_from_server(host: str, port: int, username: str, password: str,
                               platform: str) -> tuple:
    """
    Query available databases from a database server.

    Args:
        host: Database server host
        port: Database server port
        username: Database username
        password: Database password
        platform: Database platform ("SYBASE" or "MSSQL")

    Returns:
        Tuple of (success: bool, databases: list or error_message: str)
        On success: (True, ["sbnmaster", "sbnpro", ...])
        On failure: (False, "Error message")
    """
    # SQL to list databases differs by platform
    if platform.upper() == "SYBASE":
        sql = "select name from master..sysdatabases order by name"
    else:  # MSSQL
        sql = "select name from sys.databases order by name"

    success, output = execute_sql_native(
        host=host,
        port=port,
        username=username,
        password=password,
        database="master",
        platform=platform,
        sql_content=sql
    )

    if not success:
        return False, output

    # Parse output - each line is a database name
    databases = []
    for line in output.strip().split('\n'):
        line = line.strip()
        # Skip empty lines and header lines
        if not line:
            continue
        if line.startswith('---') or line.lower() == 'name':
            continue
        # Skip system messages
        if line.startswith('locale is') or line.startswith('using default'):
            continue
        if 'affected' in line.lower():
            continue
        databases.append(line)

    return True, databases


def get_tables_from_database(host: str, port: int, username: str, password: str,
                              database: str, platform: str) -> tuple:
    """
    Query available tables from a database.

    Args:
        host: Database server host
        port: Database server port
        username: Database username
        password: Database password
        database: Database name to query
        platform: Database platform ("SYBASE" or "MSSQL")

    Returns:
        Tuple of (success: bool, tables: list or error_message: str)
        On success: (True, ["users", "branches", ...])
        On failure: (False, "Error message")
    """
    # SQL to list user tables differs by platform
    if platform.upper() == "SYBASE":
        sql = "select name from sysobjects where type = 'U' order by name"
    else:  # MSSQL
        sql = "select name from sys.tables order by name"

    success, output = execute_sql_native(
        host=host,
        port=port,
        username=username,
        password=password,
        database=database,
        platform=platform,
        sql_content=sql
    )

    if not success:
        return False, output

    # Parse output - each line is a table name
    tables = []
    for line in output.strip().split('\n'):
        line = line.strip()
        # Skip empty lines and header lines
        if not line:
            continue
        if line.startswith('---') or line.lower() == 'name':
            continue
        # Skip system messages
        if line.startswith('locale is') or line.startswith('using default'):
            continue
        if 'affected' in line.lower():
            continue
        tables.append(line)

    return True, tables


def filter_tables_by_patterns(tables: list, include_patterns: list,
                               exclude_patterns: list) -> list:
    """
    Filter a list of tables based on include and exclude patterns.

    Args:
        tables: List of table names
        include_patterns: Patterns for tables to include (e.g., ["*"] for all)
        exclude_patterns: Patterns for tables to exclude (e.g., ["w#*", "srm_*"])

    Returns:
        Filtered list of table names

    Example:
        filter_tables_by_patterns(
            ["users", "branches", "w#temp", "srm_data"],
            include_patterns=["*"],
            exclude_patterns=["w#*", "srm_*"]
        ) -> ["users", "branches"]
    """
    result = []

    for table in tables:
        # Check if table matches any include pattern
        if not match_wildcard_pattern(table, include_patterns):
            continue

        # Check if table matches any exclude pattern
        if exclude_patterns and match_wildcard_pattern(table, exclude_patterns):
            continue

        result.append(table)

    return result


# =============================================================================
# DATA TRANSFER OPERATIONS
# =============================================================================
# Functions for transferring data between database servers.

def get_table_columns(host: str, port: int, username: str, password: str,
                      database: str, table: str, platform: str) -> tuple:
    """
    Query column names and types from a table.

    Args:
        host: Database server host
        port: Database server port
        username: Database username
        password: Database password
        database: Database name
        table: Table name
        platform: Database platform ("SYBASE" or "MSSQL")

    Returns:
        Tuple of (success: bool, columns: list of dict or error_message: str)
        On success: (True, [{"name": "col1", "type": "varchar", "length": 50}, ...])
        On failure: (False, "Error message")
    """
    if platform.upper() == "SYBASE":
        sql = f"""
            select c.name, t.name as type, c.length, c.prec, c.scale
            from syscolumns c
            join systypes t on c.usertype = t.usertype
            where c.id = object_id('{table}')
            order by c.colid
        """
    else:  # MSSQL
        sql = f"""
            select c.name, t.name as type, c.max_length as length, c.precision as prec, c.scale
            from sys.columns c
            join sys.types t on c.user_type_id = t.user_type_id
            where c.object_id = object_id('{table}')
            order by c.column_id
        """

    success, output = execute_sql_native(
        host=host, port=port, username=username, password=password,
        database=database, platform=platform, sql_content=sql
    )

    if not success:
        return False, output

    columns = []
    lines = output.strip().split('\n')
    for line in lines:
        line = line.strip()
        if not line or line.startswith('---') or line.lower().startswith('name'):
            continue
        if line.startswith('locale is') or line.startswith('using default'):
            continue
        if 'affected' in line.lower():
            continue

        # Parse columns - space separated
        parts = line.split()
        if len(parts) >= 2:
            col_info = {
                "name": parts[0],
                "type": parts[1],
                "length": int(parts[2]) if len(parts) > 2 and parts[2].isdigit() else 0
            }
            columns.append(col_info)

    return True, columns


def get_table_row_count(host: str, port: int, username: str, password: str,
                        database: str, table: str, platform: str) -> tuple:
    """
    Get the row count of a table.

    Returns:
        Tuple of (success: bool, count: int or error_message: str)
    """
    sql = f"select count(*) from {table}"

    success, output = execute_sql_native(
        host=host, port=port, username=username, password=password,
        database=database, platform=platform, sql_content=sql
    )

    if not success:
        return False, output

    # Parse the count from output
    for line in output.strip().split('\n'):
        line = line.strip()
        if line.isdigit():
            return True, int(line)

    return False, "Could not parse row count"


def truncate_table(host: str, port: int, username: str, password: str,
                   database: str, table: str, platform: str) -> tuple:
    """
    Truncate a table.

    Returns:
        Tuple of (success: bool, message: str)
    """
    sql = f"truncate table {table}"

    success, output = execute_sql_native(
        host=host, port=port, username=username, password=password,
        database=database, platform=platform, sql_content=sql
    )

    if not success:
        # Try DELETE if TRUNCATE fails (permission issues)
        sql = f"delete from {table}"
        success, output = execute_sql_native(
            host=host, port=port, username=username, password=password,
            database=database, platform=platform, sql_content=sql
        )
        if not success:
            return False, output

    return True, "OK"


def escape_sql_value(value, col_type: str = "varchar") -> str:
    """
    Escape a value for safe inclusion in SQL INSERT statement.

    Args:
        value: The value to escape
        col_type: Column type (for determining quoting)

    Returns:
        Escaped string safe for SQL
    """
    if value is None:
        return "NULL"

    # Convert to string
    str_val = str(value)

    # Check for NULL representations
    if str_val.upper() == "NULL" or str_val == "":
        return "NULL"

    # Numeric types don't need quoting
    numeric_types = ['int', 'smallint', 'tinyint', 'bigint', 'float', 'real',
                     'decimal', 'numeric', 'money', 'smallmoney', 'bit']
    if any(t in col_type.lower() for t in numeric_types):
        # Verify it's actually numeric
        try:
            float(str_val)
            return str_val
        except ValueError:
            return "NULL"

    # Escape single quotes for string types
    escaped = str_val.replace("'", "''")

    # Escape newlines and carriage returns (would break SQL)
    escaped = escaped.replace('\r\n', ' ').replace('\n', ' ').replace('\r', ' ')

    # Escape tabs
    escaped = escaped.replace('\t', ' ')

    return f"'{escaped}'"


def extract_table_data(host: str, port: int, username: str, password: str,
                       database: str, table: str, platform: str,
                       columns: list, batch_size: int = 1000,
                       offset: int = 0) -> tuple:
    """
    Extract rows from a table with pagination.

    Args:
        host, port, username, password: Connection info
        database: Database name
        table: Table name
        platform: "SYBASE" or "MSSQL"
        columns: List of column info dicts from get_table_columns
        batch_size: Number of rows to fetch
        offset: Starting row offset

    Returns:
        Tuple of (success: bool, rows: list of tuples or error_message: str)
    """
    col_names = ", ".join([c["name"] for c in columns])

    # Build pagination query - differs by platform
    if platform.upper() == "SYBASE":
        # Sybase uses SET ROWCOUNT
        sql = f"set rowcount {batch_size}\nselect {col_names} from {table}"
        if offset > 0:
            # For offset in Sybase, we need a different approach
            # Using a temp table or cursor would be needed for true pagination
            # For simplicity, we'll fetch all and skip in Python for now
            sql = f"select {col_names} from {table}"
    else:  # MSSQL
        sql = f"select {col_names} from {table} order by (select null) offset {offset} rows fetch next {batch_size} rows only"

    success, output = execute_sql_native(
        host=host, port=port, username=username, password=password,
        database=database, platform=platform, sql_content=sql
    )

    if not success:
        return False, output

    # Parse output into rows
    rows = []
    lines = output.strip().split('\n')
    header_skipped = False

    for line in lines:
        line = line.strip()
        if not line:
            continue
        if line.startswith('locale is') or line.startswith('using default'):
            continue
        if 'affected' in line.lower():
            continue
        if line.startswith('---'):
            header_skipped = True
            continue
        if not header_skipped:
            # Skip header row
            header_skipped = True
            continue

        # Parse tab-separated or space-separated values
        if '\t' in line:
            values = line.split('\t')
        else:
            # Space separated - tricky with string values
            values = line.split()

        if values:
            rows.append(tuple(values))

    return True, rows


def generate_insert_statements(database: str, table: str, columns: list,
                                rows: list, batch_size: int = 100) -> list:
    """
    Generate INSERT statements for rows of data.

    Args:
        database: Target database name
        table: Target table name
        columns: List of column info dicts
        rows: List of row tuples
        batch_size: Rows per INSERT statement (for batching)

    Returns:
        List of SQL INSERT statement strings
    """
    if not rows:
        return []

    col_names = ", ".join([c["name"] for c in columns])
    statements = []

    for row in rows:
        values = []
        for i, val in enumerate(row):
            col_type = columns[i]["type"] if i < len(columns) else "varchar"
            values.append(escape_sql_value(val, col_type))

        values_str = ", ".join(values)
        stmt = f"insert into {database}..{table} ({col_names}) values ({values_str})"
        statements.append(stmt)

    return statements


def verify_table_transfer(src_host: str, src_port: int, src_user: str, src_pass: str,
                          src_db: str, src_table: str, src_platform: str,
                          dest_host: str, dest_port: int, dest_user: str, dest_pass: str,
                          dest_db: str, dest_table: str, dest_platform: str,
                          mode: str, dest_count_before: int = 0) -> dict:
    """
    Verify that a table transfer completed successfully by comparing row counts.

    Args:
        src_*: Source connection info
        dest_*: Destination connection info
        mode: "TRUNCATE" or "APPEND"
        dest_count_before: Destination row count before transfer (for APPEND mode)

    Returns:
        Dict with verification results:
        {
            "verified": bool,
            "source_rows": int,
            "dest_rows": int,
            "expected_rows": int,
            "error": str or None
        }
    """
    result = {
        "verified": False,
        "source_rows": 0,
        "dest_rows": 0,
        "expected_rows": 0,
        "error": None
    }

    # Get source row count
    success, src_count = get_table_row_count(
        src_host, src_port, src_user, src_pass, src_db, src_table, src_platform
    )
    if not success:
        result["error"] = f"Failed to get source row count: {src_count}"
        return result

    result["source_rows"] = src_count

    # Get destination row count
    success, dest_count = get_table_row_count(
        dest_host, dest_port, dest_user, dest_pass, dest_db, dest_table, dest_platform
    )
    if not success:
        result["error"] = f"Failed to get destination row count: {dest_count}"
        return result

    result["dest_rows"] = dest_count

    # Verify based on mode
    if mode.upper() == "TRUNCATE":
        result["expected_rows"] = src_count
        result["verified"] = (dest_count == src_count)
    else:  # APPEND
        expected = dest_count_before + src_count
        result["expected_rows"] = expected
        result["verified"] = (dest_count == expected)

    if not result["verified"]:
        diff = result["expected_rows"] - result["dest_rows"]
        result["error"] = f"{abs(diff)} rows {'missing' if diff > 0 else 'extra'}"

    return result


def transfer_single_table(src_config: dict, dest_config: dict,
                          src_db: str, src_table: str,
                          dest_db: str, dest_table: str,
                          mode: str, batch_size: int = 1000,
                          progress_callback=None) -> dict:
    """
    Transfer a single table from source to destination.

    Args:
        src_config: Source connection config dict
        dest_config: Destination connection config dict
        src_db: Source database name
        src_table: Source table name
        dest_db: Destination database name
        dest_table: Destination table name (usually same as src_table)
        mode: "TRUNCATE" or "APPEND"
        batch_size: Rows per batch
        progress_callback: Optional callback(rows_done, total_rows)

    Returns:
        Dict with transfer results
    """
    import time
    start_time = time.time()

    result = {
        "status": "pending",
        "source_rows": 0,
        "dest_rows": 0,
        "rows_transferred": 0,
        "verified": False,
        "elapsed": "0s",
        "error": None
    }

    try:
        # Get source connection info
        src_host = src_config.get("HOST")
        src_port = src_config.get("PORT")
        src_user = src_config.get("USERNAME")
        src_pass = src_config.get("PASSWORD")
        src_platform = src_config.get("PLATFORM")

        # Get destination connection info
        dest_host = dest_config.get("HOST")
        dest_port = dest_config.get("PORT")
        dest_user = dest_config.get("USERNAME")
        dest_pass = dest_config.get("PASSWORD")
        dest_platform = dest_config.get("PLATFORM")

        result["status"] = "in_progress"

        # Step 1: Get source row count
        success, src_count = get_table_row_count(
            src_host, src_port, src_user, src_pass, src_db, src_table, src_platform
        )
        if not success:
            result["status"] = "failed"
            result["error"] = f"Failed to get source row count: {src_count}"
            return result

        result["source_rows"] = src_count

        # Step 2: Get source columns (validate source table)
        success, src_columns = get_table_columns(
            src_host, src_port, src_user, src_pass, src_db, src_table, src_platform
        )
        if not success:
            result["status"] = "skipped"
            result["error"] = f"Failed to get source columns: {src_columns}"
            logging.error(f"SKIPPED {src_db}..{src_table}: {result['error']}")
            return result

        # Step 3: Get destination columns (validate dest table exists)
        success, dest_columns = get_table_columns(
            dest_host, dest_port, dest_user, dest_pass, dest_db, dest_table, dest_platform
        )
        if not success:
            result["status"] = "skipped"
            result["error"] = f"Destination table does not exist or is inaccessible: {dest_columns}"
            logging.error(f"SKIPPED {dest_db}..{dest_table}: {result['error']}")
            return result

        # Step 4: Compare schemas - check column names match
        src_col_names = [c["name"].lower() for c in src_columns]
        dest_col_names = [c["name"].lower() for c in dest_columns]

        if src_col_names != dest_col_names:
            # Find differences
            missing_in_dest = set(src_col_names) - set(dest_col_names)
            extra_in_dest = set(dest_col_names) - set(src_col_names)

            diff_msg = []
            if missing_in_dest:
                diff_msg.append(f"missing in dest: {', '.join(missing_in_dest)}")
            if extra_in_dest:
                diff_msg.append(f"extra in dest: {', '.join(extra_in_dest)}")
            if not missing_in_dest and not extra_in_dest:
                diff_msg.append("column order differs")

            result["status"] = "skipped"
            result["error"] = f"Schema mismatch: {'; '.join(diff_msg)}"
            logging.error(f"SKIPPED {src_db}..{src_table} -> {dest_db}..{dest_table}: {result['error']}")
            return result

        # Use source columns for transfer (validated to match destination)
        columns = src_columns

        # Step 5: Get destination row count before (for APPEND mode verification)
        dest_count_before = 0
        if mode.upper() == "APPEND":
            success, dest_count_before = get_table_row_count(
                dest_host, dest_port, dest_user, dest_pass, dest_db, dest_table, dest_platform
            )
            if not success:
                dest_count_before = 0

        # Step 6: If TRUNCATE mode, truncate destination table
        # (Only after schema validation passes to avoid data loss on mismatch)
        if mode.upper() == "TRUNCATE":
            truncate_sql = f"truncate table {dest_table}"
            success, output = execute_sql_native(
                dest_host, dest_port, dest_user, dest_pass,
                dest_db, dest_platform, truncate_sql
            )
            if not success:
                # Try DELETE if TRUNCATE fails (some permission issues)
                delete_sql = f"delete from {dest_table}"
                success, output = execute_sql_native(
                    dest_host, dest_port, dest_user, dest_pass,
                    dest_db, dest_platform, delete_sql
                )
                if not success:
                    result["status"] = "failed"
                    result["error"] = f"Failed to truncate/delete destination: {output}"
                    return result

        # Step 7: Transfer data using BCP (freebcp)
        import tempfile
        import os

        logging.info(f"Starting BCP transfer: {src_db}..{src_table} -> {dest_db}..{dest_table}")
        logging.info(f"Source rows: {src_count}")

        # Create temp file for BCP data
        temp_dir = tempfile.gettempdir()
        bcp_file = os.path.join(temp_dir, f"bcp_{src_table}_{os.getpid()}.dat")

        try:
            # BCP OUT from source
            src_table_full = f"{src_db}..{src_table}"
            logging.info(f"BCP OUT: {src_table_full} -> {bcp_file}")

            success, output = execute_bcp(
                src_host, src_port, src_user, src_pass,
                src_table_full, "out", bcp_file,
                platform=src_platform
            )

            if not success:
                result["status"] = "failed"
                result["error"] = f"BCP OUT failed: {output}"
                logging.error(f"BCP OUT failed: {output}")
                return result

            rows_exported = int(output) if output.isdigit() else 0
            logging.info(f"BCP OUT complete: {rows_exported} rows exported")

            # Call progress callback (extraction complete)
            if progress_callback:
                progress_callback("extract", rows_exported, src_count)

            # BCP IN to destination
            dest_table_full = f"{dest_db}..{dest_table}"
            logging.info(f"BCP IN: {bcp_file} -> {dest_table_full}")

            success, output = execute_bcp(
                dest_host, dest_port, dest_user, dest_pass,
                dest_table_full, "in", bcp_file,
                platform=dest_platform
            )

            if not success:
                result["status"] = "failed"
                result["error"] = f"BCP IN failed: {output}"
                logging.error(f"BCP IN failed: {output}")
                return result

            rows_imported = int(output) if output.isdigit() else 0
            logging.info(f"BCP IN complete: {rows_imported} rows imported")

            result["rows_transferred"] = rows_imported

            # Call progress callback (insert complete)
            if progress_callback:
                progress_callback("insert", rows_imported, src_count)

        finally:
            # Clean up temp file
            if os.path.exists(bcp_file):
                try:
                    os.remove(bcp_file)
                    logging.debug(f"Cleaned up temp file: {bcp_file}")
                except Exception as e:
                    logging.warning(f"Failed to clean up temp file {bcp_file}: {e}")

        logging.info(f"BCP transfer complete: {result['rows_transferred']} rows transferred")

        # Step 6: Verify transfer
        verification = verify_table_transfer(
            src_host, src_port, src_user, src_pass, src_db, src_table, src_platform,
            dest_host, dest_port, dest_user, dest_pass, dest_db, dest_table, dest_platform,
            mode, dest_count_before
        )

        result["dest_rows"] = verification["dest_rows"]
        result["verified"] = verification["verified"]

        if result["verified"]:
            result["status"] = "completed"
        else:
            result["status"] = "mismatch"
            result["error"] = verification["error"]

    except Exception as e:
        result["status"] = "failed"
        result["error"] = str(e)

    finally:
        elapsed = time.time() - start_time
        if elapsed < 60:
            result["elapsed"] = f"{elapsed:.1f}s"
        else:
            result["elapsed"] = f"{elapsed/60:.1f}m"

    return result


# =============================================================================
# TRANSFER STATE MANAGEMENT
# =============================================================================

def save_transfer_state(project_name: str, state: dict) -> bool:
    """
    Save transfer state to a project in settings.json.

    Thread-safe: Uses file locking approach by reading and writing atomically.

    Args:
        project_name: Name of the data transfer project
        state: Transfer state dictionary

    Returns:
        True if saved successfully
    """
    try:
        project = load_data_transfer_project(project_name)
        if not project:
            return False

        project["TRANSFER_STATE"] = state
        return save_data_transfer_project(project_name, project)

    except Exception as e:
        logging.error(f"Failed to save transfer state: {e}")
        return False


def load_transfer_state(project_name: str) -> dict:
    """
    Load transfer state from a project.

    Args:
        project_name: Name of the data transfer project

    Returns:
        Transfer state dictionary, or None if not found
    """
    try:
        project = load_data_transfer_project(project_name)
        return project.get("TRANSFER_STATE")

    except Exception as e:
        logging.error(f"Failed to load transfer state: {e}")
        return None


def clear_transfer_state(project_name: str) -> bool:
    """
    Clear transfer state from a project.

    Args:
        project_name: Name of the data transfer project

    Returns:
        True if cleared successfully
    """
    return save_transfer_state(project_name, None)


def get_pending_tables(state: dict) -> list:
    """
    Get list of tables that still need to be transferred.

    Args:
        state: Transfer state dictionary

    Returns:
        List of table names (database..table format) that are pending or in_progress
    """
    if not state or "TABLES" not in state:
        return []

    pending = []
    for table_name, table_state in state.get("TABLES", {}).items():
        status = table_state.get("status", "pending")
        if status in ("pending", "in_progress"):
            pending.append(table_name)

    return pending


def get_completed_tables(state: dict) -> list:
    """
    Get list of tables that have been transferred.

    Args:
        state: Transfer state dictionary

    Returns:
        List of table names that are completed
    """
    if not state or "TABLES" not in state:
        return []

    completed = []
    for table_name, table_state in state.get("TABLES", {}).items():
        status = table_state.get("status", "pending")
        if status == "completed":
            completed.append(table_name)

    return completed


# =============================================================================
# THREADING AND PROGRESS DISPLAY
# =============================================================================

import threading
import queue
import time
import sys
import os

class ProgressDisplay:
    """
    Real-time progress bar display for multiple concurrent transfers.

    Uses ANSI escape codes for in-place updates.
    """

    def __init__(self, total_tables: int, num_threads: int = 5,
                 start_index: int = 0, overall_total: int = None):
        self.total_tables = total_tables
        self.num_threads = num_threads
        self.completed_count = 0
        self.start_index = start_index  # 0-based index of first table in this batch
        self.overall_total = overall_total or total_tables  # Total across all batches
        self.start_time = time.time()
        self.lock = threading.Lock()

        # Track active transfers: slot_id -> {table, rows_done, total_rows, status}
        self.active_slots = {}

        # Completed tables for display
        self.completed_tables = []

        # Terminal width
        try:
            self.term_width = os.get_terminal_size().columns
        except:
            self.term_width = 80

        # Flag to stop display
        self.stop_flag = False

    def _make_progress_bar(self, percent: float, width: int = 20) -> str:
        """Generate a progress bar string."""
        filled = int(width * percent / 100)
        empty = width - filled
        return f"[{'#' * filled}{'-' * empty}]"

    def _format_table_name(self, name: str, max_len: int = 25) -> str:
        """Truncate table name if too long."""
        if len(name) > max_len:
            return name[:max_len-2] + ".."
        return name.ljust(max_len)

    def update_progress(self, slot_id: int, table: str, rows_done: int,
                        total_rows: int, status: str = "transferring",
                        phase: str = None):
        """Update progress for a specific slot."""
        with self.lock:
            self.active_slots[slot_id] = {
                "table": table,
                "rows_done": rows_done,
                "total_rows": total_rows,
                "status": status,
                "phase": phase or "transferring"
            }

    def mark_completed(self, slot_id: int, table: str, rows: int,
                       elapsed: str, verified: bool):
        """Mark a slot as completed and record in completed list."""
        with self.lock:
            if slot_id in self.active_slots:
                del self.active_slots[slot_id]

            self.completed_count += 1
            status = "✓ VERIFIED" if verified else "✗ MISMATCH"
            self.completed_tables.append({
                "table": table,
                "rows": rows,
                "elapsed": elapsed,
                "status": status
            })

    def render(self):
        """Render the current progress display."""
        with self.lock:
            lines = []

            # Show active transfers (compact format matching single table)
            for slot_id in sorted(self.active_slots.keys()):
                slot = self.active_slots[slot_id]
                table_name = slot["table"]
                total = slot["total_rows"] or 1
                percent = (slot["rows_done"] / total) * 100 if total > 0 else 0
                bar = self._make_progress_bar(percent)
                phase = slot.get("phase", "transferring")
                phase_label = "Extracting" if phase == "extract" else "Inserting " if phase == "insert" else "          "
                table_num = self.start_index + self.completed_count + slot_id + 1
                lines.append(f"[{table_num}/{self.overall_total}] {table_name}")
                lines.append(f"        {phase_label}: {bar} {percent:3.0f}%  {slot['rows_done']}/{total}")

            return "\n".join(lines)

    def clear_and_print(self, text: str):
        """Clear previous output and print new text."""
        # Move cursor up and clear lines
        num_lines = text.count('\n') + 1
        sys.stdout.write(f"\033[{num_lines}A")  # Move up
        sys.stdout.write("\033[J")  # Clear from cursor to end
        sys.stdout.write(text + "\n")
        sys.stdout.flush()


class TransferWorker(threading.Thread):
    """
    Worker thread for transferring a single table.
    """

    def __init__(self, worker_id: int, task_queue: queue.Queue,
                 result_queue: queue.Queue, src_config: dict,
                 dest_config: dict, mode: str, batch_size: int,
                 progress_display: ProgressDisplay, stop_event: threading.Event):
        super().__init__(daemon=True)
        self.worker_id = worker_id
        self.task_queue = task_queue
        self.result_queue = result_queue
        self.src_config = src_config
        self.dest_config = dest_config
        self.mode = mode
        self.batch_size = batch_size
        self.progress_display = progress_display
        self.stop_event = stop_event
        self.current_table = None

    def run(self):
        """Main worker loop."""
        while not self.stop_event.is_set():
            try:
                # Get next task (non-blocking with timeout)
                task = self.task_queue.get(timeout=0.5)
            except queue.Empty:
                continue

            if task is None:  # Poison pill
                break

            src_db, src_table, dest_db, dest_table = task
            full_name = f"{src_db}..{src_table}"
            self.current_table = full_name

            # Progress callback
            def progress_cb(phase, rows_done, total_rows):
                self.progress_display.update_progress(
                    self.worker_id, full_name, rows_done, total_rows,
                    phase=phase
                )

            # Initial progress update
            self.progress_display.update_progress(
                self.worker_id, full_name, 0, 0, "starting"
            )

            # Transfer the table
            result = transfer_single_table(
                self.src_config, self.dest_config,
                src_db, src_table, dest_db, dest_table,
                self.mode, self.batch_size, progress_cb
            )

            # Mark completed in progress display
            self.progress_display.mark_completed(
                self.worker_id, full_name,
                result.get("rows_transferred", 0),
                result.get("elapsed", "0s"),
                result.get("verified", False)
            )

            # Put result in result queue
            self.result_queue.put((full_name, result))

            self.task_queue.task_done()
            self.current_table = None


class TransferThreadPool:
    """
    Thread pool for parallel table transfers.
    """

    def __init__(self, src_config: dict, dest_config: dict,
                 mode: str, num_threads: int = 5, batch_size: int = 1000,
                 start_index: int = 0, overall_total: int = None):
        self.src_config = src_config
        self.dest_config = dest_config
        self.mode = mode
        self.num_threads = num_threads
        self.batch_size = batch_size
        self.start_index = start_index
        self.overall_total = overall_total

        self.task_queue = queue.Queue()
        self.result_queue = queue.Queue()
        self.stop_event = threading.Event()
        self.workers = []
        self.progress_display = None

    def start(self, tables: list):
        """
        Start the transfer process.

        Args:
            tables: List of (src_db, src_table, dest_db, dest_table) tuples
        """
        overall = self.overall_total or len(tables)
        self.progress_display = ProgressDisplay(
            len(tables), self.num_threads,
            start_index=self.start_index, overall_total=overall
        )

        # Create workers
        for i in range(self.num_threads):
            worker = TransferWorker(
                i, self.task_queue, self.result_queue,
                self.src_config, self.dest_config,
                self.mode, self.batch_size,
                self.progress_display, self.stop_event
            )
            worker.start()
            self.workers.append(worker)

        # Queue all tasks
        for task in tables:
            self.task_queue.put(task)

    def stop(self):
        """Signal workers to stop after current task."""
        self.stop_event.set()

        # Send poison pills
        for _ in self.workers:
            self.task_queue.put(None)

    def wait_for_completion(self, state_callback=None):
        """
        Wait for all tasks to complete.

        Args:
            state_callback: Called with (table_name, result) after each completion
        """
        results = {}
        completed = 0
        total = self.task_queue.qsize() + len([w for w in self.workers if w.current_table])

        while completed < total and not self.stop_event.is_set():
            try:
                table_name, result = self.result_queue.get(timeout=0.5)
                results[table_name] = result
                completed += 1

                if state_callback:
                    state_callback(table_name, result)

            except queue.Empty:
                pass

        return results

    def join(self):
        """Wait for all workers to finish."""
        for worker in self.workers:
            worker.join(timeout=2.0)


# =============================================================================
# CONNECTION TESTING (using tsql)
# =============================================================================

def check_freetds_installed() -> bool:
    """
    Check if FreeTDS (tsql, freebcp) is available in PATH.

    Returns:
        True if FreeTDS tools are found, False otherwise
    """
    tsql_found = shutil.which("tsql") is not None
    freebcp_found = shutil.which("freebcp") is not None

    if not tsql_found:
        logging.warning("tsql command not found in PATH")
    if not freebcp_found:
        logging.warning("freebcp command not found in PATH")

    return tsql_found and freebcp_found


def verify_database_tools(config: dict = None) -> bool:
    """
    Verify that required database tools (FreeTDS) are installed and available.

    This is a wrapper around check_freetds_installed() for backward compatibility
    with existing code that calls verify_database_tools().

    Args:
        config: Optional configuration dictionary (not currently used)

    Returns:
        True if tools are available, False otherwise

    Raises:
        SystemExit: If required tools are not found
    """
    if not check_freetds_installed():
        logging.error("Required FreeTDS tools (tsql, freebcp) are not installed or not in PATH")
        logging.error("Please install FreeTDS before continuing")
        sys.exit(1)
    return True


def test_connection(host: str, port: int, username: str, password: str,
                    platform: str = "SYBASE") -> tuple[bool, str]:
    """
    Test database connection using FreeTDS tsql.

    Uses direct HOST:PORT connection (not server aliases) to verify that FreeTDS
    can connect to the database. This is the most reliable way to test connectivity
    before using pyodbc or freebcp.

    Args:
        host: Database host (IP address or hostname)
        port: Database port
        username: Database username
        password: Database password
        platform: Database platform (SYBASE or MSSQL)

    Returns:
        Tuple of (success: bool, message: str)

    Example:
        success, msg = test_connection("54.235.236.130", 5000, "sbn0", "ibsibs", "SYBASE")
        if success:
            print(f"Connection successful: {msg}")
        else:
            print(f"Connection failed: {msg}")
    """
    # Check if tsql is installed
    if not shutil.which("tsql"):
        return False, "tsql command not found. Install FreeTDS."

    logging.info(f"Testing connection to {platform} at {host}:{port} as {username}")

    # Build tsql command with direct connection
    # -H = host (direct connection, not server alias)
    # -p = port
    # -U = username
    # -P = password
    tsql_cmd = [
        "tsql",
        "-H", host,
        "-p", str(port),
        "-U", username,
        "-P", password
    ]

    # Simple test query
    test_query = "SELECT 1\nGO\nexit\n"

    try:
        # Run tsql with the test query
        result = subprocess.run(
            tsql_cmd,
            input=test_query,
            capture_output=True,
            text=True,
            timeout=10  # 10 second timeout
        )

        output = result.stdout + result.stderr
        output_lower = output.lower()

        # Check for specific error messages
        if "login failed" in output_lower:
            return False, f"Login failed for user '{username}'"
        elif "connection refused" in output_lower:
            return False, "Connection refused - check host and port"
        elif "unknown host" in output_lower or "name resolution" in output_lower:
            return False, "Host not found"
        elif "timeout" in output_lower or "timed out" in output_lower:
            return False, "Connection timeout"
        elif "unable to connect" in output_lower:
            return False, "Unable to connect to server"
        elif "network error" in output_lower or "host is unreachable" in output_lower:
            return False, "Network error - host unreachable"

        # Check return code
        if result.returncode == 0:
            return True, f"Connection successful to {host}:{port}"
        else:
            # Extract first meaningful error line
            if result.stderr:
                error_lines = [line.strip() for line in result.stderr.splitlines()
                              if line.strip() and not line.startswith("locale is")
                              and not line.startswith("using default charset")]
                if error_lines:
                    return False, error_lines[0][:100]  # Truncate to 100 chars

            return False, "Connection test failed"

    except FileNotFoundError:
        return False, "tsql command not found - install FreeTDS"

    except subprocess.TimeoutExpired:
        return False, "Connection timeout - check host and port"

    except Exception as e:
        logging.error(f"Connection test exception: {e}")
        return False, str(e)[:100]


# =============================================================================
# DATABASE CONNECTIONS (using pyodbc with FreeTDS)
# =============================================================================

def get_db_connection(host: str, port: int, username: str, password: str,
                      platform: str, database: str = None, autocommit: bool = True):
    """
    Establish pyodbc connection using FreeTDS ODBC driver.

    Uses direct HOST:PORT connection (NOT server aliases from freetds.conf).
    This ensures consistent behavior across Windows, macOS, and Linux.

    Args:
        host: Database host (IP or hostname)
        port: Database port
        username: Database username
        password: Database password
        platform: Database platform (SYBASE or MSSQL)
        database: Database name (optional)
        autocommit: Enable autocommit mode (default: True)

    Returns:
        pyodbc connection object

    Raises:
        pyodbc.Error: If connection fails

    Example:
        conn = get_db_connection("54.235.236.130", 5000, "sbn0", "ibsibs",
                                 "SYBASE", "sbnmaster", autocommit=True)
        cursor = conn.cursor()
        cursor.execute("SELECT @@version")
        conn.close()
    """
    logging.debug(f"Establishing pyodbc connection to {platform} at {host}:{port}")

    # Get available ODBC drivers
    import pyodbc
    available_drivers = pyodbc.drivers()
    logging.debug(f"Available ODBC drivers: {available_drivers}")

    # Build connection string based on platform and available drivers
    if platform.upper() == "MSSQL":
        # Try to find the best MSSQL driver
        if "ODBC Driver 18 for SQL Server" in available_drivers:
            driver = "ODBC Driver 18 for SQL Server"
        elif "ODBC Driver 17 for SQL Server" in available_drivers:
            driver = "ODBC Driver 17 for SQL Server"
        elif "SQL Server" in available_drivers:
            driver = "SQL Server"
        else:
            raise ValueError(f"No MSSQL ODBC driver found. Available: {available_drivers}")

        conn_str = (
            f"DRIVER={{{driver}}};"
            f"SERVER={host},{port};"
            f"UID={username};"
            f"PWD={password};"
        )
        if database:
            conn_str += f"DATABASE={database};"

    elif platform.upper() == "SYBASE":
        # Try to find the Sybase ASE driver
        if "Adaptive Server Enterprise" in available_drivers:
            driver = "Adaptive Server Enterprise"
        elif "FreeTDS" in available_drivers:
            driver = "FreeTDS"
        else:
            raise ValueError(f"No Sybase ODBC driver found. Available: {available_drivers}. "
                           f"Install the SAP/Sybase ASE ODBC driver or configure FreeTDS ODBC.")

        conn_str = (
            f"DRIVER={{{driver}}};"
            f"SERVER={host};"
            f"PORT={port};"
            f"UID={username};"
            f"PWD={password};"
        )
        if database:
            conn_str += f"DATABASE={database};"

    else:
        raise ValueError(f"Unsupported platform: {platform}. Must be SYBASE or MSSQL.")

    logging.debug(f"Connection string: DRIVER={{{driver}}};SERVER=...;UID={username};PWD=***")

    try:
        connection = pyodbc.connect(conn_str, autocommit=autocommit)
        logging.debug(f"Database connection established to {host}:{port}")
        return connection

    except pyodbc.Error as ex:
        sqlstate = ex.args[0] if ex.args else "Unknown"
        error_msg = f"Failed to connect to {platform} at {host}:{port}. SQLSTATE: {sqlstate}. Error: {ex}"
        logging.error(error_msg)
        raise


def get_db_connection_from_profile(profile_name: str, database: str = None,
                                    autocommit: bool = True):
    """
    Convenience wrapper - loads profile and connects to database.

    Args:
        profile_name: Name of profile in settings.json
        database: Database name (optional, overrides profile DATABASE)
        autocommit: Enable autocommit mode (default: True)

    Returns:
        pyodbc connection object

    Raises:
        KeyError: If profile not found
        ValueError: If profile missing required fields
        pyodbc.Error: If connection fails

    Example:
        conn = get_db_connection_from_profile("GONZO", "sbnmaster")
        cursor = conn.cursor()
        cursor.execute("SELECT @@version")
        conn.close()
    """
    profile = load_profile(profile_name)

    host = profile["HOST"]
    port = profile["PORT"]
    username = profile["USERNAME"]
    password = profile["PASSWORD"]
    platform = profile["PLATFORM"]

    # Use database from profile if not explicitly provided
    if database is None:
        database = profile.get("DATABASE")

    return get_db_connection(host, port, username, password, platform, database, autocommit)


# =============================================================================
# BCP OPERATIONS (using freebcp)
# =============================================================================

# Ctrl-Z (0x1a) causes freebcp to fail on Windows because Windows text mode
# interprets 0x1a as EOF. This is a known limitation of freebcp.
_CTRLZ_BYTE = b'\x1a'


def _diagnose_bcp_failure(chunk_data: bytes, chunk_start_line: int,
                          field_term: bytes = b'\x1e', row_term: bytes = b'\x00') -> str:
    """
    Analyze a failed BCP chunk to identify problematic rows and explain why.

    Args:
        chunk_data: The raw BCP data that failed to import
        chunk_start_line: 1-based line number where this chunk starts in the original file
        field_term: Field terminator byte
        row_term: Row terminator byte

    Returns:
        Diagnostic message explaining what went wrong and where
    """
    rows = chunk_data.split(row_term)
    rows = [r for r in rows if r]  # Remove empty

    issues = []

    for i, row in enumerate(rows):
        line_num = chunk_start_line + i
        fields = row.split(field_term)
        row_issues = []

        # Check for Ctrl-Z (0x1a) - Windows EOF marker
        if _CTRLZ_BYTE in row:
            pos = row.find(_CTRLZ_BYTE)
            row_issues.append(f"contains Ctrl-Z (0x1a) at byte {pos} - Windows interprets as EOF")

        # Check for very long rows (freebcp may have buffer limits)
        if len(row) > 65000:
            row_issues.append(f"row is {len(row)} bytes - may exceed freebcp buffer")

        # Check for unexpected field count
        # (This is informational - may indicate parsing issues)

        if row_issues:
            issues.append(f"  Line {line_num}: {'; '.join(row_issues)}")

    if issues:
        if len(issues) <= 20:
            return "Problematic rows detected:\n" + "\n".join(issues)
        else:
            return (f"Problematic rows detected ({len(issues)} total):\n" +
                    "\n".join(issues[:10]) +
                    f"\n  ... and {len(issues) - 10} more lines with issues")
    else:
        return "No obvious data issues detected - failure may be due to server-side constraints"


def write_bcp_data_file(rows: list, file_path: str) -> int:
    """
    Write rows to a BCP-compatible data file.

    Uses the same delimiters as execute_bcp defaults:
      - Field separator: 0x1E (Record Separator, char 30)
      - Row terminator:  0x00 (null byte)

    Args:
        rows: List of tuples/lists of string values. Each element is one field.
        file_path: Path to write the data file.

    Returns:
        Number of rows written.
    """
    field_sep = b'\x1e'
    row_term = b'\x00'
    count = 0
    with open(file_path, 'wb') as f:
        for row in rows:
            f.write(field_sep.join(str(v).encode('utf-8') for v in row) + row_term)
            count += 1
    return count


def execute_bcp(host: str, port: int, username: str, password: str,
                table: str, direction: str, file_path: str,
                platform: str = "SYBASE",
                field_terminator: str = None,
                row_terminator: str = None,
                textsize: int = None,
                batch_size: int = 1000) -> tuple[bool, str]:
    """
    Execute freebcp using host:port connection.

    Args:
        host: Database host
        port: Database port
        username: Database username
        password: Database password
        table: Table name (e.g., "sbnmaster..users")
        direction: "in" or "out"
        file_path: Path to data file
        platform: Database platform (SYBASE or MSSQL), default SYBASE
        field_terminator: Field terminator (default: tab)
        row_terminator: Row terminator (default: newline)
        textsize: Max text/varchar size in bytes (default: 64512)
        batch_size: Max rows per BCP chunk (default: 1000)

    Returns:
        Tuple of (success: bool, message: str with row count)

    Example:
        success, msg = execute_bcp("54.235.236.130", 5000, "sbn0", "ibsibs",
                                   "sbnmaster..users", "out", "/tmp/users.dat")
    """
    # Check if freebcp is installed
    if not shutil.which("freebcp"):
        return False, "freebcp command not found. Install FreeTDS."

    logging.info(f"Executing freebcp {direction} for table {table}")


    # For large BCP "in" files, split into chunks to work around freebcp row limit
    max_rows_per_chunk = batch_size
    if direction == "in":
        file_path_obj = Path(file_path)
        if file_path_obj.exists():
            file_size = file_path_obj.stat().st_size
            # Estimate rows: typical row ~70 bytes, so 1.5M rows ~105MB
            if file_size > 100 * 1024 * 1024:  # > 100MB, likely needs chunking
                logging.info(f"Large BCP file ({file_size / 1024 / 1024:.1f}MB), checking row count...")
                row_term = b'\x00'  # null byte row terminator
                with open(file_path, 'rb') as f:
                    data = f.read()
                row_count = data.count(row_term)

                if row_count > max_rows_per_chunk:
                    logging.info(f"Splitting {row_count} rows into chunks of {max_rows_per_chunk}")
                    total_imported = 0
                    chunk_num = 0
                    pos = 0

                    current_line = 1  # Track 1-based line numbers

                    while pos < len(data):
                        # Find end of this chunk (at row terminator boundary)
                        chunk_rows = 0
                        chunk_end = pos
                        chunk_start_line = current_line
                        while chunk_end < len(data) and chunk_rows < max_rows_per_chunk:
                            next_null = data.find(row_term, chunk_end)
                            if next_null == -1:
                                chunk_end = len(data)
                                break
                            chunk_end = next_null + 1
                            chunk_rows += 1

                        if chunk_end <= pos:
                            break

                        chunk_data = data[pos:chunk_end]

                        # Write chunk to temp file
                        chunk_file = file_path_obj.with_suffix(f'.chunk{chunk_num}.bcp')
                        with open(chunk_file, 'wb') as f:
                            f.write(chunk_data)

                        logging.info(f"Importing chunk {chunk_num + 1} (lines {chunk_start_line}-{chunk_start_line + chunk_rows - 1}, {chunk_rows} rows)...")

                        # Recursively call execute_bcp for chunk (won't re-split since < max)
                        success, msg = execute_bcp(host, port, username, password, table,
                                                   direction, chunk_file, platform,
                                                   field_terminator, row_terminator, textsize,
                                                   batch_size)

                        # Clean up chunk file
                        chunk_file.unlink()

                        if success:
                            try:
                                chunk_imported = int(msg)
                                total_imported += chunk_imported
                                logging.info(f"Chunk {chunk_num + 1}: imported {chunk_imported}/{chunk_rows} rows")
                            except ValueError:
                                logging.warning(f"Chunk {chunk_num + 1}: could not parse row count from: {msg}")
                        else:
                            # Chunk failed - run diagnostics
                            diag = _diagnose_bcp_failure(chunk_data, chunk_start_line)
                            logging.error(f"Chunk {chunk_num + 1} FAILED (lines {chunk_start_line}-{chunk_start_line + chunk_rows - 1}):")
                            logging.error(f"  freebcp error: {msg[:200]}")
                            logging.error(f"  {diag}")

                        chunk_num += 1
                        current_line += chunk_rows
                        pos = chunk_end

                    logging.info(f"Total imported from {chunk_num} chunks: {total_imported}")
                    return True, str(total_imported)

    # Build freebcp command
    # -S = server (host:port format)
    # -U = username
    # -P = password
    # -c = character mode
    # -t = field terminator
    # -r = row terminator
    # -T = textsize
    server_arg = f"{host}:{port}"

    bcp_command = [
        "freebcp",
        table,
        direction,
        str(file_path),
        "-S", server_arg,
        "-U", username,
        "-P", password,
        "-c",  # Character mode
    ]

    # Add field terminator (default to Record Separator char(30) to handle embedded tabs in text fields)
    if field_terminator is not None:
        bcp_command.extend(["-t", field_terminator])
    else:
        bcp_command.extend(["-t", "\x1e"])

    # Add row terminator (default to null byte to handle embedded newlines in text fields)
    # Note: freebcp expects the escape sequence "\0" (backslash-zero), not a literal null byte
    if row_terminator is not None:
        bcp_command.extend(["-r", row_terminator])
    else:
        bcp_command.extend(["-r", r"\0"])  # raw string to pass literal \0 to freebcp

    # Add textsize for large varchar/text fields (default 64KB)
    if textsize is not None:
        bcp_command.extend(["-T", str(textsize)])
    else:
        bcp_command.extend(["-T", "65536"])

    # Allow max errors (default 1000000 to handle duplicate key violations gracefully)
    bcp_command.extend(["-m", "1000000"])

    # Log command for debugging (hide password)
    safe_cmd = bcp_command.copy()
    pw_idx = safe_cmd.index("-P") + 1
    safe_cmd[pw_idx] = "****"
    logging.debug(f"BCP command: {' '.join(safe_cmd)}")

    try:
        # Note: Do NOT use text=True as it can cause freebcp to fail on large files
        result = subprocess.run(bcp_command, capture_output=True, check=False)

        # Decode output as text, ignoring encoding errors
        stdout_text = result.stdout.decode('utf-8', errors='replace') if result.stdout else ''
        stderr_text = result.stderr.decode('utf-8', errors='replace') if result.stderr else ''

        logging.debug(f"freebcp STDOUT:\n{stdout_text}")

        if stderr_text:
            logging.warning(f"freebcp STDERR:\n{stderr_text}")

        # Check for success indicators
        if result.returncode == 0:
            # Extract row count from output (e.g., "7 rows copied.")
            row_count = 0
            import re
            match = re.search(r'(\d+)\s+rows?\s+copied', stdout_text, re.IGNORECASE)
            if match:
                row_count = int(match.group(1))
            return True, str(row_count)
        else:
            # Extract error message
            error_msg = f"BCP failed with returncode {result.returncode}"
            if stderr_text:
                error_msg += f": {stderr_text[:200]}"
            if stdout_text:
                error_msg += f" {stdout_text[:200]}"

            return False, error_msg

    except FileNotFoundError:
        return False, "freebcp command not found. Install FreeTDS."

    except Exception as e:
        logging.error(f"BCP exception: {e}")
        return False, str(e)[:100]


def get_config(args_list=None, profile_name=None, existing_config=None, allow_create=True):
    """
    Loads settings.json, parses command-line args, selects/expands a profile.
    If the file or a profile is not found, it prompts the user to create it
    (unless allow_create=False, in which case it raises KeyError).
    """
    if existing_config is None:
        config = {}
    else:
        config = existing_config.copy()

    temp_parser = argparse.ArgumentParser(add_help=False)
    # Positional argument for profile/server, now optional
    temp_parser.add_argument("profile_or_server", nargs='?', default=profile_name, help="Configuration profile or server name.")
    
    # Allow overriding common connection args directly
    temp_parser.add_argument("-S", "--server", help="Server name (overrides profile).")
    temp_parser.add_argument("-U", "--username", help="Database username (overrides profile).")
    temp_parser.add_argument("-P", "--password", help="Database password (overrides profile).")
    temp_parser.add_argument("-D", "--database", help="Database name (overrides profile).")
    
    # Parse only known args to avoid consuming args meant for the calling script
    parsed_args, remaining_args = temp_parser.parse_known_args(args_list)
    
    # Determine profile name: explicit positional arg > -S server arg > DEFAULT
    # All profile names are normalized to uppercase
    selected_profile_name = parsed_args.profile_or_server
    if selected_profile_name is None:
        if parsed_args.server:
            selected_profile_name = parsed_args.server.upper()
            logging.debug(f"No explicit profile, using server '{selected_profile_name}' as profile name.")
        else:
            selected_profile_name = "DEFAULT"
            logging.debug(f"No profile or server specified, using '{selected_profile_name}' profile.")
    else:
        selected_profile_name = selected_profile_name.upper()

    # 1. Check for settings.json and 2. Create if it doesn't exist
    settings_file = find_settings_file()
    if not settings_file.exists():
        logging.warning(f"{settings_file} not found. A new file will be created.")
        settings_data = {"Profiles": {}}
    else:
        try:
            settings_data = json.loads(settings_file.read_text())
        except json.JSONDecodeError as e:
            logging.error(f"Error decoding settings.json: {e}. Please fix or delete the file.")
            sys.exit(1)

    profiles = settings_data.get("Profiles", {})

    # Validate aliases - fail fast if conflicts detected
    alias_errors = validate_profile_aliases(settings_data)
    if alias_errors:
        print("ERROR: Alias conflicts in settings.json:")
        for err in alias_errors:
            print(f"  {err}")
        print("\nPlease fix settings.json manually.")
        sys.exit(1)

    # 3. Find profile by name or alias
    found_name, profile_data = find_profile_by_name_or_alias(settings_data, selected_profile_name)

    # If not found, either prompt to create or raise error
    if found_name is None:
        if allow_create:
            new_profile_data = prompt_for_new_profile(selected_profile_name)
            profiles[selected_profile_name] = new_profile_data
            settings_data["Profiles"] = profiles
            save_settings(settings_data)  # Save the new profile for future use
            profile_config = new_profile_data.copy()
            # Store the canonical profile name
            profile_config['PROFILE_NAME'] = selected_profile_name.upper()
        else:
            raise KeyError(f"Profile '{selected_profile_name}' not found")
    else:
        profile_config = profile_data.copy()
        # Store the canonical profile name (resolves aliases to real name)
        profile_config['PROFILE_NAME'] = found_name.upper()

    # Merge profile config with overrides
    final_config = profile_config
    # Command-line args take precedence over profile settings
    if parsed_args.server: final_config['DSQUERY'] = parsed_args.server
    if parsed_args.username: final_config['USERNAME'] = parsed_args.username
    if parsed_args.password: final_config['PASSWORD'] = parsed_args.password
    if parsed_args.database: final_config['DATABASE'] = parsed_args.database
    
    # Merge with existing_config last (from calling scripts)
    final_config.update(config)

    # Expand SQL_SOURCE (installation root) - use current directory if not set or null
    if 'SQL_SOURCE' not in final_config or final_config['SQL_SOURCE'] is None:
        final_config['SQL_SOURCE'] = os.getcwd()
    else:
        final_config['SQL_SOURCE'] = str(Path(final_config['SQL_SOURCE']).resolve())
                
    final_config['_remaining_args'] = remaining_args
    
    return final_config

def replace_placeholders(text, config, remove_ampersands=False):
    """
    Replaces &...& placeholders in the given text using values from the config.
    """
    processed_text = text
    for key, value in config.items():
        if isinstance(value, (str, int, float)): # Only replace with simple types
            placeholder = f"&{key.upper()}&"
            if placeholder in processed_text:
                processed_text = processed_text.replace(placeholder, str(value))
    
    if remove_ampersands:
        processed_text = processed_text.replace('&', '')
        
    return processed_text

# --- File Utilities ---
def convert_non_linked_paths(filename):
    """
    Converts symbolic (non-linked) paths to real directory paths.

    Symbolic paths like \\ss\\ba\\ are shorthand references used in
    scripts and documentation. This function expands them to full paths
    like \\SQL_Sources\\Basics\\.

    This is essential for file finding when scripts reference files using
    symbolic notation (e.g., "css/ss/ba/pro_users.sql").

    Args:
        filename: File path that may contain symbolic paths

    Returns:
        Expanded file path with real directory names

    Example:
        convert_non_linked_paths("css/ss/ba/pro_users.sql")
        -> "css/SQL_Sources/Basics/pro_users.sql"
    """
    # Path mappings from C# NonLinkedFilename (common.cs)
    # Format: (symbolic_path, real_path) - using forward slashes only
    # Input is normalized to forward slashes before matching, then converted
    # back to OS-appropriate separator (os.sep) on output.
    # This ensures cross-platform compatibility (Windows, macOS, Linux).
    convert_paths = [
        ("/ss/api/",    "/SQL_Sources/Application_Program_Interface/"),
        ("/ss/api2/",   "/SQL_Sources/Application_Program_Interface_V2/"),
        ("/ss/api3/",   "/SQL_Sources/Application_Program_Interface_V3/"),
        ("/ss/at/",     "/SQL_Sources/Alarm_Treatment/"),
        ("/ss/ba/",     "/SQL_Sources/Basics/"),
        ("/ss/bl/",     "/SQL_Sources/Billing/"),
        ("/ss/ct/",     "/SQL_Sources/Create_Temp/"),
        ("/ss/cv/",     "/SQL_Sources/Conversions/"),
        ("/ss/da/",     "/SQL_Sources/da/"),
        ("/ss/dv/",     "/SQL_Sources/IBS_Development/"),
        ("/ss/fe/",     "/SQL_Sources/Front_End/"),
        ("/ss/in/",     "/SQL_Sources/Internal/"),
        ("/ss/ma/",     "/SQL_Sources/Co_Monitoring/"),
        ("/ss/mb/",     "/SQL_Sources/Mobile/"),
        ("/ss/mo/",     "/SQL_Sources/Monitoring/"),
        ("/ss/mobile/", "/SQL_Sources/Mobile/"),
        ("/ss/sdi/",    "/SQL_Sources/SDI_App/"),
        ("/ss/si/",     "/SQL_Sources/System_Init/"),
        ("/ss/sv/",     "/SQL_Sources/Service/"),
        ("/ss/tm/",     "/SQL_Sources/Telemarketing/"),
        ("/ss/test/",   "/SQL_Sources/Test/"),
        ("/ss/ub/",     "/SQL_Sources/US_Basics/"),
        ("/ibs/ss/",    "/IBS/SQL_Sources/"),
    ]

    # Check if filename contains css or ibs (case-insensitive)
    if not re.search(r'(/|\\)(css|ibs)(/|\\)', filename, re.IGNORECASE):
        return filename

    # Normalize to forward slashes for consistent matching
    converted = filename.replace('\\', '/')

    # Try each path conversion (case-insensitive)
    for symbolic, real in convert_paths:
        idx = converted.lower().find(symbolic.lower())
        if idx != -1:
            converted = converted[:idx] + real + converted[idx + len(symbolic):]
            break

    # Convert back to OS-appropriate path separator
    if os.sep == '\\':
        converted = converted.replace('/', '\\')

    return converted


def _get_symbolic_links_config():
    """Returns the list of symbolic links to create: (link_path, target_directory)."""
    return [
        # Lowercase aliases for top-level directories (Linux case-sensitivity)
        ("css", "CSS"),
        ("ibs", "IBS"),
        # CSS top-level links
        ("CSS/ss", "CSS/SQL_Sources"),
        ("CSS/upd", "CSS/Updates"),
        # CSS/ss subdirectory links - each points to a directory under CSS/SQL_Sources
        ("CSS/ss/api", "CSS/SQL_Sources/Application_Program_Interface"),
        ("CSS/ss/api2", "CSS/SQL_Sources/Application_Program_Interface_V2"),
        ("CSS/ss/api3", "CSS/SQL_Sources/Application_Program_Interface_V3"),
        ("CSS/ss/at", "CSS/SQL_Sources/Alarm_Treatment"),
        ("CSS/ss/ba", "CSS/SQL_Sources/Basics"),
        ("CSS/ss/bl", "CSS/SQL_Sources/Billing"),
        ("CSS/ss/ct", "CSS/SQL_Sources/Create_Temp"),
        ("CSS/ss/da", "CSS/SQL_Sources/da"),
        ("CSS/ss/dv", "CSS/SQL_Sources/IBS_Development"),
        ("CSS/ss/fe", "CSS/SQL_Sources/Front_End"),
        ("CSS/ss/in", "CSS/SQL_Sources/Internal"),
        ("CSS/ss/ma", "CSS/SQL_Sources/Co_Monitoring"),
        ("CSS/ss/mb", "CSS/SQL_Sources/Mobile"),
        ("CSS/ss/mo", "CSS/SQL_Sources/Monitoring"),
        ("CSS/ss/mobile", "CSS/SQL_Sources/Mobile"),
        ("CSS/ss/sdi", "CSS/SQL_Sources/SDI_App"),
        ("CSS/ss/si", "CSS/SQL_Sources/System_Init"),
        ("CSS/ss/sv", "CSS/SQL_Sources/Service"),
        ("CSS/ss/tm", "CSS/SQL_Sources/Telemarketing"),
        ("CSS/ss/ub", "CSS/SQL_Sources/US_Basics"),
        # IBS links
        ("IBS/ss", "IBS/SQL_Sources"),
    ]


def _run_elevated_symlink_creation(base_path: str) -> bool:
    """
    Run symlink creation with elevated privileges on Windows.
    Creates a temporary script and runs it with UAC elevation.
    """
    import subprocess
    import tempfile

    # Build the mklink commands
    symbolic_links = _get_symbolic_links_config()
    commands = []

    for link_rel, target_name in symbolic_links:
        link_path = Path(base_path) / link_rel
        target_path = Path(base_path) / target_name

        # Skip if link already exists
        if link_path.exists() or link_path.is_symlink():
            continue

        # Skip if target doesn't exist
        if not target_path.exists():
            continue

        # Ensure parent exists
        link_parent = link_path.parent
        if not link_parent.exists():
            commands.append(f'mkdir "{link_parent}"')

        # Calculate relative target
        try:
            relative_target = os.path.relpath(target_path, link_parent)
        except ValueError:
            relative_target = str(target_path)

        commands.append(f'mklink /D "{link_path}" "{relative_target}"')

    if not commands:
        return True  # Nothing to do

    # Create a batch file with all commands
    with tempfile.NamedTemporaryFile(mode='w', suffix='.bat', delete=False) as f:
        f.write('@echo off\n')
        for cmd in commands:
            f.write(cmd + '\n')
        f.write('pause\n')
        batch_file = f.name

    try:
        # Run with elevation using ShellExecute
        import ctypes
        result = ctypes.windll.shell32.ShellExecuteW(
            None,           # hwnd
            "runas",        # operation - triggers UAC
            "cmd.exe",      # file
            f'/c "{batch_file}"',  # parameters
            None,           # directory
            1               # show command (SW_SHOWNORMAL)
        )
        # ShellExecuteW returns > 32 on success
        return result > 32
    finally:
        # Clean up batch file after a delay (let it run first)
        import threading
        def cleanup():
            import time
            time.sleep(5)
            try:
                os.unlink(batch_file)
            except:
                pass
        threading.Thread(target=cleanup, daemon=True).start()


def create_symbolic_links(config: dict, prompt: bool = True) -> bool:
    """
    Creates symbolic links for the compiler directory structure.

    Only creates links if SQL_SOURCE is defined and valid in the config.
    Links are created relative to the SQL_SOURCE directory.

    On Windows, if symlink creation fails due to privileges, can prompt for
    Administrator elevation via UAC (controlled by prompt parameter).

    Uses IBS_SYMLINKS_CHECKED environment variable to ensure this only runs
    once per session, even when commands call other commands (e.g., runcreate
    calling runsql). This keeps execution fast.

    Args:
        config: Configuration dictionary containing SQL_SOURCE
        prompt: If True, prompt user for elevation on Windows when needed.
                If False, just attempt and return success/failure.

    Returns:
        True if all links created successfully (or already exist), False otherwise
    """
    # Fast path: skip if already checked this session
    if os.environ.get('IBS_SYMLINKS_CHECKED') == '1':
        return True

    # Skip in raw mode
    if is_raw_mode(config):
        return True

    # Mark as checked immediately to prevent re-entry
    os.environ['IBS_SYMLINKS_CHECKED'] = '1'

    path_append = config.get('SQL_SOURCE')

    if not path_append:
        logging.warning("SQL_SOURCE not defined in config - skipping symbolic link creation")
        return False

    base_path = Path(path_append)

    if not base_path.exists():
        logging.warning(f"SQL_SOURCE directory does not exist: {base_path} - skipping symbolic link creation")
        return False

    symbolic_links = _get_symbolic_links_config()

    # First, check which links need to be created
    links_needed = []
    for link_rel, target_name in symbolic_links:
        link_path = base_path / link_rel
        target_path = base_path / target_name

        # Skip if link already exists
        if link_path.exists() or link_path.is_symlink():
            continue

        # Skip if target doesn't exist
        if not target_path.exists():
            continue

        links_needed.append((link_rel, target_name, link_path, target_path))

    if not links_needed:
        return True  # All links already exist

    success = True
    needs_elevation = False

    for link_rel, target_name, link_path, target_path in links_needed:
        link_parent = link_path.parent

        # Ensure parent directory exists
        try:
            link_parent.mkdir(parents=True, exist_ok=True)
        except OSError as e:
            print(f"[ERROR] Failed to create parent directory {link_parent}: {e}", file=sys.stderr)
            success = False
            continue

        # Calculate relative path from link location to target
        try:
            relative_target = os.path.relpath(target_path, link_parent)
        except ValueError:
            relative_target = str(target_path)

        # Re-check if link now exists (may have been created by parent symlink)
        if link_path.exists() or link_path.is_symlink():
            continue

        # Create the symbolic link
        try:
            if os.name == 'nt':
                # Windows: try mklink /D (directory symlink)
                import subprocess
                result = subprocess.run(
                    ['cmd', '/c', 'mklink', '/D', str(link_path), relative_target],
                    capture_output=True,
                    text=True
                )
                if result.returncode != 0:
                    if 'privilege' in result.stderr.lower():
                        needs_elevation = True
                        break  # Stop and request elevation
                    else:
                        print(f"[ERROR] Failed to create symbolic link {link_path}: {result.stderr.strip()}", file=sys.stderr)
                        success = False
                else:
                    print(f"[OK] Created symbolic link: {link_path} -> {relative_target}")
            else:
                # Unix: use os.symlink directly
                os.symlink(relative_target, link_path, target_is_directory=True)
                print(f"[OK] Created symbolic link: {link_path} -> {relative_target}")
        except OSError as e:
            print(f"[ERROR] Failed to create symbolic link {link_path}: {e}", file=sys.stderr)
            success = False

    # If we need elevation on Windows
    if needs_elevation and os.name == 'nt':
        if prompt:
            # Interactive mode - ask user
            print("\nSymbolic link creation requires Administrator privileges.")
            response = input("Would you like to run as Administrator? [Y/n]: ").strip().lower()
            if response in ('', 'y', 'yes'):
                if _run_elevated_symlink_creation(str(base_path)):
                    print("Administrator command launched. Please approve the UAC prompt.")
                    print("After the command window closes, re-run your original command.")
                    return True
                else:
                    print("[ERROR] Failed to launch elevated command", file=sys.stderr)
                    return False
            else:
                print("Skipping symbolic link creation. Some file paths may not resolve correctly.")
                return False
        else:
            # Non-interactive mode - just fail
            return False

    return success


def find_file(filename, config):
    """
    File lookup with different behavior based on RAW_MODE.

    RAW MODE (RAW_MODE=True):
        - File must exist in current directory exactly as specified
        - No .sql extension auto-append
        - No recursive search

    NORMAL MODE (RAW_MODE=False or not set):
        - First check current directory
        - If not found, search SQL_SOURCE recursively
        - If multiple matches found, show error and return None
        - Auto-append .sql if file not found without it

    Args:
        filename: File path to search for
        config: Configuration dictionary (used for path conversion and SQL_SOURCE)

    Returns:
        Absolute path to file if found, None if not found
    """
    # First, convert any non-linked paths (/ss/ba/ -> /SQL_Sources/Basics/)
    filename = convert_non_linked_paths(filename)

    file_path = Path(filename)
    raw_mode = is_raw_mode(config)

    # Absolute path - must exist at exact location (both modes)
    if file_path.is_absolute():
        if file_path.exists():
            return str(file_path)
        # Try with .sql extension (only in normal mode)
        if not raw_mode and not filename.lower().endswith('.sql'):
            sql_path = Path(filename + '.sql')
            if sql_path.exists():
                return str(sql_path)
        return None

    # RAW MODE: File must exist in current directory exactly as specified
    if raw_mode:
        cwd_path = Path.cwd() / file_path
        if cwd_path.exists():
            return str(cwd_path)
        return None

    # NORMAL MODE: Search current directory first, then SQL_SOURCE

    # 1. Check current directory for exact match
    cwd_path = Path.cwd() / file_path
    if cwd_path.exists():
        return str(cwd_path)

    # 2. Check current directory with .sql extension
    if not filename.lower().endswith('.sql'):
        sql_path = Path.cwd() / (filename + '.sql')
        if sql_path.exists():
            return str(sql_path)

    # 3. Search SQL_SOURCE recursively
    sql_source = config.get('SQL_SOURCE', '')
    if sql_source and Path(sql_source).exists():
        # Search for exact filename
        matches = _search_sql_source(sql_source, filename)

        # If no matches and no .sql extension, try with .sql
        if not matches and not filename.lower().endswith('.sql'):
            matches = _search_sql_source(sql_source, filename + '.sql')

        if len(matches) == 1:
            return str(matches[0])
        elif len(matches) > 1:
            print(f"{Fore.RED}{Icons.ERROR} Multiple files found matching '{filename}':{Style.RESET_ALL}", file=sys.stderr)
            for match in matches:
                # Show path relative to SQL_SOURCE for readability
                try:
                    rel_path = match.relative_to(sql_source)
                    print(f"  {Icons.ARROW} {rel_path}", file=sys.stderr)
                except ValueError:
                    print(f"  {Icons.ARROW} {match}", file=sys.stderr)
            print(f"\nPlease specify the full path or run from the file's directory.", file=sys.stderr)
            return None

    return None


def _search_sql_source(sql_source: str, filename: str) -> list:
    """
    Recursively search SQL_SOURCE for files matching filename.

    Args:
        sql_source: Root directory to search
        filename: Filename to search for (just the name, not path)

    Returns:
        List of Path objects for matching files
    """
    sql_source_path = Path(sql_source)
    # Extract just the filename in case a partial path was given
    search_name = Path(filename).name

    matches = []
    try:
        for match in sql_source_path.rglob(search_name):
            if match.is_file():
                matches.append(match)
    except (PermissionError, OSError):
        pass  # Skip directories we can't access

    return matches

# --- UI Utilities ---
def console_yes_no(prompt_text, default=None):
    """
    Gets a yes/no answer from the user.

    Args:
        prompt_text: The question to ask
        default: Default value if user presses Enter (True=yes, False=no, None=no default)
    """
    if default is True:
        hint = "Y/n"
    elif default is False:
        hint = "y/N"
    else:
        hint = "y/n"

    while True:
        response = input(f"{prompt_text} ({hint}): ").lower().strip()
        if response == '' and default is not None:
            return default
        if response in ['y', 'yes']:
            return True
        elif response in ['n', 'no']:
            return False
        else:
            print("Please answer 'y' or 'n'.")

def launch_editor(file_path):
    """
    Launches an editor to edit the given file and waits for it to close.

    Resolution order (matches Unix eact which uses $EDITOR):
        1. $EDITOR environment variable
        2. $VISUAL environment variable
        3. vim on PATH
        4. vi on PATH
        5. notepad on Windows (last resort)
    """
    editor = None

    # 1. Check EDITOR env var (matches Unix: $EDITOR $file)
    env_editor = os.environ.get('EDITOR', '').strip()
    if env_editor:
        # Could be a full path or just a command name
        if os.path.isfile(env_editor) or shutil.which(env_editor):
            editor = env_editor

    # 2. Check VISUAL env var
    if not editor:
        env_visual = os.environ.get('VISUAL', '').strip()
        if env_visual:
            if os.path.isfile(env_visual) or shutil.which(env_visual):
                editor = env_visual

    # 3. Try vim on PATH
    if not editor:
        vim_path = shutil.which('vim')
        if vim_path:
            editor = vim_path

    # 4. Try vi on PATH
    if not editor:
        vi_path = shutil.which('vi')
        if vi_path:
            editor = vi_path

    # 5. Windows fallback: notepad
    if not editor and sys.platform == "win32":
        notepad_path = shutil.which('notepad')
        if notepad_path:
            editor = notepad_path

    if not editor:
        print("ERROR: No editor found. Set the EDITOR environment variable.")
        return

    logging.info(f"Launching '{editor}' for {file_path}")
    try:
        subprocess.run([editor, str(file_path)], check=True)
    except (subprocess.CalledProcessError, FileNotFoundError) as e:
        logging.error(f"Could not launch editor '{editor}': {e}")
        print(f"ERROR: Could not launch editor: {e}")

def setup_logging(config):
    """Configures Python's logging module."""
    log_level_str = config.get('LOG_LEVEL', 'INFO').upper()
    log_file_path = config.get('OUTFILE', None)

    numeric_level = getattr(logging, log_level_str, None)
    if not isinstance(numeric_level, int):
        raise ValueError(f'Invalid log level: {log_level_str}')

    log_format = '%(asctime)s - %(levelname)s - %(message)s'
    
    handlers = [logging.StreamHandler(sys.stdout)]
    if log_file_path:
        Path(log_file_path).parent.mkdir(parents=True, exist_ok=True)
        handlers.append(logging.FileHandler(log_file_path))

    logging.basicConfig(level=numeric_level, format=log_format, handlers=handlers, force=True)
    logging.debug(f"Logging setup with level {log_level_str}.")


# =============================================================================
# SOFT-COMPILER OPTIONS CLASS
# =============================================================================

class Options:
    """
    Soft-compiler Options class for placeholder resolution.

    Handles:
    - v: value options (v: name << value >> description)
    - c: conditional options (c: name +/- description)
    - -> table options (-> tablename &dbvar& description)
    - Option file merging with hierarchy precedence
    - Caching with 24-hour TTL

    Option File Hierarchy (in precedence order - later overrides earlier):
    1. options.def                      (default values) - REQUIRED
    2. options.{company}                (e.g., options.101) - REQUIRED
    3. options.{company}.{server}       (e.g., options.101.GONZO) - OPTIONAL
    4. table_locations                  (table location mappings) - REQUIRED

    All files are found in {SQL_SOURCE}\\CSS\\Setup.
    Later files override earlier files (opposite of previous implementation).
    """

    def __init__(self, config: dict):
        """
        Initialize Options with configuration.

        Args:
            config: Configuration dict with COMPANY, PLATFORM, PROFILE_NAME, SQL_SOURCE, etc.
        """
        self.config = config
        self._options = {}  # Dictionary of placeholder -> value
        self._option_sources = {}  # Dictionary of placeholder -> source file path
        self._cache_file = None
        self._cache_ttl_minutes = 1440  # 24 hours (24 * 60 minutes)
        self._was_rebuilt = False  # Track whether cache was rebuilt or reused
        self._setup_directory = None  # Track setup directory path
        self._loaded_files = []  # Track which files were loaded

    def _parse_v_option(self, line: str) -> tuple:
        """
        Parse a v: (value) option line.

        Format: v: variable_name << value >> description

        Args:
            line: Option line starting with 'v:'

        Returns:
            Tuple of (placeholder, value) or (None, None) if invalid
        """
        # Remove 'v:' prefix and strip
        content = line[2:].strip()

        # Find << and >> markers
        start_marker = content.find('<<')
        end_marker = content.find('>>')

        if start_marker == -1 or end_marker == -1 or end_marker <= start_marker:
            logging.warning(f"Invalid v: option format: {line}")
            return None, None

        # Extract variable name (before <<)
        var_name = content[:start_marker].strip()

        # Extract value (between << and >>)
        value = content[start_marker + 2:end_marker].strip()

        if not var_name:
            logging.warning(f"Empty variable name in v: option: {line}")
            return None, None

        # Create placeholder with & delimiters
        placeholder = f"&{var_name}&"

        return placeholder, value

    def _parse_c_option(self, line: str) -> list:
        """
        Parse a c: (conditional) option line.

        Format: c: condition_name +/- description
        + = enabled, - = disabled

        Creates 4 placeholders:
        - &if_name& / &endif_name& - empty if enabled, /* */ if disabled
        - &ifn_name& / &endifn_name& - opposite (for "if not")

        Args:
            line: Option line starting with 'c:'

        Returns:
            List of (placeholder, value) tuples (4 items) or empty list if invalid
        """
        # Remove 'c:' prefix and strip
        content = line[2:].strip()

        if not content:
            logging.warning(f"Empty c: option: {line}")
            return []

        # Split into parts - first word is name, second should be +/-
        parts = content.split()
        if len(parts) < 2:
            logging.warning(f"Invalid c: option format (need name and +/-): {line}")
            return []

        name = parts[0]
        flag = parts[1]

        if flag not in ('+', '-'):
            logging.warning(f"Invalid c: option flag (must be + or -): {line}")
            return []

        enabled = (flag == '+')

        # Generate 4 placeholders
        results = []

        if enabled:
            # Condition is TRUE - if blocks are active, ifn blocks are commented out
            results.append((f"&if_{name}&", ""))
            results.append((f"&endif_{name}&", ""))
            results.append((f"&ifn_{name}&", "/*"))
            results.append((f"&endifn_{name}&", "*/"))
        else:
            # Condition is FALSE - if blocks are commented out, ifn blocks are active
            results.append((f"&if_{name}&", "/*"))
            results.append((f"&endif_{name}&", "*/"))
            results.append((f"&ifn_{name}&", ""))
            results.append((f"&endifn_{name}&", ""))

        return results

    def _parse_table_option(self, line: str) -> list:
        """
        Parse a -> (table location) option line.

        Format: -> tablename &database_var& description

        Creates 2 placeholders:
        - &tablename& -> database..tablename
        - &db-tablename& -> database

        Args:
            line: Option line starting with '->'

        Returns:
            List of (placeholder, value) tuples (2 items) or empty list if invalid
        """
        # Remove '->' prefix and strip
        content = line[2:].strip()

        if not content:
            logging.warning(f"Empty -> option: {line}")
            return []

        # Split into parts - first word is table name, second is &dbvar&
        parts = content.split()
        if len(parts) < 2:
            logging.warning(f"Invalid -> option format (need tablename and &dbvar&): {line}")
            return []

        table_name = parts[0]
        db_var = parts[1]

        # The db_var should be a placeholder like &dbpro&
        # We need to resolve it first using existing options
        db_value = self._resolve_placeholder(db_var)

        if not db_value:
            logging.warning(f"Could not resolve database variable {db_var} in -> option: {line}")
            return []

        # Generate 2 placeholders
        results = []
        results.append((f"&{table_name}&", f"{db_value}..{table_name}"))
        results.append((f"&db-{table_name}&", db_value))

        return results

    def _resolve_placeholder(self, text: str) -> str:
        """
        Resolve placeholders in text using current options.

        Args:
            text: Text that may contain &placeholder& patterns

        Returns:
            Text with placeholders resolved
        """
        if '&' not in text:
            return text

        result = text
        for placeholder, value in self._options.items():
            if placeholder in result:
                result = result.replace(placeholder, value)

        return result

    def _load_option_file_combined(self, filepath: str) -> tuple:
        """
        Load and parse a single option file in one pass (performance optimization).

        Returns both v:/c: options AND raw '->' lines in a single file read.
        The '->' lines are returned unparsed since they need v:/c: options to be
        loaded first before they can be resolved.

        Args:
            filepath: Path to option file

        Returns:
            Tuple of (options_list, table_lines_list) where:
            - options_list: List of (placeholder, value) tuples for v:/c: options
            - table_lines_list: List of raw '->' lines to be parsed later
        """
        options = []
        table_lines = []

        if not os.path.exists(filepath):
            logging.debug(f"Option file not found: {filepath}")
            return options, table_lines

        try:
            with open(filepath, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.rstrip('\r\n')

                    # Skip empty lines and comments
                    if not line.strip() or line.strip().startswith('#'):
                        continue

                    # Parse based on line prefix
                    # v: = static value (compiled into SQL)
                    # V: = dynamic value (queried from &options& table at runtime)
                    # c: = static on/off (compiled as &if_/&endif_ comment blocks)
                    # C: = dynamic on/off (queried from &options&.act_flg at runtime)
                    prefix = line[:2] if len(line) >= 2 else ""

                    if prefix == 'v:':
                        # Static value - compile into SQL
                        placeholder, value = self._parse_v_option(line)
                        if placeholder:
                            options.append((placeholder, value))

                    elif prefix == 'V:':
                        # Dynamic value - NOT compiled, queried at runtime
                        # We still parse it to know it exists, but value is empty
                        # (actual value comes from database)
                        pass

                    elif prefix == 'c:':
                        # Static on/off - compile as &if_/&endif_ blocks
                        items = self._parse_c_option(line)
                        options.extend(items)

                    elif prefix == 'C:':
                        # Dynamic on/off - NOT compiled, queried at runtime
                        # (actual act_flg comes from database)
                        pass

                    elif line.startswith('->'):
                        # Table options - store raw line for later processing
                        table_lines.append(line)

        except Exception as e:
            logging.error(f"Error reading option file {filepath}: {e}")

        return options, table_lines

    def _load_option_file(self, filepath: str) -> list:
        """
        Load and parse a single option file (v:/c: options only).

        Args:
            filepath: Path to option file

        Returns:
            List of (placeholder, value) tuples
        """
        options, _ = self._load_option_file_combined(filepath)
        return options

    def _load_table_options(self, filepath: str) -> list:
        """
        Load table (-> ) options from a file. Must be called after v:/c: options are loaded.

        Args:
            filepath: Path to option file (or table_locations file)

        Returns:
            List of (placeholder, value) tuples
        """
        results = []

        if not os.path.exists(filepath):
            return results

        try:
            with open(filepath, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.rstrip('\r\n')

                    if line.startswith('->'):
                        items = self._parse_table_option(line)
                        results.extend(items)

        except Exception as e:
            logging.error(f"Error reading table options from {filepath}: {e}")

        return results

    def _get_cache_filepath(self) -> str:
        """
        Get the cache file path for merged options.

        Format: {SQL_SOURCE}/CSS/Setup/temp/{profile}.options.tmp

        The cache is stored within the SQL source tree so each version (current, 94, 93, etc.)
        has its own cache directory.

        Returns:
            Path to cache file
        """
        profile = self.config.get('PROFILE_NAME', 'default')
        sql_source = self.config.get('SQL_SOURCE', os.getcwd())

        # Sanitize profile name (replace \ and . with _)
        profile_safe = str(profile).replace('\\', '_').replace('.', '_')

        cache_name = f"{profile_safe}.options.tmp"

        # Use {SQL_SOURCE}/CSS/Setup/temp/ directory
        cache_dir = os.path.join(sql_source, 'CSS', 'Setup', 'temp')

        # Create directory if it doesn't exist
        os.makedirs(cache_dir, exist_ok=True)

        return os.path.join(cache_dir, cache_name)

    def _is_cache_valid(self) -> bool:
        """
        Check if cache file exists and is not expired (24 hour TTL).

        Returns:
            True if cache is valid, False otherwise
        """
        cache_path = self._get_cache_filepath()

        if not os.path.exists(cache_path):
            return False

        # Check age
        try:
            mtime = os.path.getmtime(cache_path)
            age_minutes = (datetime.datetime.now().timestamp() - mtime) / 60

            if age_minutes > self._cache_ttl_minutes:
                logging.debug(f"Cache expired (age: {age_minutes:.1f} minutes)")
                return False

            return True

        except Exception as e:
            logging.warning(f"Error checking cache validity: {e}")
            return False

    def _save_cache(self):
        """Save current options to cache file."""
        cache_path = self._get_cache_filepath()

        try:
            with open(cache_path, 'w', encoding='utf-8') as f:
                for placeholder, value in self._options.items():
                    # Format: placeholder=value (one per line)
                    f.write(f"{placeholder}={value}\n")

            logging.debug(f"Saved options cache: {cache_path}")

        except Exception as e:
            logging.warning(f"Failed to save cache: {e}")

    def _load_cache(self) -> bool:
        """
        Load options from cache file.

        Returns:
            True if loaded successfully, False otherwise
        """
        cache_path = self._get_cache_filepath()

        try:
            self._options = {}

            with open(cache_path, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.rstrip('\r\n')
                    if '=' in line:
                        placeholder, value = line.split('=', 1)
                        self._options[placeholder] = value

            logging.debug(f"Loaded {len(self._options)} options from cache")
            return True

        except Exception as e:
            logging.warning(f"Failed to load cache: {e}")
            return False

    def generate_option_files(self, force_rebuild: bool = False) -> bool:
        """
        Load and merge option files with caching.

        Hierarchy (in precedence order - later overrides earlier):
        1. options.def                  (default values) - REQUIRED
        2. options.{company}            (e.g., options.101) - REQUIRED
        3. options.{company}.{server}   (e.g., options.101.GONZO) - OPTIONAL
        4. table_locations              (table location mappings) - REQUIRED

        All files must be in {SQL_SOURCE}\\CSS\\Setup.

        Args:
            force_rebuild: If True, ignore cache and rebuild

        Returns:
            True if successful, False otherwise
        """
        # Check cache first
        if not force_rebuild and self._is_cache_valid():
            if self._load_cache():
                self._was_rebuilt = False
                return True

        # Mark that we're rebuilding
        self._was_rebuilt = True

        # Reset loaded files list
        self._loaded_files = []

        # Get base path for option files: {SQL_SOURCE}\CSS\Setup
        path_append = self.config.get('SQL_SOURCE', os.getcwd())
        base_path = os.path.join(path_append, 'CSS', 'Setup')

        # Normalize path separators for Windows
        base_path = os.path.normpath(base_path)

        # Store setup directory for later access
        self._setup_directory = base_path

        company = str(self.config.get('COMPANY', ''))
        server = str(self.config.get('PROFILE_NAME', ''))

        # Helper to print search context on error
        def print_search_context():
            print(f"\nSearch context:")
            print(f"  Location: {base_path}")
            print(f"  Company:  {company if company else '(not set)'}")
            print(f"  Profile:  {server if server else '(not set)'}")

        # Build list of option files in priority order (lowest to highest)
        # Later files override earlier files
        option_files = []

        # 1. options.def - REQUIRED
        opt_def = os.path.join(base_path, "options.def")
        if not os.path.exists(opt_def):
            logging.error(f"REQUIRED file not found: {opt_def}")
            print(f"\nERROR: Required file 'options.def' not found.")
            print_search_context()
            print(f"\nExpected file: {opt_def}")
            return False
        option_files.append(opt_def)

        # 2. options.{company} - REQUIRED
        if not company:
            logging.error("Company (COMPANY) not specified in configuration")
            print("\nERROR: Company (COMPANY) not specified in profile configuration.")
            print_search_context()
            return False

        opt_company = os.path.join(base_path, f"options.{company}")
        if not os.path.exists(opt_company):
            logging.error(f"REQUIRED file not found: {opt_company}")
            print(f"\nERROR: Required file 'options.{company}' not found.")
            print_search_context()
            print(f"\nExpected file: {opt_company}")
            return False
        option_files.append(opt_company)

        # 3. options.{company}.{server} - OPTIONAL
        if company and server:
            opt_server = os.path.join(base_path, f"options.{company}.{server}")
            if os.path.exists(opt_server):
                option_files.append(opt_server)
            else:
                logging.debug(f"Optional server file not found (this is OK): {opt_server}")

        # Load options - LATER files override EARLIER files
        # Performance optimization: single-pass loading collects both v:/c: options
        # AND -> table lines, avoiding re-reading files
        self._options = {}
        self._option_sources = {}
        collected_table_lines = []  # Store (filepath, line) tuples for later processing

        for filepath in option_files:
            logging.debug(f"Loading option file: {filepath}")
            items, table_lines = self._load_option_file_combined(filepath)
            for placeholder, value in items:
                # Later values override earlier values
                self._options[placeholder] = value
                self._option_sources[placeholder] = filepath
                logging.debug(f"Set option: {placeholder} = {value}")
            # Collect -> lines for later processing (need v:/c: options to resolve db vars)
            for line in table_lines:
                collected_table_lines.append((filepath, line))

        # 4. table_locations - REQUIRED
        # Load table options (need v:/c: options first to resolve db vars)
        table_file = os.path.join(base_path, "table_locations")
        if not os.path.exists(table_file):
            logging.error(f"REQUIRED file not found: {table_file}")
            print(f"\nERROR: Required file 'table_locations' not found.")
            print_search_context()
            print(f"\nExpected file: {table_file}")
            return False

        logging.debug(f"Loading table locations: {table_file}")
        items = self._load_table_options(table_file)
        for placeholder, value in items:
            # Table locations can override any existing placeholders
            self._options[placeholder] = value
            self._option_sources[placeholder] = table_file

        # Now process -> lines from option files (these override table_locations)
        # These were collected during single-pass loading above
        for filepath, line in collected_table_lines:
            items = self._parse_table_option(line)
            for placeholder, value in items:
                self._options[placeholder] = value
                self._option_sources[placeholder] = filepath

        # Track all loaded files (option files + table_locations)
        self._loaded_files = option_files + [table_file]

        # Add profile-based placeholders (these come from settings.json, not option files)
        # &lang& = DEFAULT_LANGUAGE (or IBSLANG for backwards compatibility)
        # &cmpy& = COMPANY
        lang_value = self.config.get('DEFAULT_LANGUAGE') or self.config.get('IBSLANG') or ''
        cmpy_value = self.config.get('COMPANY') or ''
        if lang_value:
            self._options['&lang&'] = str(lang_value)
            self._option_sources['&lang&'] = 'settings.json'
        if cmpy_value:
            self._options['&cmpy&'] = str(cmpy_value)
            self._option_sources['&cmpy&'] = 'settings.json'

        logging.info(f"Loaded {len(self._options)} options from {len(self._loaded_files)} files")

        # Save to cache
        self._save_cache()

        return True

    def replace_options(self, text: str, sequence: int = -1) -> str:
        """
        Replace all placeholders in text.

        Args:
            text: Text containing &placeholder& patterns
            sequence: Optional sequence number to replace @sequence@

        Returns:
            Text with all placeholders resolved
        """
        if not text:
            return text

        result = text

        # Replace @sequence@ first (if provided)
        if sequence >= 0:
            result = result.replace('@sequence@', str(sequence))
            result = result.replace('@SEQUENCE@', str(sequence))

        # Replace &placeholder& patterns
        if '&' not in result:
            return result

        # Use pre-compiled regex to find and replace only placeholders that exist in the text
        # This is faster than iterating through all 500+ options
        # Pattern matches valid placeholder names: letters, numbers, underscores, hyphens, hash signs
        # Examples: &users&, &db-ba_agent_activity&, &w#sys_globals&
        # This avoids matching literal & in SQL strings like '%[!,@,#,$,%,^,&,(,)]%'
        def replacer(match):
            return self._options.get(match.group(0), match.group(0))
        result = _PLACEHOLDER_PATTERN.sub(replacer, result)

        return result

    def replace_options_in_list(self, lines: list, sequence: int = -1) -> list:
        """
        Replace placeholders in a list of strings.

        Args:
            lines: List of strings
            sequence: Optional sequence number

        Returns:
            List with placeholders resolved
        """
        return [self.replace_options(line, sequence) for line in lines]

    def get_option(self, name: str) -> str:
        """
        Get a specific option value.

        Args:
            name: Option name (without & delimiters)

        Returns:
            Option value or empty string if not found
        """
        placeholder = f"&{name}&"
        return self._options.get(placeholder, '')

    def set_option(self, name: str, value: str):
        """
        Set a specific option value.

        Args:
            name: Option name (without & delimiters)
            value: Option value
        """
        placeholder = f"&{name}&"
        self._options[placeholder] = value

    def was_rebuilt(self) -> bool:
        """
        Check if the options were rebuilt or loaded from cache.

        Returns:
            True if options were rebuilt, False if loaded from cache
        """
        return self._was_rebuilt

    def get_cache_filepath(self) -> str:
        """
        Get the cache file path for public access.

        Returns:
            Path to cache file
        """
        return self._get_cache_filepath()

    def get_setup_directory(self) -> str:
        """
        Get the setup directory path where option files are located.

        Returns:
            Path to setup directory (e.g., {SQL_SOURCE}/CSS/Setup)
        """
        return self._setup_directory or ""

    def get_loaded_files(self) -> list:
        """
        Get the list of option files that were loaded.

        Returns:
            List of full file paths that were loaded
        """
        return self._loaded_files.copy()

    def get_option_source(self, placeholder: str) -> str:
        """
        Get the source file path for a specific option placeholder.

        Args:
            placeholder: The option placeholder (e.g., '&users&' or '&gclog12&')

        Returns:
            Full file path where the option was defined, or empty string if not found
        """
        return self._option_sources.get(placeholder, "")


# =============================================================================
# CHANGE LOG FUNCTIONS
# =============================================================================

def is_changelog_enabled(config: dict, force_check: bool = False) -> tuple:
    """
    Check if changelog is enabled by querying the database (cached per session).

    Checks two things:
    1. gclog12 option act_flg = '+' in &options& table
    2. ba_gen_chg_log_new stored procedure exists in &dbpro&

    The result is cached in the IBS_CHANGELOG_STATUS environment variable to avoid
    repeated database queries. This is especially important when runcreate calls
    runsql hundreds of times.

    Any error (table doesn't exist, database doesn't exist, connection error)
    silently returns (False, message) - no errors logged or shown to user.

    Args:
        config: Configuration dictionary with database connection info and SQL_SOURCE
        force_check: If True, bypass cache and re-check the database

    Returns:
        Tuple of (enabled: bool, message: str)
        - (True, "Changelog enabled") if both checks pass
        - (False, reason) if either check fails or error occurs
    """
    # Always disabled in raw mode
    if is_raw_mode(config):
        os.environ['IBS_CHANGELOG_STATUS'] = '0'
        return False, "Raw mode - changelog disabled"

    # Check session cache first (unless force_check is True)
    if not force_check:
        cached = os.environ.get('IBS_CHANGELOG_STATUS')
        if cached is not None:
            return cached == '1', "Cached result"

    try:
        # Need to resolve placeholders first
        options = Options(config)
        if not options.generate_option_files():
            os.environ['IBS_CHANGELOG_STATUS'] = '0'
            return False, "Failed to load options files"

        # Resolve &dbpro& for the database to query
        dbpro = options.replace_options("&dbpro&")
        if dbpro == "&dbpro&":
            os.environ['IBS_CHANGELOG_STATUS'] = '0'
            return False, "&dbpro& placeholder not resolved"

        # Check both conditions in a single query (single connection):
        # 1. gclog12 option act_flg = '+' in options table
        # 2. ba_gen_chg_log_new stored procedure exists
        combined_query = options.replace_options(
            "select 'OPT=' + act_flg from &options& where id = 'gclog12'\n"
            "go\n"
            "select 'PROC=' + convert(varchar, count(*)) from sysobjects "
            "where name = 'ba_gen_chg_log_new' and type = 'P'"
        )

        success, output = execute_sql_native(
            host=config.get('HOST', ''),
            port=config.get('PORT', 5000),
            username=config.get('USERNAME', ''),
            password=config.get('PASSWORD', ''),
            database=dbpro,  # Query against &dbpro& database
            platform=config.get('PLATFORM', 'SYBASE'),
            sql_content=combined_query
        )

        if not success:
            os.environ['IBS_CHANGELOG_STATUS'] = '0'
            return False, "Could not query changelog status (table may not exist)"

        if 'OPT=+' not in output:
            os.environ['IBS_CHANGELOG_STATUS'] = '0'
            return False, "gclog12 option is disabled (act_flg != '+')"

        if 'PROC=1' not in output:
            os.environ['IBS_CHANGELOG_STATUS'] = '0'
            return False, "ba_gen_chg_log_new stored procedure not found in " + dbpro

        # Cache success and return
        os.environ['IBS_CHANGELOG_STATUS'] = '1'
        return True, "Changelog enabled"

    except Exception as e:
        os.environ['IBS_CHANGELOG_STATUS'] = '0'
        return False, f"Error checking changelog: {str(e)}"


def insert_changelog_entry(config: dict, command_type: str, command: str,
                           description: str = None, upgrade_no: str = '') -> tuple:
    """
    Insert an entry into the change log by calling ba_gen_chg_log_new.

    Args:
        config: Configuration dictionary with database connection info
        command_type: Type of command (e.g., 'RUNSQL', 'ISQLLINE', 'TEST')
        command: The command that was executed
        description: Optional description (defaults to 'User username ...')
        upgrade_no: Optional upgrade reference number

    Returns:
        Tuple of (success: bool, message: str)
    """
    try:
        # Get OS username for changelog (not database username)
        username = os.environ.get('USERNAME', os.environ.get('USER', 'unknown'))

        # Build description if not provided
        if description is None:
            description = f"User {username} executed {command_type.lower()}"

        # Escape single quotes
        command_escaped = command.replace("'", "''")
        description_escaped = description.replace("'", "''")

        # Need to resolve placeholders
        options = Options(config)
        if not options.generate_option_files():
            return False, "Failed to load options files"

        # Resolve &dbpro& for the database to execute against
        dbpro = options.replace_options("&dbpro&")
        if dbpro == "&dbpro&":
            return False, "&dbpro& placeholder not resolved"

        # Build the exec statement
        database = config.get('DATABASE', '')
        server = config.get('PROFILE_NAME', config.get('HOST', ''))
        company = config.get('COMPANY', '')

        cmd_str = f"{command_type.lower()} {command_escaped} {database} {server} {company}"

        # Execute ba_gen_chg_log_new directly (no need to resolve, we're in &dbpro& already)
        exec_sql = f"exec ba_gen_chg_log_new '', '{description_escaped}', '{command_type}', '', '{cmd_str}', '{upgrade_no}', 'X'"

        success, output = execute_sql_native(
            host=config.get('HOST', ''),
            port=config.get('PORT', 5000),
            username=config.get('USERNAME', ''),
            password=config.get('PASSWORD', ''),
            database=dbpro,  # Execute against &dbpro& database
            platform=config.get('PLATFORM', 'SYBASE'),
            sql_content=exec_sql
        )

        if success:
            return True, "Changelog entry inserted"
        else:
            return False, f"Failed to insert changelog entry: {output[:100] if output else 'unknown error'}"

    except Exception as e:
        return False, f"Error inserting changelog: {str(e)}"


# =============================================================================
# TABLE LOCATIONS
# =============================================================================

def get_table_locations_path(config: dict) -> str:
    """
    Get the path to the table_locations file.

    Args:
        config: Configuration dictionary with SQL_SOURCE

    Returns:
        Full path to {SQL_SOURCE}/CSS/Setup/table_locations
    """
    sql_source = config.get('SQL_SOURCE', os.getcwd())
    return os.path.join(sql_source, 'CSS', 'Setup', 'table_locations')


def parse_table_locations(source_path: str, options: 'Options') -> list:
    """
    Parse table_locations source file and return list of tab-delimited rows.

    The table_locations source file maps logical table names to physical database
    locations using &placeholder& syntax. This function parses the file and resolves
    all placeholders using the current options.

    SOURCE FILE FORMAT:
        Lines starting with '->' define table mappings:
            -> table_name    &db_placeholder&    description

        Example:
            -> users         &dbtbl&             User accounts
            -> options       &dbibs&             System options
            -> ba_users_get  &dbpro&             User lookup procedure

    OUTPUT FORMAT:
        Each parsed line becomes a tab-delimited string with 4 fields:
            table_name\tlogical_db\tphysical_db\tdb_table

        Example:
            users\tdbtbl\tsbnmaster\tsbnmaster..users

    These fields map directly to the table_locations table columns:
        - table_name:  The logical table/procedure name
        - logical_db:  The option placeholder name (without &)
        - physical_db: The resolved database name
        - db_table:    Full qualified name (physical_db..table_name)

    Args:
        source_path: Path to {SQL_SOURCE}/CSS/Setup/table_locations file
        options: Options instance for resolving &placeholder& values

    Returns:
        List of tab-delimited strings ready for database insertion
    """
    rows = []

    with open(source_path, 'r', encoding='utf-8') as f:
        for line in f:
            line = line.rstrip('\r\n')

            # Only process lines starting with '->'
            if not line.startswith('->'):
                continue

            # Extract table name (between '->' and first '&')
            content = line[2:]  # Remove '->'
            first_amp = content.find('&')
            if first_amp == -1:
                continue

            table_name = content[:first_amp].replace('\t', ' ').strip()

            # Extract option placeholder (between first '&' and second '&')
            second_amp = content.find('&', first_amp + 1)
            if second_amp == -1:
                continue

            opt_placeholder = content[first_amp:second_amp + 1]  # e.g., &dbpro&
            opt_name = opt_placeholder.strip('&')  # e.g., dbpro

            # Resolve the placeholder to get physical database name
            db_name = options.replace_options(opt_placeholder)

            # Build full qualified name: database..table
            full_name = f"{db_name}..{table_name}"

            rows.append(f"{table_name}\t{opt_name}\t{db_name}\t{full_name}")

    return rows


def compile_table_locations(config: dict, options: 'Options' = None, output_handle=None) -> tuple:
    """
    Compile table_locations source file into the database.

    This function reads the table_locations source file, parses the table-to-database
    mappings, and inserts them into the table_locations table using SQL INSERT statements.

    PROCESS:
        1. Load options (if not provided) to resolve &placeholder& values
        2. Parse {SQL_SOURCE}/CSS/Setup/table_locations source file
        3. Resolve all &db_placeholder& references to actual database names
        4. Truncate the target table_locations table
        5. Insert all rows using SQL INSERT statements

    SOURCE FILE:
        {SQL_SOURCE}/CSS/Setup/table_locations

        Format:
            -> table_name    &db_placeholder&    description

    TARGET TABLE:
        &table_locations& (typically ibs..table_locations)

        Schema:
            table_name   varchar(40)   - Logical table/procedure name
            logical_db   varchar(8)    - Option placeholder name (e.g., "dbtbl")
            physical_db  varchar(32)   - Resolved database name (e.g., "sbnmaster")
            db_table     varchar(100)  - Full path (e.g., "sbnmaster..users")

    NOTE:
        This function uses SQL INSERT statements instead of BCP (Bulk Copy Program)
        to avoid the 255 character limit in freebcp.

    Args:
        config: Configuration dictionary with connection info (HOST, PORT, USERNAME,
                PASSWORD, SQL_SOURCE, PLATFORM)
        options: Optional Options instance for resolving placeholders. If not provided,
                 a new Options instance will be created from the config.
        output_handle: Optional file handle for output. If None, prints to console.

    Returns:
        Tuple of (success: bool, message: str, row_count: int)
        - success: True if all rows were inserted successfully
        - message: Status message or error description
        - row_count: Number of rows inserted (0 on failure)
    """
    def log(msg):
        if output_handle:
            output_handle.write(msg + '\n')
            output_handle.flush()
        else:
            print(msg)

    # Create options if not provided
    if options is None:
        options = Options(config)
        if not options.generate_option_files():
            return False, "Failed to load options files", 0

    # Get table_locations file path
    locations_file = get_table_locations_path(config)
    if not os.path.exists(locations_file):
        return False, f"table_locations file not found: {locations_file}", 0

    # Resolve &table_locations& to get target table
    target_table = options.replace_options("&table_locations&")
    if target_table == "&table_locations&":
        return False, "Could not resolve &table_locations& placeholder", 0

    # Parse the file
    rows = parse_table_locations(locations_file, options)
    if not rows:
        return False, "No table location entries found", 0

    # Extract database from target_table (e.g., "ibs..table_locations" -> "ibs")
    target_db = target_table.split('..')[0] if '..' in target_table else None

    # Truncate target table
    success, output = execute_sql_native(
        host=config.get('HOST'),
        port=config.get('PORT'),
        username=config.get('USERNAME'),
        password=config.get('PASSWORD'),
        database=target_db,
        platform=config.get('PLATFORM', 'SYBASE'),
        sql_content=f"truncate table {target_table}"
    )

    if not success:
        return False, f"Failed to truncate table: {output}", 0

    # Insert using SQL INSERT statements
    # Rows are tab-delimited: table_name\topt_name\tdb_name\tfull_name
    sql_lines = []
    for row in rows:
        parts = row.split('\t')
        if len(parts) != 4:
            continue
        table_name, opt_name, db_name, full_name = parts
        # Escape single quotes
        table_name = table_name.replace("'", "''")
        opt_name = opt_name.replace("'", "''")
        db_name = db_name.replace("'", "''")
        full_name = full_name.replace("'", "''")
        sql_lines.append(f"insert {target_table} (table_name, logical_db, physical_db, db_table) values ('{table_name}', '{opt_name}', '{db_name}', '{full_name}')")

    log(f"Inserting {len(sql_lines)} rows into {target_table}...")

    # Execute in batches of 1000
    batch_size = 1000
    total = len(sql_lines)
    for i in range(0, total, batch_size):
        batch = sql_lines[i:i + batch_size]
        end_idx = min(i + batch_size, total)
        log(f"  Inserting {i + 1}-{end_idx}")
        sql_content = "\n".join(batch)
        success, output = execute_sql_native(
            host=config.get('HOST'),
            port=config.get('PORT'),
            username=config.get('USERNAME'),
            password=config.get('PASSWORD'),
            database=target_db,
            platform=config.get('PLATFORM', 'SYBASE'),
            sql_content=sql_content
        )
        if not success:
            return False, f"Insert failed: {output}", 0

    return True, f"Compiled into {target_table}", len(rows)


# =============================================================================
# ACTIONS COMPILE
# =============================================================================

def get_actions_path(config: dict) -> str:
    """
    Get the path to the actions source file.

    Args:
        config: Configuration dictionary with SQL_SOURCE

    Returns:
        Full path to {SQL_SOURCE}/CSS/Setup/actions
    """
    sql_source = config.get('SQL_SOURCE', os.getcwd())
    return os.path.join(sql_source, 'CSS', 'Setup', 'actions')


def get_actions_dtl_path(config: dict) -> str:
    """
    Get the path to the actions_dtl source file.

    Args:
        config: Configuration dictionary with SQL_SOURCE

    Returns:
        Full path to {SQL_SOURCE}/CSS/Setup/actions_dtl
    """
    sql_source = config.get('SQL_SOURCE', os.getcwd())
    return os.path.join(sql_source, 'CSS', 'Setup', 'actions_dtl')


def compile_actions(config: dict, options: 'Options' = None, output_handle=None) -> tuple:
    """
    Compile actions source files into the database.

    This function reads the actions and actions_dtl source files, parses them,
    and loads them into work tables using BCP (freebcp). Then it
    executes the ba_compile_actions stored procedure to move data to final tables.

    PROCESS:
        1. Load options (if not provided) to resolve &placeholder& values
        2. Parse {SQL_SOURCE}/CSS/Setup/actions source file
        3. Parse {SQL_SOURCE}/CSS/Setup/actions_dtl source file
        4. Truncate work tables (w#actions, w#actions_dtl)
        5. Load all rows via BCP (freebcp)
        6. Execute ba_compile_actions stored procedure

    SOURCE FILES:
        {SQL_SOURCE}/CSS/Setup/actions
            Lines starting with :> are action definitions.
            Format: :>ACTION_LINE_CONTENT

        {SQL_SOURCE}/CSS/Setup/actions_dtl
            Fixed-width format for action details.
            Format: :>AAAA BBB CCC DDDDD EEE description
            Where columns are: action(2-5), index(7-9), subindex(11-13),
                              textid(15-19), lang(21-23), description(24+)

    TARGET TABLES:
        w#actions - Work table for action headers
        w#actions_dtl - Work table for action details

    Args:
        config: Configuration dictionary with connection info
        options: Optional Options instance for resolving placeholders
        output_handle: Optional file handle for output. If None, prints to console.

    Returns:
        Tuple of (success: bool, message: str, count: int)
    """
    def log(msg):
        if output_handle:
            output_handle.write(msg + '\n')
            output_handle.flush()
        else:
            print(msg)

    # Create options if not provided
    if options is None:
        options = Options(config)
        if not options.generate_option_files():
            return False, "Failed to load options files", 0

    # Get source file paths
    actions_file = get_actions_path(config)
    actions_dtl_file = get_actions_dtl_path(config)

    if not os.path.exists(actions_file):
        return False, f"actions file not found: {actions_file}", 0

    if not os.path.exists(actions_dtl_file):
        return False, f"actions_dtl file not found: {actions_dtl_file}", 0

    # Resolve work table names
    w_actions = options.replace_options("&w#actions&")
    w_actions_dtl = options.replace_options("&w#actions_dtl&")
    dbpro = options.replace_options("&dbpro&")

    if w_actions == "&w#actions&":
        return False, "Could not resolve &w#actions& placeholder", 0
    if w_actions_dtl == "&w#actions_dtl&":
        return False, "Could not resolve &w#actions_dtl& placeholder", 0

    # Extract database from work table
    work_db = w_actions.split('..')[0] if '..' in w_actions else None

    # Parse actions file (header)
    # Lines starting with :> after placeholder resolution
    header_rows = []
    row_num = 0
    with open(actions_file, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            # Check if line starts with & or :>
            if line.startswith('&') or line.startswith(':>'):
                # Resolve placeholders
                line = options.replace_options(line)
                # After resolution, only keep lines that start with :>
                if line.strip().startswith(':>'):
                    row_num += 1
                    # Format: row_num\tline
                    header_rows.append((row_num, line))

    # Parse actions_dtl file (detail)
    # Fixed-width format after :>
    detail_rows = []
    with open(actions_dtl_file, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            line = line.strip()
            if len(line) < 3:
                continue
            # Check if line starts with & or :>
            if line.startswith('&') or line.startswith(':>'):
                # Resolve placeholders
                line = options.replace_options(line)
                # After resolution, only keep lines that start with :>
                if line.strip().startswith(':>'):
                    # Parse fixed-width columns
                    # :>AAAA BBB CCC DDDDD EEE description
                    # Cols: 2-5, 7-9, 11-13, 15-19, 21-23, 24+
                    content = line.strip()
                    try:
                        col1 = content[2:6].strip()   # action (4 chars)
                        col2 = content[7:10].strip()  # index (3 chars)
                        col3 = content[11:14].strip() # subindex (3 chars)
                        col4 = content[15:20].strip() # textid (5 chars)
                        col5 = content[21:24].strip() # lang (3 chars)
                        col6 = content[24:].strip()   # description (rest)
                        detail_rows.append((col1, col2, col3, col4, col5, col6))
                    except IndexError:
                        # Line too short, skip
                        continue

    log(f"Parsed {len(header_rows)} action headers and {len(detail_rows)} action details")

    # Truncate work tables
    log(f"Truncating {w_actions}...")
    success, output = execute_sql_native(
        host=config.get('HOST'),
        port=config.get('PORT'),
        username=config.get('USERNAME'),
        password=config.get('PASSWORD'),
        database=work_db,
        platform=config.get('PLATFORM', 'SYBASE'),
        sql_content=f"truncate table {w_actions}"
    )
    if not success:
        return False, f"Failed to truncate {w_actions}: {output}", 0

    log(f"Truncating {w_actions_dtl}...")
    success, output = execute_sql_native(
        host=config.get('HOST'),
        port=config.get('PORT'),
        username=config.get('USERNAME'),
        password=config.get('PASSWORD'),
        database=work_db,
        platform=config.get('PLATFORM', 'SYBASE'),
        sql_content=f"truncate table {w_actions_dtl}"
    )
    if not success:
        return False, f"Failed to truncate {w_actions_dtl}: {output}", 0

    # BCP load action headers and details into work tables
    temp_dir = tempfile.gettempdir()
    pid = os.getpid()
    hdr_file = os.path.join(temp_dir, f"bcp_w_actions_{pid}.dat")
    dtl_file = os.path.join(temp_dir, f"bcp_w_actions_dtl_{pid}.dat")

    try:
        # Write and BCP-in action headers
        if header_rows:
            hdr_bcp_rows = [(str(row_num), line) for row_num, line in header_rows]
            written = write_bcp_data_file(hdr_bcp_rows, hdr_file)
            log(f"BCP loading {written} rows into {w_actions}...")
            success, output = execute_bcp(
                host=config.get('HOST'),
                port=int(config.get('PORT')),
                username=config.get('USERNAME'),
                password=config.get('PASSWORD'),
                table=w_actions,
                direction="in",
                file_path=hdr_file,
                platform=config.get('PLATFORM', 'SYBASE')
            )
            if not success:
                return False, f"Failed to BCP load action headers: {output}", 0
            rows_loaded = int(output) if output.isdigit() else 0
            if rows_loaded != len(header_rows):
                log(f"  WARNING: expected {len(header_rows)} header rows, BCP reported {rows_loaded}")

        # Write and BCP-in action details
        if detail_rows:
            dtl_bcp_rows = [(col1, col2, col3, col4, col5, col6)
                            for col1, col2, col3, col4, col5, col6 in detail_rows]
            written = write_bcp_data_file(dtl_bcp_rows, dtl_file)
            log(f"BCP loading {written} rows into {w_actions_dtl}...")
            success, output = execute_bcp(
                host=config.get('HOST'),
                port=int(config.get('PORT')),
                username=config.get('USERNAME'),
                password=config.get('PASSWORD'),
                table=w_actions_dtl,
                direction="in",
                file_path=dtl_file,
                platform=config.get('PLATFORM', 'SYBASE')
            )
            if not success:
                return False, f"Failed to BCP load action details: {output}", 0
            rows_loaded = int(output) if output.isdigit() else 0
            if rows_loaded != len(detail_rows):
                log(f"  WARNING: expected {len(detail_rows)} detail rows, BCP reported {rows_loaded}")

    finally:
        # Clean up temp files
        for f in (hdr_file, dtl_file):
            if os.path.exists(f):
                os.remove(f)

    # Execute ba_compile_actions stored procedure
    log(f"Executing {dbpro}..ba_compile_actions...")
    success, output = execute_sql_native(
        host=config.get('HOST'),
        port=config.get('PORT'),
        username=config.get('USERNAME'),
        password=config.get('PASSWORD'),
        database=dbpro,
        platform=config.get('PLATFORM', 'SYBASE'),
        sql_content=f"exec {dbpro}..ba_compile_actions"
    )

    total_rows = len(header_rows) + len(detail_rows)
    if success:
        return True, f"Compiled {len(header_rows)} headers and {len(detail_rows)} details", total_rows
    else:
        return False, f"ba_compile_actions failed: {output}", 0


# =============================================================================
# REQUIRED FIELDS COMPILE
# =============================================================================

def get_required_fields_path(config: dict) -> str:
    """
    Get the path to the required_fields source file.

    Args:
        config: Configuration dictionary with SQL_SOURCE

    Returns:
        Full path to {SQL_SOURCE}/CSS/Setup/css.required_fields
    """
    sql_source = config.get('SQL_SOURCE', os.getcwd())
    return os.path.join(sql_source, 'CSS', 'Setup', 'css.required_fields')


def get_required_fields_dtl_path(config: dict) -> str:
    """
    Get the path to the required_fields_dtl source file.

    Args:
        config: Configuration dictionary with SQL_SOURCE

    Returns:
        Full path to {SQL_SOURCE}/CSS/Setup/css.required_fields_dtl
    """
    sql_source = config.get('SQL_SOURCE', os.getcwd())
    return os.path.join(sql_source, 'CSS', 'Setup', 'css.required_fields_dtl')


def compile_required_fields(config: dict, options: 'Options' = None, output_handle=None) -> tuple:
    """
    Compile required_fields source files into the database.

    This function reads the required_fields and required_fields_dtl source files,
    parses them, and inserts them into work tables using SQL INSERT statements.
    Then it executes the i_required_fields_install stored procedure.

    PROCESS:
        1. Load options (if not provided) to resolve &placeholder& values
        2. Parse {SQL_SOURCE}/CSS/Setup/css.required_fields source file
        3. Parse {SQL_SOURCE}/CSS/Setup/css.required_fields_dtl source file
        4. Delete from work tables (w#i_required_fields, w#i_required_fields_dtl)
        5. Insert all rows using SQL INSERT statements
        6. Execute i_required_fields_install stored procedure

    SOURCE FILES:
        {SQL_SOURCE}/CSS/Setup/css.required_fields
            Tab-delimited with 6 columns:
            s#rf, name, title, helptxt, inact_flg, s#sk

        {SQL_SOURCE}/CSS/Setup/css.required_fields_dtl
            Tab-delimited with 30 columns

    TARGET TABLES:
        w#i_required_fields - Work table for required field headers
        w#i_required_fields_dtl - Work table for required field details

    Args:
        config: Configuration dictionary with connection info
        options: Optional Options instance for resolving placeholders
        output_handle: Optional file handle for output. If None, prints to console.

    Returns:
        Tuple of (success: bool, message: str, count: int)
    """
    def log(msg):
        if output_handle:
            output_handle.write(msg + '\n')
            output_handle.flush()
        else:
            print(msg)

    # Create options if not provided
    if options is None:
        options = Options(config)
        if not options.generate_option_files():
            return False, "Failed to load options files", 0

    # Get source file paths
    rf_file = get_required_fields_path(config)
    rf_dtl_file = get_required_fields_dtl_path(config)

    if not os.path.exists(rf_file):
        return False, f"Required Fields import file is missing ({rf_file})", 0

    if not os.path.exists(rf_dtl_file):
        return False, f"Required Fields Detail import file is missing ({rf_dtl_file})", 0

    # Resolve work table names
    w_rf = options.replace_options("&w#i_required_fields&")
    w_rf_dtl = options.replace_options("&w#i_required_fields_dtl&")
    dbpro = options.replace_options("&dbpro&")

    if w_rf == "&w#i_required_fields&":
        return False, "Could not resolve &w#i_required_fields& placeholder", 0
    if w_rf_dtl == "&w#i_required_fields_dtl&":
        return False, "Could not resolve &w#i_required_fields_dtl& placeholder", 0

    # Extract database from work table
    work_db = w_rf.split('..')[0] if '..' in w_rf else None

    # Parse required_fields file (header)
    # Tab-delimited: s#rf, name, title, helptxt, inact_flg, s#sk
    header_rows = []
    with open(rf_file, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            line = line.rstrip('\r\n')
            if not line:
                continue
            parts = line.split('\t')
            if len(parts) >= 6:
                header_rows.append(parts[:6])

    # Parse required_fields_dtl file (detail)
    # Tab-delimited - keep all columns as-is
    detail_rows = []
    with open(rf_dtl_file, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            line = line.rstrip('\r\n')
            if not line:
                continue
            parts = line.split('\t')
            detail_rows.append(parts)

    log(f"Parsed {len(header_rows)} required field headers and {len(detail_rows)} required field details")

    # Delete from work tables (C# used DELETE, not TRUNCATE)
    log(f"Clearing {w_rf}...")
    success, output = execute_sql_native(
        host=config.get('HOST'),
        port=config.get('PORT'),
        username=config.get('USERNAME'),
        password=config.get('PASSWORD'),
        database=work_db,
        platform=config.get('PLATFORM', 'SYBASE'),
        sql_content=f"delete {w_rf}"
    )
    if not success:
        return False, f"Failed to clear {w_rf}: {output}", 0

    log(f"Clearing {w_rf_dtl}...")
    success, output = execute_sql_native(
        host=config.get('HOST'),
        port=config.get('PORT'),
        username=config.get('USERNAME'),
        password=config.get('PASSWORD'),
        database=work_db,
        platform=config.get('PLATFORM', 'SYBASE'),
        sql_content=f"delete {w_rf_dtl}"
    )
    if not success:
        return False, f"Failed to clear {w_rf_dtl}: {output}", 0

    # Insert required field headers
    # Columns: s#rf (int), name (varchar), title (varchar), helptxt (varchar), inact_flg (char), s#sk (int)
    # Numeric columns: 0, 5
    if header_rows:
        log(f"Inserting {len(header_rows)} rows into {w_rf}...")
        sql_lines = []
        for row in header_rows:
            # Escape single quotes in string fields
            c0 = row[0].strip() or '0'  # s#rf - numeric
            c1 = row[1].replace("'", "''")  # name - string
            c2 = row[2].replace("'", "''")  # title - string
            c3 = row[3].replace("'", "''")  # helptxt - string
            c4 = row[4].replace("'", "''")  # inact_flg - string
            c5 = row[5].strip() or '0'  # s#sk - numeric
            sql_lines.append(f"insert {w_rf} values ({c0}, '{c1}', '{c2}', '{c3}', '{c4}', {c5})")

        # Execute in batches of 1000
        batch_size = 1000
        total = len(sql_lines)
        for i in range(0, total, batch_size):
            batch = sql_lines[i:i + batch_size]
            end_idx = min(i + batch_size, total)
            log(f"  Inserting {i + 1}-{end_idx}")
            sql_content = "\n".join(batch)
            success, output = execute_sql_native(
                host=config.get('HOST'),
                port=config.get('PORT'),
                username=config.get('USERNAME'),
                password=config.get('PASSWORD'),
                database=work_db,
                platform=config.get('PLATFORM', 'SYBASE'),
                sql_content=sql_content
            )
            if not success:
                return False, f"Failed to insert required field headers: {output}", 0

    # Insert required field details
    # 34 columns - numeric columns identified by data pattern (-1, 0, 1045, etc.)
    # Numeric column indices: 0, 1, 2, 4, 11, 12, 13, 14, 19, 21, 23, 25, 28, 30, 33
    numeric_cols = {0, 1, 2, 4, 11, 12, 13, 14, 19, 21, 23, 25, 28, 30, 33}
    if detail_rows:
        log(f"Inserting {len(detail_rows)} rows into {w_rf_dtl}...")
        sql_lines = []
        for row in detail_rows:
            values = []
            for i, col in enumerate(row):
                if i in numeric_cols:
                    # Numeric column - no quotes, default to 0 if empty
                    val = col.strip() or '0'
                    values.append(val)
                else:
                    # String column - escape quotes
                    val = col.replace("'", "''")
                    values.append(f"'{val}'")
            sql_lines.append(f"insert {w_rf_dtl} values ({', '.join(values)})")

        # Execute in batches of 1000
        batch_size = 1000
        total = len(sql_lines)
        for i in range(0, total, batch_size):
            batch = sql_lines[i:i + batch_size]
            end_idx = min(i + batch_size, total)
            log(f"  Inserting {i + 1}-{end_idx}")
            sql_content = "\n".join(batch)
            success, output = execute_sql_native(
                host=config.get('HOST'),
                port=config.get('PORT'),
                username=config.get('USERNAME'),
                password=config.get('PASSWORD'),
                database=work_db,
                platform=config.get('PLATFORM', 'SYBASE'),
                sql_content=sql_content
            )
            if not success:
                return False, f"Failed to insert required field details: {output}", 0

    # Execute i_required_fields_install stored procedure
    log(f"Executing {dbpro}..i_required_fields_install...")
    success, output = execute_sql_native(
        host=config.get('HOST'),
        port=config.get('PORT'),
        username=config.get('USERNAME'),
        password=config.get('PASSWORD'),
        database=dbpro,
        platform=config.get('PLATFORM', 'SYBASE'),
        sql_content=f"exec {dbpro}..i_required_fields_install"
    )

    total_rows = len(header_rows) + len(detail_rows)
    if success:
        return True, f"Compiled {len(header_rows)} headers and {len(detail_rows)} details", total_rows
    else:
        return False, f"i_required_fields_install failed: {output}", 0


# =============================================================================
# MESSAGES COMPILE
# =============================================================================

def get_messages_path(config: dict, file_ext: str) -> str:
    """
    Get the path to a message source file.

    Args:
        config: Configuration dictionary with SQL_SOURCE
        file_ext: File extension (e.g., '.ibs_msg', '.gui_msgrp')

    Returns:
        Full path to {SQL_SOURCE}/CSS/Setup/css{file_ext}
    """
    sql_source = config.get('SQL_SOURCE', os.getcwd())
    return os.path.join(sql_source, 'CSS', 'Setup', f'css{file_ext}')


def compile_messages(config: dict, options: 'Options' = None, output_handle=None) -> tuple:
    """
    Compile message source files into the database (Import mode).

    This function reads the message flat files, parses them, and inserts them
    into work tables using SQL INSERT statements. Then it executes stored
    procedures to move data to final tables.

    PROCESS:
        1. Validate all source files exist
        2. Preserve translated messages via ba_compile_gui_messages_save proc
        3. Truncate work tables
        4. Insert all rows via SQL INSERT
        5. Execute compile stored procedures (i_compile_messages handles restore)

    MESSAGE TYPES:
        ibs - IBS framework messages
        jam - JAM messages
        sqr - SQR report messages
        sql - SQL messages
        gui - GUI/desktop app messages

    SOURCE FILES (in {SQL_SOURCE}/CSS/Setup/):
        css.ibs_msg, css.ibs_msgrp
        css.jam_msg, css.jam_msgrp
        css.sqr_msg, css.sqr_msgrp
        css.sql_msg, css.sql_msgrp
        css.gui_msg, css.gui_msgrp

    FILE FORMATS:
        *_msg files: 7 columns (tab-delimited)
        *_msgrp files: 3 columns (tab-delimited)

    Args:
        config: Configuration dictionary with connection info
        options: Optional Options instance for resolving placeholders
        output_handle: Optional file handle for output. If None, prints to console.

    Returns:
        Tuple of (success: bool, message: str, count: int)
    """
    def log(msg):
        if output_handle:
            output_handle.write(msg + '\n')
            output_handle.flush()
        else:
            print(msg)

    # Create options if not provided
    if options is None:
        options = Options(config)
        if not options.generate_option_files():
            return False, "Failed to load options files", 0

    # Message types and their file extensions
    message_types = [
        ('ibs', '.ibs_msg', '.ibs_msgrp'),
        ('jam', '.jam_msg', '.jam_msgrp'),
        ('sqr', '.sqr_msg', '.sqr_msgrp'),
        ('sql', '.sql_msg', '.sql_msgrp'),
        ('gui', '.gui_msg', '.gui_msgrp'),
    ]

    # Check all source files exist
    log("Validating source files...")
    for msg_type, msg_ext, grp_ext in message_types:
        msg_file = get_messages_path(config, msg_ext)
        grp_file = get_messages_path(config, grp_ext)

        if not os.path.exists(msg_file):
            return False, f"{msg_type.upper()} Messages file is missing ({msg_file})", 0
        if not os.path.exists(grp_file):
            return False, f"{msg_type.upper()} Message Group file is missing ({grp_file})", 0

    # Resolve database name
    dbpro = options.replace_options("&dbpro&")
    dbwrk = options.replace_options("&dbwrk&")

    # Step 1: Preserve translated messages via stored procedure
    # This copies user translations from gui_messages to gui_messages_save
    log("Preserving translated messages into table gui_messages_save...")
    ba_compile_gui_messages_save = options.replace_options("&dbpro&..ba_compile_gui_messages_save")
    success, output = execute_sql_native(
        host=config.get('HOST'),
        port=config.get('PORT'),
        username=config.get('USERNAME'),
        password=config.get('PASSWORD'),
        database=dbpro,
        platform=config.get('PLATFORM', 'SYBASE'),
        sql_content=f"exec {ba_compile_gui_messages_save}"
    )
    if not success:
        return False, f"ba_compile_gui_messages_save failed: {output}", 0

    # Step 2: Truncate work tables and insert from flat files
    total_msg_rows = 0
    total_grp_rows = 0

    for msg_type, msg_ext, grp_ext in message_types:
        msg_file = get_messages_path(config, msg_ext)
        grp_file = get_messages_path(config, grp_ext)

        # Resolve work table names
        w_msg = options.replace_options(f"&w#{msg_type}_messages&")
        w_grp = options.replace_options(f"&w#{msg_type}_message_groups&")

        # Truncate work tables
        log(f"Truncating {w_msg}...")
        success, output = execute_sql_native(
            host=config.get('HOST'),
            port=config.get('PORT'),
            username=config.get('USERNAME'),
            password=config.get('PASSWORD'),
            database=dbwrk,
            platform=config.get('PLATFORM', 'SYBASE'),
            sql_content=f"truncate table {w_msg}"
        )
        if not success:
            return False, f"Failed to truncate {w_msg}: {output}"

        log(f"Truncating {w_grp}...")
        success, output = execute_sql_native(
            host=config.get('HOST'),
            port=config.get('PORT'),
            username=config.get('USERNAME'),
            password=config.get('PASSWORD'),
            database=dbwrk,
            platform=config.get('PLATFORM', 'SYBASE'),
            sql_content=f"truncate table {w_grp}"
        )
        if not success:
            return False, f"Failed to truncate {w_grp}: {output}"

        # Parse and insert message file (7 columns)
        # Columns: s#msgno (int), lang (int), cmpy (int), grp (varchar), upd_flg (char), chg_tm (int), message (varchar)
        log(f"Importing {msg_type.upper()} Messages...")
        msg_rows = []
        with open(msg_file, 'r', encoding='utf-8', errors='replace') as f:
            for line in f:
                line = line.rstrip('\r\n')
                if not line:
                    continue
                parts = line.split('\t')
                if len(parts) >= 7:
                    msg_rows.append(parts[:7])

        if msg_rows:
            log(f"  Inserting {len(msg_rows)} rows into {w_msg}...")
            # Build SQL statements
            sql_lines = []
            for row in msg_rows:
                # Numeric: 0 (s#msgno), 1 (lang), 2 (cmpy), 5 (chg_tm)
                # String: 3 (grp), 4 (upd_flg), 6 (message)
                c0 = row[0].strip() or '0'
                c1 = row[1].strip() or '0'
                c2 = row[2].strip() or '0'
                c3 = row[3].replace("'", "''")
                c4 = row[4].replace("'", "''")
                c5 = row[5].strip() or '0'
                c6 = row[6].replace("'", "''")
                sql_lines.append(f"insert {w_msg} values ({c0}, {c1}, {c2}, '{c3}', '{c4}', {c5}, '{c6}')")

            # Execute in batches of 1000
            batch_size = 1000
            total = len(sql_lines)
            for i in range(0, total, batch_size):
                batch = sql_lines[i:i + batch_size]
                end_idx = min(i + batch_size, total)
                log(f"    Inserting {i + 1}-{end_idx}")
                sql_content = "\n".join(batch)
                success, output = execute_sql_native(
                    host=config.get('HOST'),
                    port=config.get('PORT'),
                    username=config.get('USERNAME'),
                    password=config.get('PASSWORD'),
                    database=dbwrk,
                    platform=config.get('PLATFORM', 'SYBASE'),
                    sql_content=sql_content
                )
                if not success:
                    return False, f"Failed to insert {msg_type} messages: {output}"
            total_msg_rows += len(msg_rows)

        # Parse and insert message group file (3 columns)
        # Columns: grp (varchar), s#minmsg (int), description (varchar)
        grp_rows = []
        with open(grp_file, 'r', encoding='utf-8', errors='replace') as f:
            for line in f:
                line = line.rstrip('\r\n')
                if not line:
                    continue
                parts = line.split('\t')
                if len(parts) >= 3:
                    grp_rows.append(parts[:3])

        if grp_rows:
            log(f"  Inserting {len(grp_rows)} rows into {w_grp}...")
            # Build SQL statements
            sql_lines = []
            for row in grp_rows:
                # String: 0 (grp), 2 (description); Numeric: 1 (s#minmsg)
                c0 = row[0].replace("'", "''")
                c1 = row[1].strip() or '0'
                c2 = row[2].replace("'", "''")
                sql_lines.append(f"insert {w_grp} values ('{c0}', {c1}, '{c2}')")

            # Execute in batches of 1000
            batch_size = 1000
            total = len(sql_lines)
            for i in range(0, total, batch_size):
                batch = sql_lines[i:i + batch_size]
                end_idx = min(i + batch_size, total)
                log(f"    Inserting {i + 1}-{end_idx}")
                sql_content = "\n".join(batch)
                success, output = execute_sql_native(
                    host=config.get('HOST'),
                    port=config.get('PORT'),
                    username=config.get('USERNAME'),
                    password=config.get('PASSWORD'),
                    database=dbwrk,
                    platform=config.get('PLATFORM', 'SYBASE'),
                    sql_content=sql_content
                )
                if not success:
                    return False, f"Failed to insert {msg_type} message groups: {output}"
            total_grp_rows += len(grp_rows)

    # Step 3: Execute compile stored procedures
    # i_compile_messages moves work tables to final tables and calls i_compile_gui_messages
    # i_compile_gui_messages restores translations from gui_messages_save
    log(f"Running i_compile_messages...")
    success, output = execute_sql_native(
        host=config.get('HOST'),
        port=config.get('PORT'),
        username=config.get('USERNAME'),
        password=config.get('PASSWORD'),
        database=dbpro,
        platform=config.get('PLATFORM', 'SYBASE'),
        sql_content=f"exec {dbpro}..i_compile_messages"
    )
    if not success:
        return False, f"i_compile_messages failed: {output}", 0

    log(f"Running i_compile_jam_messages...")
    success, output = execute_sql_native(
        host=config.get('HOST'),
        port=config.get('PORT'),
        username=config.get('USERNAME'),
        password=config.get('PASSWORD'),
        database=dbpro,
        platform=config.get('PLATFORM', 'SYBASE'),
        sql_content=f"exec {dbpro}..i_compile_jam_messages"
    )
    if not success:
        return False, f"i_compile_jam_messages failed: {output}", 0

    log(f"Running i_compile_jrw_messages...")
    success, output = execute_sql_native(
        host=config.get('HOST'),
        port=config.get('PORT'),
        username=config.get('USERNAME'),
        password=config.get('PASSWORD'),
        database=dbpro,
        platform=config.get('PLATFORM', 'SYBASE'),
        sql_content=f"exec {dbpro}..i_compile_jrw_messages"
    )
    if not success:
        return False, f"i_compile_jrw_messages failed: {output}", 0

    total_rows = total_msg_rows + total_grp_rows
    return True, f"Compiled {total_msg_rows} messages and {total_grp_rows} message groups", total_rows


def export_messages(config: dict, options: 'Options' = None, output_handle=None) -> tuple:
    """
    Export messages from the database to flat files.

    This function uses SELECT statements to export message data from the database
    tables back to the flat files in {SQL_SOURCE}/CSS/Setup/.

    MESSAGE TYPES:
        ibs - IBS framework messages
        jam - JAM messages
        sqr - SQR report messages
        sql - SQL messages
        gui - GUI/desktop app messages

    TARGET FILES (in {SQL_SOURCE}/CSS/Setup/):
        css.ibs_msg, css.ibs_msgrp
        css.jam_msg, css.jam_msgrp
        css.sqr_msg, css.sqr_msgrp
        css.sql_msg, css.sql_msgrp
        css.gui_msg, css.gui_msgrp

    FILE FORMATS (tab-delimited):
        *_msg files: 7 columns (s#msg, s#lang, s#sk, grp_id, def_flg, chksum, text)
        *_msgrp files: 3 columns (grp_id, s#msg, description)

    Args:
        config: Configuration dictionary with connection info
        options: Optional Options instance for resolving placeholders
        output_handle: Optional file handle for output. If None, prints to console.

    Returns:
        Tuple of (success: bool, message: str)
    """
    def log(msg):
        if output_handle:
            output_handle.write(msg + '\n')
            output_handle.flush()
        else:
            print(msg)

    # Create options if not provided
    if options is None:
        options = Options(config)
        if not options.generate_option_files():
            return False, "Failed to load options files"

    # Message types and their file extensions
    message_types = [
        ('ibs', '.ibs_msg', '.ibs_msgrp'),
        ('jam', '.jam_msg', '.jam_msgrp'),
        ('sqr', '.sqr_msg', '.sqr_msgrp'),
        ('sql', '.sql_msg', '.sql_msgrp'),
        ('gui', '.gui_msg', '.gui_msgrp'),
    ]

    # Get connection info
    host = config.get('HOST')
    port = config.get('PORT')
    username = config.get('USERNAME')
    password = config.get('PASSWORD')
    platform = config.get('PLATFORM', 'SYBASE')

    # Resolve database name
    dbtbl = options.replace_options("&dbtbl&")

    total_exports = 0
    total_rows = 0
    errors = []

    for msg_type, msg_ext, grp_ext in message_types:
        # Get target file paths
        msg_file = get_messages_path(config, msg_ext)
        grp_file = get_messages_path(config, grp_ext)

        # Resolve final table names (not work tables)
        msg_table = options.replace_options(f"&{msg_type}_messages&")
        grp_table = options.replace_options(f"&{msg_type}_message_groups&")

        # Export messages table
        # Columns: s#msgno, lang, cmpy, grp, upd_flg, chg_tm, message
        log(f"Exporting {msg_table} to {msg_file}...")
        sql = f"select * from {msg_table}"
        success, output = execute_sql_native(
            host=host,
            port=port,
            username=username,
            password=password,
            database=dbtbl,
            platform=platform,
            sql_content=sql
        )
        if not success:
            errors.append(f"Failed to export {msg_table}: {output}")
        else:
            # Parse output and write tab-delimited file
            rows = _parse_select_to_rows(output)
            with open(msg_file, 'w', encoding='utf-8') as f:
                for row in rows:
                    f.write('\t'.join(str(col) for col in row) + '\n')
            log(f"  Exported {len(rows)} rows")
            total_exports += 1
            total_rows += len(rows)

        # Export message groups table
        # Columns: grp, s#minmsg, description
        log(f"Exporting {grp_table} to {grp_file}...")
        sql = f"select * from {grp_table}"
        success, output = execute_sql_native(
            host=host,
            port=port,
            username=username,
            password=password,
            database=dbtbl,
            platform=platform,
            sql_content=sql
        )
        if not success:
            errors.append(f"Failed to export {grp_table}: {output}")
        else:
            # Parse output and write tab-delimited file
            rows = _parse_select_to_rows(output)
            with open(grp_file, 'w', encoding='utf-8') as f:
                for row in rows:
                    f.write('\t'.join(str(col) for col in row) + '\n')
            log(f"  Exported {len(rows)} rows")
            total_exports += 1
            total_rows += len(rows)

    if errors:
        return False, f"Export completed with errors:\n" + "\n".join(errors)

    return True, f"Exported {total_exports} tables ({total_rows} total rows) to flat files"


def _parse_select_to_rows(output: str) -> list:
    """
    Parse tsql SELECT output into rows of column values.

    tsql output format (with -o q quiet mode) is tab-delimited with a header row
    followed by data rows (no separator line).
    Example:
        col1    col2    col3
        val1    val2    val3
        val1    val2    val3

    Args:
        output: Raw output from execute_sql_native()

    Returns:
        List of lists, each containing column values for one row
    """
    if not output:
        return []

    rows = []
    lines = output.strip().split('\n')

    # Skip the first line (header row)
    for i, line in enumerate(lines):
        if i == 0:
            # Skip header row
            continue

        line = line.rstrip('\r')

        # Skip empty lines
        if not line.strip():
            continue

        # Parse data row
        cols = line.split('\t')
        # Strip whitespace from each column
        cols = [col.strip() for col in cols]
        rows.append(cols)

    return rows


# =============================================================================
# OPTIONS COMPILE
# =============================================================================

def get_options_company_path(config: dict) -> str:
    """
    Get the path to the options.{COMPANY} file.

    Args:
        config: Configuration dictionary with SQL_SOURCE and COMPANY

    Returns:
        Full path to {SQL_SOURCE}/CSS/Setup/options.{COMPANY}
    """
    sql_source = config.get('SQL_SOURCE', os.getcwd())
    company = config.get('COMPANY', '101')
    return os.path.join(sql_source, 'CSS', 'Setup', f'options.{company}')


def get_options_profile_path(config: dict) -> str:
    """
    Get the path to the options.{COMPANY}.{PROFILE} file.

    Args:
        config: Configuration dictionary with SQL_SOURCE, COMPANY, PROFILE_NAME

    Returns:
        Full path to {SQL_SOURCE}/CSS/Setup/options.{COMPANY}.{PROFILE}
    """
    sql_source = config.get('SQL_SOURCE', os.getcwd())
    company = config.get('COMPANY', '101')
    profile = config.get('PROFILE_NAME', 'default')
    return os.path.join(sql_source, 'CSS', 'Setup', f'options.{company}.{profile}')


def generate_import_option_file(source_file: str) -> list:
    """
    Parse options source file and convert to :> format for database import.

    This function reads an options file (v:/V:/c:/C: format) and converts each
    option to the :> format expected by the i_import_options stored procedure.

    SOURCE FORMAT (options.{company} or options.{company}.{profile}):

        Value options:
            v:option_name <<value>> description     (static option)
            V:option_name <<value>> description     (dynamic/user-changeable)

        Condition options:
            c:option_name +/- description           (static option)
            C:option_name +/- description           (dynamic/user-changeable)

    OUTPUT FORMAT (for w#options table):

        Value options become:
            :>name     - - + [+/-] <<value>> description

        Condition options become:
            :>name     [+/-] + - [+/-] description

        The :> prefix and fixed-width name field (8 chars) allow the
        i_import_options stored procedure to parse the line using substring().

        Field positions after :>name (8 chars):
            Position 10: act_flg (active flag for conditions)
            Position 12: if_flg (if-condition flag)
            Position 14: val_flg (value flag)
            Position 16: dyn_flg (dynamic flag - uppercase prefix = +)

    NOTE:
        Lines are truncated to 2000 characters to fit the w#options.line column
        (nvarchar(2000)). This is an increase from the legacy 254 char limit.

    Args:
        source_file: Path to options file (options.{company} or options.{company}.{profile})

    Returns:
        List of :> formatted lines ready for INSERT into w#options
    """
    dest = []
    if not os.path.exists(source_file):
        return dest

    with open(source_file, 'r', encoding='utf-8') as f:
        for line in f:
            line = line.rstrip('\r\n')

            # Skip empty lines and comments
            if len(line) < 2 or line.startswith('#'):
                continue

            opt_type = line[:2].lower()
            if opt_type not in ('v:', 'c:'):
                # Also check uppercase
                opt_type_upper = line[:2]
                if opt_type_upper not in ('V:', 'C:'):
                    continue
                opt_type = opt_type_upper.lower()
                is_dynamic = True
            else:
                is_dynamic = line[:2].isupper()

            # Remove the prefix and trim
            content = line[2:].strip()

            # Extract option name (everything before first space)
            space_idx = content.find(' ')
            if space_idx == -1:
                continue

            opt_name = content[:space_idx].strip()
            rest = content[space_idx:].strip()

            if opt_type == 'v:':
                # Value option: v: name <<value>> description
                # Find <<value>>
                start_idx = rest.find('<<')
                end_idx = rest.find('>>')
                if start_idx == -1 or end_idx == -1:
                    continue

                opt_value = rest[start_idx:end_idx + 2]  # includes << and >>
                opt_desc = rest[end_idx + 2:].strip()

                # Format: :>name     - - + [+/-] <<value>> description
                # Dynamic flag is + if V:, - if v:
                dynamic_flag = '+' if is_dynamic else '-'
                mystr = f":>{opt_name.ljust(8)} - - + {dynamic_flag} {opt_value} {opt_desc}"

            elif opt_type == 'c:':
                # Condition option: c: name +/- description
                # The +/- indicates active or inactive
                if rest.startswith('-'):
                    opt_value = '-'
                    opt_desc = rest[1:].strip()
                elif rest.startswith('+'):
                    opt_value = '+'
                    opt_desc = rest[1:].strip()
                else:
                    opt_value = '+'
                    opt_desc = rest

                # Format: :>name     +/- + - [+/-] description
                # Dynamic flag is + if C:, - if c:
                dynamic_flag = '+' if is_dynamic else '-'
                mystr = f":>{opt_name.ljust(8)} {opt_value} + - {dynamic_flag} {opt_desc}"

            else:
                continue

            # Truncate to 2000 chars max (w#options.line is nvarchar(2000)) and ensure no embedded newlines
            mystr = mystr.replace('\r', '').replace('\n', '')
            if len(mystr) > 2000:
                mystr = mystr[:2000]

            dest.append(mystr)

    return dest


def combine_option_files(company_options: list, profile_options: list) -> list:
    """
    Combine company and profile options, with profile taking precedence.

    Options are keyed by name (first 8 characters after :> prefix). When the same
    option exists in both files, the profile version overrides the company version.

    This allows server-specific settings to override company-wide defaults.

    Example:
        Company:  :>timeout  - - + - <<30>> Connection timeout
        Profile:  :>timeout  - - + - <<60>> Connection timeout

        Result uses profile value (60), not company value (30).

    Args:
        company_options: List of :> formatted options from options.{COMPANY}
        profile_options: List of :> formatted options from options.{COMPANY}.{PROFILE}

    Returns:
        Combined list with profile options overriding company options for same name
    """
    # Build dict keyed by option name (first 10 chars after :>)
    options_dict = {}

    for opt in company_options:
        if opt.startswith(':>'):
            key = opt[2:10].strip()  # Option name is in chars 2-10
            options_dict[key] = opt

    for opt in profile_options:
        if opt.startswith(':>'):
            key = opt[2:10].strip()
            options_dict[key] = opt  # Override company with profile

    return list(options_dict.values())


def compile_options(config: dict, options: 'Options' = None, output_handle=None) -> tuple:
    """
    Compile options source files into the database.

    This function reads company and profile options files, converts them to the
    :> format, inserts them into the w#options work table, and executes the
    i_import_options stored procedure to populate the final options table.

    PROCESS:
        1. Load options (if not provided) for placeholder resolution
        2. Parse options.{COMPANY} file (company-wide settings)
        3. Parse options.{COMPANY}.{PROFILE} file (server-specific overrides)
        4. Convert both to :> format using generate_import_option_file()
        5. Combine options (profile overrides company for same option name)
        6. Delete from w#options work table
        7. Insert all options using SQL INSERT statements
        8. Execute i_import_options stored procedure to populate final table
        9. Delete options cache file to force rebuild
        10. Also compile table_locations (options may affect database mappings)

    SOURCE FILES:
        options.{COMPANY}           - Company-wide options (e.g., options.101)
        options.{COMPANY}.{PROFILE} - Server-specific overrides (e.g., options.101.GONZO)

    TARGET TABLES:
        w#options (work table):
            - Single column: line nvarchar(2000)
            - Receives :> formatted option lines

        options (final table, via i_import_options):
            - id, act_flg, if_flg, val_flg, value, dyn_flg, description
            - Populated by parsing w#options lines

    NOTE:
        This function uses SQL INSERT statements instead of BCP (Bulk Copy Program)
        to avoid the 255 character limit in freebcp. This allows option values
        up to 2000 characters (the w#options.line column width).

    Args:
        config: Configuration dictionary with connection info (HOST, PORT, USERNAME,
                PASSWORD, SQL_SOURCE, COMPANY, PROFILE_NAME, PLATFORM)
        options: Optional Options instance for resolving placeholders. If not provided,
                 a new Options instance will be created from the config.
        output_handle: Optional file handle for output. If None, prints to console.

    Returns:
        Tuple of (success: bool, message: str)
        - success: True if options were imported successfully
        - message: Status message including count of options imported
    """
    def log(msg):
        if output_handle:
            output_handle.write(msg + '\n')
            output_handle.flush()
        else:
            print(msg)

    # Create options if not provided
    if options is None:
        options = Options(config)
        if not options.generate_option_files():
            return False, "Failed to load options files"

    # Get file paths
    company_file = get_options_company_path(config)
    profile_file = get_options_profile_path(config)

    if not os.path.exists(company_file):
        return False, f"Company options file not found: {company_file}"

    if not os.path.exists(profile_file):
        log(f"Warning: Profile options file not found: {profile_file}")

    # Parse and convert to :> format
    log(f"Processing company options: {company_file}")
    company_options = generate_import_option_file(company_file)

    log(f"Processing profile options: {profile_file}")
    profile_options = generate_import_option_file(profile_file)

    # Combine options (profile overrides company for same option name)
    combined_options = combine_option_files(company_options, profile_options)

    if not combined_options:
        return False, "No options found to import"

    log(f"Combined {len(combined_options)} options")

    # Resolve work table
    work_table = options.replace_options("&w#options&")
    if work_table == "&w#options&":
        return False, "Could not resolve &w#options& placeholder"

    # Extract database from work_table
    work_db = work_table.split('..')[0] if '..' in work_table else None

    # Delete from work table
    log(f"Clearing {work_table}...")
    success, output = execute_sql_native(
        host=config.get('HOST'),
        port=config.get('PORT'),
        username=config.get('USERNAME'),
        password=config.get('PASSWORD'),
        database=work_db,
        platform=config.get('PLATFORM', 'SYBASE'),
        sql_content=f"delete {work_table}"
    )

    if not success:
        return False, f"Failed to clear work table: {output}", 0

    # Filter out empty lines
    filtered_options = [opt for opt in combined_options if opt and opt.strip()]

    # Insert options using SQL INSERT statements (BCP has 255 char limit)
    log(f"Inserting {len(filtered_options)} options into {work_table}...")

    # Build SQL with all INSERT statements
    sql_lines = []
    for opt in filtered_options:
        # Escape single quotes
        escaped_opt = opt.replace("'", "''")
        sql_lines.append(f"insert {work_table} (line) values ('{escaped_opt}')")

    # Execute in batches of 1000
    batch_size = 1000
    total = len(sql_lines)
    for i in range(0, total, batch_size):
        batch = sql_lines[i:i + batch_size]
        end_idx = min(i + batch_size, total)
        log(f"  Inserting {i + 1}-{end_idx}")
        sql_content = "\n".join(batch)
        success, output = execute_sql_native(
            host=config.get('HOST'),
            port=config.get('PORT'),
            username=config.get('USERNAME'),
            password=config.get('PASSWORD'),
            database=work_db,
            platform=config.get('PLATFORM', 'SYBASE'),
            sql_content=sql_content
        )
        if not success:
            return False, f"Failed to insert options: {output}", 0

    # Execute i_import_options stored procedure
    dbpro = options.replace_options("&dbpro&")
    proc_call = f"exec {dbpro}..i_import_options"
    log(f"Executing: {proc_call}")

    success, output = execute_sql_native(
        host=config.get('HOST'),
        port=config.get('PORT'),
        username=config.get('USERNAME'),
        password=config.get('PASSWORD'),
        database=dbpro,
        platform=config.get('PLATFORM', 'SYBASE'),
        sql_content=proc_call
    )

    if not success:
        return False, f"Failed to execute i_import_options: {output}", 0

    # Delete the options cache file so it gets rebuilt next time
    cache_file = options.get_cache_filepath()
    try:
        if os.path.exists(cache_file):
            os.remove(cache_file)
            log(f"Deleted options cache: {cache_file}")
    except Exception:
        pass

    # Also compile table_locations (options may have changed database mappings)
    log("\nCompiling table_locations...")
    tbl_success, tbl_message, tbl_count = compile_table_locations(config, options, output_handle)
    if tbl_success:
        log(f"Inserted {tbl_count} rows into table_locations")
    else:
        log(f"Warning: table_locations compile failed: {tbl_message}")

    return True, f"Imported {len(filtered_options)} options", len(filtered_options)


# =============================================================================
# SQL SCRIPT BUILDING
# =============================================================================

def generate_changelog_sql(sql_command: str, database: str, server: str,
                           company: str, username: str, changelog_enabled: bool = False) -> list:
    """
    Generate changelog SQL lines to insert before user command.

    This replicates the C# change_log.lines() behavior, which logs the SQL
    execution to a changelog table if enabled.

    Args:
        sql_command: The user's SQL command
        database: Database name
        server: Server name (without port)
        company: Company ID
        username: Current username
        changelog_enabled: Whether to enable changelog

    Returns:
        List of SQL lines to execute before the user's command
    """
    if not changelog_enabled:
        return []

    # Escape single quotes in command
    escaped_cmd = sql_command.replace("'", "''")

    lines = [
        "if exists (select 1 from &options& where id = 'gclog12' and act_flg = '+')",
        "begin",
        "  if exists (select 1 from &dbpro&..sysobjects where name = 'ba_gen_chg_log_new')",
        f"    exec &dbpro&..ba_gen_chg_log_new '', 'User {username} recompiled sproc or ran sql', 'RUNSQL', '', 'runsql {escaped_cmd} {database} {server} {company}', '', 'X'",
        "end",
        "go",
        ""
    ]

    return lines


def build_sql_script(sql_command: str, config: dict = None,
                     changelog_enabled: bool = False, database: str = "",
                     host: str = "") -> str:
    """
    Build SQL script with optional changelog and placeholder replacement.

    Args:
        sql_command: The SQL command to execute
        config: Configuration dict for placeholder replacement
        changelog_enabled: Whether to prepend changelog SQL
        database: Database name (for changelog)
        host: Host name (for changelog)

    Returns:
        Complete SQL script content
    """
    script_lines = []

    # Add changelog SQL if enabled
    if changelog_enabled and config:
        company = config.get('COMPANY', '')
        current_user = os.environ.get('USERNAME', os.environ.get('USER', 'unknown'))

        changelog_lines = generate_changelog_sql(
            sql_command, database, host, company, current_user, changelog_enabled
        )
        script_lines.extend(changelog_lines)

    # Add the user's SQL command
    script_lines.append(sql_command)

    # Build script content
    script_content = "\n".join(script_lines)

    # Perform placeholder replacement if config provided
    if config:
        script_content = replace_placeholders(script_content, config, remove_ampersands=False)

    return script_content


# =============================================================================
# NATIVE SQL COMPILER EXECUTION (tsql)
# =============================================================================


def get_mssql_init_sql() -> str:
    """
    Load MSSQL init SQL from MSSQL_INIT environment variable.

    Similar to SQLCMDINI for sqlcmd.exe - if the MSSQL_INIT environment variable
    is set and points to a readable file, return its contents to be prepended
    before every MSSQL command execution.

    Returns:
        Init SQL content with trailing GO, or empty string if not configured.
    """
    init_file = os.environ.get('MSSQL_INIT', '').strip()
    if not init_file:
        return ""

    try:
        init_path = Path(init_file)
        if not init_path.is_file():
            logging.warning(f"MSSQL_INIT file not found: {init_file}")
            return ""

        with open(init_path, 'r', encoding='utf-8') as f:
            init_content = f.read().strip()

        if not init_content:
            return ""

        logging.debug(f"Loaded MSSQL init SQL from: {init_file}")

        # Ensure it ends with GO for proper batch separation
        if not init_content.lower().endswith('go'):
            init_content += "\ngo"

        return init_content + "\n"

    except Exception as e:
        logging.warning(f"Failed to read MSSQL_INIT file '{init_file}': {e}")
        return ""


def execute_sql_native(host: str, port: int, username: str, password: str,
                       database: str, platform: str, sql_content: str,
                       output_file: str = None, echo_input: bool = False,
                       include_info_messages: bool = False) -> tuple[bool, str]:
    """
    Execute SQL using FreeTDS tsql via subprocess.

    tsql works for both Sybase ASE and MSSQL. This pipes SQL through stdin
    to tsql, which provides better compatibility with the original C# implementation.

    Note: Always uses cp1252 encoding for tsql communication. FreeTDS tsql doesn't
    handle UTF-8 multi-byte sequences properly (e.g., UTF-8 Æ = C3 86 confuses it).
    Files should be read with their proper encoding, then Python will transcode
    to cp1252 when sending to tsql.

    Args:
        host: Database host
        port: Database port
        username: Database username
        password: Database password
        database: Database name (can be empty)
        platform: Database platform (SYBASE or MSSQL) - used for logging only
        sql_content: SQL content to execute (can be single command or full script)
        output_file: Optional output file path
        echo_input: Whether to echo input (maps to -v for tsql)
        include_info_messages: If True, include severity <= 10 messages from stderr
            in the output. Used by sp_showplan which sends plan output as PRINT
            messages (severity 10). Default False to preserve existing behavior.

    Returns:
        Tuple of (success: bool, output: str)

    Example:
        success, output = execute_sql_native(
            "54.235.236.130", 5000, "sbn0", "ibsibs",
            "sbnmaster", "SYBASE", "select @@version"
        )
    """
    # Use tsql for both Sybase and MSSQL (FreeTDS handles both)
    compiler = "tsql"

    # Check if compiler is available
    if not shutil.which(compiler):
        return False, f"{compiler} command not found. Install FreeTDS."

    logging.debug(f"Using FreeTDS tsql for {platform}")

    try:
        # Prepend MSSQL init SQL if configured (similar to SQLCMDINI for sqlcmd.exe)
        if platform.upper() == "MSSQL":
            init_sql = get_mssql_init_sql()
            if init_sql:
                sql_content = init_sql + sql_content

        # Ensure SQL ends with go and exit for proper termination
        script_content = sql_content.strip()
        if not script_content.lower().endswith('go'):
            script_content += "\ngo"
        script_content += "\nexit\n"

        logging.debug(f"Script content:\n{script_content[:500]}...")

        # Build tsql command - same for both Sybase and MSSQL
        # -H = host (direct connection)
        # -p = port
        # -U = username
        # -P = password
        # -D = database (optional)
        # -o q = quiet mode (suppress prompts like 1>, 2>)
        # -v = verbose mode (maps to -e echo flag for backward compatibility)
        cmd = [
            compiler,
            "-H", host,
            "-p", str(port),
            "-U", username,
            "-P", password,
            "-o", "q"  # Quiet mode - suppress prompts
        ]

        if database:
            cmd.extend(["-D", database])

        # Add verbose flag if echo requested (tsql uses -v, not -e)
        if echo_input:
            cmd.append("-v")

        logging.debug(f"Executing: {compiler} -H {host} -p {port} -U {username} ... (credentials hidden)")

        # Execute tsql, piping SQL via stdin
        # Always use cp1252 for tsql communication - FreeTDS doesn't handle UTF-8 multi-byte
        # sequences properly in its line parser (e.g., UTF-8 Æ = C3 86 confuses it).
        # The Python string from script_content will be transcoded to cp1252 here.
        result = subprocess.run(
            cmd,
            input=script_content,
            capture_output=True,
            text=True,
            encoding='cp1252',
            errors='replace',
            timeout=300
        )

        # Process output - filter out tsql noise
        stdout = result.stdout or ""
        stderr = result.stderr or ""

        output_lines = []
        for line in stdout.splitlines():
            # Strip carriage returns and other whitespace issues
            line = line.rstrip('\r').lstrip('\r')

            # Skip tsql informational lines
            if line.startswith("locale is"):
                continue
            if line.startswith("locale charset is"):
                continue
            if line.startswith("using default charset"):
                continue

            # Remove prompt prefixes (N> with or without space) - handle leading whitespace too
            line = re.sub(r'^\s*\d+>\s?', '', line)

            # Skip if line is now empty after removing prompt
            if not line.strip():
                continue

            output_lines.append(line)

        clean_output = "\n".join(output_lines).strip()

        # Check for SQL errors in stderr (tsql puts errors in stderr)
        # Format: Msg 207 (severity 16, state 4) from SERVER Line 1:
        #         \t"Error message text.
        #         "
        # Severity 10 = informational, 11+ = actual errors
        sql_error = None
        info_lines = []
        if stderr:
            # Look for ALL error messages and check if any have severity > 10
            error_matches = re.findall(r'Msg (\d+) \(severity (\d+)', stderr)
            has_real_error = any(int(sev) > 10 for msg_num, sev in error_matches)

            # Process stderr lines
            error_lines = []
            in_error_block = False
            in_info_block = False
            for line in stderr.splitlines():
                header_match = re.match(r'Msg (\d+) \(severity (\d+)', line)
                if header_match:
                    sev = int(header_match.group(2))
                    if sev > 10:
                        in_error_block = True
                        in_info_block = False
                        error_lines.append(line)
                    else:
                        in_error_block = False
                        in_info_block = include_info_messages
                    continue

                cleaned = line.strip().strip('\t').strip('"').strip()

                if in_error_block and cleaned:
                    error_lines.append(cleaned)
                elif in_info_block and cleaned:
                    # Skip tsql noise that appears as info messages
                    if not cleaned.startswith("Changed client character set"):
                        info_lines.append(cleaned)

            if has_real_error:
                sql_error = "\n".join(error_lines) if error_lines else stderr[:500]

        if sql_error:
            return False, sql_error

        # Append informational messages to output (e.g., sp_showplan PRINT output)
        if info_lines:
            info_output = "\n".join(info_lines)
            if clean_output:
                clean_output = clean_output + "\n" + info_output
            else:
                clean_output = info_output

        # Write to output file if specified
        if output_file:
            try:
                with open(output_file, 'w', encoding='utf-8') as f:
                    f.write(clean_output + "\n")
                logging.debug(f"Output written to: {output_file}")
            except Exception as e:
                return False, f"Failed to write output file: {e}"

        # Check result
        if result.returncode == 0:
            return True, clean_output if clean_output else "(return status = 0)"
        else:
            error_msg = stderr if stderr else stdout
            if not error_msg:
                error_msg = f"Command failed with return code {result.returncode}"
            return False, error_msg

    except subprocess.TimeoutExpired:
        return False, "Command execution timeout (5 minutes exceeded)"

    except Exception as e:
        logging.error(f"Failed to execute SQL command: {e}")
        return False, str(e)


def execute_sql_interleaved(host: str, port: int, username: str, password: str,
                            database: str, platform: str, sql_content: str,
                            echo: bool = False, output_handle=None) -> bool:
    """
    Execute SQL with interleaved echo and output, maintaining a single connection.

    This function:
    1. Splits SQL into batches (separated by 'go')
    2. For each batch: echoes with line numbers (resetting per batch), then shows tsql output
    3. Uses a single persistent tsql connection throughout

    This matches Unix isql behavior where 'use' statements affect subsequent batches.

    Args:
        host: Database host
        port: Database port
        username: Database username
        password: Database password
        database: Initial database name
        platform: Database platform (SYBASE or MSSQL)
        sql_content: Full SQL content (may contain multiple batches separated by 'go')
        echo: If True, echo each batch with line numbers before execution
        output_handle: File handle for output, or None for stdout

    Returns:
        True if all batches succeeded, False if any had errors
    """
    import threading
    import queue

    def write_output(msg: str):
        """Write to output_handle or stdout."""
        if output_handle:
            output_handle.write(msg + '\n')
            output_handle.flush()
        else:
            print(msg)

    def split_batches(content: str) -> list:
        """Split SQL into batches ending with 'go'."""
        lines = content.splitlines(keepends=True)
        batches = []
        current_batch = []

        for line in lines:
            current_batch.append(line)
            stripped = line.strip().lower()
            if stripped == 'go' or stripped.startswith('go ') or stripped.startswith('go\t'):
                batch_sql = ''.join(current_batch)
                if batch_sql.strip():
                    batches.append(batch_sql)
                current_batch = []

        # Handle remaining content after last 'go'
        if current_batch:
            remaining = ''.join(current_batch)
            if remaining.strip():
                batches.append(remaining)

        return batches

    def escape_tsql_metacommands(content: str) -> str:
        """
        Escape tsql metacommands that appear inside block comments.

        tsql interprets certain keywords as metacommands even inside /* */ comments.
        This function prepends -- to these keywords when they appear at the start
        of a line inside a block comment to prevent tsql from interpreting them.

        Metacommands: reset, quit, bye, version, :r
        """
        # Keywords that tsql interprets as metacommands (case-insensitive)
        metacommands = ['reset', 'quit', 'bye', 'version', ':r']

        lines = content.splitlines(keepends=True)
        result = []
        in_block_comment = False

        for line in lines:
            # Track block comment state
            # Note: This is simplified and doesn't handle nested comments or
            # comments inside strings, but should work for typical SQL files
            temp_line = line
            check_line = line

            # Process the line to track comment state
            while True:
                if not in_block_comment:
                    # Look for start of block comment
                    start_pos = check_line.find('/*')
                    if start_pos == -1:
                        break
                    # Check if there's an end before we process further
                    end_pos = check_line.find('*/', start_pos + 2)
                    if end_pos != -1:
                        # Comment opens and closes on same line segment
                        check_line = check_line[end_pos + 2:]
                        continue
                    else:
                        in_block_comment = True
                        break
                else:
                    # Look for end of block comment
                    end_pos = check_line.find('*/')
                    if end_pos != -1:
                        in_block_comment = False
                        check_line = check_line[end_pos + 2:]
                        continue
                    else:
                        break

            # If we're inside a block comment, check if line starts with metacommand
            if in_block_comment or '/*' in line:
                stripped = line.lstrip()
                for cmd in metacommands:
                    # Check if line starts with metacommand (case-insensitive)
                    if stripped.lower().startswith(cmd.lower()):
                        # Check it's followed by whitespace, newline, or end of string
                        rest = stripped[len(cmd):]
                        if not rest or rest[0].isspace() or rest[0] in '\r\n':
                            # Prepend -- to escape the metacommand
                            leading_space = line[:len(line) - len(stripped)]
                            line = leading_space + '--' + stripped
                            break

            result.append(line)

            # Update block comment state for lines that might end a comment
            if '*/' in temp_line and '/*' not in temp_line.split('*/')[-1]:
                # Line ends a block comment without starting a new one
                pass  # State already updated in the loop above

        return ''.join(result)

    compiler = "tsql"
    if not shutil.which(compiler):
        write_output(f"ERROR: {compiler} command not found. Install FreeTDS.")
        return False

    # Prepend MSSQL init SQL if configured (similar to SQLCMDINI for sqlcmd.exe)
    if platform.upper() == "MSSQL":
        init_sql = get_mssql_init_sql()
        if init_sql:
            sql_content = init_sql + sql_content

    # Escape tsql metacommands inside block comments to prevent misinterpretation
    sql_content = escape_tsql_metacommands(sql_content)

    # Split into batches for echo purposes
    batches = split_batches(sql_content)

    try:
        # Build tsql command
        cmd = [
            compiler,
            "-H", host,
            "-p", str(port),
            "-U", username,
            "-P", password,
            "-o", "q"  # Quiet mode
        ]
        if database:
            cmd.extend(["-D", database])

        # Start tsql process with persistent connection
        # Use cp1252 encoding - FreeTDS doesn't handle UTF-8 multi-byte sequences properly
        process = subprocess.Popen(
            cmd,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding='cp1252',
            errors='replace'
        )

        # Queue for collecting output from threads
        output_queue = queue.Queue()
        has_error = False

        def read_stream(stream, stream_name):
            """Read from stream and put lines in queue."""
            try:
                for line in stream:
                    output_queue.put((stream_name, line))
            except:
                pass
            finally:
                output_queue.put((stream_name, None))

        # Start reader threads
        stdout_thread = threading.Thread(target=read_stream, args=(process.stdout, 'stdout'))
        stderr_thread = threading.Thread(target=read_stream, args=(process.stderr, 'stderr'))
        stdout_thread.daemon = True
        stderr_thread.daemon = True
        stdout_thread.start()
        stderr_thread.start()

        def process_output_line(stream_name: str, line: str):
            """Process a single output line."""
            nonlocal has_error
            line = line.rstrip('\r\n')

            # Skip tsql noise
            if line.startswith("locale is"):
                return
            if line.startswith("locale charset is"):
                return
            if line.startswith("using default charset"):
                return
            if line.startswith("using TDS version"):
                return
            if line.startswith("Setting ") and "as default database" in line:
                return
            # Skip character set change messages
            if "Changed client character set" in line:
                return
            if line.strip() == '"':
                return

            if stream_name == 'stdout':
                # Remove prompt prefixes
                line = re.sub(r'^\s*\d+>\s?', '', line)
                if line.strip():
                    write_output(line)
            elif stream_name == 'stderr':
                # Check for real errors (severity > 10)
                # Severity 10 = informational, 11+ = actual errors
                match = re.match(r'Msg \d+ \(severity (\d+)', line)
                if match:
                    severity = int(match.group(1))
                    if severity > 10:
                        has_error = True
                        # Output error messages
                        line = line.strip().strip('\t').strip('"')
                        if line:
                            write_output(line)
                    # else: severity <= 10, skip informational messages
                else:
                    # Non-Msg lines (error details, etc.) - output them
                    line = line.strip().strip('\t').strip('"')
                    if line:
                        write_output(line)

        def drain_output_until_idle(max_wait_ms: int = 2000):
            """Drain output until queue is idle for a short period."""
            import time
            idle_threshold_ms = 200  # Consider idle after 200ms of no output
            last_output_time = time.time()
            deadline = time.time() + (max_wait_ms / 1000.0)

            while time.time() < deadline:
                try:
                    stream_name, line = output_queue.get(timeout=0.01)
                    if line is None:
                        continue
                    process_output_line(stream_name, line)
                    last_output_time = time.time()
                except queue.Empty:
                    # Check if we've been idle long enough
                    if (time.time() - last_output_time) > (idle_threshold_ms / 1000.0):
                        break

        # Process each batch: echo then execute
        for batch_sql in batches:
            # Echo this batch with line numbers (reset to 1 per batch)
            if echo:
                batch_lines = batch_sql.splitlines()
                for i, line in enumerate(batch_lines):
                    write_output(f"{i+1}> {line}")

            # Send this batch to tsql
            try:
                process.stdin.write(batch_sql)
                if not batch_sql.strip().lower().endswith('go'):
                    process.stdin.write('\ngo\n')
                process.stdin.flush()
            except (OSError, BrokenPipeError) as e:
                # Process may have crashed due to earlier error
                write_output(f"ERROR: Connection to database lost: {e}")
                break

            # Wait for tsql to process this batch and drain output
            drain_output_until_idle(max_wait_ms=5000)

        # Send exit command (may fail if process already exited due to error)
        try:
            process.stdin.write('exit\n')
            process.stdin.flush()
        except (OSError, BrokenPipeError):
            pass  # Process may have already exited

        try:
            process.stdin.close()
        except (OSError, BrokenPipeError):
            pass

        # Wait for process to complete and drain remaining output
        try:
            process.wait(timeout=30)
        except subprocess.TimeoutExpired:
            process.kill()

        drain_output_until_idle(max_wait_ms=2000)

        # Wait for threads
        stdout_thread.join(timeout=2)
        stderr_thread.join(timeout=2)

        return not has_error

    except Exception as e:
        logging.error(f"Interleaved execution failed: {e}")
        write_output(f"ERROR: {e}")
        return False