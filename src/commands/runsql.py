#!/usr/bin/env python3
"""
runsql.py: Execute SQL scripts with sequence and placeholder replacement.

This script replaces the C# runsql project, providing a powerful SQL script
runner that uses FreeTDS tsql for execution.

Key Features:
- Full soft-compiler support (v:, c:, ->, @sequence@ placeholders)
- Hierarchical option file merging (SQL -> Company -> Server)
- Change logging ON by default (use --no-changelog to disable)
- Sequence processing (-F/-L flags)
- Preview mode (--preview)

Changelog Behavior:
- runsql logs to ba_gen_chg_log by default (if gclog12 is enabled in database)
- Use --no-changelog to disable (e.g., when called by runcreate)
- isqlline never logs to changelog

Usage:
    runsql script.sql sbnmaster SBNA
    runsql script.sql sbnmaster -S SBNA
    runsql script.sql sbnmaster SBNA -O output.txt
    runsql script.sql sbnmaster SBNA --no-changelog

CHG 241124 Integrated Options class and change_log for full soft-compiler support
CHG 241129 Refactored to use tsql native execution like isqlline
CHG 241206 Changed changelog to ON by default, added --no-changelog flag
"""

import argparse
import getpass
import logging
import sys
import os
from pathlib import Path

# Import shared functions from ibs_common
from .ibs_common import (
    load_profile,
    list_profiles,
    replace_placeholders,
    test_connection,
    execute_sql_native,
    find_file,
    Options
)



