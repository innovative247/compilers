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
    Load a specific profile from settings.json.

    Args:
        profile_name: Name of the profile to load

    Returns:
        Dictionary with HOST, PORT, USERNAME, PASSWORD, PLATFORM, etc.

    Raises:
        KeyError: If profile not found in settings.json
        FileNotFoundError: If settings.json cannot be found
    """
    settings = load_settings()
    profiles = settings.get("Profiles", {})

    if profile_name not in profiles:
        raise KeyError(f"Profile '{profile_name}' not found in settings.json")

    profile = profiles[profile_name].copy()

    # Validate required fields
    required_fields = ["HOST", "PORT", "USERNAME", "PASSWORD", "PLATFORM"]
    missing = [field for field in required_fields if field not in profile]

    if missing:
        raise ValueError(f"Profile '{profile_name}' missing required fields: {', '.join(missing)}")

    return profile


def save_profile(profile_name: str, profile_data: dict) -> bool:
    """
    Save a profile to settings.json.

    Args:
        profile_name: Name of the profile
        profile_data: Dictionary containing profile settings

    Returns:
        True if successfully saved, False otherwise
    """
    try:
        settings = load_settings()

        if "Profiles" not in settings:
            settings["Profiles"] = {}

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
                platform: str = "SYBASE") -> tuple[bool, str]:
    """
    Execute freebcp with direct connection (-H host -p port).

    Uses FreeTDS freebcp for cross-platform parity. Works identically on Windows,
    macOS, and Linux using direct HOST:PORT connections (not server aliases).

    Args:
        host: Database host
        port: Database port
        username: Database username
        password: Database password
        table: Table name (e.g., "sbnmaster..users")
        direction: "in" or "out"
        file_path: Path to data file
        platform: Database platform (SYBASE or MSSQL), default SYBASE

    Returns:
        Tuple of (success: bool, message: str)

    Example:
        success, msg = execute_bcp("54.235.236.130", 5000, "sbn0", "ibsibs",
                                   "sbnmaster..users", "out", "/tmp/users.dat")
        if success:
            print(f"BCP successful: {msg}")
        else:
            print(f"BCP failed: {msg}")
    """
    # Check if freebcp is installed
    if not shutil.which("freebcp"):
        return False, "freebcp command not found. Install FreeTDS."

    logging.info(f"Executing freebcp {direction} for table {table}")

    # Build freebcp command with direct connection
    # -H = host (direct connection)
    # -p = port
    # -U = username
    # -P = password
    # -c = character mode (default)
    # -t = field terminator (tab)
    bcp_command = [
        "freebcp",
        table,
        direction,
        str(file_path),
        "-H", host,
        "-p", str(port),
        "-U", username,
        "-P", password,
        "-c",  # Character mode
        "-t", "\t"  # Tab-delimited
    ]

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


def get_config(args_list=None, profile_name=None, existing_config=None):
    """
    Loads settings.json, parses command-line args, selects/expands a profile.
    If the file or a profile is not found, it prompts the user to create it.
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
    selected_profile_name = parsed_args.profile_or_server
    if selected_profile_name is None:
        if parsed_args.server:
            selected_profile_name = parsed_args.server.upper()
            logging.debug(f"No explicit profile, using server '{selected_profile_name}' as profile name.")
        else:
            selected_profile_name = "DEFAULT"
            logging.debug(f"No profile or server specified, using '{selected_profile_name}' profile.")

    # 1. Check for settings.json and 2. Create if it doesn't exist
    if not SETTINGS_FILE.exists():
        logging.warning(f"{SETTINGS_FILE} not found. A new file will be created.")
        settings_data = {"Profiles": {}}
    else:
        try:
            settings_data = json.loads(SETTINGS_FILE.read_text())
        except json.JSONDecodeError as e:
            logging.error(f"Error decoding settings.json: {e}. Please fix or delete the file.")
            sys.exit(1)

    profiles = settings_data.get("Profiles", {})
    
    # 3. If the specified profile does not exist, prompt the user
    if selected_profile_name not in profiles:
        new_profile_data = prompt_for_new_profile(selected_profile_name)
        profiles[selected_profile_name] = new_profile_data
        settings_data["Profiles"] = profiles
        save_settings(settings_data) # Save the new profile for future use
        
    profile_config = profiles[selected_profile_name].copy()

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

# =============================================================================
# LEGACY COMPATIBILITY WRAPPERS (config dictionary-based)
# =============================================================================
# These wrappers maintain backward compatibility with existing commands that use
# the config dictionary approach. New code should use the direct parameter functions above.

