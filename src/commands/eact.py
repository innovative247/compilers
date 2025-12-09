"""
eact.py: Edit and compile actions.

This command allows editing and importing the actions definition files into the
database. Actions define menu items, buttons, and permissions throughout the
SBN application.

SOURCE FILES:
    {SQL_SOURCE}/CSS/Setup/actions
        Main action definitions. Lines starting with :> define actions.
        Format: :>ACTION_TEXT_AND_DETAILS

    {SQL_SOURCE}/CSS/Setup/actions_dtl
        Action detail definitions. Fixed-width format:
        :>AAAA BBB CCC DDDDD EEE description
        Where:
            AAAA  = Action number (cols 2-5)
            BBB   = Index (cols 7-9)
            CCC   = Sub-index (cols 11-13)
            DDDDD = Text ID (cols 15-19)
            EEE   = Language (cols 21-23)
            rest  = Description (cols 24+)

TARGET TABLES:
    w#actions - Work table for action headers
    w#actions_dtl - Work table for action details

    After BCP import, ba_compile_actions stored procedure moves data
    to the final actions tables.

PROCESS:
    1. Prompt to edit actions file (default: Yes)
    2. Prompt to edit actions_dtl file (default: Yes)
    3. Prompt to compile into database (default: Yes)
    4. Parse source files, resolving &placeholders&
    5. Truncate work tables
    6. Insert data via SQL INSERT statements
    7. Execute ba_compile_actions stored procedure

USAGE:
    eact PROFILE [-O output_file]

ARGUMENTS:
    PROFILE     Configuration profile name (e.g., GONZO, PROD)
    -O          Optional output file for messages (not currently used)

EXAMPLES:
    eact GONZO          Edit and compile actions for GONZO profile
    eact PROD           Edit and compile actions for PROD profile

RELATED:
    eloc - Edit and compile table_locations
    eopt - Edit and compile options
"""

import argparse
import sys
import os

from .ibs_common import (
    get_config,
    get_actions_path,
    get_actions_dtl_path,
    compile_actions,
    console_yes_no,
    launch_editor,
)


def main(args_list=None):
    """
    Main entry point for the eact command.

    Workflow:
        1. Load configuration for the specified profile
        2. Locate the actions and actions_dtl source files
        3. Prompt user to edit actions file (default: Yes)
        4. Prompt user to edit actions_dtl file (default: Yes)
        5. Prompt user to compile into database (default: Yes)
        6. Call compile_actions() to parse and insert data

    Args:
        args_list: Command line arguments (defaults to sys.argv[1:])
    """
    if args_list is None:
        args_list = sys.argv[1:]

    parser = argparse.ArgumentParser(
        description="Edit and compile actions.",
        usage="eact PROFILE [-O output_file]"
    )
    parser.add_argument("profile", help="Configuration profile (required)")
    parser.add_argument("-O", "--outfile", help="Output file for messages")

    args = parser.parse_args(args_list)

    # Load config from profile (contains HOST, PORT, USERNAME, PASSWORD, SQL_SOURCE, etc.)
    # PROFILE_NAME is set by get_config (resolves aliases to real profile name)
    config = get_config(profile_name=args.profile)

    # Get paths to source files
    actions_file = get_actions_path(config)
    actions_dtl_file = get_actions_dtl_path(config)

    # Check files exist
    if not os.path.exists(actions_file):
        print(f"ERROR: actions file not found: {actions_file}")
        sys.exit(1)

    if not os.path.exists(actions_dtl_file):
        print(f"ERROR: actions_dtl file not found: {actions_dtl_file}")
        sys.exit(1)

    # Collect output messages
    output_lines = []

    def output(msg):
        """Write message to output file or console."""
        if args.outfile:
            output_lines.append(msg)
        else:
            print(msg)

    # Prompt to edit the actions file (default: Yes)
    if console_yes_no(f"Edit {actions_file}?", default=True):
        launch_editor(actions_file)

    # Prompt to edit the actions_dtl file (default: Yes)
    if console_yes_no(f"Edit {actions_dtl_file}?", default=True):
        launch_editor(actions_dtl_file)

    # Prompt to compile/insert into database (default: Yes)
    if not console_yes_no(f"Compile actions into {args.profile.upper()}?", default=True):
        print("Finished.")
        sys.exit(0)

    # Compile: parse source files and insert into database
    output("Compiling actions...")
    success, message = compile_actions(config)

    if success:
        output(message)
        output("SUCCESS")
    else:
        output(f"ERROR: {message}")

    # Write output file if specified
    if args.outfile:
        try:
            with open(args.outfile, 'w', encoding='utf-8') as f:
                f.write("\n".join(output_lines) + "\n")
        except IOError as e:
            print(f"ERROR: Failed to write output file: {e}", file=sys.stderr)
            sys.exit(1)

    if not success:
        sys.exit(1)


if __name__ == "__main__":
    main()
