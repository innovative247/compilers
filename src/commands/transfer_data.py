#!/usr/bin/env python3
"""
transfer_data - Cross-platform data transfer utility.

Transfers data between SYBASE and MSSQL servers using INSERT statements
to overcome FreeTDS BCP 255-character field limitations.

This tool manages projects stored in the "data_transfer" section of settings.json,
completely separate from the "Profiles" section used by other compiler tools.
"""

import sys
import os
import getpass
import time
import shutil
import subprocess
import re
from datetime import datetime

from .ibs_common import (
    # Styling utilities
    Icons, Fore, Style,
    print_header, print_subheader, print_step,
    print_success, print_error, print_warning, print_info,
    style_path, style_database, style_command, style_dim,
)


def check_freetds_version() -> tuple:
    """
    Check FreeTDS installation and version for long column support.

    Returns:
        Tuple of (success: bool, message: str, version: str or None)
    """
    # Check if tools exist
    tsql_path = shutil.which("tsql")
    freebcp_path = shutil.which("freebcp")

    if not tsql_path:
        return False, "tsql not found in PATH. Install FreeTDS.", None
    if not freebcp_path:
        return False, "freebcp not found in PATH. Install FreeTDS.", None

    # Get version info from tsql -C
    try:
        result = subprocess.run(
            ["tsql", "-C"],
            capture_output=True,
            text=True,
            timeout=10
        )
        output = result.stdout + result.stderr

        # Parse version: "Version: freetds v1.5.4"
        version_match = re.search(r'Version:\s*freetds\s*v?(\d+\.\d+\.?\d*)', output, re.IGNORECASE)
        if not version_match:
            return False, "Could not determine FreeTDS version.", None

        version_str = version_match.group(1)

        # Parse version components
        parts = version_str.split('.')
        major = int(parts[0])
        minor = int(parts[1]) if len(parts) > 1 else 0

        # Check minimum version (1.0+ recommended for reliable long column support)
        # Version 0.95+ has partial support, 1.0+ is reliable
        if major < 1:
            if major == 0 and minor >= 95:
                return True, f"FreeTDS v{version_str} (warning: v1.0+ recommended for long columns)", version_str
            else:
                return False, f"FreeTDS v{version_str} is too old. Upgrade to v1.0+ for long column support.", version_str

        return True, f"FreeTDS v{version_str}", version_str

    except subprocess.TimeoutExpired:
        return False, "Timeout checking FreeTDS version.", None
    except Exception as e:
        return False, f"Error checking FreeTDS: {e}", None

# Cross-platform keyboard input
if sys.platform == 'win32':
    import msvcrt
    def get_key():
        """Get a single keypress on Windows."""
        key = msvcrt.getch()
        if key == b'\xe0':  # Arrow key prefix
            key = msvcrt.getch()
            if key == b'H':
                return 'up'
            elif key == b'P':
                return 'down'
        elif key == b'\r':
            return 'enter'
        elif key == b' ':
            return 'space'
        elif key in (b'a', b'A'):
            return 'a'
        elif key in (b'n', b'N'):
            return 'n'
        elif key in (b'q', b'Q', b'\x1b'):  # q or Escape
            return 'quit'
        return None
else:
    import tty
    import termios
    def get_key():
        """Get a single keypress on Unix/Mac."""
        fd = sys.stdin.fileno()
        old_settings = termios.tcgetattr(fd)
        try:
            tty.setraw(fd)
            ch = sys.stdin.read(1)
            if ch == '\x1b':  # Escape sequence
                ch2 = sys.stdin.read(1)
                if ch2 == '[':
                    ch3 = sys.stdin.read(1)
                    if ch3 == 'A':
                        return 'up'
                    elif ch3 == 'B':
                        return 'down'
                return 'quit'
            elif ch == '\r' or ch == '\n':
                return 'enter'
            elif ch == ' ':
                return 'space'
            elif ch in ('a', 'A'):
                return 'a'
            elif ch in ('n', 'N'):
                return 'n'
            elif ch in ('q', 'Q'):
                return 'quit'
        finally:
            termios.tcsetattr(fd, termios.TCSADRAIN, old_settings)
        return None


def interactive_checkbox(title: str, items: list, selected: set = None) -> list:
    """
    Interactive checkbox selection with arrow keys.

    Args:
        title: Title to display
        items: List of items to select from
        selected: Set of pre-selected items (default: all selected)

    Returns:
        List of selected items, or None if cancelled
    """
    if selected is None:
        selected = set(items)
    else:
        selected = set(selected)

    cursor = 0
    total = len(items)

    # Determine visible window (for long lists)
    max_visible = 20

    while True:
        # Calculate visible range
        if total <= max_visible:
            start = 0
            end = total
        else:
            # Keep cursor roughly centered
            half = max_visible // 2
            start = max(0, cursor - half)
            end = min(total, start + max_visible)
            if end == total:
                start = total - max_visible

        # Clear screen and draw
        print(f"\033[2J\033[H", end="")  # Clear screen, cursor to top
        print(f"{title}")
        print(f"↑↓=move  Space=toggle  A=all  N=none  Q=cancel\n")

        for i in range(start, end):
            marker = "[X]" if items[i] in selected else "[ ]"
            pointer = ">" if i == cursor else " "
            print(f"  {pointer} {marker} {items[i]}")

        if total > max_visible:
            print(f"\n  ... showing {start+1}-{end} of {total}")

        print(f"\nSelected: {len(selected)}/{total}")
        print(f">>> Press ENTER to confirm <<<")

        # Get keypress
        key = get_key()

        if key == 'up':
            cursor = (cursor - 1) % total
        elif key == 'down':
            cursor = (cursor + 1) % total
        elif key == 'space':
            item = items[cursor]
            if item in selected:
                selected.remove(item)
            else:
                selected.add(item)
        elif key == 'enter':
            print()  # Newline after selection
            return [item for item in items if item in selected]
        elif key == 'a':
            selected = set(items)
        elif key == 'n':
            selected = set()
        elif key == 'quit':
            print()
            return None