def get_db_connection_legacy(config: dict, autocommit: bool = True):
    """
    Legacy wrapper: Establishes pyodbc connection from config dictionary.

    This function maintains backward compatibility with existing commands that pass
    a config dictionary. It extracts HOST, PORT, etc. from the config and calls
    the new get_db_connection() function.

    Args:
        config: Configuration dictionary with HOST, PORT, USERNAME, PASSWORD, PLATFORM, DATABASE
        autocommit: Enable autocommit mode (default: True)

    Returns:
        pyodbc connection object

    Note:
        New code should use get_db_connection() or get_db_connection_from_profile() directly.
    """
    # Extract connection parameters from config
    host = config.get('HOST')
    port = config.get('PORT')
    username = config.get('USERNAME')
    password = config.get('PASSWORD')
    platform = config.get('PLATFORM', '').upper()
    database = config.get('DATABASE')

    # Fall back to DSQUERY if HOST/PORT not present (legacy profiles)
    if not host or not port:
        dsquery = config.get('DSQUERY')
        if dsquery:
            logging.warning(f"Profile uses legacy DSQUERY '{dsquery}'. Please update to HOST/PORT format.")
            # For now, treat DSQUERY as HOST and use default ports
            host = dsquery
            port = 1433 if platform == "MSSQL" else 5000

    if not all([host, port, username, password, platform]):
        missing = []
        if not host: missing.append("HOST")
        if not port: missing.append("PORT")
        if not username: missing.append("USERNAME")
        if not password: missing.append("PASSWORD")
        if not platform: missing.append("PLATFORM")
        raise ValueError(f"Config missing required connection fields: {', '.join(missing)}")

    # Call the new direct connection function
    return get_db_connection(host, port, username, password, platform, database, autocommit)

def execute_sql(config, sql_string, database=None, fetch_results=False):
    """
    Executes a SQL string against the database. Can handle multiple batches separated by 'GO'.

    Args:
        config: Configuration dictionary with connection details
        sql_string: SQL commands to execute (can contain GO separators)
        database: Optional database name to override config DATABASE
        fetch_results: If True, return query results

    Returns:
        List of result rows if fetch_results=True, empty list otherwise
    """
    logging.debug(f"Executing SQL (fetch_results={fetch_results}, DB_override={database}):\n{sql_string[:200]}...")

    results = []
    conn_config = config.copy()
    if database:
        conn_config['DATABASE'] = database

    connection = None
    try:
        # Use the new legacy wrapper which handles HOST/PORT or DSQUERY
        connection = get_db_connection_legacy(conn_config)
        cursor = connection.cursor()

        batches = re.split(r'^\s*GO\s*$', sql_string, flags=re.MULTILINE | re.IGNORECASE)
        batches = [batch.strip() for batch in batches if batch.strip()]

        for batch in batches:
            if not batch: continue
            try:
                cursor.execute(batch)
                if fetch_results and cursor.description:
                    results.extend(cursor.fetchall())
            except pyodbc.ProgrammingError as e:
                logging.error(f"SQL batch failed: {batch[:100]}... Error: {e}")
                raise

    except pyodbc.Error as ex:
        sqlstate = ex.args[0] if ex.args else "Unknown"
        logging.error(f"SQL execution failed. SQLSTATE: {sqlstate}. Error: {ex}")
        sys.exit(1)
    finally:
        if connection:
            connection.close()

    return results

def execute_sql_procedure(config, proc_name, params=None, database=None, fetch_results=False):
    """Executes a stored procedure."""
    logging.debug(f"Executing stored procedure '{proc_name}' with params={params} (DB_override={database})...")
    sql = f"EXEC {proc_name}"
    if params:
        param_str = ", ".join([f"'{p.replace(chr(39), chr(39)+chr(39))}'" if isinstance(p, str) else str(p) for p in params])
        sql = f"EXEC {proc_name} {param_str}"
        
    return execute_sql(config, sql, database=database, fetch_results=fetch_results)

