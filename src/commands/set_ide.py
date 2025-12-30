"""
set_ide.py: Generate VSCode configuration for IBS Compilers

This script creates .vscode/tasks.json in the profile's SQL_SOURCE directory,
enabling one-hotkey SQL compilation from within VSCode.

What this does:
    - Creates a .vscode/tasks.json file in your SQL source directory
    - Configures VSCode to run 'runsql' on the current file when you press Ctrl+Shift+B
    - Sets up error parsing so SQL errors appear in VSCode's Problems panel
    - Allows clicking on errors to jump directly to the problematic line

Usage:
    set_ide                           # Interactive wizard (recommended for first-time setup)
    set_ide PROFILE_NAME              # Use profile directly, prompt for databases
    set_ide PROFILE_NAME -d db1,db2   # Fully non-interactive (for scripting)
    set_ide PROFILE_NAME -d db1 -f    # Force overwrite existing config

Prerequisites:
    1. Run 'set_profile' first to create a database connection profile
    2. Ensure 'runsql' is in your PATH (run 'pip install -e .' from compilers/src)
    3. Have VSCode installed

After running set_ide:
    1. Open the SQL_SOURCE folder in VSCode (File > Open Folder)
    2. Open any .sql file
    3. Press Ctrl+Shift+B to compile to the default database
    4. Or press Ctrl+Shift+P, type "Run Task", and pick a specific database
"""

import sys
import os
import json
import argparse
from pathlib import Path

# Import from ibs_common for profile management and styling
from .ibs_common import (
    find_settings_file,
    load_settings as ibs_load_settings,
    find_profile_by_name_or_alias,
    # Styling utilities
    Icons, Fore, Style,
    print_header, print_subheader, print_step,
    print_success, print_error, print_warning, print_info,
    style_path, style_database, style_command, style_dim,
)


# =============================================================================
# CONSTANTS
# =============================================================================

DEFAULT_DATABASES = ["sbnmaster", "sbnpro", "sbnwork", "sbnstatic", "ibsmaster"]

# Problem matcher for runsql error format:
# Msg 207 (severity 16, state 4) from SERVER Line 1:
#     "Invalid column name 'foo'."
PROBLEM_MATCHER = {
    "owner": "runsql",
    "fileLocation": ["absolute"],
    "pattern": [
        {
            "regexp": r"^Msg\s+(\d+)\s+\(severity\s+(\d+),\s+state\s+\d+\).*Line\s+(\d+):",
            "line": 3,
            "code": 1
        },
        {
            "regexp": r"^\s*\"?(.+?)\"?\s*$",
            "message": 1,
            "loop": True
        }
    ]
}


# =============================================================================
# HELPER FUNCTIONS
# =============================================================================

def load_settings():
    """Load settings from settings.json."""
    settings_path = find_settings_file()
    if not settings_path:
        return None, None

    try:
        with open(settings_path, 'r') as f:
            return json.load(f), settings_path
    except (json.JSONDecodeError, IOError) as e:
        print_error(f"Failed to load settings.json: {e}")
        return None, settings_path


# =============================================================================
# PROFILE SELECTION
# =============================================================================

def display_profiles_for_selection(settings):
    """Display profiles with numbers for selection."""
    profiles = settings.get("Profiles", {})

    if not profiles:
        print_warning("No profiles configured.")
        print()
        print(f"  Run {style_command('set_profile')} to create a database connection profile first.")
        print("  A profile stores your database server, credentials, and SQL source path.")
        return []

    print_step(1, "Select a profile")
    print()
    print(f"  {style_dim('A profile contains your database connection settings and SQL source path.')}")
    print(f"  {style_dim('The SQL_SOURCE is where the .vscode folder will be created.')}")
    print()

    profile_list = []
    for i, (name, profile) in enumerate(profiles.items(), 1):
        aliases = profile.get("ALIASES", [])
        sql_source = profile.get("SQL_SOURCE", "(not set)")

        if aliases:
            aliases_joined = ', '.join(aliases)
            aliases_str = f" {style_dim(f'(aliases: {aliases_joined})')}"
        else:
            aliases_str = ""

        print(f"  {Fore.CYAN}{i}.{Style.RESET_ALL} {Style.BRIGHT}{name}{Style.RESET_ALL}{aliases_str}")
        print(f"     {Icons.FOLDER} {Fore.LIGHTBLUE_EX}{sql_source}{Style.RESET_ALL}")
        profile_list.append((name, profile))

    print()
    return profile_list