class ProjectLog:
    """Simple logger that writes to both console and project-specific log file."""

    def __init__(self, project_name: str, project_dir: str, append: bool = False):
        self.project_name = project_name
        self.project_dir = project_dir
        self.log_path = os.path.join(project_dir, f"{project_name}.log")
        mode = 'a' if append else 'w'
        self.file = open(self.log_path, mode, encoding='utf-8')
        # Write header to log file only (not console)
        if not append:
            self.file.write(f"=== Transfer Log: {project_name} ===\n")
            self.file.write(f"Started: {datetime.now().isoformat()}\n")
        else:
            self.file.write(f"\n=== Resumed: {datetime.now().isoformat()} ===\n")
        self.file.flush()

    def log(self, message: str = "", end: str = '\n'):
        """Print to console and write to log file."""
        print(message, end=end, flush=True)
        # Strip carriage returns for log file (progress updates)
        if message.startswith('\r'):
            message = message[1:]
        self.file.write(message + end)
        self.file.flush()

    def close(self):
        """Close the log file."""
        self.file.write(f"\nCompleted: {datetime.now().isoformat()}\n")
        self.file.close()
        print(f"\nLog saved to: {self.log_path}")


def load_manifest(project_dir: str, database: str) -> dict:
    """Load manifest for a database from project directory."""
    import json
    manifest_path = os.path.join(project_dir, f"{database}_manifest.json")
    if os.path.exists(manifest_path):
        with open(manifest_path, 'r', encoding='utf-8') as f:
            return json.load(f)
    return {"database": database, "tables": {}}


def save_manifest(project_dir: str, database: str, manifest: dict):
    """Save manifest for a database to project directory."""
    import json
    manifest_path = os.path.join(project_dir, f"{database}_manifest.json")
    with open(manifest_path, 'w', encoding='utf-8') as f:
        json.dump(manifest, f, indent=2)


def update_manifest(project_dir: str, database: str, table: str, extracted_rows: int):
    """Update manifest with extraction info for a table."""
    manifest = load_manifest(project_dir, database)
    manifest["tables"][table] = {
        "extracted_rows": extracted_rows,
        "extracted_at": datetime.now().isoformat()
    }
    save_manifest(project_dir, database, manifest)


def get_expected_rows(project_dir: str, database: str, table: str) -> int:
    """Get expected row count from manifest. Returns -1 if not found."""
    manifest = load_manifest(project_dir, database)
    table_info = manifest.get("tables", {}).get(table, {})
    return table_info.get("extracted_rows", -1)


# Add parent directory to path for imports
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from commands.ibs_common import (
    # Data transfer project management
    load_data_transfer_projects,
    save_data_transfer_project,
    delete_data_transfer_project,
    list_data_transfer_projects,
    load_data_transfer_project,
    # Database/table discovery
    get_databases_from_server,
    get_tables_from_database,
    filter_tables_by_patterns,
    match_wildcard_pattern,
    # Transfer operations
    transfer_single_table,
    get_table_row_count,
    # State management
    save_transfer_state,
    load_transfer_state,
    clear_transfer_state,
    get_pending_tables,
    get_completed_tables,
    # Settings
    open_settings_in_editor,
)


def clear_screen():
    """Clear the terminal screen."""
    os.system('cls' if os.name == 'nt' else 'clear')


def parse_pattern_input(input_str: str) -> list:
    """
    Parse user input into a list of patterns.
    Supports comma-separated, space-separated, or mixed.

    Args:
        input_str: User input like "sbn*, ibs" or "w#* srm_*"

    Returns:
        List of pattern strings
    """
    if not input_str.strip():
        return []

    # Replace commas with spaces, then split
    normalized = input_str.replace(',', ' ')
    patterns = [p.strip() for p in normalized.split() if p.strip()]
    return patterns


def test_connection(platform: str, host: str, port: int,
                    username: str, password: str) -> tuple:
    """
    Test database connection with proper error handling.

    Returns:
        Tuple of (success: bool, message: str)
    """
    print(f"\nTesting connection to {host}:{port}...")

    try:
        success, result = get_databases_from_server(
            host, port, username, password, platform
        )

        if success:
            db_count = len(result) if isinstance(result, list) else 0
            return True, f"Connected successfully. Found {db_count} databases."
        else:
            # Provide helpful error messages for common issues
            error_lower = str(result).lower()
            if 'login' in error_lower or 'password' in error_lower:
                return False, f"Authentication failed. Check username/password.\nDetails: {result}"
            elif 'permission' in error_lower or 'denied' in error_lower:
                return False, f"Permission denied. User may lack required privileges.\nDetails: {result}"
            elif 'connection' in error_lower or 'refused' in error_lower:
                return False, f"Connection refused. Check host and port.\nDetails: {result}"
            elif 'timeout' in error_lower:
                return False, f"Connection timed out. Server may be unreachable.\nDetails: {result}"
            else:
                return False, f"Connection failed: {result}"

    except Exception as e:
        return False, f"Connection error: {str(e)}"