def execute_bcp_legacy(config: dict, table: str, direction: str, file_path: str) -> bool:
    """
    Legacy wrapper: Execute freebcp from config dictionary.

    This function maintains backward compatibility with existing commands that pass
    a config dictionary. It extracts HOST, PORT, etc. from the config and calls
    the new execute_bcp() function.

    Args:
        config: Configuration dictionary with HOST, PORT, USERNAME, PASSWORD, PLATFORM
        table: Table name (e.g., "sbnmaster..users")
        direction: "in" or "out"
        file_path: Path to data file

    Returns:
        True if successful, False otherwise

    Note:
        New code should use execute_bcp() directly.
    """
    # Extract connection parameters from config
    host = config.get('HOST')
    port = config.get('PORT')
    username = config.get('USERNAME')
    password = config.get('PASSWORD')
    platform = config.get('PLATFORM', 'SYBASE')

    # Fall back to DSQUERY if HOST/PORT not present (legacy profiles)
    if not host or not port:
        dsquery = config.get('DSQUERY')
        if dsquery:
            logging.warning(f"Profile uses legacy DSQUERY '{dsquery}'. Please update to HOST/PORT format.")
            host = dsquery
            port = 1433 if platform.upper() == "MSSQL" else 5000

    if not all([host, port, username, password]):
        missing = []
        if not host: missing.append("HOST")
        if not port: missing.append("PORT")
        if not username: missing.append("USERNAME")
        if not password: missing.append("PASSWORD")
        logging.error(f"Config missing required connection fields: {', '.join(missing)}")
        return False

    # Call the new direct BCP function
    success, message = execute_bcp(host, port, username, password, table, direction, file_path, platform)

    if not success:
        logging.error(f"BCP failed: {message}")

    return success

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


def create_symbolic_links(config: dict) -> bool:
    """
    Creates symbolic links for the compiler directory structure.

    Only creates links if SQL_SOURCE is defined and valid in the config.
    Links are created relative to the SQL_SOURCE directory.

    Directory structure created:
        css/ss      -> SQL_Sources
        css/upd     -> Updates
        css/ss/api  -> Application_Program_Interface
        css/ss/ba   -> Basics
        css/ss/bl   -> Billing
        css/ss/ma   -> Co_Monitoring
        css/ss/fe   -> Front_End
        css/ss/mo   -> Monitoring
        css/ss/sv   -> Service
        css/ss/si   -> System_Init
        css/ss/tm   -> Telemarketing
        css/ss/ub   -> US_Basics
        css/ss/mb   -> Mobile
        ibs/ss      -> SQL_Sources

    Args:
        config: Configuration dictionary containing SQL_SOURCE

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

    # Define symbolic links: (link_path, target_path) relative to base_path
    # Note: targets are relative to the link's parent directory
    symbolic_links = [
        # css directory links
        ("css/ss", "SQL_Sources"),
        ("css/upd", "Updates"),
        # css/ss subdirectory links (targets relative to css/ss, so need ../..)
        ("css/ss/api", "Application_Program_Interface"),
        ("css/ss/ba", "Basics"),
        ("css/ss/bl", "Billing"),
        ("css/ss/ma", "Co_Monitoring"),
        ("css/ss/fe", "Front_End"),
        ("css/ss/mo", "Monitoring"),
        ("css/ss/sv", "Service"),
        ("css/ss/si", "System_Init"),
        ("css/ss/tm", "Telemarketing"),
        ("css/ss/ub", "US_Basics"),
        ("css/ss/mb", "Mobile"),
        # ibs directory links
        ("ibs/ss", "SQL_Sources"),
    ]

    success = True

    for link_rel, target_name in symbolic_links:
        link_path = base_path / link_rel

        # Calculate relative target path from link's parent directory
        link_parent = link_path.parent
        # Target is always at base_path level
        target_path = base_path / target_name

        # Skip if link already exists
        if link_path.exists() or link_path.is_symlink():
            logging.debug(f"Symbolic link already exists: {link_path}")
            continue

        # Check if target exists
        if not target_path.exists():
            logging.warning(f"Target directory does not exist: {target_path} - skipping link {link_rel}")
            continue

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
            # On Windows, relpath fails if paths are on different drives
            relative_target = str(target_path)

        # Create the symbolic link
        try:
            if os.name == 'nt':
                # Windows: use subprocess to run mklink (requires admin or developer mode)
                import subprocess
                # mklink /D creates a directory symbolic link
                result = subprocess.run(
                    ['cmd', '/c', 'mklink', '/D', str(link_path), relative_target],
                    capture_output=True,
                    text=True
                )
                if result.returncode != 0:
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

    return success


def find_file(filename, config):
    """
    Searches for a file in specified paths (e.g., SQL_SOURCE path, current dir).

    Enhanced to support:
    - Non-linked path conversion (\\ss\\ba\\ -> \\SQL_Sources\\Basics\\)
    - Automatic .sql extension appending
    - Wildcard support (basic)
    - Multiple search locations (current dir, SQL_SOURCE path)

    Args:
        filename: File path to search for
        config: Configuration dictionary containing SQL_SOURCE path

    Returns:
        Absolute path to file if found, None otherwise
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

    logging.debug(f"File '{filename}' not found in common paths.")
    return None

