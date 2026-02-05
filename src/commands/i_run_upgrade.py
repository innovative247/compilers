"""
i_run_upgrade.py: Execute database upgrade scripts.

This command:
1. If upgrade number provided or found in filename, checks ba_upgrades_check
2. Executes the SQL script file
3. Updates the upgrade end time when complete (if upgrade number found)

Usage:
    i_run_upgrade database profile script_file [-O output] [-e]
    i_run_upgrade database profile upgrade_no script_file [-O output] [-e]

Options:
    -O, --output   Output file (overwrites)
    -e, --echo     Echo input commands (maps to tsql -v)

Examples:
    i_run_upgrade sbnmaster GONZO test.sql
    i_run_upgrade sbnmaster GONZO test.sql -O upgrade.log
    i_run_upgrade sbnmaster GONZO test.sql -e
    i_run_upgrade sbnmaster GONZO test_07.95.12345_post.sql
    i_run_upgrade sbnmaster GONZO 07.95.12345 sct_07.95.12345_bef.sql

CHG 241208 Simplified to use execute_sql_native directly
CHG 241208 Added -O output file and -e echo flags
"""

import argparse
import sys
import re

from .ibs_common import (
    get_config,
    execute_sql_native,
    find_file,
    Options,
    # Styling utilities
    Icons, Fore, Style,
    print_success, print_error, print_warning, print_info,
)

# Pattern to match upgrade number: xx.yy.zzzzz (2 digits, 2 digits, 5 digits)
UPGRADE_PATTERN = re.compile(r'(\d{2}\.\d{2}\.\d{5})')


def check_upgrade_status(config: dict, options: Options, upgrade_no: str) -> bool:
    """
    Check if the upgrade can be run by calling ba_upgrades_check.

    Returns:
        True if upgrade can proceed, False otherwise
    """
    print(f"Checking upgrade status for {upgrade_no}...")

    dbpro = options.replace_options("&dbpro&")
    sql = f"exec {dbpro}..ba_upgrades_check '{upgrade_no}'"

    success, output = execute_sql_native(
        host=config.get('HOST'),
        port=config.get('PORT'),
        username=config.get('USERNAME'),
        password=config.get('PASSWORD'),
        database=dbpro,
        platform=config.get('PLATFORM', 'SYBASE'),
        sql_content=sql
    )

    if not success:
        print(f"ERROR: Failed to check upgrade status: {output}")
        return False

    # Parse return value from output
    # Format: "return" followed by the value digit
    return_value = _find_upgrade_return_value(output)

    if return_value == "1":
        upgrades_table = options.replace_options("&upgrades&")
        print(f"ERROR: Upgrade control table {upgrades_table} does not exist or control record missing.")
        return False
    elif return_value == "2":
        print(f"Upgrade {upgrade_no} has already been run!")
        return False
    elif return_value == "0" or return_value == "":
        print(f"Upgrade {upgrade_no} is ready to run.")
        return True
    else:
        print(f"ERROR: Unknown status from ba_upgrades_check: {return_value}")
        return False


def _find_upgrade_return_value(output: str) -> str:
    """Extract return value from ba_upgrades_check output."""
    match = re.search(r'return[\s\r\n\-]+(\d)', output, re.IGNORECASE)
    if match:
        return match.group(1)
    return ""


def set_upgrade_end_time(config: dict, options: Options, upgrade_no: str):
    """Update the upgrades table to mark the end time of the script."""
    upgrades_table = options.replace_options("&upgrades&")
    sql = f"update {upgrades_table} set end_tm=datediff(ss,'800101',getdate()) where upgrade_no='{upgrade_no}' and ix=0 and opc=1"

    success, output = execute_sql_native(
        host=config.get('HOST'),
        port=config.get('PORT'),
        username=config.get('USERNAME'),
        password=config.get('PASSWORD'),
        database=config.get('DATABASE', ''),
        platform=config.get('PLATFORM', 'SYBASE'),
        sql_content=sql
    )

    if success:
        print(f"Updated end time for upgrade {upgrade_no}.")
    else:
        print(f"WARNING: Failed to update end time: {output}")


def extract_upgrade_number(script_name: str) -> str:
    """
    Extract upgrade number from script filename if present.

    Returns:
        Upgrade number (xx.yy.zzzzz) if found, empty string otherwise
    """
    match = UPGRADE_PATTERN.search(script_name)
    if match:
        return match.group(1)
    return ""


def write_output(message: str, output_file: str = None):
    """Write message to console and/or output file."""
    if output_file:
        try:
            with open(output_file, 'a', encoding='utf-8') as f:
                f.write(message + '\n')
        except IOError as e:
            print(f"ERROR: Failed to write to output file: {e}")
    else:
        print(message)


