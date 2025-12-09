"""
eloc.py: Edit and compile table_locations.

This command allows editing and importing the table_locations mapping file into the
database. The table_locations table maps logical table names to physical database
locations, enabling the IBS compiler system to resolve &table& placeholders.

SOURCE FILE:
    {SQL_SOURCE}/CSS/Setup/table_locations

    The source file contains lines in the format:
        -> table_name    &db_placeholder&    description

    Example:
        -> users         &dbtbl&             User accounts table
        -> options       &dbibs&             System options

TARGET TABLE:
    &table_locations& (typically ibs..table_locations)

    Schema:
        table_name   varchar(40)   - The logical table name
        logical_db   varchar(8)    - The option placeholder name (e.g., "dbtbl")
        physical_db  varchar(32)   - The resolved database name (e.g., "sbnmaster")
        db_table     varchar(100)  - Full path (e.g., "sbnmaster..users")

PROCESS:
    1. Prompt to edit the source file (default: Yes)
    2. Open vim editor if user confirms
    3. Prompt to compile into database (default: Yes)
    4. Parse the source file, resolving &placeholders& using current options
    5. Truncate the target table_locations table
    6. Insert all rows using SQL INSERT statements

USAGE:
    eloc PROFILE [-O output_file]

ARGUMENTS:
    PROFILE     Configuration profile name (e.g., GONZO, PROD)
    -O          Optional output file for messages (not currently used)

EXAMPLES:
    eloc GONZO          Edit and compile table_locations for GONZO profile
    eloc PROD           Edit and compile table_locations for PROD profile

RELATED:
    eopt - Edit and compile options (also updates table_locations)

NOTE:
    This command uses SQL INSERT statements instead of BCP (Bulk Copy Program)
    to avoid the 255 character limit in freebcp.
"""

import argparse
import sys
import os

from .ibs_common import (
    get_config,
    get_table_locations_path,
    compile_table_locations,
    console_yes_no,
    launch_editor,
)


def main(args_list=None):
    """
    Main entry point for the eloc command.

    Workflow:
        1. Load configuration for the specified profile
        2. Locate the table_locations source file
        3. Prompt user to edit the file (default: Yes)
        4. Prompt user to compile into database (default: Yes)
        5. Call compile_table_locations() to parse and insert data

    Args:
        args_list: Command line arguments (defaults to sys.argv[1:])
    """
    if args_list is None:
        args_list = sys.argv[1:]

    parser = argparse.ArgumentParser(
        description="Edit and compile table_locations.",
        usage="eloc PROFILE [-O output_file]"
    )
    parser.add_argument("profile", help="Configuration profile (required)")
    parser.add_argument("-O", "--outfile", help="Output file for messages")

    args = parser.parse_args(args_list)

    # Load config from profile (contains HOST, PORT, USERNAME, PASSWORD, SQL_SOURCE, etc.)
    config = get_config(profile_name=args.profile)
    config['PROFILE_NAME'] = args.profile.upper()

    # Get path to table_locations source file: {SQL_SOURCE}/CSS/Setup/table_locations
    locations_file = get_table_locations_path(config)

    if not os.path.exists(locations_file):
        print(f"ERROR: table_locations file not found: {locations_file}")
        sys.exit(1)

    # Collect output messages
    output_lines = []

    def output(msg):
        """Write message to output file or console."""
        if args.outfile:
            output_lines.append(msg)
        else:
            print(msg)

    # Prompt to edit the source file (default: Yes)
    if console_yes_no(f"Edit {locations_file}?", default=True):
        launch_editor(locations_file)

    # Prompt to compile/insert into database (default: Yes)
    if not console_yes_no(f"Compile table_locations into {args.profile.upper()}?", default=True):
        print("Cancelled.")
        sys.exit(0)

    # Compile: parse source file and insert rows into table_locations table
    output("Compiling table_locations...")
    success, message, row_count = compile_table_locations(config)

    if success:
        output(f"Inserted {row_count} rows into table_locations")
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