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

def _handle_interrupt(sig, frame):
    """Handle Ctrl-C gracefully."""
    print("\nCtrl-C")
    sys.exit(1)

signal.signal(signal.SIGINT, _handle_interrupt)

# =============================================================================
# CONFIGURATION MANAGEMENT
# =============================================================================

def find_settings_file() -> Path:
    """
    Find settings.json in the same directory as this script (src/commands/).

    Returns:
        Path to settings.json (src/commands/settings.json)
    """
    script_dir = Path(__file__).parent.resolve()
    return script_dir / "settings.json"

def load_settings() -> dict:
    """
    Load settings.json and return as dict.

    Returns:
        Dictionary containing settings data with 'Profiles' section

    Raises:
        FileNotFoundError: If settings.json cannot be found
        json.JSONDecodeError: If settings.json is invalid JSON
    """
    settings_file = find_settings_file()

    if not settings_file.exists():
        logging.warning(f"settings.json not found at {settings_file}. Creating empty settings.")
        return {"Profiles": {}}

    try:
        with open(settings_file, 'r', encoding='utf-8') as f:
            settings_data = json.load(f)

        if "Profiles" not in settings_data:
            logging.warning("settings.json missing 'Profiles' section. Adding empty Profiles.")
            settings_data["Profiles"] = {}

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