def prompt_connection_info(label: str, existing: dict = None) -> dict:
    """
    Prompt user for database connection information.

    Args:
        label: "Source" or "Destination"
        existing: Existing config to use as defaults (for editing)

    Returns:
        Dict with connection info, or None if cancelled
    """
    existing = existing or {}
    print()
    print_subheader(f"{label} Connection")
    print(f"  {style_dim(f'Configure the {label.lower()} database server connection.')}")
    print()

    config = {}

    # Platform
    default_platform = existing.get("PLATFORM", "SYBASE" if label == "Source" else "MSSQL")
    while True:
        platform = input(f"Platform (SYBASE/MSSQL) [{default_platform}]: ").strip().upper()
        if not platform:
            platform = default_platform
        if platform in ("SYBASE", "MSSQL"):
            config["PLATFORM"] = platform
            break
        print("Invalid platform. Enter SYBASE or MSSQL.")

    # Host
    default_host = existing.get("HOST", "")
    if default_host:
        host = input(f"Host [{default_host}]: ").strip()
        config["HOST"] = host if host else default_host
    else:
        config["HOST"] = input("Host (IP or hostname): ").strip()
        if not config["HOST"]:
            print("Host is required.")
            return None

    # Port
    default_port = existing.get("PORT", 5000 if config["PLATFORM"] == "SYBASE" else 1433)
    port_input = input(f"Port [{default_port}]: ").strip()
    config["PORT"] = int(port_input) if port_input else default_port

    # Username
    default_user = existing.get("USERNAME", "")
    if default_user:
        username = input(f"Username [{default_user}]: ").strip()
        config["USERNAME"] = username if username else default_user
    else:
        config["USERNAME"] = input("Username: ").strip()
        if not config["USERNAME"]:
            print("Username is required.")
            return None

    # Password - show hint if existing
    if existing.get("PASSWORD"):
        password = getpass.getpass("Password [****]: ")
        config["PASSWORD"] = password if password else existing["PASSWORD"]
    else:
        config["PASSWORD"] = getpass.getpass("Password: ")
        if not config["PASSWORD"]:
            print("Password is required.")
            return None

    # Test connection
    success, message = test_connection(
        config["PLATFORM"], config["HOST"], config["PORT"],
        config["USERNAME"], config["PASSWORD"]
    )

    if success:
        print_success(message)
        return config
    else:
        print()
        print_error(message)
        retry = input("\nRetry connection? [Y/n]: ").strip().lower()
        if retry != 'n':
            return prompt_connection_info(label, config)
        return None


def prompt_database_selection(config: dict) -> list:
    """
    Prompt user to select databases with wildcard support.

    Args:
        config: Connection config dict

    Returns:
        List of selected database names
    """
    print("\n--- Database Selection ---")
    print("Enter databases to transfer (wildcards allowed, comma/space separated)")
    print("Example: ibs, sbn*")

    db_input = input("\nDatabases: ").strip()
    if not db_input:
        print("At least one database is required.")
        return []

    patterns = parse_pattern_input(db_input)

    # Get all databases from server
    print("\nQuerying server for databases...")
    success, all_dbs = get_databases_from_server(
        config["HOST"], config["PORT"],
        config["USERNAME"], config["PASSWORD"],
        config["PLATFORM"]
    )

    if not success:
        print(f"ERROR: Failed to get databases: {all_dbs}")
        return []

    # Match patterns
    matched = []
    for db in all_dbs:
        if match_wildcard_pattern(db, patterns):
            matched.append(db)

    if not matched:
        print(f"No databases matched patterns: {patterns}")
        return []

    print(f"Matched databases: {', '.join(matched)}")

    return matched


def prompt_table_selection(config: dict, database: str,
                           current_include: list = None, current_exclude: list = None) -> dict:
    """
    Prompt user to select tables with include/exclude patterns.

    Args:
        config: Connection config dict
        database: Database name
        current_include: Current include patterns (for editing existing selection)
        current_exclude: Current exclude patterns (for editing existing selection)

    Returns:
        Dict with keys: tables, include_patterns, exclude_patterns
        Or empty dict if cancelled/error
    """
    print(f"\n--- Table Selection for {database} ---")

    # Get all tables
    print(f"Querying tables in {database}...")
    success, all_tables = get_tables_from_database(
        config["HOST"], config["PORT"],
        config["USERNAME"], config["PASSWORD"],
        database, config["PLATFORM"]
    )

    if not success:
        print(f"ERROR: Failed to get tables: {all_tables}")
        print("This may be a permission issue. Check that the user has access to this database.")
        skip = input("Skip this database? [Y/n]: ").strip().lower()
        if skip != 'n':
            return {}
        return None

    if not all_tables:
        print(f"No tables found in {database}.")
        return {}

    print(f"Found {len(all_tables)} tables.")

    # Include patterns - use current as default
    default_include = ', '.join(current_include) if current_include else "*"
    include_prompt = f"Tables to include [{default_include}]: " if current_include else "Tables to include (* for all): "
    include_input = input(include_prompt).strip()
    if not include_input:
        include_input = default_include
    include_patterns = parse_pattern_input(include_input)

    # Exclude patterns - use current as default
    default_exclude = ', '.join(current_exclude) if current_exclude else ""
    exclude_prompt = f"Tables to exclude [{default_exclude}]: " if current_exclude else "Tables to exclude (e.g., w#*, srm_*): "
    exclude_input = input(exclude_prompt).strip()
    if not exclude_input and current_exclude:
        exclude_input = default_exclude
    exclude_patterns = parse_pattern_input(exclude_input)

    # Filter tables
    filtered = filter_tables_by_patterns(all_tables, include_patterns, exclude_patterns)

    if not filtered:
        print("No tables matched after filtering.")
        return {}

    print(f"\nFound {len(filtered)} tables after filtering.")

    # Interactive selection with arrow keys
    result = interactive_checkbox(f"Select tables for {database}:", filtered)

    if result is None:
        return {}

    return {
        "tables": result,
        "include_patterns": include_patterns,
        "exclude_patterns": exclude_patterns
    }


def save_project_progress(project_name: str, project: dict) -> bool:
    """Save project and show confirmation."""
    if save_data_transfer_project(project_name, project):
        print(f"  [Saved: {project_name}]")
        return True
    else:
        print("  [ERROR: Failed to save]")
        return False