# --- UI Utilities ---
def console_yes_no(prompt_text):
    """Gets a yes/no answer from the user."""
    while True:
        response = input(f"{prompt_text} (y/n): ").lower().strip()
        if response in ['y', 'yes']:
            return True
        elif response in ['n', 'no']:
            return False
        else:
            print("Please answer 'y' or 'n'.")

def launch_editor(file_path):
    """Launches a text editor for the given file."""
    editor = os.getenv('EDITOR')
    if not editor:
        editor = 'code' if shutil.which('code') else 'notepad' if sys.platform == "win32" else 'vim'
            
    logging.info(f"Launching editor '{editor}' for {file_path}")
    try:
        subprocess.run([editor, str(file_path)], check=True)
    except (subprocess.CalledProcessError, FileNotFoundError) as e:
        logging.error(f"Could not launch editor '{editor}': {e}")
        logging.info(f"Please open the file manually: {file_path}")

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
        self._cache_file = None
        self._cache_ttl_minutes = 1440  # 24 hours (24 * 60 minutes)
        self._was_rebuilt = False  # Track whether cache was rebuilt or reused

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
                    if line.startswith('v:'):
                        placeholder, value = self._parse_v_option(line)
                        if placeholder:
                            results.append((placeholder, value))

                    elif line.startswith('c:'):
                        items = self._parse_c_option(line)
                        results.extend(items)

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

        Format: options.{servertype}.{company}.{server}.tmp

        Returns:
            Path to cache file in temp directory
        """
        company = self.config.get('COMPANY', 'default')
        server = self.config.get('PROFILE_NAME', 'default')
        platform = self.config.get('PLATFORM', 'SYBASE')

        # Sanitize server name (replace \ and . with _)
        server_safe = str(server).replace('\\', '_').replace('.', '_')

        cache_name = f"options.{platform}.{company}.{server_safe}.tmp"

        # Use temp directory
        temp_dir = tempfile.gettempdir()
        return os.path.join(temp_dir, cache_name)

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

        # Get base path for option files: {SQL_SOURCE}\CSS\Setup
        path_append = self.config.get('SQL_SOURCE', os.getcwd())
        base_path = os.path.join(path_append, 'CSS', 'Setup')

        # Normalize path separators for Windows
        base_path = os.path.normpath(base_path)

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

        for filepath in option_files:
            logging.debug(f"Loading option file: {filepath}")
            items = self._load_option_file(filepath)
            for placeholder, value in items:
                # Later values override earlier values
                self._options[placeholder] = value
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

        # Also check for -> lines in option files (these override table_locations)
        for filepath in option_files:
            items = self._load_table_options(filepath)
            for placeholder, value in items:
                self._options[placeholder] = value

        logging.info(f"Loaded {len(self._options)} options from {len(option_files)} files")

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

        for placeholder, value in self._options.items():
            if placeholder in result:
                result = result.replace(placeholder, value)

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
        result = subprocess.run(
            cmd,
            input=script_content,
            capture_output=True,
            text=True,
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

        # Write to output file if specified
        if output_file:
            try:
                with open(output_file, 'w', encoding='utf-8') as f:
                    f.write(clean_output + "\n")
                logging.debug(f"Output written to: {output_file}")
            except Exception as e:
                return False, f"Failed to write output file: {e}"

        # Filter stderr for real errors (skip informational messages)
        if stderr:
            error_lines = []
            for line in stderr.splitlines():
                # Skip empty lines or lines with just quotes/whitespace
                stripped = line.strip().strip('"').strip()
                if not stripped:
                    continue
                # Skip locale/charset informational messages
                if line.startswith("locale is"):
                    continue
                if line.startswith("locale charset is"):
                    continue
                if line.startswith("using default charset"):
                    continue
                # Skip Sybase informational messages (severity 10)
                if "severity 10" in line.lower():
                    continue
                # Skip character set change messages (informational)
                if "Changed client character set" in line:
                    continue
                if "character set setting" in line.lower():
                    continue
                # Skip Msg lines from Sybase (informational status messages)
                if line.strip().startswith("Msg "):
                    continue
                error_lines.append(line)

            if error_lines:
                clean_stderr = "\n".join(error_lines).strip()
                if clean_stderr:
                    logging.warning(f"stderr output: {clean_stderr}")

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