def main():
    """Main entry point for runsql."""

    # Show help if no arguments provided
    if len(sys.argv) == 1:
        sys.argv.append('-h')

    parser = argparse.ArgumentParser(
        description="Execute SQL scripts with sequence and placeholder replacement.",
        epilog="""
Examples:
  runsql script.sql sbnmaster SBNA
  runsql script.sql sbnmaster -S SBNA
  runsql script.sql sbnmaster SBNA -O output.txt
  runsql script.sql sbnmaster -H 10.0.0.1 -p 5000 -U sa
  runsql script.sql -D sbnmaster -H 10.0.0.1 -p 5000 -U sa --platform MSSQL

Notes:
  - Uses FreeTDS tsql for both Sybase and MSSQL
  - Supports placeholder replacement (&OPTIONS&, &DBPRO&, etc.) when using profiles
  - Use -S to specify profile (prevents last positional arg from being treated as profile)
""",
        formatter_class=argparse.RawDescriptionHelpFormatter
    )

    # Positional arguments
    parser.add_argument("script_file", nargs="?", help="Path to the SQL script file")
    parser.add_argument("database", nargs="?", help="Database name")
    parser.add_argument("profile", nargs="?", help="Profile name from settings.json")

    # Connection options
    parser.add_argument("-S", "--server", dest="server_profile",
                        help="Profile/server name (takes precedence over positional profile)")
    parser.add_argument("-H", "--host", help="Database server host/IP (overrides profile)")
    parser.add_argument("-p", "--port", type=int, help="Database server port (overrides profile)")
    parser.add_argument("-U", "--user", help="Username (overrides profile)")
    parser.add_argument("-P", "--password", help="Password (overrides profile, will prompt if not provided)")
    parser.add_argument("-D", "--database-override", dest="db_override",
                        help="Database name (overrides positional database)")
    parser.add_argument("--platform", choices=["SYBASE", "MSSQL"], default="SYBASE",
                        help="Database platform (default: SYBASE)")

    # Output options
    parser.add_argument("-O", "--output", help="Output file (default: stdout)")
    parser.add_argument("-e", "--echo", action="store_true", help="Echo input commands (maps to tsql -v)")

    # Sequence processing
    parser.add_argument("-F", "--first-sequence", type=int, default=1,
                        help="First sequence number to run (inclusive)")
    parser.add_argument("-L", "--last-sequence", type=int, default=1,
                        help="Last sequence number to run (inclusive)")

    # Advanced options
    parser.add_argument("--preview", action="store_true",
                        help="Print processed SQL to console instead of executing")
    parser.add_argument("--no-changelog", action="store_true",
                        help="Disable changelog logging (default: changelog ON)")
    parser.add_argument("--test-connection", action="store_true",
                        help="Test connection before executing SQL")
    parser.add_argument("--debug", action="store_true", help="Enable debug output")

    args = parser.parse_args()

    # Check that script_file was provided
    if not args.script_file:
        parser.print_help()
        return 1

    # Configure logging
    if args.debug:
        logging.basicConfig(level=logging.DEBUG, format='[DEBUG] %(message)s')
    else:
        logging.basicConfig(level=logging.WARNING)

    # Determine database name (flag takes precedence over positional)
    database = args.db_override or args.database or ""

    # Determine profile: -S flag takes precedence over positional argument
    profile_name = args.server_profile or args.profile

    # Check if user is attempting direct connection (any direct param provided)
    attempting_direct = args.host or args.port or args.user

    try:
        # Build connection parameters and config dict
        config = {}

        if attempting_direct:
            # Direct connection mode - must have host and user at minimum
            if not args.host:
                print("ERROR: -H/--host is required for direct connection", file=sys.stderr)
                return 1
            if not args.user:
                print("ERROR: -U/--user is required for direct connection", file=sys.stderr)
                return 1

            # Use default port if not provided
            port = args.port
            if not port:
                port = 1433 if args.platform == "MSSQL" else 5000

            # Prompt for password if not provided
            password = args.password
            if not password:
                password = getpass.getpass("Password: ")
                if not password:
                    print("ERROR: Password is required", file=sys.stderr)
                    return 1

            # Build minimal config for placeholder replacement
            config = {
                'HOST': args.host,
                'PORT': port,
                'USERNAME': args.user,
                'PASSWORD': password,
                'PLATFORM': args.platform,
                'DATABASE': database,
                'SQL_SOURCE': os.getcwd()
            }

            host = args.host
            username = args.user
            platform = args.platform

        elif profile_name:
            # Profile mode - load from settings.json
            try:
                profile_data = load_profile(profile_name)
            except KeyError:
                print(f"ERROR: Profile '{profile_name}' not found in settings.json", file=sys.stderr)
                print("\nAvailable profiles:", file=sys.stderr)
                for pname in list_profiles():
                    print(f"  - {pname}", file=sys.stderr)
                return 1

            # Allow command-line overrides
            host = args.host or profile_data["HOST"]
            port = args.port or profile_data["PORT"]
            username = args.user or profile_data["USERNAME"]
            password = args.password or profile_data["PASSWORD"]
            platform = profile_data["PLATFORM"]

            # Build full config from profile for placeholder replacement
            config = profile_data.copy()
            config['HOST'] = host
            config['PORT'] = port
            config['USERNAME'] = username
            config['PASSWORD'] = password
            config['DATABASE'] = database
            # PROFILE_NAME is already set by load_profile (resolves aliases)

            # Expand SQL_SOURCE - use current directory if not set
            if not config.get('SQL_SOURCE'):
                config['SQL_SOURCE'] = os.getcwd()

        else:
            # No profile and no direct connection params
            print("ERROR: Either specify a profile name or provide direct connection parameters", file=sys.stderr)
            print("\nUsage:", file=sys.stderr)
            print("  runsql script.sql sbnmaster GONZO", file=sys.stderr)
            print("  runsql script.sql sbnmaster -S GONZO", file=sys.stderr)
            print("  runsql script.sql sbnmaster -H 10.0.0.1 -U sa", file=sys.stderr)
            return 1

        # Test connection if requested
        if args.test_connection:
            print(f"Testing connection to {platform} at {host}:{port}...", file=sys.stderr)
            success, msg = test_connection(host, port, username, password, platform)
            if not success:
                print(f"ERROR: Connection test failed: {msg}", file=sys.stderr)
                return 1
            print(f"Connection test successful: {msg}", file=sys.stderr)

        # Find the script file
        script_path = find_file(args.script_file, config)
        if not script_path:
            print(f"ERROR: Script file '{args.script_file}' not found", file=sys.stderr)
            return 1

        logging.debug(f"Found script file: {script_path}")

        # Read the script content
        try:
            with open(script_path, 'r', encoding='utf-8') as f:
                script_content = f.read()
        except IOError as e:
            print(f"ERROR: Could not read script file '{script_path}': {e}", file=sys.stderr)
            return 1

        # Apply Options-based placeholder resolution (e.g., &users& -> sbnmaster..users)
        # Only if we have a profile with the necessary config (COMPANY, PROFILE_NAME)
        if profile_name and config.get('COMPANY'):
            options = Options(config)
            if options.generate_option_files():
                # Options loaded successfully - we'll use them for placeholder resolution
                logging.debug("Options loaded successfully for placeholder resolution")
            else:
                # Options failed to load - continue without them
                logging.warning("Failed to generate option files. Continuing without soft-compiler.")
                options = None
        else:
            options = None
            logging.debug("No profile or COMPANY - skipping Options-based placeholder resolution")

        # Loop through the sequence
        current_seq = args.first_sequence
        all_output = []

        while current_seq <= args.last_sequence:
            logging.info(f"Processing sequence {current_seq} of {args.last_sequence}...")

            # Build the SQL content for this sequence
            # Start with the original file content
            sql_content = script_content

            # Inject change log (ON by default, use --no-changelog to disable)
            if not args.no_changelog:
                from .ibs_common import is_changelog_enabled, generate_changelog_sql
                enabled, msg = is_changelog_enabled(config)
                if enabled:
                    logging.info("Change logging enabled. Injecting audit trail SQL...")
                    username = config.get('USERNAME', os.environ.get('USERNAME', 'unknown'))
                    changelog_lines = generate_changelog_sql(
                        sql_command=args.script_file,
                        database=database,
                        server=config.get('PROFILE_NAME', host),
                        company=str(config.get('COMPANY', '')),
                        username=username,
                        changelog_enabled=True
                    )
                    changelog_sql = '\n'.join(changelog_lines)
                    sql_content = changelog_sql + '\n' + sql_content
                else:
                    logging.debug(f"Changelog not available: {msg}")

            # Apply placeholder resolution
            if options:
                # Use Options class for full soft-compiler support (handles &placeholders& and @sequence@)
                sql_content = options.replace_options(sql_content, sequence=current_seq)
            else:
                # Fall back to simple placeholder replacement
                sql_content = replace_placeholders(sql_content, config, remove_ampersands=False)
                # Replace @sequence@ manually
                sql_content = sql_content.replace('@sequence@', str(current_seq))
                sql_content = sql_content.replace('@SEQUENCE@', str(current_seq))

            # Echo input if requested (print resolved SQL before execution)
            if args.echo:
                echo_header = f"-- Executing SQL (sequence {current_seq}):"
                echo_footer = "--"
                if args.output:
                    # Write to output file only (no console output)
                    all_output.append(echo_header)
                    all_output.append(sql_content)
                    all_output.append(echo_footer)
                else:
                    # Print to console only
                    print(echo_header)
                    print(sql_content)
                    print(echo_footer)

            if args.preview:
                print(f"--- PREVIEW: Sequence {current_seq} ---")
                print(sql_content)
                print("--- END PREVIEW ---\n")
            else:
                logging.info(f"Executing sequence {current_seq}...")

                # Execute the SQL via native compiler (tsql)
                success, output = execute_sql_native(
                    host, port, username, password, database, platform,
                    sql_content,
                    output_file=None,  # We'll handle output aggregation ourselves
                    echo_input=False  # We handle echo ourselves above to show resolved SQL
                )

                if success:
                    if output and output.strip():
                        all_output.append(output)
                else:
                    # Append error to output
                    all_output.append(f"ERROR at sequence {current_seq}: {output}")

                    # Write to file or stderr before returning
                    if not args.preview:
                        combined_output = "\n".join(all_output)
                        if args.output:
                            try:
                                with open(args.output, 'w', encoding='utf-8') as f:
                                    f.write(combined_output + "\n")
                            except IOError as e:
                                print(f"ERROR: Failed to write output file: {e}", file=sys.stderr)
                        else:
                            print(f"ERROR at sequence {current_seq}: {output}", file=sys.stderr)
                    return 1

            current_seq += 1

        # Handle output
        if not args.preview:
            combined_output = "\n".join(all_output)

            if args.output:
                try:
                    with open(args.output, 'w', encoding='utf-8') as f:
                        f.write(combined_output + "\n")
                except IOError as e:
                    print(f"ERROR: Failed to write output file: {e}", file=sys.stderr)
                    return 1
            else:
                if combined_output.strip():
                    print(combined_output)

        logging.info("runsql completed successfully.")
        return 0

    except KeyboardInterrupt:
        print("\nInterrupted by user", file=sys.stderr)
        return 130

    except Exception as e:
        print(f"ERROR: {e}", file=sys.stderr)
        if args.debug:
            import traceback
            traceback.print_exc()
        return 1


if __name__ == "__main__":
    sys.exit(main())