def prompt_save_continue(project_name: str, project: dict) -> str:
    """
    Prompt user to save progress.

    Returns:
        'continue' - saved and continue
        'exit' - saved and exit
        'skip' - continue without saving
    """
    choice = input("\n[S]ave and continue, [E]xit (save and quit), or Enter to continue: ").strip().lower()
    if choice in ('s', 'save'):
        save_project_progress(project_name, project)
        return 'continue'
    elif choice in ('e', 'exit'):
        save_project_progress(project_name, project)
        return 'exit'
    return 'skip'


def prompt_transfer_options(current_options: dict = None) -> dict:
    """
    Prompt for transfer options (mode, BCP batch size).

    Args:
        current_options: Existing options to show as defaults

    Returns:
        Dict with OPTIONS config
    """
    current_options = current_options or {}
    current_mode = current_options.get("MODE", "TRUNCATE")
    current_bcp_batch = current_options.get("BCP_BATCH_SIZE", 1000)

    print("\n--- Transfer Options ---")

    # Mode
    while True:
        mode_prompt = f"Mode: [T]runcate or [A]ppend? [{current_mode[0]}]: "
        mode = input(mode_prompt).strip().upper()
        if not mode:
            mode = current_mode
            break
        if mode in ('T', 'TRUNCATE'):
            mode = 'TRUNCATE'
            break
        elif mode in ('A', 'APPEND'):
            mode = 'APPEND'
            break
        print("Enter T for Truncate or A for Append.")

    # BCP batch size (rows per chunk for freebcp import)
    bcp_batch_input = input(f"BCP batch size (rows per chunk) [{current_bcp_batch}]: ").strip()
    bcp_batch_size = int(bcp_batch_input) if bcp_batch_input.isdigit() else current_bcp_batch

    return {
        "MODE": mode,
        "BCP_BATCH_SIZE": bcp_batch_size
    }


def create_project_wizard():
    """
    Interactive wizard to create a new data transfer project.
    Saves progress after each step.
    """
    print_subheader("Create New Project")
    print()
    print(f"  {style_dim('A project stores source/destination connections and table selections.')}")
    print(f"  {style_dim('Projects can be run repeatedly to transfer data.')}")
    print()

    # Project name
    print_step(1, "Project Name")
    print(f"  {style_dim('Use a descriptive name like PROD_TO_DEV or SYBASE_MIGRATION.')}")
    project_name = input("  Name: ").strip()
    if not project_name:
        print_warning("Project name is required.")
        return

    # Check if exists
    existing = load_data_transfer_project(project_name)
    if existing:
        overwrite = input(f"Project '{project_name}' already exists. Overwrite? [y/N]: ").strip().lower()
        if overwrite != 'y':
            return

    # Initialize empty project
    project = {
        "SOURCE": None,
        "DESTINATION": None,
        "DATABASES": {},
        "OPTIONS": {"MODE": "TRUNCATE", "BCP_BATCH_SIZE": 1000},
        "TRANSFER_STATE": None
    }

    # Step 1: Source connection
    src_config = prompt_connection_info("Source", "SYBASE")
    if not src_config:
        print("Source connection cancelled.")
        return
    project["SOURCE"] = src_config

    action = prompt_save_continue(project_name, project)
    if action == 'exit':
        return

    # Step 2: Destination connection
    dest_config = prompt_connection_info("Destination", "MSSQL")
    if not dest_config:
        print("Destination connection cancelled.")
        return
    project["DESTINATION"] = dest_config

    action = prompt_save_continue(project_name, project)
    if action == 'exit':
        return

    # Step 3: Database selection
    databases = prompt_database_selection(src_config)
    if not databases:
        print("No databases selected.")
        return

    # Step 4: Table selection per database (save after each database)
    db_configs = {}
    for database in databases:
        # Ask for destination database name
        dest_db = input(f"\nDestination database for '{database}' [{database}]: ").strip()
        if not dest_db:
            dest_db = database  # Default to same name

        result = prompt_table_selection(src_config, database)
        if result is None:
            print("Table selection failed.")
            return
        if result and result.get("tables"):
            db_configs[database] = {
                "DEST_DATABASE": dest_db,
                "TABLES": result["tables"],
                "INCLUDE_PATTERNS": result.get("include_patterns", ["*"]),
                "EXCLUDE_PATTERNS": result.get("exclude_patterns", [])
            }
            project["DATABASES"] = db_configs

            # Save after each database's tables are selected
            action = prompt_save_continue(project_name, project)
            if action == 'exit':
                return

    if not db_configs:
        print("No tables selected from any database.")
        return

    # Step 5: Transfer options
    project["OPTIONS"] = prompt_transfer_options()

    # Final save
    total_tables = sum(len(db["TABLES"]) for db in db_configs.values())
    if save_data_transfer_project(project_name, project):
        print(f"\nProject saved: {project_name}")
        print(f"Total: {len(db_configs)} databases, {total_tables} tables")
    else:
        print("\nERROR: Failed to save project.")


