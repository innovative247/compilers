"""
compile_msg.py: Compile and install messages into the database.

This command compiles the message flat files into the database work tables,
then executes stored procedures to move data to final tables.

MODES:
    1. Import - Push messages from files into the database (preserving translations)
    2. Export - Export messages from database to files (not yet implemented)

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
    2. Preserve translated messages (ba_compile_gui_messages_save)
    3. Truncate work tables
    4. Insert all rows via SQL INSERT
    5. Execute compile stored procedures:
       - i_compile_messages
       - i_compile_jam_messages
       - i_compile_jrw_messages

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
)


def prompt_mode():
    """
    Prompt user to select import or export mode.

    Returns:
        str: 'import' or 'export'
    """
    print()
    print("Select operation:")
    print("  1) Import - Push messages from files into the database")
    print("  2) Export - Export messages from database to files")
    print()

    while True:
        choice = input("Enter choice (1 or 2): ").strip()
        if choice == '1':
            return 'import'
        elif choice == '2':
            return 'export'
        else:
            print("Invalid choice. Please enter 1 or 2.")


def main(args_list=None):
    """
    Main entry point for the compile_msg command.

    Workflow:
        1. Prompt user for import/export mode
        2. Load configuration for the specified profile
        3. For import: validate source files and call compile_messages()
        4. For export: not yet implemented

    Args:
        args_list: Command line arguments (defaults to sys.argv[1:])
    """
    if args_list is None:
        args_list = sys.argv[1:]

    parser = argparse.ArgumentParser(
        description="Compile and install messages into the database.",
        usage="compile_msg PROFILE"
    )
    parser.add_argument("profile", help="Configuration profile (required)")

    args = parser.parse_args(args_list)

    # Load config from profile
    config = get_config(profile_name=args.profile)
    config['PROFILE_NAME'] = args.profile.upper()

    # Prompt for operation mode
    mode = prompt_mode()

    if mode == 'export':
        # Export mode: pull messages from database to files
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

        # Quick check that at least one source file exists
        test_file = get_messages_path(config, '.ibs_msg')
        if not os.path.exists(test_file):
            print(f"ERROR: Message files not found at expected location.")
            print(f"Expected: {os.path.dirname(test_file)}")
            sys.exit(1)

        # Compile: parse source files and insert into database
        print("Compiling messages...")
        success, message = compile_messages(config)

        if success:
            print(message)
            print("compile_msg DONE.")
        else:
            print(f"ERROR: {message}")
            sys.exit(1)


if __name__ == "__main__":
    main()