def execute_bcp(host: str, port: int, username: str, password: str,
                table: str, direction: str, file_path: str,
                platform: str = "SYBASE",
                field_terminator: str = None) -> tuple[bool, str]:
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
        field_terminator: Field terminator (default: tab for multi-column, none for single-column)

    Returns:
        Tuple of (success: bool, message: str)

    Example:
        success, msg = execute_bcp("54.235.236.130", 5000, "sbn0", "ibsibs",
                                   "sbnmaster..users", "out", "/tmp/users.dat")
    """
    # Check if freebcp is installed
    if not shutil.which("freebcp"):
        return False, "freebcp command not found. Install FreeTDS."

    logging.info(f"Executing freebcp {direction} for table {table}")

    # Build freebcp command
    # -S = server (host:port format)
    # -U = username
    # -P = password
    # -c = character mode
    # -t = field terminator (optional)
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

    # Add field terminator if specified
    if field_terminator is not None:
        bcp_command.extend(["-t", field_terminator])


    # Print command for debugging (hide password)
    safe_cmd = bcp_command.copy()
    pw_idx = safe_cmd.index("-P") + 1
    safe_cmd[pw_idx] = "****"
    print(f"BCP command: {' '.join(safe_cmd)}")

    try:
        result = subprocess.run(bcp_command, capture_output=True, text=True, check=False)

        logging.debug(f"freebcp STDOUT:\n{result.stdout}")

        if result.stderr:
            logging.warning(f"freebcp STDERR:\n{result.stderr}")

        # Check for success indicators
        if result.returncode == 0:
            # Extract row count from output
            output_lower = result.stdout.lower()
            if "rows copied" in output_lower or "rows successfully" in output_lower:
                return True, f"BCP {direction} completed successfully"
            else:
                return True, f"BCP {direction} completed (returncode 0)"
        else:
            # Extract error message
            error_msg = f"BCP failed with returncode {result.returncode}"
            if result.stderr:
                error_msg += f": {result.stderr[:200]}"
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

    This should be called at the start of runcreate to ensure all symbolic
    links exist before processing create files that reference them.

    Args:
        config: Configuration dictionary containing SQL_SOURCE
        prompt: If True, prompt user for elevation on Windows when needed.
                If False, just attempt and return success/failure.

    Returns:
        True if all links created successfully (or already exist), False otherwise
    """
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
            logging.error(f"Failed to create parent directory {link_parent}: {e}")
            success = False
            continue

        # Calculate relative path from link location to target
        try:
            relative_target = os.path.relpath(target_path, link_parent)
        except ValueError:
            relative_target = str(target_path)

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
                        logging.error(f"Failed to create symbolic link {link_path}: {result.stderr.strip()}")
                        success = False
                else:
                    logging.info(f"Created symbolic link: {link_path} -> {relative_target}")
            else:
                # Unix: use os.symlink directly
                os.symlink(relative_target, link_path, target_is_directory=True)
                logging.info(f"Created symbolic link: {link_path} -> {relative_target}")
        except OSError as e:
            logging.error(f"Failed to create symbolic link {link_path}: {e}")
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
                    logging.error("Failed to launch elevated command")
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
    Searches for a file in specified paths, with recursive search and interactive selection.

    Search order:
    1. Absolute path (if provided)
    2. Current working directory
    3. Current directory + .sql extension
    4. SQL_SOURCE path directly
    5. SQL_SOURCE path + .sql extension
    6. Recursive search in SQL_SOURCE subdirectories

    If multiple matches are found during recursive search, the user is prompted
    to select which file to use via a numbered menu.

    Enhanced to support:
    - Non-linked path conversion (\\ss\\ba\\ -> \\SQL_Sources\\Basics\\)
    - Automatic .sql extension appending
    - Recursive search in SQL_SOURCE subdirectories
    - Interactive file selection when multiple matches found

    Args:
        filename: File path to search for
        config: Configuration dictionary containing SQL_SOURCE path

    Returns:
        Absolute path to file if found, None if not found or user cancelled
    """
    # First, convert any non-linked paths
    filename = convert_non_linked_paths(filename)

    file_path = Path(filename)

    # Try as absolute path
    if file_path.is_absolute() and file_path.exists():
        return str(file_path.resolve())

    # Try in current directory
    if (Path.cwd() / file_path).exists():
        return str((Path.cwd() / file_path).resolve())

    # Try with .sql extension
    if not filename.endswith('.sql'):
        sql_file = Path(filename + '.sql')
        if (Path.cwd() / sql_file).exists():
            return str((Path.cwd() / sql_file).resolve())

    # Try in SQL_SOURCE path
    path_append_str = config.get('SQL_SOURCE', '')
    if path_append_str:
        path_append = Path(path_append_str)

        # Try direct path in SQL_SOURCE
        if (path_append / file_path).exists():
            return str((path_append / file_path).resolve())

        # Try with .sql extension in SQL_SOURCE
        if not filename.endswith('.sql'):
            sql_file = Path(filename + '.sql')
            if (path_append / sql_file).exists():
                return str((path_append / sql_file).resolve())

        # Recursive search in SQL_SOURCE subdirectories
        # Use only the basename for rglob - it requires a relative pattern
        basename = file_path.name if file_path.name else filename
        search_pattern = basename if basename.endswith('.sql') else basename + '.sql'
        matches = list(path_append.rglob(search_pattern))

        if len(matches) == 1:
            # Single match - show path and use it
            found_path = str(matches[0].resolve())
            print(f"Executing: {found_path}")
            return found_path

        elif len(matches) > 1:
            # Multiple matches - prompt user to select
            print(f"\nMultiple files found matching '{search_pattern}':")
            for i, match in enumerate(matches, 1):
                # Show path relative to SQL_SOURCE for readability
                try:
                    rel_path = match.relative_to(path_append)
                except ValueError:
                    rel_path = match
                print(f"  {i}. {rel_path}")
            print(f"  {len(matches) + 1}. Cancel")

            while True:
                try:
                    choice = input(f"\nChoose [1-{len(matches) + 1}]: ").strip()
                    if not choice:
                        continue
                    choice_num = int(choice)
                    if choice_num == len(matches) + 1:
                        # User chose cancel
                        return None
                    if 1 <= choice_num <= len(matches):
                        return str(matches[choice_num - 1].resolve())
                    print(f"Please enter a number between 1 and {len(matches) + 1}")
                except ValueError:
                    print("Please enter a valid number")

    logging.debug(f"File '{filename}' not found in common paths.")
    return None

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
    Launches vim to edit the given file and waits for it to close.

    On Windows: Uses vim.exe from src/commands/ directory
    On macOS/Linux: Uses system vim (pre-installed)
    """
    if sys.platform == "win32":
        # Use vim.exe from the same directory as this script
        script_dir = Path(__file__).parent.resolve()
        editor = str(script_dir / "vim.exe")
        if not os.path.exists(editor):
            logging.error(f"vim.exe not found at {editor}")
            print(f"ERROR: vim.exe not found at {editor}")
            return
    else:
        # macOS and Linux have vim pre-installed
        editor = 'vim'

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

    def _load_option_file(self, filepath: str) -> list:
        """
        Load and parse a single option file.

        Args:
            filepath: Path to option file

        Returns:
            List of (placeholder, value) tuples
        """
        results = []

        if not os.path.exists(filepath):
            logging.debug(f"Option file not found: {filepath}")
            return results

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
                            results.append((placeholder, value))

                    elif prefix == 'V:':
                        # Dynamic value - NOT compiled, queried at runtime
                        # We still parse it to know it exists, but value is empty
                        # (actual value comes from database)
                        pass

                    elif prefix == 'c:':
                        # Static on/off - compile as &if_/&endif_ blocks
                        items = self._parse_c_option(line)
                        results.extend(items)

                    elif prefix == 'C:':
                        # Dynamic on/off - NOT compiled, queried at runtime
                        # (actual act_flg comes from database)
                        pass

                    elif line.startswith('->'):
                        # Table options need existing options to resolve db vars
                        # These are processed in a second pass
                        pass

        except Exception as e:
            logging.error(f"Error reading option file {filepath}: {e}")

        return results

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
        Check if cache file exists and is not expired (60 minute TTL).

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
        self._options = {}
        self._option_sources = {}

        for filepath in option_files:
            logging.debug(f"Loading option file: {filepath}")
            items = self._load_option_file(filepath)
            for placeholder, value in items:
                # Later values override earlier values
                self._options[placeholder] = value
                self._option_sources[placeholder] = filepath
                logging.debug(f"Set option: {placeholder} = {value}")

        # 4. table_locations - REQUIRED
        # Second pass: load table options (need v:/c: options first to resolve db vars)
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

        # Also check for -> lines in option files (these override table_locations)
        for filepath in option_files:
            items = self._load_table_options(filepath)
            for placeholder, value in items:
                self._options[placeholder] = value
                self._option_sources[placeholder] = filepath

        # Track all loaded files (option files + table_locations)
        self._loaded_files = option_files + [table_file]

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

        # Use regex to find and replace only placeholders that exist in the text
        # This is faster than iterating through all 500+ options
        def replacer(match):
            return self._options.get(match.group(0), match.group(0))
        result = re.sub(r'&[^&]+&', replacer, result)

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