def select_profile(settings, profile_list):
    """Prompt user to select a profile by number or name."""
    while True:
        choice = input("Select profile [1-N or name]: ").strip()

        if not choice:
            print("Please enter a profile number or name.")
            continue

        # Try as number first
        if choice.isdigit():
            idx = int(choice) - 1
            if 0 <= idx < len(profile_list):
                return profile_list[idx]
            else:
                print(f"Invalid number. Enter 1-{len(profile_list)}.")
                continue

        # Try as name or alias
        profile_name, profile_data = find_profile_by_name_or_alias(settings, choice.upper())
        if profile_name:
            return (profile_name, profile_data)

        print(f"Profile '{choice}' not found.")


# =============================================================================
# DATABASE INPUT
# =============================================================================

def prompt_for_databases():
    """Prompt user for comma-separated database list."""
    default_str = ",".join(DEFAULT_DATABASES)

    print()
    print_step(2, "Choose target databases")
    print()
    print(f"  {style_dim('Each database becomes a separate build task in VSCode.')}")
    print(f"  {style_dim('The FIRST database will be the default (Ctrl+Shift+B).')}")
    print(f"  {style_dim('Other databases can be selected via: Ctrl+Shift+P > Run Task')}")
    print()
    print(f"  {Style.BRIGHT}Common databases:{Style.RESET_ALL}")
    print(f"    {Icons.DATABASE} {style_database('sbnmaster')}  - Main tables (users, addresses, etc.)")
    print(f"    {Icons.DATABASE} {style_database('sbnpro')}     - Stored procedures")
    print(f"    {Icons.DATABASE} {style_database('sbnwork')}    - Work/temporary tables")
    print(f"    {Icons.DATABASE} {style_database('sbnstatic')}  - Static reference data")
    print(f"    {Icons.DATABASE} {style_database('ibsmaster')}  - IBS framework tables")
    print()
    print(f"Enter databases {style_dim('(comma-separated)')}")
    user_input = input(f"[{default_str}]: ").strip()

    if not user_input:
        return DEFAULT_DATABASES

    # Parse comma or space separated
    databases = [db.strip() for db in user_input.replace(",", " ").split()]
    databases = [db for db in databases if db]  # Remove empties

    if not databases:
        print("No databases entered, using defaults.")
        return DEFAULT_DATABASES

    return databases


# =============================================================================
# TASKS.JSON GENERATION
# =============================================================================

def generate_task(database, profile_name, is_default=False):
    """Generate a single task entry for tasks.json."""
    task = {
        "label": f"runsql ({database})",
        "type": "shell",
        "command": "runsql",
        "args": [
            "${file}",
            database,
            profile_name
        ],
        "presentation": {
            "reveal": "always",
            "panel": "shared",
            "clear": True
        },
        "problemMatcher": PROBLEM_MATCHER
    }

    if is_default:
        task["group"] = {
            "kind": "build",
            "isDefault": True
        }
    else:
        task["group"] = "build"

    return task


def generate_tasks_json(databases, profile_name):
    """Generate complete tasks.json content."""
    tasks = []

    for i, database in enumerate(databases):
        is_default = (i == 0)  # First database is the default
        task = generate_task(database, profile_name, is_default)
        tasks.append(task)

    return {
        "version": "2.0.0",
        "tasks": tasks
    }


# =============================================================================
# FILE WRITING
# =============================================================================

