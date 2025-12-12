"""
set_required_fields.py: Edit and compile required field definitions. (Also available as: ereq, install_required_fields)

This command compiles the required_fields and required_fields_dtl source files
into the database work tables, then executes a stored procedure to finalize.

SOURCE FILES:
    {SQL_SOURCE}/CSS/Setup/css.required_fields
        Tab-delimited with 6 columns:
        s#rf, name, title, helptxt, inact_flg, s#sk

    {SQL_SOURCE}/CSS/Setup/css.required_fields_dtl
        Tab-delimited with 30+ columns

TARGET TABLES:
    w#i_required_fields - Work table for required field headers
    w#i_required_fields_dtl - Work table for required field details

    After INSERT, i_required_fields_install stored procedure moves data
    to the final tables.

PROCESS:
    1. Load configuration for the specified profile
    2. Parse source files
    3. Delete from work tables
    4. Insert data via SQL INSERT statements
    5. Execute i_required_fields_install stored procedure

USAGE:
    ereq PROFILE

ARGUMENTS:
    PROFILE     Configuration profile name (e.g., GONZO, PROD)

EXAMPLES:
    ereq GONZO
    ereq PROD
"""

import argparse
import sys
import os

from .ibs_common import (
    get_config,
    get_required_fields_path,
    get_required_fields_dtl_path,
    compile_required_fields,
)


def main(args_list=None):
    """
    Main entry point for the ereq command.

    Workflow:
        1. Load configuration for the specified profile
        2. Locate the source files
        3. Call compile_required_fields() to parse and insert data

    Args:
        args_list: Command line arguments (defaults to sys.argv[1:])
    """
    if args_list is None:
        args_list = sys.argv[1:]

    parser = argparse.ArgumentParser(
        description="Edit and compile required field definitions.",
        usage="ereq PROFILE"
    )
    parser.add_argument("profile", help="Configuration profile (required)")

    args = parser.parse_args(args_list)

    # Load config from profile
    # PROFILE_NAME is set by get_config (resolves aliases to real profile name)
    config = get_config(profile_name=args.profile)

    # Get paths to source files
    rf_file = get_required_fields_path(config)
    rf_dtl_file = get_required_fields_dtl_path(config)

    # Check files exist
    if not os.path.exists(rf_file):
        print(f"ERROR: Required Fields import file is missing ({rf_file})")
        sys.exit(1)

    if not os.path.exists(rf_dtl_file):
        print(f"ERROR: Required Fields Detail import file is missing ({rf_dtl_file})")
        sys.exit(1)

    # Compile: parse source files and insert into database
    print("Compiling required fields...")
    success, message, count = compile_required_fields(config)

    if success:
        print(message)
        print("ereq DONE.")
    else:
        print(f"ERROR: {message}")
        sys.exit(1)


if __name__ == "__main__":
    main()