def display_project_summary(project_name: str, project: dict):
    """Display a summary of a project's configuration."""
    src = project.get("SOURCE", {})
    dest = project.get("DESTINATION", {})
    options = project.get("OPTIONS", {})
    dbs = project.get("DATABASES", {})

    total_tables = sum(len(db.get("TABLES", [])) for db in dbs.values())

    print()
    print_subheader(f"Project: {project_name}")
    print()

    # Source
    src_platform = src.get('PLATFORM', 'N/A')
    src_host = src.get('HOST', 'N/A')
    src_port = src.get('PORT', 'N/A')
    print(f"  {Icons.DATABASE} Source:      {Fore.CYAN}{src_platform}{Style.RESET_ALL} @ {Fore.GREEN}{src_host}:{src_port}{Style.RESET_ALL}")

    # Destination
    dest_platform = dest.get('PLATFORM', 'N/A')
    dest_host = dest.get('HOST', 'N/A')
    dest_port = dest.get('PORT', 'N/A')
    print(f"  {Icons.DATABASE} Destination: {Fore.CYAN}{dest_platform}{Style.RESET_ALL} @ {Fore.GREEN}{dest_host}:{dest_port}{Style.RESET_ALL}")

    # Options
    mode = options.get('MODE', 'TRUNCATE')
    batch = options.get('BCP_BATCH_SIZE', 1000)
    print(f"  {Icons.GEAR} Mode:        {Fore.YELLOW}{mode}{Style.RESET_ALL}")
    print(f"  {Icons.GEAR} BCP Batch:   {batch:,} rows/chunk")

    # Database mappings
    print()
    print(f"  {Style.BRIGHT}Database Mappings:{Style.RESET_ALL} {style_dim(f'({total_tables} tables total)')}")
    for src_db, db_config in dbs.items():
        dest_db = db_config.get("DEST_DATABASE", src_db)
        table_count = len(db_config.get("TABLES", []))
        print(f"    {Icons.ARROW} {style_database(src_db)} {Fore.WHITE}->{Style.RESET_ALL} {style_database(dest_db)}  {style_dim(f'({table_count} tables)')}")


def run_project(project_name: str):
    """
    Run a data transfer project with edit options.
    """
    from commands.ibs_common import (
        get_databases_from_server, execute_bcp, get_table_row_count,
        truncate_table, verify_table_transfer
    )

    project = load_data_transfer_project(project_name)
    if not project:
        print(f"Project '{project_name}' not found.")
        return

    while True:
        # Reload project in case edits were made
        project = load_data_transfer_project(project_name)
        display_project_summary(project_name, project)

        # Create/check project directory
        project_dir = os.path.join(os.getcwd(), f"transfer_data_{project_name}")
        if not os.path.exists(project_dir):
            os.makedirs(project_dir)

        # Check for existing BCP files
        existing_bcp_files = [f for f in os.listdir(project_dir) if f.endswith('.bcp')]
        bcp_info = f" {style_dim(f'({len(existing_bcp_files)} BCP files)')}" if existing_bcp_files else ""

        print()
        print(f"{Style.BRIGHT}Options{Style.RESET_ALL}")
        print()
        print(f"  {Fore.CYAN}1.{Style.RESET_ALL} {Fore.GREEN}Run transfer{Style.RESET_ALL}{bcp_info}")
        print(f"  {Fore.CYAN}2.{Style.RESET_ALL} Edit source connection")
        print(f"  {Fore.CYAN}3.{Style.RESET_ALL} Edit destination connection")
        print(f"  {Fore.CYAN}4.{Style.RESET_ALL} Edit databases and tables")
        print(f"  {Fore.CYAN}5.{Style.RESET_ALL} Edit transfer options")
        print(f" {Fore.CYAN}99.{Style.RESET_ALL} Exit")

        choice = input("\nChoose [1-5]: ").strip()

        if choice == '1':
            # Run transfer - show mode submenu
            _execute_transfer(project_name, project, project_dir, existing_bcp_files)

        elif choice == '2':
            # Edit source
            src = project.get("SOURCE", {})
            new_src = prompt_connection_info("Source", src)
            if new_src:
                project["SOURCE"] = new_src
                clear_transfer_state(project_name)
                save_data_transfer_project(project_name, project)
                print("Source updated and saved.")

        elif choice == '3':
            # Edit destination
            dest = project.get("DESTINATION", {})
            new_dest = prompt_connection_info("Destination", dest)
            if new_dest:
                project["DESTINATION"] = new_dest
                clear_transfer_state(project_name)
                save_data_transfer_project(project_name, project)
                print("Destination updated and saved.")

        elif choice == '4':
            # Edit databases and tables
            src_config = project.get("SOURCE")
            if not src_config:
                print("Configure source connection first.")
                continue
            _edit_databases(project_name, project, src_config)

        elif choice == '5':
            # Edit transfer options
            current_options = project.get("OPTIONS", {})
            project["OPTIONS"] = prompt_transfer_options(current_options)
            save_data_transfer_project(project_name, project)
            print("Options updated and saved.")

        elif choice == '99':
            return

        else:
            print("Invalid choice.")


