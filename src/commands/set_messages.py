"""
set_messages.py: Compile and install messages into the database. (Also available as: compile_msg, install_msg)

This command compiles the message flat files into the database work tables,
then executes stored procedures to move data to final tables.

MODES:
    1. Import - Push messages from files into the database (preserving translations)
    2. Export - Export messages from database to files

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

IMPORT PROCESS:
    1. Validate all source files exist
    2. Check for saved translations from a prior failed compile (prompt user)
    3. Preserve translated messages (unless already saved)
    4. Truncate work tables
    5. Load all rows via BCP (freebcp)
    6. Execute compile stored procedures (translations restored at the end)

EXPORT PROCESS:
    Export messages from the database to flat files. GONZO (or G) is the
    canonical source of messages. When importing to GONZO, export must be
    done first to preserve any new messages.

USAGE:
    compile_msg PROFILE

ARGUMENTS:
    PROFILE     Configuration profile name (e.g., GONZO, PROD)

EXAMPLES:
    compile_msg GONZO
    compile_msg PROD
"""

import argparse
import sys
import os

from .ibs_common import (
    get_config,
    get_messages_path,
    compile_messages,
    export_messages,
    # Styling utilities
    Icons, Fore, Style,
    print_success, print_error, print_warning, print_info,
)


def prompt_mode():
    """
    Prompt user to select import or export mode.

    Returns:
        str: 'import' or 'export'
    """
    print()
    print("Select operation:")
    print("  1) Import messages from files into the database")
    print("  2) Export messages from the database into the files")
    print()

    while True:
        choice = input("Enter choice (1 or 2): ").strip()
        if choice == '1':
            return 'import'
        elif choice == '2':
            return 'export'
        else:
            print("Invalid choice. Please enter 1 or 2.")


def is_gonzo_profile(profile_name: str) -> bool:
    """
    Check if the profile is GONZO (the canonical message source).

    GONZO can be specified as 'GONZO', 'G', or 'gonzo' (case insensitive).

    Args:
        profile_name: Profile name to check

    Returns:
        True if this is the GONZO profile
    """
    return profile_name.upper() in ('GONZO', 'G')


def main(args_list=None):
    """
    Main entry point for the compile_msg command.

    Workflow:
        1. Prompt user for import/export mode
        2. Load configuration for the specified profile
        3. For GONZO import: require export first to preserve new messages
        4. For import: validate source files and call compile_messages()
        5. For export: call export_messages()

    Args:
        args_list: Command line arguments (defaults to sys.argv[1:])
    """
    # Check for updates (once per day)
    from .version_check import check_for_updates
    if not check_for_updates("compile_msg"):
        sys.exit(0)

    if args_list is None:
        args_list = sys.argv[1:]

    parser = argparse.ArgumentParser(
        description="Compile and install messages into the database.",
        usage="compile_msg PROFILE"
    )
    parser.add_argument("profile", help="Configuration profile (required)")

    args = parser.parse_args(args_list)

    # Load config from profile
    # PROFILE_NAME is set by get_config (resolves aliases to real profile name)
    config = get_config(profile_name=args.profile)

    # Check if this is GONZO profile (use resolved name from config)
    is_gonzo = is_gonzo_profile(config['PROFILE_NAME'])

    # Prompt for operation mode
    mode = prompt_mode()

    if mode == 'export':
        # Export mode: pull messages from database to files

        # Warn if exporting from non-GONZO server
        if not is_gonzo:
            print()
            print("WARNING: Exporting from this server will override local message files.")
            choice = input("Are you sure? Y/n: ").strip().lower()
            if choice not in ('y', 'yes', ''):
                print("Cancelled.")
                sys.exit(0)
            print()

        print("Exporting messages from database...")
        success, message = export_messages(config)

        if success:
            print(message)
            print("compile_msg DONE.")
        else:
            print(f"ERROR: {message}")
            sys.exit(1)
    else:
        # Import mode: push messages from files into database

        # GONZO protection: require export before import
        if is_gonzo:
            print()
            print("=" * 60)
            print("WARNING: You are importing to GONZO (canonical message source)")
            print("=" * 60)
            print()
            print("GONZO is the master source for messages. Before importing,")
            print("you should export first to capture any new messages that")
            print("may have been added directly to the database.")
            print()
            print("Options:")
            print("  1) Export first, then import (recommended)")
            print("  2) Import only (skip export)")
            print("  3) Cancel")
            print()

            while True:
                choice = input("Enter choice (1, 2, or 3): ").strip()
                if choice == '1':
                    # Export first
                    print()
                    print("Exporting messages from GONZO...")
                    success, message = export_messages(config)
                    if not success:
                        print(f"ERROR: Export failed: {message}")
                        sys.exit(1)
                    print(message)
                    print()
                    break
                elif choice == '2':
                    # Skip export, proceed to import
                    print("Skipping export, proceeding to import...")
                    break
                elif choice == '3':
                    print("Cancelled.")
                    sys.exit(0)
                else:
                    print("Invalid choice. Please enter 1, 2, or 3.")

        # Quick check that at least one source file exists
        test_file = get_messages_path(config, '.ibs_msg')
        if not os.path.exists(test_file):
            print(f"ERROR: Message files not found at expected location.")
            print(f"Expected: {os.path.dirname(test_file)}")
            sys.exit(1)

        # Compile: parse source files and insert into database
        print("Compiling messages...")
        success, message, count = compile_messages(config)

        if success:
            print(message)
            print("compile_msg DONE.")
        else:
            print(f"ERROR: {message}")
            sys.exit(1)


def extract_main(args_list=None):
    """
    Entry point for extract_msg command.

    Bypasses prompts and runs Export then Import automatically.
    This is useful for scripted/automated message extraction and compilation.

    Args:
        args_list: Command line arguments (defaults to sys.argv[1:])
    """
    if args_list is None:
        args_list = sys.argv[1:]

    parser = argparse.ArgumentParser(
        description="Extract and compile messages (export then import, no prompts).",
        usage="extract_msg PROFILE"
    )
    parser.add_argument("profile", help="Configuration profile (required)")

    args = parser.parse_args(args_list)

    # Load config from profile
    config = get_config(profile_name=args.profile)

    # Step 1: Export messages from database to files
    print("Step 1: Exporting messages from database...")
    success, message = export_messages(config)
    if not success:
        print(f"ERROR: Export failed: {message}")
        sys.exit(1)
    print(message)
    print()

    # Step 2: Import messages from files into database
    print("Step 2: Compiling messages...")
    success, message, count = compile_messages(config)
    if not success:
        print(f"ERROR: Import failed: {message}")
        sys.exit(1)
    print(message)
    print("extract_msg DONE.")


if __name__ == "__main__":
    main()