def main(args_list=None):
    """Main entry point for i_run_upgrade."""
    # Check for updates (once per day)
    from .version_check import check_for_updates
    if not check_for_updates("i_run_upgrade"):
        sys.exit(0)

    if args_list is None:
        args_list = sys.argv[1:]

    # Parse flags first (-O, -e, etc.)
    output_file = None
    echo_input = False
    positional_args = []

    i = 0
    while i < len(args_list):
        arg = args_list[i]
        if arg in ('-O', '--output'):
            if i + 1 < len(args_list):
                output_file = args_list[i + 1]
                i += 2
            else:
                print("ERROR: -O requires an output file path")
                sys.exit(1)
        elif arg.startswith('-O'):
            # Handle -Ofilename (no space)
            output_file = arg[2:]
            i += 1
        elif arg in ('-e', '--echo'):
            echo_input = True
            i += 1
        elif arg in ('-h', '--help'):
            print("Usage:")
            print("  i_run_upgrade database profile script_file [-O output] [-e]")
            print("  i_run_upgrade database profile upgrade_no script_file [-O output] [-e]")
            print()
            print("Options:")
            print("  -O, --output   Output file (overwrites)")
            print("  -e, --echo     Echo input commands (maps to tsql -v)")
            print()
            print("Examples:")
            print("  i_run_upgrade sbnmaster GONZO test.sql")
            print("  i_run_upgrade sbnmaster GONZO test.sql -O upgrade.log")
            print("  i_run_upgrade sbnmaster GONZO test.sql -e")
            print("  i_run_upgrade sbnmaster GONZO test_07.95.12345_post.sql")
            print("  i_run_upgrade sbnmaster GONZO 07.95.12345 sct_07.95.12345_bef.sql")
            sys.exit(0)
        else:
            positional_args.append(arg)
            i += 1

    # Determine format based on positional argument count
    # 4 args: database profile upgrade_no script_file
    # 3 args: database profile script_file (extracts upgrade_no from filename)
    if len(positional_args) == 4:
        database = positional_args[0]
        profile = positional_args[1]
        upgrade_no = positional_args[2]
        script_file = positional_args[3]
    elif len(positional_args) == 3:
        database = positional_args[0]
        profile = positional_args[1]
        script_file = positional_args[2]
        upgrade_no = extract_upgrade_number(script_file)
    elif len(positional_args) == 0:
        print("Usage:")
        print("  i_run_upgrade database profile script_file [-O output] [-e]")
        print("  i_run_upgrade database profile upgrade_no script_file [-O output] [-e]")
        sys.exit(0)
    else:
        print("ERROR: Invalid arguments. Expected 3 or 4 positional arguments.")
        print("Usage:")
        print("  i_run_upgrade database profile script_file [-O output] [-e]")
        print("  i_run_upgrade database profile upgrade_no script_file [-O output] [-e]")
        sys.exit(1)

    # Initialize output file (overwrite mode)
    if output_file:
        try:
            with open(output_file, 'w', encoding='utf-8') as f:
                from datetime import datetime
                f.write(f"# i_run_upgrade output - {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n")
        except IOError as e:
            print(f"ERROR: Failed to create output file: {e}")
            sys.exit(1)

    # Load configuration (don't prompt to create profile)
    try:
        config = get_config(args_list=[], profile_name=profile, allow_create=False)
    except KeyError:
        print(f"Profile '{profile}' does not exist. Run `set_profile`, then retry this command.")
        sys.exit(1)
    # PROFILE_NAME is already set by get_config (resolves aliases to real profile name)

    # Initialize options
    options = Options(config)
    if not options.generate_option_files():
        print("ERROR: Failed to load options files")
        sys.exit(1)

    # Find the script file first (before any database checks)
    script_path = find_file(script_file, config)
    if not script_path:
        write_output(f"ERROR: Script file not found: {script_file}", output_file)
        sys.exit(1)

    # Check upgrade status if upgrade number found/provided
    if upgrade_no:
        write_output(f"Starting i_run_upgrade for upgrade {upgrade_no}...", output_file)
        if not check_upgrade_status(config, options, upgrade_no):
            sys.exit(0)
    else:
        write_output(f"Running script (no upgrade number detected)...", output_file)

    write_output(f"Running script: {script_path}", output_file)
    with open(script_path, 'r', encoding='utf-8') as f:
        sql_content = f.read()

    # Replace placeholders in the script
    sql_content = options.replace_options(sql_content)

    # Echo input if requested
    if echo_input:
        write_output("-- Executing SQL:", output_file)
        write_output(sql_content, output_file)
        write_output("--", output_file)

    # Execute the script
    success, output = execute_sql_native(
        host=config.get('HOST'),
        port=config.get('PORT'),
        username=config.get('USERNAME'),
        password=config.get('PASSWORD'),
        database=database,
        platform=config.get('PLATFORM', 'SYBASE'),
        sql_content=sql_content,
        echo_input=echo_input
    )

    if not success:
        write_output(f"ERROR: Script execution failed: {output}", output_file)
        sys.exit(1)

    if output and output.strip():
        write_output(output, output_file)

    # Set upgrade end time if upgrade number was found/provided
    if upgrade_no:
        set_upgrade_end_time(config, options, upgrade_no)
        write_output(f"Upgrade {upgrade_no} DONE.", output_file)
    else:
        write_output("Script execution DONE.", output_file)


if __name__ == "__main__":
    main()