def _edit_databases(project_name: str, project: dict, src_config: dict):
    """Edit databases and tables for a project."""
    current_dbs = list(project.get("DATABASES", {}).keys())

    print(f"\nDatabase mappings:")
    if current_dbs:
        for i, src_db in enumerate(current_dbs, 1):
            dest_db = project["DATABASES"][src_db].get("DEST_DATABASE", src_db)
            table_count = len(project["DATABASES"][src_db].get("TABLES", []))
            print(f"  {i}. {src_db} -> {dest_db}  ({table_count} tables)")
        print(f"  {len(current_dbs) + 1}. [Add new database]")
    else:
        print("  None configured")
        print(f"  1. [Add new database]")

    db_choice = input("\nSelect database (or 99 to cancel): ").strip()
    if db_choice == '99' or not db_choice:
        return

    if db_choice.isdigit():
        idx = int(db_choice) - 1
        add_new_idx = len(current_dbs)

        if idx == add_new_idx:
            # Add new database
            new_dbs = prompt_database_selection(src_config)
            if new_dbs:
                for db in new_dbs:
                    if db not in project["DATABASES"]:
                        dest_db = input(f"Destination database for '{db}' [{db}]: ").strip()
                        if not dest_db:
                            dest_db = db
                        result = prompt_table_selection(src_config, db)
                        if result and result.get("tables"):
                            project["DATABASES"][db] = {
                                "DEST_DATABASE": dest_db,
                                "TABLES": result["tables"],
                                "INCLUDE_PATTERNS": result.get("include_patterns", ["*"]),
                                "EXCLUDE_PATTERNS": result.get("exclude_patterns", [])
                            }
                            print(f"\n  {db} -> {dest_db}:")
                            for t in result["tables"]:
                                print(f"    - {t}")
                clear_transfer_state(project_name)
                save_data_transfer_project(project_name, project)
                print("\nDatabases added and saved.")

        elif 0 <= idx < len(current_dbs):
            # Edit or delete existing database
            src_db = current_dbs[idx]
            dest_db = project["DATABASES"][src_db].get("DEST_DATABASE", src_db)
            tables = project["DATABASES"][src_db].get("TABLES", [])

            print(f"\n{src_db} -> {dest_db}  ({len(tables)} tables)")

            action = input("[E]dit or [D]elete? ").strip().lower()

            if action in ('d', 'delete'):
                confirm = input(f"Delete {src_db}? [y/N]: ").strip().lower()
                if confirm == 'y':
                    del project["DATABASES"][src_db]
                    clear_transfer_state(project_name)
                    save_data_transfer_project(project_name, project)
                    print(f"Deleted and saved: {src_db}")

            elif action in ('e', 'edit'):
                new_dest = input(f"Destination database [{dest_db}]: ").strip()
                if new_dest:
                    project["DATABASES"][src_db]["DEST_DATABASE"] = new_dest
                    dest_db = new_dest

                # Get current patterns for editing
                current_include = project["DATABASES"][src_db].get("INCLUDE_PATTERNS", [])
                current_exclude = project["DATABASES"][src_db].get("EXCLUDE_PATTERNS", [])

                result = prompt_table_selection(src_config, src_db, current_include, current_exclude)
                if result and result.get("tables"):
                    project["DATABASES"][src_db]["TABLES"] = result["tables"]
                    project["DATABASES"][src_db]["INCLUDE_PATTERNS"] = result.get("include_patterns", ["*"])
                    project["DATABASES"][src_db]["EXCLUDE_PATTERNS"] = result.get("exclude_patterns", [])
                    clear_transfer_state(project_name)
                    save_data_transfer_project(project_name, project)
                    print(f"\n  {src_db} -> {dest_db} (saved):")
                    for t in result["tables"]:
                        print(f"    - {t}")
                elif result is not None and not result.get("tables"):
                    confirm = input(f"No tables selected. Delete {src_db}? [y/N]: ").strip().lower()
                    if confirm == 'y':
                        del project["DATABASES"][src_db]
                        clear_transfer_state(project_name)
                        save_data_transfer_project(project_name, project)
                        print(f"Deleted and saved: {src_db}")