def handle_existing_tasks(vscode_dir, tasks_content, force=False):
    """Handle existing tasks.json - offer overwrite/merge/cancel."""
    tasks_path = vscode_dir / "tasks.json"

    if not tasks_path.exists():
        return tasks_content, True  # No existing file, proceed

    if force:
        return tasks_content, True  # Force overwrite

    print()
    print_warning(f".vscode/tasks.json already exists.")
    print()
    print(f"  {Style.BRIGHT}Options:{Style.RESET_ALL}")
    print(f"    {Fore.CYAN}[O]verwrite{Style.RESET_ALL} - Replace the entire file with new runsql tasks")
    print(f"    {Fore.CYAN}[M]erge{Style.RESET_ALL}     - Keep existing non-runsql tasks, replace runsql tasks")
    print(f"    {Fore.CYAN}[C]ancel{Style.RESET_ALL}    - Abort without making changes")
    print()

    while True:
        choice = input("  Choose [O/M/C]: ").strip().upper()

        if choice in ('O', 'OVERWRITE'):
            return tasks_content, True

        elif choice in ('M', 'MERGE'):
            # Load existing and merge
            try:
                with open(tasks_path, 'r') as f:
                    existing = json.load(f)
            except (json.JSONDecodeError, IOError) as e:
                print(f"  Cannot read existing file: {e}")
                print("  Using overwrite instead.")
                return tasks_content, True

            # Remove existing runsql tasks, keep others
            existing_tasks = existing.get("tasks", [])
            non_runsql_tasks = [t for t in existing_tasks
                               if not t.get("label", "").startswith("runsql (")]

            # Combine: keep non-runsql tasks, add new runsql tasks
            merged_tasks = non_runsql_tasks + tasks_content["tasks"]
            tasks_content["tasks"] = merged_tasks

            print(f"  Keeping {len(non_runsql_tasks)} existing non-runsql tasks.")
            return tasks_content, True

        elif choice in ('C', 'CANCEL'):
            return None, False

        else:
            print("  Please enter O, M, or C.")


def write_vscode_files(sql_source, tasks_content, force=False):
    """Write .vscode/tasks.json to the SQL_SOURCE directory."""
    vscode_dir = Path(sql_source) / ".vscode"

    # Handle existing file
    tasks_content, proceed = handle_existing_tasks(vscode_dir, tasks_content, force=force)
    if not proceed:
        print("\nCancelled.")
        return False

    # Create .vscode directory if needed
    try:
        vscode_dir.mkdir(parents=True, exist_ok=True)
    except OSError as e:
        print_error(f"Failed to create .vscode directory: {e}")
        return False

    # Write tasks.json
    tasks_path = vscode_dir / "tasks.json"
    try:
        with open(tasks_path, 'w') as f:
            json.dump(tasks_content, f, indent=4)
    except IOError as e:
        print_error(f"Failed to write tasks.json: {e}")
        return False

    return True


# =============================================================================
# MAIN WIZARD
# =============================================================================

def print_intro():
    """Print introduction explaining what set_ide does."""
    print()
    print(f"This wizard configures VSCode to compile SQL files with a single hotkey.")
    print()
    print(f"{Fore.CYAN}{Style.BRIGHT}WHAT YOU'LL GET:{Style.RESET_ALL}")
    print(f"  {Icons.SUCCESS} Press {Style.BRIGHT}Ctrl+Shift+B{Style.RESET_ALL} in VSCode to compile the current SQL file")
    print(f"  {Icons.SUCCESS} SQL errors appear in VSCode's Problems panel (click to jump to line)")
    print(f"  {Icons.SUCCESS} Multiple database targets available via the Task picker")
    print()
    print(f"{Fore.CYAN}{Style.BRIGHT}HOW IT WORKS:{Style.RESET_ALL}")
    print(f"  {Icons.GEAR} Creates a .vscode/tasks.json file in your SQL source directory")
    print(f"  {Icons.GEAR} Each database (sbnmaster, sbnpro, etc.) becomes a separate build task")
    print(f"  {Icons.GEAR} The first database is the default (triggered by Ctrl+Shift+B)")
    print()