def is_changelog_enabled(config: dict) -> tuple:
    """
    Check if changelog is enabled by querying the database.

    Checks two things:
    1. gclog12 option act_flg = '+' in &options& table
    2. ba_gen_chg_log_new stored procedure exists in &dbpro&

    Any error (table doesn't exist, database doesn't exist, connection error)
    silently returns (False, message) - no errors logged or shown to user.

    Args:
        config: Configuration dictionary with database connection info and SQL_SOURCE

    Returns:
        Tuple of (enabled: bool, message: str)
        - (True, "Changelog enabled") if both checks pass
        - (False, reason) if either check fails or error occurs
    """
    try:
        # Need to resolve placeholders first
        options = Options(config)
        if not options.generate_option_files():
            return False, "Failed to load options files"

        # Resolve &dbpro& for the database to query
        dbpro = options.replace_options("&dbpro&")
        if dbpro == "&dbpro&":
            return False, "&dbpro& placeholder not resolved"

        # Check 1: Is gclog12 enabled?
        # Query the options table in &dbpro& database
        query1 = options.replace_options("select act_flg from &options& where id = 'gclog12'")

        success, output = execute_sql_native(
            host=config.get('HOST', ''),
            port=config.get('PORT', 5000),
            username=config.get('USERNAME', ''),
            password=config.get('PASSWORD', ''),
            database=dbpro,  # Query against &dbpro& database
            platform=config.get('PLATFORM', 'SYBASE'),
            sql_content=query1
        )

        if not success:
            return False, "Could not query options table (may not exist)"

        if '+' not in output:
            return False, "gclog12 option is disabled (act_flg != '+')"

        # Check 2: Does ba_gen_chg_log_new exist?
        query2 = "select 1 from sysobjects where name = 'ba_gen_chg_log_new' and type = 'P'"

        success, output = execute_sql_native(
            host=config.get('HOST', ''),
            port=config.get('PORT', 5000),
            username=config.get('USERNAME', ''),
            password=config.get('PASSWORD', ''),
            database=dbpro,  # Query against &dbpro& database
            platform=config.get('PLATFORM', 'SYBASE'),
            sql_content=query2
        )

        if not success or '1' not in output:
            return False, "ba_gen_chg_log_new stored procedure not found in " + dbpro

        return True, "Changelog enabled"

    except Exception as e:
        return False, f"Error checking changelog: {str(e)}"