def _execute_transfer(project_name: str, project: dict, project_dir: str, existing_bcp_files: list):
    """Execute the actual data transfer."""
    from commands.ibs_common import (
        get_databases_from_server, execute_bcp, get_table_row_count,
        truncate_table
    )

    src_config = project.get("SOURCE", {})
    dest_config = project.get("DESTINATION", {})
    options = project.get("OPTIONS", {})
    databases = project.get("DATABASES", {})
    mode = options.get("MODE", "TRUNCATE")
    bcp_batch_size = options.get("BCP_BATCH_SIZE", 1000)

    # Build list of tables
    all_tables = []
    for src_db, db_config in databases.items():
        dest_db = db_config.get("DEST_DATABASE", src_db)
        for table in db_config.get("TABLES", []):
            all_tables.append((src_db, table, dest_db, table))

    if not all_tables:
        print("No tables to transfer.")
        return

    total_tables = len(all_tables)

    # Run mode selection
    print()
    print_subheader("Run Mode")
    print(f"  {style_dim('Select which phase(s) of the transfer to execute.')}")
    print()
    print(f"  {Fore.CYAN}1.{Style.RESET_ALL} Full transfer {style_dim('(Extract + Insert)')}")
    print(f"  {Fore.CYAN}2.{Style.RESET_ALL} Extract only {style_dim('(create BCP files)')}")
    print(f"  {Fore.CYAN}3.{Style.RESET_ALL} Insert only {style_dim('(import existing BCP files)')}")
    print(f" {Fore.CYAN}99.{Style.RESET_ALL} Exit")
    if existing_bcp_files:
        print(f"\n  {Icons.INFO} {len(existing_bcp_files)} BCP files found in project directory")

    while True:
        run_mode = input("\nChoose [1-3]: ").strip()
        if run_mode == '99':
            return
        if run_mode in ('1', '2', '3'):
            break
        print_warning("Invalid choice.")

    do_extract = run_mode in ('1', '2')
    do_insert = run_mode in ('1', '3')

    # Initialize project log
    log = ProjectLog(project_name, project_dir, append=False)

    # Validate connections based on mode
    if do_extract:
        log.log(f"\n--- Validating source connection ---")
        src_platform = src_config.get("PLATFORM", "SYBASE")
        success, src_dbs = get_databases_from_server(
            src_config.get("HOST"), src_config.get("PORT"),
            src_config.get("USERNAME"), src_config.get("PASSWORD"), src_platform
        )
        if not success:
            log.log(f"ERROR: Cannot connect to source server: {src_dbs}")
            log.close()
            return
        log.log(f"  Source connection OK")

    if do_insert:
        log.log(f"\n--- Validating destination connection ---")
        dest_platform = dest_config.get("PLATFORM", "MSSQL")
        success, dest_dbs = get_databases_from_server(
            dest_config.get("HOST"), dest_config.get("PORT"),
            dest_config.get("USERNAME"), dest_config.get("PASSWORD"), dest_platform
        )
        if not success:
            log.log(f"ERROR: Cannot connect to destination server: {dest_dbs}")
            log.close()
            return

        # Check destination databases exist
        dest_databases = set(t[2] for t in all_tables)
        dest_dbs_lower = [db.lower() for db in dest_dbs]
        missing_dbs = [db for db in dest_databases if db.lower() not in dest_dbs_lower]
        if missing_dbs:
            log.log(f"\nERROR: Missing destination databases: {', '.join(missing_dbs)}")
            log.close()
            return
        log.log(f"  Destination connection OK")

    # Summary
    log.log(f"\n=== Transfer Summary ===")
    log.log(f"Project:     {project_name}")
    log.log(f"Tables:      {total_tables}")
    log.log(f"Mode:        {mode}")
    log.log(f"Directory:   {project_dir}")
    mode_desc = "Full transfer" if run_mode == '1' else ("Extract only" if run_mode == '2' else "Insert only")
    log.log(f"Run mode:    {mode_desc}")
    if do_extract:
        log.log(f"Source:      {src_config.get('PLATFORM')} {src_config.get('HOST')}:{src_config.get('PORT')}")
    if do_insert:
        log.log(f"Destination: {dest_config.get('PLATFORM')} {dest_config.get('HOST')}:{dest_config.get('PORT')}")

    confirm = input(f"\nReady to start? [y/N]: ").strip().lower()
    if confirm != 'y':
        log.log("Cancelled.")
        log.close()
        return

    # Track results
    extract_results = {}
    insert_results = {}

    # ===== EXTRACTION PHASE =====
    if do_extract:
        log.log(f"\n{'='*50}")
        log.log(f"EXTRACTION PHASE")
        log.log(f"{'='*50}")

        src_host = src_config.get("HOST")
        src_port = src_config.get("PORT")
        src_user = src_config.get("USERNAME")
        src_pass = src_config.get("PASSWORD")
        src_platform = src_config.get("PLATFORM", "SYBASE")

        log.log(f"Source: {src_platform} {src_host}:{src_port} (user: {src_user})")
        log.log("")

        for idx, (src_db, src_table, dest_db, dest_table) in enumerate(all_tables, 1):
            full_name = f"{src_db}..{src_table}"
            bcp_file = os.path.join(project_dir, f"{src_db}_{src_table}.bcp")

            # Get source row count
            success, src_count = get_table_row_count(
                src_host, src_port, src_user, src_pass,
                src_db, src_table, src_platform
            )
            if not success:
                src_count = 0

            # Show extracting status with expected row count
            log.log(f"[{idx}/{total_tables}] Extracting: {full_name} ({src_count:,} rows)...")

            # BCP OUT
            src_table_full = f"{src_db}..{src_table}"
            success, output = execute_bcp(
                src_host, src_port, src_user, src_pass,
                src_table_full, "out", bcp_file, platform=src_platform,
                batch_size=bcp_batch_size
            )

            if success:
                rows = int(output) if output.isdigit() else 0
                log.log(f"[{idx}/{total_tables}] Extracted:  {full_name}  {rows:,} rows  OK")
                extract_results[full_name] = {"status": "ok", "rows": rows, "file": bcp_file}
                # Update manifest with extracted row count
                update_manifest(project_dir, src_db, src_table, rows)
            else:
                log.log(f"[{idx}/{total_tables}] Extracted:  {full_name}  FAILED")
                log.log(f"        ERROR: {output}")
                extract_results[full_name] = {"status": "failed", "error": output}

        # Extraction summary
        extracted = sum(1 for r in extract_results.values() if r["status"] == "ok")
        failed = sum(1 for r in extract_results.values() if r["status"] == "failed")
        log.log(f"\nExtraction complete: {extracted} OK, {failed} failed")

    # ===== INSERTION PHASE =====
    if do_insert:
        log.log(f"\n{'='*50}")
        log.log(f"INSERTION PHASE")
        log.log(f"{'='*50}")

        dest_host = dest_config.get("HOST")
        dest_port = dest_config.get("PORT")
        dest_user = dest_config.get("USERNAME")
        dest_pass = dest_config.get("PASSWORD")
        dest_platform = dest_config.get("PLATFORM", "MSSQL")

        log.log(f"Destination: {dest_platform} {dest_host}:{dest_port} (user: {dest_user})")
        log.log("")

        # If insert-only mode, check for BCP files
        if not do_extract:
            for src_db, src_table, dest_db, dest_table in all_tables:
                full_name = f"{src_db}..{src_table}"
                bcp_file = os.path.join(project_dir, f"{src_db}_{src_table}.bcp")
                if os.path.exists(bcp_file):
                    extract_results[full_name] = {"status": "ok", "file": bcp_file}
                else:
                    extract_results[full_name] = {"status": "missing"}

        for idx, (src_db, src_table, dest_db, dest_table) in enumerate(all_tables, 1):
            full_name = f"{src_db}..{src_table}"
            bcp_file = os.path.join(project_dir, f"{src_db}_{src_table}.bcp")

            # Check if BCP file exists
            if full_name not in extract_results or extract_results[full_name]["status"] != "ok":
                if not os.path.exists(bcp_file):
                    log.log(f"[{idx}/{total_tables}] Inserting: {full_name} - SKIPPED (no BCP file)")
                    insert_results[full_name] = {"status": "skipped", "error": "No BCP file"}
                    continue

            # Get expected rows from manifest
            expected = get_expected_rows(project_dir, src_db, src_table)
            if expected >= 0:
                log.log(f"[{idx}/{total_tables}] Inserting: {dest_db}..{dest_table} ({expected:,} rows)...")
            else:
                log.log(f"[{idx}/{total_tables}] Inserting: {dest_db}..{dest_table}...")

            # Truncate if needed
            if mode == "TRUNCATE":
                truncate_table(dest_host, dest_port, dest_user, dest_pass,
                              dest_db, dest_table, dest_platform)

            # BCP IN
            dest_table_full = f"{dest_db}..{dest_table}"
            success, output = execute_bcp(
                dest_host, dest_port, dest_user, dest_pass,
                dest_table_full, "in", bcp_file, platform=dest_platform,
                batch_size=bcp_batch_size
            )

            if success:
                rows = int(output) if output.isdigit() else 0
                if expected >= 0:
                    if rows == expected:
                        log.log(f"[{idx}/{total_tables}] Inserted:  {dest_db}..{dest_table}  {rows:,} rows  OK")
                        insert_results[full_name] = {"status": "ok", "rows": rows, "expected": expected}
                    else:
                        log.log(f"[{idx}/{total_tables}] Inserted:  {dest_db}..{dest_table}  {rows:,}/{expected:,} rows  ** MISMATCH **")
                        insert_results[full_name] = {"status": "mismatch", "rows": rows, "expected": expected}
                else:
                    log.log(f"[{idx}/{total_tables}] Inserted:  {dest_db}..{dest_table}  {rows:,} rows  OK (no manifest)")
                    insert_results[full_name] = {"status": "ok", "rows": rows, "expected": -1}
            else:
                log.log(f"[{idx}/{total_tables}] Inserted:  {dest_db}..{dest_table}  FAILED")
                log.log(f"        ERROR: {output}")
                insert_results[full_name] = {"status": "failed", "error": output}

        # Insertion summary
        inserted = sum(1 for r in insert_results.values() if r["status"] == "ok")
        mismatched = sum(1 for r in insert_results.values() if r["status"] == "mismatch")
        failed = sum(1 for r in insert_results.values() if r["status"] == "failed")
        skipped = sum(1 for r in insert_results.values() if r["status"] == "skipped")
        log.log(f"\nInsertion complete: {inserted} OK, {mismatched} mismatched, {failed} failed, {skipped} skipped")

    # Final summary
    log.log(f"\n{'='*50}")
    log.log("TRANSFER COMPLETE")
    log.log(f"{'='*50}")

    total_rows = 0
    if do_insert:
        for r in insert_results.values():
            if r["status"] == "ok":
                total_rows += r.get("rows", 0)
        log.log(f"Total rows inserted: {total_rows:,}")

    log.log(f"BCP files in: {project_dir}")
    log.close()