def run_wizard():
    """Run the interactive wizard."""
    print_header("Innovative247 Compiler VSCode Integration")

    # Show introduction
    print_intro()

    # Load settings
    settings, settings_path = load_settings()
    if settings is None:
        print_error("Could not load settings.json. Run 'set_profile' first.")
        return 1

    # Display and select profile
    profile_list = display_profiles_for_selection(settings)
    if not profile_list:
        return 1

    profile_name, profile_data = select_profile(settings, profile_list)

    # Validate SQL_SOURCE
    sql_source = profile_data.get("SQL_SOURCE")
    if not sql_source:
        print_error(f"Profile '{profile_name}' has no SQL_SOURCE configured.")
        print("  Run 'set_profile' and edit the profile to add SQL_SOURCE.")
        return 1

    print(f"\n{Icons.SUCCESS} Selected: {Style.BRIGHT}{profile_name}{Style.RESET_ALL}")
    print(f"  {Icons.FOLDER} SQL_SOURCE: {Fore.LIGHTBLUE_EX}{sql_source}{Style.RESET_ALL}")

    # Check if SQL_SOURCE exists
    if not os.path.isdir(sql_source):
        print()
        print_warning(f"SQL_SOURCE directory does not exist: {sql_source}")
        proceed = input("Continue anyway? [y/N]: ").strip().lower()
        if proceed not in ('y', 'yes'):
            return 1

    # Get databases
    print()
    databases = prompt_for_databases()

    # Generate tasks.json
    tasks_content = generate_tasks_json(databases, profile_name)

    # Show target
    target_path = sql_source + "\\.vscode\\"
    print(f"\n{Icons.FOLDER} Target: {Fore.LIGHTBLUE_EX}{target_path}{Style.RESET_ALL}")

    # Write files
    if not write_vscode_files(sql_source, tasks_content):
        return 1

    # Success message
    print()
    print_success(f"Generated tasks.json with {len(databases)} tasks:")
    for i, db in enumerate(databases):
        if i == 0:
            print(f"  {Icons.ARROW} runsql ({style_database(db)}) {Fore.GREEN}[default - Ctrl+Shift+B]{Style.RESET_ALL}")
        else:
            print(f"  {Icons.ARROW} runsql ({style_database(db)})")

    # Comprehensive next steps
    print()
    print_header("NEXT STEPS")
    print()
    print(f"  {Fore.CYAN}{Style.BRIGHT}1. OPEN THE FOLDER IN VSCODE:{Style.RESET_ALL}")
    print(f"     {Icons.ARROW} Open VSCode")
    print(f"     {Icons.ARROW} File > Open Folder > {style_path(sql_source)}")
    code_cmd = f'code "{sql_source}"'
    print(f"     {Icons.ARROW} Or run: {style_command(code_cmd)}")
    print()
    print(f"  {Fore.CYAN}{Style.BRIGHT}2. COMPILE A SQL FILE:{Style.RESET_ALL}")
    print(f"     {Icons.ARROW} Open any .sql file (e.g., pro_users_get.sql)")
    print(f"     {Icons.ARROW} Press {Style.BRIGHT}Ctrl+Shift+B{Style.RESET_ALL} to compile to the default database")
    print(f"     {Icons.ARROW} Output appears in the Terminal panel at the bottom")
    print()
    print(f"  {Fore.CYAN}{Style.BRIGHT}3. COMPILE TO A DIFFERENT DATABASE:{Style.RESET_ALL}")
    print(f"     {Icons.ARROW} Press {Style.BRIGHT}Ctrl+Shift+P{Style.RESET_ALL} to open Command Palette")
    print(f"     {Icons.ARROW} Type 'Run Task' and press Enter")
    print(f"     {Icons.ARROW} Select: runsql ({style_database('sbnpro')}), runsql ({style_database('sbnwork')}), etc.")
    print()
    print(f"  {Fore.CYAN}{Style.BRIGHT}4. VIEW AND NAVIGATE ERRORS:{Style.RESET_ALL}")
    print(f"     {Icons.ARROW} If compilation fails, errors appear in the {Style.BRIGHT}PROBLEMS{Style.RESET_ALL} panel")
    print(f"     {Icons.ARROW} Click an error to jump directly to that line in your SQL file")
    print(f"     {Icons.ARROW} View > Problems (or {Style.BRIGHT}Ctrl+Shift+M{Style.RESET_ALL}) to show the panel")
    print()
    print_subheader("TROUBLESHOOTING")
    print()
    print(f"  {Fore.YELLOW}'runsql' is not recognized:{Style.RESET_ALL}")
    print(f"     {Icons.ARROW} Ensure you ran {style_command('pip install -e .')} from the compilers/src folder")
    print(f"     {Icons.ARROW} Restart VSCode after installing to refresh PATH")
    print()
    print(f"  {Fore.YELLOW}Ctrl+Shift+B does nothing:{Style.RESET_ALL}")
    print(f"     {Icons.ARROW} Make sure you opened the SQL_SOURCE folder, not a parent folder")
    print(f"     {Icons.ARROW} The .vscode folder must be in the root of the opened folder")
    print()
    print(f"  {Fore.YELLOW}Errors don't appear in Problems panel:{Style.RESET_ALL}")
    print(f"     {Icons.ARROW} Check View > Problems ({Style.BRIGHT}Ctrl+Shift+M{Style.RESET_ALL}) is visible")
    print(f"     {Icons.ARROW} The problem matcher parses 'Msg N (severity N) ... Line N:' format")
    print()

    return 0


