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
from datetime import datetime

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
    # Threading
    TransferThreadPool,
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


def prompt_connection_info(label: str, default_platform: str = "SYBASE") -> dict:
    """
    Prompt user for database connection information.

    Args:
        label: "Source" or "Destination"
        default_platform: Default platform to suggest

    Returns:
        Dict with connection info, or None if cancelled
    """
    print(f"\n--- {label} Connection ---")

    config = {}

    # Platform
    while True:
        platform = input(f"Platform (SYBASE/MSSQL) [{default_platform}]: ").strip().upper()
        if not platform:
            platform = default_platform
        if platform in ("SYBASE", "MSSQL"):
            config["PLATFORM"] = platform
            break
        print("Invalid platform. Enter SYBASE or MSSQL.")

    # Host
    config["HOST"] = input("Host (IP or hostname): ").strip()
    if not config["HOST"]:
        print("Host is required.")
        return None

    # Port
    default_port = 5000 if config["PLATFORM"] == "SYBASE" else 1433
    port_input = input(f"Port [{default_port}]: ").strip()
    config["PORT"] = int(port_input) if port_input else default_port

    # Username
    config["USERNAME"] = input("Username: ").strip()
    if not config["USERNAME"]:
        print("Username is required.")
        return None

    # Password
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
        print(f"  {message}")
        return config
    else:
        print(f"\n  ERROR: {message}")
        retry = input("\nRetry connection? [Y/n]: ").strip().lower()
        if retry != 'n':
            return prompt_connection_info(label, config["PLATFORM"])
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