def insert_changelog_entry(config: dict, command_type: str, command: str,
                           description: str = None, upgrade_no: str = '') -> tuple:
    """
    Insert an entry into the change log by calling ba_gen_chg_log_new.

    Args:
        config: Configuration dictionary with database connection info
        command_type: Type of command (e.g., 'RUNSQL', 'ISQLLINE', 'TEST')
        command: The command that was executed
        description: Optional description (defaults to 'User `username` ...')
        upgrade_no: Optional upgrade reference number

    Returns:
        Tuple of (success: bool, message: str)
    """
    try:
        # Get username
        username = config.get('USERNAME', os.environ.get('USERNAME', 'unknown'))

        # Build description if not provided
        if description is None:
            description = f"User `{username}` executed {command_type.lower()}"

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


def compile_table_locations(config: dict, options: 'Options' = None) -> tuple:
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

    Returns:
        Tuple of (success: bool, message: str, row_count: int)
        - success: True if all rows were inserted successfully
        - message: Status message or error description
        - row_count: Number of rows inserted (0 on failure)
    """

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

    print(f"Inserting {len(sql_lines)} rows into {target_table}...")

    # Execute in batches of 1000
    batch_size = 1000
    total = len(sql_lines)
    for i in range(0, total, batch_size):
        batch = sql_lines[i:i + batch_size]
        end_idx = min(i + batch_size, total)
        print(f"  Inserting {i + 1}-{end_idx}")
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


def compile_actions(config: dict, options: 'Options' = None) -> tuple:
    """
    Compile actions source files into the database.

    This function reads the actions and actions_dtl source files, parses them,
    and inserts them into work tables using SQL INSERT statements. Then it
    executes the ba_compile_actions stored procedure to move data to final tables.

    PROCESS:
        1. Load options (if not provided) to resolve &placeholder& values
        2. Parse {SQL_SOURCE}/CSS/Setup/actions source file
        3. Parse {SQL_SOURCE}/CSS/Setup/actions_dtl source file
        4. Truncate work tables (w#actions, w#actions_dtl)
        5. Insert all rows using SQL INSERT statements
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

    Returns:
        Tuple of (success: bool, message: str)
    """

    # Create options if not provided
    if options is None:
        options = Options(config)
        if not options.generate_option_files():
            return False, "Failed to load options files"

    # Get source file paths
    actions_file = get_actions_path(config)
    actions_dtl_file = get_actions_dtl_path(config)

    if not os.path.exists(actions_file):
        return False, f"actions file not found: {actions_file}"

    if not os.path.exists(actions_dtl_file):
        return False, f"actions_dtl file not found: {actions_dtl_file}"

    # Resolve work table names
    w_actions = options.replace_options("&w#actions&")
    w_actions_dtl = options.replace_options("&w#actions_dtl&")
    dbpro = options.replace_options("&dbpro&")

    if w_actions == "&w#actions&":
        return False, "Could not resolve &w#actions& placeholder"
    if w_actions_dtl == "&w#actions_dtl&":
        return False, "Could not resolve &w#actions_dtl& placeholder"

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

    print(f"Parsed {len(header_rows)} action headers and {len(detail_rows)} action details")

    # Truncate work tables
    print(f"Truncating {w_actions}...")
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
        return False, f"Failed to truncate {w_actions}: {output}"

    print(f"Truncating {w_actions_dtl}...")
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
        return False, f"Failed to truncate {w_actions_dtl}: {output}"

    # Insert action headers
    if header_rows:
        print(f"Inserting {len(header_rows)} rows into {w_actions}...")
        sql_lines = []
        for row_num, line in header_rows:
            escaped_line = line.replace("'", "''")
            sql_lines.append(f"insert {w_actions} (lineix, message) values ({row_num}, '{escaped_line}')")

        # Execute in batches of 1000
        batch_size = 1000
        total = len(sql_lines)
        for i in range(0, total, batch_size):
            batch = sql_lines[i:i + batch_size]
            end_idx = min(i + batch_size, total)
            print(f"  Inserting {i + 1}-{end_idx}")
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
                return False, f"Failed to insert action headers: {output}"

    # Insert action details
    if detail_rows:
        print(f"Inserting {len(detail_rows)} rows into {w_actions_dtl}...")
        sql_lines = []
        for col1, col2, col3, col4, col5, col6 in detail_rows:
            # Escape single quotes
            col1 = col1.replace("'", "''")
            col2 = col2.replace("'", "''")
            col3 = col3.replace("'", "''")
            col4 = col4.replace("'", "''")
            col5 = col5.replace("'", "''")
            col6 = col6.replace("'", "''")
            sql_lines.append(f"insert {w_actions_dtl} ([s#act], typ, ix, [s#msgno], shix, text) values ({col1}, {col2}, {col3}, {col4}, {col5}, '{col6}')")

        # Execute in batches of 1000
        batch_size = 1000
        total = len(sql_lines)
        for i in range(0, total, batch_size):
            batch = sql_lines[i:i + batch_size]
            end_idx = min(i + batch_size, total)
            print(f"  Inserting {i + 1}-{end_idx}")
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
                return False, f"Failed to insert action details: {output}"

    # Execute ba_compile_actions stored procedure
    print(f"Executing {dbpro}..ba_compile_actions...")
    success, output = execute_sql_native(
        host=config.get('HOST'),
        port=config.get('PORT'),
        username=config.get('USERNAME'),
        password=config.get('PASSWORD'),
        database=dbpro,
        platform=config.get('PLATFORM', 'SYBASE'),
        sql_content=f"exec {dbpro}..ba_compile_actions"
    )

    if success:
        return True, f"Compiled {len(header_rows)} headers and {len(detail_rows)} details"
    else:
        return False, f"ba_compile_actions failed: {output}"


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


def compile_required_fields(config: dict, options: 'Options' = None) -> tuple:
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

    Returns:
        Tuple of (success: bool, message: str)
    """

    # Create options if not provided
    if options is None:
        options = Options(config)
        if not options.generate_option_files():
            return False, "Failed to load options files"

    # Get source file paths
    rf_file = get_required_fields_path(config)
    rf_dtl_file = get_required_fields_dtl_path(config)

    if not os.path.exists(rf_file):
        return False, f"Required Fields import file is missing ({rf_file})"

    if not os.path.exists(rf_dtl_file):
        return False, f"Required Fields Detail import file is missing ({rf_dtl_file})"

    # Resolve work table names
    w_rf = options.replace_options("&w#i_required_fields&")
    w_rf_dtl = options.replace_options("&w#i_required_fields_dtl&")
    dbpro = options.replace_options("&dbpro&")

    if w_rf == "&w#i_required_fields&":
        return False, "Could not resolve &w#i_required_fields& placeholder"
    if w_rf_dtl == "&w#i_required_fields_dtl&":
        return False, "Could not resolve &w#i_required_fields_dtl& placeholder"

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

    print(f"Parsed {len(header_rows)} required field headers and {len(detail_rows)} required field details")

    # Delete from work tables (C# used DELETE, not TRUNCATE)
    print(f"Clearing {w_rf}...")
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
        return False, f"Failed to clear {w_rf}: {output}"

    print(f"Clearing {w_rf_dtl}...")
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
        return False, f"Failed to clear {w_rf_dtl}: {output}"

    # Insert required field headers
    # Columns: s#rf (int), name (varchar), title (varchar), helptxt (varchar), inact_flg (char), s#sk (int)
    # Numeric columns: 0, 5
    if header_rows:
        print(f"Inserting {len(header_rows)} rows into {w_rf}...")
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
            print(f"  Inserting {i + 1}-{end_idx}")
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
                return False, f"Failed to insert required field headers: {output}"

    # Insert required field details
    # 34 columns - numeric columns identified by data pattern (-1, 0, 1045, etc.)
    # Numeric column indices: 0, 1, 2, 4, 11, 12, 13, 14, 19, 21, 23, 25, 28, 30, 33
    numeric_cols = {0, 1, 2, 4, 11, 12, 13, 14, 19, 21, 23, 25, 28, 30, 33}
    if detail_rows:
        print(f"Inserting {len(detail_rows)} rows into {w_rf_dtl}...")
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
            print(f"  Inserting {i + 1}-{end_idx}")
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
                return False, f"Failed to insert required field details: {output}"

    # Execute i_required_fields_install stored procedure
    print(f"Executing {dbpro}..i_required_fields_install...")
    success, output = execute_sql_native(
        host=config.get('HOST'),
        port=config.get('PORT'),
        username=config.get('USERNAME'),
        password=config.get('PASSWORD'),
        database=dbpro,
        platform=config.get('PLATFORM', 'SYBASE'),
        sql_content=f"exec {dbpro}..i_required_fields_install"
    )

    if success:
        return True, f"Compiled {len(header_rows)} headers and {len(detail_rows)} details"
    else:
        return False, f"i_required_fields_install failed: {output}"


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


def compile_messages(config: dict, options: 'Options' = None) -> tuple:
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

    Returns:
        Tuple of (success: bool, message: str)
    """

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

    # Check all source files exist
    print("Validating source files...")
    for msg_type, msg_ext, grp_ext in message_types:
        msg_file = get_messages_path(config, msg_ext)
        grp_file = get_messages_path(config, grp_ext)

        if not os.path.exists(msg_file):
            return False, f"{msg_type.upper()} Messages file is missing ({msg_file})"
        if not os.path.exists(grp_file):
            return False, f"{msg_type.upper()} Message Group file is missing ({grp_file})"

    # Resolve database name
    dbpro = options.replace_options("&dbpro&")
    dbwrk = options.replace_options("&dbwrk&")

    # Step 1: Preserve translated messages via stored procedure
    # This copies user translations from gui_messages to gui_messages_save
    print("Preserving translated messages into table gui_messages_save...")
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
        return False, f"ba_compile_gui_messages_save failed: {output}"

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
        print(f"Truncating {w_msg}...")
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

        print(f"Truncating {w_grp}...")
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
        print(f"Importing {msg_type.upper()} Messages...")
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
            print(f"  Inserting {len(msg_rows)} rows into {w_msg}...")
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
                print(f"    Inserting {i + 1}-{end_idx}")
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
            print(f"  Inserting {len(grp_rows)} rows into {w_grp}...")
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
                print(f"    Inserting {i + 1}-{end_idx}")
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
    print(f"Running i_compile_messages...")
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
        return False, f"i_compile_messages failed: {output}"

    print(f"Running i_compile_jam_messages...")
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
        return False, f"i_compile_jam_messages failed: {output}"

    print(f"Running i_compile_jrw_messages...")
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
        return False, f"i_compile_jrw_messages failed: {output}"

    return True, f"Compiled {total_msg_rows} messages and {total_grp_rows} message groups"


def export_messages(config: dict, options: 'Options' = None) -> tuple:
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

    Returns:
        Tuple of (success: bool, message: str)
    """

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
        print(f"Exporting {msg_table} to {msg_file}...")
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
            print(f"  Exported {len(rows)} rows")
            total_exports += 1
            total_rows += len(rows)

        # Export message groups table
        # Columns: grp, s#minmsg, description
        print(f"Exporting {grp_table} to {grp_file}...")
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
            print(f"  Exported {len(rows)} rows")
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


def compile_options(config: dict, options: 'Options' = None) -> tuple:
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

    Returns:
        Tuple of (success: bool, message: str)
        - success: True if options were imported successfully
        - message: Status message including count of options imported
    """
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
        print(f"Warning: Profile options file not found: {profile_file}")

    # Parse and convert to :> format
    print(f"Processing company options: {company_file}")
    company_options = generate_import_option_file(company_file)

    print(f"Processing profile options: {profile_file}")
    profile_options = generate_import_option_file(profile_file)

    # Combine options (profile overrides company for same option name)
    combined_options = combine_option_files(company_options, profile_options)

    if not combined_options:
        return False, "No options found to import"

    print(f"Combined {len(combined_options)} options")

    # Resolve work table
    work_table = options.replace_options("&w#options&")
    if work_table == "&w#options&":
        return False, "Could not resolve &w#options& placeholder"

    # Extract database from work_table
    work_db = work_table.split('..')[0] if '..' in work_table else None

    # Delete from work table
    print(f"Clearing {work_table}...")
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
        return False, f"Failed to clear work table: {output}"

    # Filter out empty lines
    filtered_options = [opt for opt in combined_options if opt and opt.strip()]

    # Insert options using SQL INSERT statements (BCP has 255 char limit)
    print(f"Inserting {len(filtered_options)} options into {work_table}...")

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
        print(f"  Inserting {i + 1}-{end_idx}")
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
            return False, f"Failed to insert options: {output}"

    # Execute i_import_options stored procedure
    dbpro = options.replace_options("&dbpro&")
    proc_call = f"exec {dbpro}..i_import_options"
    print(f"Executing: {proc_call}")

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
        return False, f"Failed to execute i_import_options: {output}"

    # Delete the options cache file so it gets rebuilt next time
    cache_file = options.get_cache_filepath()
    try:
        if os.path.exists(cache_file):
            os.remove(cache_file)
            print(f"Deleted options cache: {cache_file}")
    except Exception:
        pass

    # Also compile table_locations (options may have changed database mappings)
    print("\nCompiling table_locations...")
    tbl_success, tbl_message, tbl_count = compile_table_locations(config, options)
    if tbl_success:
        print(f"Inserted {tbl_count} rows into table_locations")
    else:
        print(f"Warning: table_locations compile failed: {tbl_message}")

    return True, f"Imported {len(filtered_options)} options"


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
        "if exists (select * from &options& where id = 'gclog12' and act_flg = '+')",
        "if exists (select * from &dbpro&..sysobjects where name = 'ba_gen_chg_log_new')",
        f"exec &dbpro&..ba_gen_chg_log_new '', 'User `{username}` ran isqlline', 'ISQLLINE', '', 'isqlline {escaped_cmd} {database} {server} {company}', '', 'X'",
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

def execute_sql_native(host: str, port: int, username: str, password: str,
                       database: str, platform: str, sql_content: str,
                       output_file: str = None, echo_input: bool = False) -> tuple[bool, str]:
    """
    Execute SQL using FreeTDS tsql via subprocess.

    tsql works for both Sybase ASE and MSSQL. This pipes SQL through stdin
    to tsql, which provides better compatibility with the original C# implementation.

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

        # Execute the compiler, piping SQL via stdin
        # Use UTF-8 encoding to handle Unicode characters in SQL content
        result = subprocess.run(
            cmd,
            input=script_content,
            capture_output=True,
            text=True,
            encoding='utf-8',
            errors='replace',  # Replace unencodable chars rather than failing
            timeout=300  # 5 minute timeout
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
        if stderr:
            # Look for ALL error messages and check if any have severity > 10
            error_matches = re.findall(r'Msg (\d+) \(severity (\d+)', stderr)
            has_real_error = any(int(sev) > 10 for msg_num, sev in error_matches)

            if has_real_error:
                # Extract error details - collect lines after error headers
                error_lines = []
                in_error_block = False
                for line in stderr.splitlines():
                    # Check if this is an error header line (severity > 10)
                    header_match = re.match(r'Msg (\d+) \(severity (\d+)', line)
                    if header_match:
                        sev = int(header_match.group(2))
                        if sev > 10:
                            in_error_block = True
                            error_lines.append(line)
                        else:
                            in_error_block = False
                        continue

                    # If we're in an error block, collect the message lines
                    if in_error_block:
                        # Clean up the line - remove tabs and quotes
                        cleaned = line.strip().strip('\t').strip('"').strip()
                        if cleaned:
                            error_lines.append(cleaned)

                sql_error = "\n".join(error_lines) if error_lines else stderr[:500]

        if sql_error:
            return False, sql_error

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
            return True, clean_output if clean_output else "(No rows returned)"
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