def delete_project_menu():
    """Delete a project."""
    projects = list_data_transfer_projects()

    if not projects:
        print_warning("No projects to delete.")
        return

    print()
    print_subheader("Delete Project")
    for i, name in enumerate(projects, 1):
        print(f"  {Fore.CYAN}{i}.{Style.RESET_ALL} {name}")
    print(f" {Fore.CYAN}99.{Style.RESET_ALL} Exit")

    choice = input("\nChoose: ").strip()
    if choice == '99' or not choice:
        return

    try:
        idx = int(choice) - 1
        if 0 <= idx < len(projects):
            project_name = projects[idx]
            confirm = input(f"Delete '{project_name}'? [y/N]: ").strip().lower()
            if confirm == 'y':
                if delete_data_transfer_project(project_name):
                    print_success(f"Project '{project_name}' deleted.")
                else:
                    print_error("Failed to delete project.")
    except ValueError:
        print_warning("Invalid selection.")


def run_project_menu():
    """Select and run a project."""
    projects = list_data_transfer_projects()

    if not projects:
        print_warning("No projects found. Create one first.")
        return

    print()
    print_subheader("Select Project")
    for i, name in enumerate(projects, 1):
        project = load_data_transfer_project(name)
        state = project.get("TRANSFER_STATE") if project else None
        status = ""
        if state:
            pending = get_pending_tables(state)
            if pending:
                status = f" {Fore.YELLOW}[INCOMPLETE: {len(pending)} pending]{Style.RESET_ALL}"

        print(f"  {Fore.CYAN}{i}.{Style.RESET_ALL} {Style.BRIGHT}{name}{Style.RESET_ALL}{status}")
    print(f" {Fore.CYAN}99.{Style.RESET_ALL} Exit")

    choice = input("\nChoose: ").strip()
    if choice == '99' or not choice:
        return

    try:
        idx = int(choice) - 1
        if 0 <= idx < len(projects):
            run_project(projects[idx])
    except ValueError:
        print_warning("Invalid selection.")


def main_menu():
    """Main interactive menu."""
    # Show header on first display
    print_header("Data Transfer Utility")
    print()
    print(f"  {style_dim('Transfer data between Sybase ASE and Microsoft SQL Server databases.')}")
    print(f"  {style_dim('Projects store connection settings and table selections for reuse.')}")

    while True:
        # Get project count
        projects = list_data_transfer_projects()
        project_count = len(projects)

        print()
        print(f"{Style.BRIGHT}Main Menu{Style.RESET_ALL} {style_dim(f'({project_count} projects)')}")
        print()
        print(f"  {Fore.CYAN}1.{Style.RESET_ALL} Create new project")
        print(f"  {Fore.CYAN}2.{Style.RESET_ALL} Delete a project")
        print(f"  {Fore.CYAN}3.{Style.RESET_ALL} Run a project")
        print(f"  {Fore.CYAN}4.{Style.RESET_ALL} Open settings.json")
        print(f" {Fore.CYAN}99.{Style.RESET_ALL} Exit")

        choice = input("\nChoose [1-4]: ").strip()

        if choice == '1':
            create_project_wizard()
        elif choice == '2':
            delete_project_menu()
        elif choice == '3':
            run_project_menu()
        elif choice == '4':
            open_settings_in_editor()
        elif choice == '99':
            print()
            print_info("Goodbye!")
            break
        else:
            print_warning("Invalid choice.")


def main():
    """Entry point."""
    # Check FreeTDS version at startup
    success, message, version = check_freetds_version()
    if not success:
        print()
        print_error(message)
        print("FreeTDS with freebcp is required for data transfers.")
        sys.exit(1)

    print()
    print_success(message)

    try:
        main_menu()
    except KeyboardInterrupt:
        print()
        print_info("Interrupted. Goodbye!")
        sys.exit(0)
    except Exception as e:
        print()
        print_error(str(e))
        sys.exit(1)


if __name__ == "__main__":
    main()