def run_noninteractive(profile_name, databases, force=False):
    """Run in non-interactive mode with provided arguments."""
    # Load settings
    settings, _ = load_settings()
    if settings is None:
        print_error("Could not load settings.json")
        return 1

    # Find profile
    found_name, profile_data = find_profile_by_name_or_alias(settings, profile_name.upper())
    if not found_name:
        print_error(f"Profile '{profile_name}' not found")
        return 1

    # Validate SQL_SOURCE
    sql_source = profile_data.get("SQL_SOURCE")
    if not sql_source:
        print_error(f"Profile '{found_name}' has no SQL_SOURCE configured")
        return 1

    # Use provided databases or defaults
    if not databases:
        databases = DEFAULT_DATABASES

    # Generate and write
    tasks_content = generate_tasks_json(databases, found_name)

    if not write_vscode_files(sql_source, tasks_content, force=force):
        return 1

    vscode_path = sql_source + "\\.vscode\\"
    print_success(f"Generated tasks.json in {Fore.LIGHTBLUE_EX}{vscode_path}{Style.RESET_ALL}")
    for i, db in enumerate(databases):
        if i == 0:
            print(f"  {Icons.ARROW} runsql ({style_database(db)}) {Fore.GREEN}[default - Ctrl+Shift+B]{Style.RESET_ALL}")
        else:
            print(f"  {Icons.ARROW} runsql ({style_database(db)})")

    print()
    print_info(f"Next: Open the folder in VSCode and press {Style.BRIGHT}Ctrl+Shift+B{Style.RESET_ALL} to compile.")
    code_cmd = f'code "{sql_source}"'
    print(f"      {style_command(code_cmd)}")

    return 0


# =============================================================================
# ENTRY POINT
# =============================================================================

def main():
    """Main entry point."""
    parser = argparse.ArgumentParser(
        description="Generate VSCode configuration for IBS Compilers",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
WHAT THIS DOES:
  Creates a .vscode/tasks.json file that enables:
  - Ctrl+Shift+B to compile the current SQL file
  - SQL errors appear in VSCode's Problems panel
  - Click errors to jump directly to that line

EXAMPLES:
  set_ide                             Interactive wizard (recommended)
  set_ide GONZO                       Use GONZO profile, prompt for databases
  set_ide GONZO -d sbnpro             Compile to sbnpro only
  set_ide GONZO -d sbnmaster,sbnpro   Multiple databases
  set_ide GONZO -f                    Overwrite existing config

AFTER RUNNING:
  1. Open the SQL_SOURCE folder in VSCode (File > Open Folder)
  2. Open any .sql file
  3. Press Ctrl+Shift+B to compile

PREREQUISITES:
  - Run 'set_profile' first to create a database profile
  - Ensure 'runsql' is in PATH (pip install -e . from compilers/src)
"""
    )
    parser.add_argument(
        "profile",
        nargs="?",
        metavar="PROFILE",
        help="Profile name or alias (runs interactive wizard if omitted)"
    )
    parser.add_argument(
        "-d", "--databases",
        metavar="DB1,DB2",
        help="Comma-separated databases (default: sbnmaster,sbnpro,sbnwork,sbnstatic,ibsmaster)"
    )
    parser.add_argument(
        "-f", "--force",
        action="store_true",
        help="Overwrite existing tasks.json without prompting"
    )

    args = parser.parse_args()

    try:
        if args.profile:
            # Non-interactive mode
            databases = None
            if args.databases:
                databases = [db.strip() for db in args.databases.split(",")]
            return run_noninteractive(args.profile, databases, force=args.force)
        else:
            # Interactive wizard
            return run_wizard()

    except KeyboardInterrupt:
        print("\n\nCancelled.")
        return 130
    except Exception as e:
        print_error(f"Unexpected error: {e}")
        import traceback
        traceback.print_exc()
        return 1


if __name__ == "__main__":
    sys.exit(main())