def prompt_table_selection(config: dict, database: str) -> list:
    """
    Prompt user to select tables with include/exclude patterns.

    Args:
        config: Connection config dict
        database: Database name

    Returns:
        List of selected table names
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
            return []
        return None

    if not all_tables:
        print(f"No tables found in {database}.")
        return []

    print(f"Found {len(all_tables)} tables.")

    # Include patterns
    include_input = input("Tables to include (* for all): ").strip()
    if not include_input:
        include_input = "*"
    include_patterns = parse_pattern_input(include_input)

    # Exclude patterns
    exclude_input = input("Tables to exclude (e.g., w#*, srm_*): ").strip()
    exclude_patterns = parse_pattern_input(exclude_input)

    # Filter tables
    filtered = filter_tables_by_patterns(all_tables, include_patterns, exclude_patterns)

    if not filtered:
        print("No tables matched after filtering.")
        return []

    print(f"\nFound {len(filtered)} tables after filtering.")

    # Interactive review
    selected = set(filtered)  # All selected by default

    while True:
        print(f"\nReview tables for {database}:")
        for i, table in enumerate(filtered, 1):
            marker = "[X]" if table in selected else "[ ]"
            print(f"{marker} {i:3}. {table}")

        print(f"\nSelected: {len(selected)}/{len(filtered)}")
        print("Commands: 1-N toggle, 'all', 'none', 'done'")

        cmd = input("> ").strip().lower()

        if cmd == 'done':
            break
        elif cmd == 'all':
            selected = set(filtered)
        elif cmd == 'none':
            selected = set()
        elif cmd.isdigit():
            idx = int(cmd) - 1
            if 0 <= idx < len(filtered):
                table = filtered[idx]
                if table in selected:
                    selected.remove(table)
                else:
                    selected.add(table)
        else:
            # Try parsing as range or multiple numbers
            try:
                nums = [int(n.strip()) for n in cmd.replace(',', ' ').split()]
                for num in nums:
                    idx = num - 1
                    if 0 <= idx < len(filtered):
                        table = filtered[idx]
                        if table in selected:
                            selected.remove(table)
                        else:
                            selected.add(table)
            except ValueError:
                print("Invalid command. Use numbers, 'all', 'none', or 'done'.")

    # Return in original order
    return [t for t in filtered if t in selected]


def create_project_wizard():
    """
    Interactive wizard to create a new data transfer project.
    """
    print("\n" + "=" * 50)
    print("CREATE NEW DATA TRANSFER PROJECT")
    print("=" * 50)

    # Project name
    project_name = input("\nProject name: ").strip()
    if not project_name:
        print("Project name is required.")
        return

    # Check if exists
    existing = load_data_transfer_project(project_name)
    if existing:
        overwrite = input(f"Project '{project_name}' already exists. Overwrite? [y/N]: ").strip().lower()
        if overwrite != 'y':
            return

    # Source connection
    src_config = prompt_connection_info("Source", "SYBASE")
    if not src_config:
        print("Source connection cancelled.")
        return

    # Destination connection
    dest_config = prompt_connection_info("Destination", "MSSQL")
    if not dest_config:
        print("Destination connection cancelled.")
        return

    # Database selection
    databases = prompt_database_selection(src_config)
    if not databases:
        print("No databases selected.")
        return

    # Table selection per database
    db_configs = {}
    for database in databases:
        tables = prompt_table_selection(src_config, database)
        if tables is None:
            # User chose not to skip, but failed - abort
            print("Table selection failed.")
            return
        if tables:
            db_configs[database] = {
                "DEST_DATABASE": database,  # Same name by default
                "TABLES": tables
            }

    if not db_configs:
        print("No tables selected from any database.")
        return

    # Transfer options
    print("\n--- Transfer Options ---")

    # Mode
    while True:
        mode = input("Mode: [T]runcate or [A]ppend? ").strip().upper()
        if mode in ('T', 'TRUNCATE'):
            mode = 'TRUNCATE'
            break
        elif mode in ('A', 'APPEND'):
            mode = 'APPEND'
            break
        print("Enter T for Truncate or A for Append.")

    # Threads
    threads_input = input("Parallel threads [5]: ").strip()
    threads = int(threads_input) if threads_input.isdigit() else 5
    threads = max(1, min(threads, 20))  # Limit to 1-20

    # Batch size
    batch_input = input("Batch size [1000]: ").strip()
    batch_size = int(batch_input) if batch_input.isdigit() else 1000

    # Build project config
    project = {
        "SOURCE": src_config,
        "DESTINATION": dest_config,
        "DATABASES": db_configs,
        "OPTIONS": {
            "MODE": mode,
            "THREADS": threads,
            "BATCH_SIZE": batch_size
        },
        "TRANSFER_STATE": None
    }

    # Calculate totals
    total_tables = sum(len(db["TABLES"]) for db in db_configs.values())

    # Save project
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

    print(f"\n=== Project: {project_name} ===")
    print(f"Source:      {src.get('PLATFORM')} @ {src.get('HOST')}:{src.get('PORT')}")
    print(f"Destination: {dest.get('PLATFORM')} @ {dest.get('HOST')}:{dest.get('PORT')}")
    print(f"Databases:   {len(dbs)}")
    print(f"Tables:      {total_tables}")
    print(f"Mode:        {options.get('MODE', 'TRUNCATE')}")
    print(f"Threads:     {options.get('THREADS', 5)}")


def run_project(project_name: str):
    """
    Run a data transfer project.
    """
    project = load_data_transfer_project(project_name)
    if not project:
        print(f"Project '{project_name}' not found.")
        return

    display_project_summary(project_name, project)

    src_config = project.get("SOURCE", {})
    dest_config = project.get("DESTINATION", {})
    options = project.get("OPTIONS", {})
    databases = project.get("DATABASES", {})

    mode = options.get("MODE", "TRUNCATE")
    threads = options.get("THREADS", 5)
    batch_size = options.get("BATCH_SIZE", 1000)

    # Build list of tables to transfer
    all_tables = []  # List of (src_db, src_table, dest_db, dest_table)
    for src_db, db_config in databases.items():
        dest_db = db_config.get("DEST_DATABASE", src_db)
        for table in db_config.get("TABLES", []):
            all_tables.append((src_db, table, dest_db, table))

    if not all_tables:
        print("No tables to transfer.")
        return

    # Check for incomplete transfer state
    state = load_transfer_state(project_name)
    resume = False

    if state and get_pending_tables(state):
        completed = get_completed_tables(state)
        pending = get_pending_tables(state)
        print(f"\n*** Previous transfer was interrupted ***")
        print(f"Started:   {state.get('STARTED_AT', 'Unknown')}")
        print(f"Last:      {state.get('LAST_UPDATE', 'Unknown')}")
        print(f"Completed: {len(completed)}/{len(all_tables)} tables")
        print(f"Pending:   {len(pending)} tables")

        choice = input("\nResume from where you left off? [Y/n]: ").strip().lower()
        if choice != 'n':
            resume = True
            # Filter to only pending tables
            pending_set = set(pending)
            all_tables = [t for t in all_tables if f"{t[0]}..{t[1]}" in pending_set]
        else:
            clear_transfer_state(project_name)

    total_tables = len(all_tables)

    # Pre-transfer confirmation
    print(f"\n=== Transfer Summary ===")
    print(f"Tables to transfer: {total_tables}")
    print(f"Mode: {mode}")
    print(f"Threads: {threads}")

    confirm = input(f"\nReady to start transfer? [y/N]: ").strip().lower()
    if confirm != 'y':
        print("Transfer cancelled.")
        return

    # Initialize state
    if not resume:
        state = {
            "STARTED_AT": datetime.now().isoformat(),
            "LAST_UPDATE": datetime.now().isoformat(),
            "TABLES": {}
        }
        for src_db, src_table, dest_db, dest_table in all_tables:
            full_name = f"{src_db}..{src_table}"
            state["TABLES"][full_name] = {"status": "pending"}

        save_transfer_state(project_name, state)

    # FIRST TABLE - Single threaded for review
    if all_tables:
        first_table = all_tables[0]
        src_db, src_table, dest_db, dest_table = first_table
        full_name = f"{src_db}..{src_table}"

        print(f"\n[1/{total_tables}] {full_name}  TRANSFERRING...")

        # Update state
        state["TABLES"][full_name] = {"status": "in_progress"}
        save_transfer_state(project_name, state)

        # Progress callback for single table
        def progress_cb(rows_done, total_rows):
            percent = (rows_done / total_rows * 100) if total_rows > 0 else 0
            bar_width = 20
            filled = int(bar_width * percent / 100)
            bar = '█' * filled + '-' * (bar_width - filled)
            print(f"\r        [{bar}]  {percent:3.0f}%   {rows_done}/{total_rows} rows", end='', flush=True)

        # Transfer first table
        result = transfer_single_table(
            src_config, dest_config,
            src_db, src_table, dest_db, dest_table,
            mode, batch_size, progress_cb
        )

        print()  # Newline after progress

        # Show verification
        print(f"\nVerifying row counts...")
        print(f"  Source:      {result.get('source_rows', 0)}")
        print(f"  Destination: {result.get('dest_rows', 0)}")
        verified = result.get("verified", False)
        print(f"  Status:      {'VERIFIED' if verified else 'MISMATCH'}")

        # Update state
        state["TABLES"][full_name] = {
            "status": result.get("status", "failed"),
            "source_rows": result.get("source_rows", 0),
            "dest_rows": result.get("dest_rows", 0),
            "verified": verified,
            "elapsed": result.get("elapsed", "0s"),
            "error": result.get("error")
        }
        state["LAST_UPDATE"] = datetime.now().isoformat()
        save_transfer_state(project_name, state)

        # Handle mismatch
        if not verified and result.get("status") == "mismatch":
            print(f"\nWARNING: Row count mismatch!")
            if result.get("error"):
                print(f"  {result['error']}")
            choice = input("\n[R]etry, [S]kip, [A]bort? ").strip().upper()
            if choice == 'A':
                print("Transfer aborted.")
                return
            elif choice == 'R':
                # For simplicity, we'll just continue and let user re-run
                print("Please re-run the project to retry.")
                return

        # First table review
        print(f"\n--- First Table Complete ---")
        print(f"Table:    {full_name}")
        print(f"Rows:     {result.get('rows_transferred', 0)}")
        print(f"Time:     {result.get('elapsed', '0s')}")
        print(f"Verified: {'YES' if verified else 'NO'}")

        if len(all_tables) > 1:
            remaining = len(all_tables) - 1
            continue_choice = input(f"\nContinue with remaining {remaining} tables ({threads} parallel)? [y/N]: ").strip().lower()
            if continue_choice != 'y':
                print("Transfer paused. Run again to resume.")
                return

            # Remove first table from list
            all_tables = all_tables[1:]

    # PARALLEL TRANSFER for remaining tables
    if all_tables:
        print(f"\n[Now running {threads} tables in parallel...]")
        print("Press Ctrl+C to stop after current tables complete.\n")

        # State callback to save progress after each table
        def state_callback(table_name, result):
            state["TABLES"][table_name] = {
                "status": result.get("status", "failed"),
                "source_rows": result.get("source_rows", 0),
                "dest_rows": result.get("dest_rows", 0),
                "verified": result.get("verified", False),
                "elapsed": result.get("elapsed", "0s"),
                "error": result.get("error")
            }
            state["LAST_UPDATE"] = datetime.now().isoformat()
            save_transfer_state(project_name, state)

        # Create thread pool
        pool = TransferThreadPool(
            src_config, dest_config,
            mode, threads, batch_size
        )

        try:
            pool.start(all_tables)
            results = pool.wait_for_completion(state_callback)
        except KeyboardInterrupt:
            print("\n\nStopping after current tables...")
            pool.stop()
            pool.join()
            print("Transfer paused. Run again to resume.")
            return
        finally:
            pool.join()

    # Final summary
    print("\n" + "=" * 50)
    print("TRANSFER COMPLETE")
    print("=" * 50)

    # Count results
    completed = 0
    verified = 0
    mismatches = 0
    failed = 0
    skipped = 0
    total_rows = 0

    for table_name, table_state in state.get("TABLES", {}).items():
        status = table_state.get("status", "pending")
        if status == "completed":
            completed += 1
            if table_state.get("verified"):
                verified += 1
            total_rows += table_state.get("dest_rows", 0)
        elif status == "mismatch":
            mismatches += 1
            completed += 1
        elif status == "failed":
            failed += 1
        elif status == "skipped":
            skipped += 1

    print(f"\nTotal tables:  {len(state.get('TABLES', {}))}")
    print(f"Completed:     {completed}")
    print(f"Verified:      {verified}")
    print(f"Mismatches:    {mismatches}")
    print(f"Skipped:       {skipped}")
    print(f"Failed:        {failed}")
    print(f"Total rows:    {total_rows:,}")

    # Show skipped tables if any
    if skipped > 0:
        print("\nSkipped tables (missing or schema mismatch):")
        for table_name, table_state in state.get("TABLES", {}).items():
            if table_state.get("status") == "skipped":
                print(f"  - {table_name}: {table_state.get('error', 'Unknown error')}")

    # Show mismatches if any
    if mismatches > 0:
        print("\nMismatched tables (row count verification failed):")
        for table_name, table_state in state.get("TABLES", {}).items():
            if table_state.get("status") == "mismatch":
                print(f"  - {table_name}: {table_state.get('error', 'Unknown error')}")


def list_projects_menu():
    """Display list of all projects."""
    projects = list_data_transfer_projects()

    if not projects:
        print("\nNo data transfer projects found.")
        return

    print("\n=== Data Transfer Projects ===\n")

    for name in projects:
        project = load_data_transfer_project(name)
        if project:
            src = project.get("SOURCE", {})
            dest = project.get("DESTINATION", {})
            dbs = project.get("DATABASES", {})
            total_tables = sum(len(db.get("TABLES", [])) for db in dbs.values())

            state = project.get("TRANSFER_STATE")
            status = ""
            if state:
                completed = len(get_completed_tables(state))
                total = len(state.get("TABLES", {}))
                if completed < total:
                    status = f" [INCOMPLETE: {completed}/{total}]"
                else:
                    status = " [DONE]"

            print(f"  {name}")
            print(f"    {src.get('PLATFORM')} -> {dest.get('PLATFORM')} | "
                  f"{len(dbs)} databases, {total_tables} tables{status}")
        print()


def delete_project_menu():
    """Delete a project."""
    projects = list_data_transfer_projects()

    if not projects:
        print("\nNo projects to delete.")
        return

    print("\nSelect project to delete:")
    for i, name in enumerate(projects, 1):
        print(f"  {i}. {name}")

    choice = input("\nChoose (or 0 to cancel): ").strip()
    if choice == '0' or not choice:
        return

    try:
        idx = int(choice) - 1
        if 0 <= idx < len(projects):
            project_name = projects[idx]
            confirm = input(f"Delete '{project_name}'? [y/N]: ").strip().lower()
            if confirm == 'y':
                if delete_data_transfer_project(project_name):
                    print(f"Project '{project_name}' deleted.")
                else:
                    print("Failed to delete project.")
    except ValueError:
        print("Invalid selection.")


def run_project_menu():
    """Select and run a project."""
    projects = list_data_transfer_projects()

    if not projects:
        print("\nNo projects found. Create one first.")
        return

    print("\nSelect project to run:")
    for i, name in enumerate(projects, 1):
        project = load_data_transfer_project(name)
        state = project.get("TRANSFER_STATE") if project else None
        status = ""
        if state:
            pending = get_pending_tables(state)
            if pending:
                status = f" [INCOMPLETE: {len(pending)} pending]"

        print(f"  {i}. {name}{status}")

    choice = input("\nChoose (or 0 to cancel): ").strip()
    if choice == '0' or not choice:
        return

    try:
        idx = int(choice) - 1
        if 0 <= idx < len(projects):
            run_project(projects[idx])
    except ValueError:
        print("Invalid selection.")


def main_menu():
    """Main interactive menu."""
    while True:
        print("\n" + "=" * 40)
        print("DATA TRANSFER UTILITY")
        print("=" * 40)
        print("\n  1. Create new project")
        print("  2. Edit project (coming soon)")
        print("  3. Delete project")
        print("  4. Run project")
        print("  5. List projects")
        print("  6. Exit")

        choice = input("\nChoose [1-6]: ").strip()

        if choice == '1':
            create_project_wizard()
        elif choice == '2':
            print("\nEdit functionality coming soon. Delete and recreate for now.")
        elif choice == '3':
            delete_project_menu()
        elif choice == '4':
            run_project_menu()
        elif choice == '5':
            list_projects_menu()
        elif choice == '6':
            print("\nGoodbye!")
            break
        else:
            print("Invalid choice. Enter 1-6.")


def main():
    """Entry point."""
    try:
        main_menu()
    except KeyboardInterrupt:
        print("\n\nInterrupted. Goodbye!")
        sys.exit(0)
    except Exception as e:
        print(f"\nERROR: {e}")
        sys.exit(1)


if __name__ == "__main__":
    main()
