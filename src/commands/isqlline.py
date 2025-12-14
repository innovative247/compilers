#!/usr/bin/env python3
"""
isqlline.py: Execute a single SQL command against a database.

This script replaces the C# isqlline project, providing a way to execute
ad-hoc SQL commands from the command line. It matches the C# implementation
by using native SQL tools (tsql) via subprocess.

Key features:
- Placeholder replacement (&OPTIONS&, &DBPRO&, etc.)
- Native compiler execution (tsql for both Sybase and MSSQL)
- Output file support

Note: isqlline does NOT log to changelog. Use runsql for audit-logged operations.

OPTIONS FILE HANDLING:
    When a profile is used, isqlline loads options from {SQL_SOURCE}/CSS/Setup/:
    - options.def (required)
    - options.{company} (required)
    - options.{company}.{profile} (optional)
    - table_locations (required)

    The merged options are cached in {SQL_SOURCE}/CSS/Setup/temp/{profile}.options.tmp
    and reused for 24 hours. All option resolution is handled by the Options class
    in ibs_common.py.

    When -e (echo) is used, the options cache file path is logged.

Usage:
    isqlline 'select @@version' sbnmaster SBNA
    isqlline 'select * from users' sbnmaster SBNA -O output.txt
    isqlline 'select @@version' sbnmaster -H 10.0.0.1 -p 5000 -U sa -P pass
    isqlline 'select @@version' -D sbnmaster -H 10.0.0.1 -p 5000 -U sa --platform MSSQL
"""

import argparse
import getpass
import logging
import os
import sys

# Import shared functions from ibs_common (relative import within commands package)
from .ibs_common import (
    load_profile,
    list_profiles,
    test_connection,
    execute_sql_native,
    build_sql_script,
    Options,
    create_symbolic_links,
    is_raw_mode,
)


def main():
    """Main entry point for isqlline."""

    # Show help if no arguments provided
    if len(sys.argv) == 1:
        sys.argv.append('-h')

    parser = argparse.ArgumentParser(
        description="Execute a single SQL command against a database using native SQL tools.",
        epilog="""
Examples:
  isqlline 'select @@version' sbnmaster SBNA
  isqlline 'select @@version' sbnmaster -S SBNA
  isqlline 'select * from users' sbnmaster SBNA -O output.txt
  isqlline 'select @@version' sbnmaster -H 10.0.0.1 -p 5000 -U sa
  isqlline 'select @@version' -D sbnmaster -H 10.0.0.1 -p 5000 -U sa --platform MSSQL

Notes:
  - Uses FreeTDS tsql for both Sybase and MSSQL
  - Supports placeholder replacement (&OPTIONS&, &DBPRO&, etc.) when using profiles
  - Does NOT log to changelog (use runsql for audit-logged operations)
  - Use -S to specify profile (prevents last positional arg from being treated as profile)
""",
        formatter_class=argparse.RawDescriptionHelpFormatter
    )

    # Positional arguments
    parser.add_argument("sql_command", nargs="?", help="SQL command to execute")
    parser.add_argument("database", nargs="?", help="Database name")
    parser.add_argument("profile", nargs="?", help="Profile name from settings.json")

    # Optional overrides
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

    # Output options (use -O only, -o conflicts with tsql's -o options flag)
    parser.add_argument("-O", "--output", help="Output file (default: stdout)")
    parser.add_argument("-e", "--echo", action="store_true", help="Echo input commands")

    # Advanced options
    parser.add_argument("--test-connection", action="store_true",
                        help="Test connection before executing SQL")
    parser.add_argument("--debug", action="store_true", help="Enable debug output")

    args = parser.parse_args()

    # Check that sql_command was provided
    if not args.sql_command:
        parser.print_help()
        return 1

    # Configure logging
    if args.debug:
        logging.basicConfig(level=logging.DEBUG, format='[DEBUG] %(message)s')
    else:
        logging.basicConfig(level=logging.ERROR)

    # Determine database name (flag takes precedence over positional)
    database = args.db_override or args.database or ""

    # Determine profile: -S flag takes precedence over positional argument
    profile_name = args.server_profile or args.profile

    # Check if user is attempting direct connection (any direct param provided)
    attempting_direct = args.host or args.port or args.user

    try:
        # Build connection parameters and config dict
        config = None

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
                'DATABASE': database
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

            # Expand SQL_SOURCE - use current directory if not set
            if not config.get('SQL_SOURCE'):
                config['SQL_SOURCE'] = os.getcwd()

            # Ensure symbolic links exist (fast no-op if already checked this session)
            create_symbolic_links(config, prompt=False)

        else:
            # No profile and no direct connection params
            print("ERROR: Either specify a profile name or provide direct connection parameters", file=sys.stderr)
            print("\nUsage:", file=sys.stderr)
            print("  isqlline 'select @@version' sbnmaster GONZO", file=sys.stderr)
            print("  isqlline 'select @@version' sbnmaster -S GONZO", file=sys.stderr)
            print("  isqlline 'select @@version' sbnmaster -H 10.0.0.1 -U sa", file=sys.stderr)
            return 1

        # Test connection if requested
        if args.test_connection:
            print(f"Testing connection to {platform} at {host}:{port}...", file=sys.stderr)
            success, msg = test_connection(host, port, username, password, platform)
            if not success:
                print(f"ERROR: Connection test failed: {msg}", file=sys.stderr)
                return 1
            print(f"Connection test successful: {msg}", file=sys.stderr)

        # Build SQL script with placeholder replacement (no changelog for isqlline)
        sql_script = build_sql_script(
            args.sql_command,
            config=config,
            changelog_enabled=False,
            database=database,
            host=host
        )

        # ==========================================================================
        # OPTIONS FILE HANDLING
        # ==========================================================================
        # Load/create options file ONCE per isqlline execution.
        # Options are cached in {SQL_SOURCE}/CSS/Setup/temp/{profile}.options.tmp
        # and reused for 24 hours. All processing is done by Options class in ibs_common.py.
        #
        # When -e (echo) is used, the options file path is logged.
        # ==========================================================================
        options = None
        options_log_line = None
        if not is_raw_mode(config) and profile_name and config.get('COMPANY'):
            options = Options(config)
            if options.generate_option_files():
                # Log options file path when echo is enabled
                if args.echo:
                    cache_file = options.get_cache_filepath()
                    options_log_line = f"-- Options: {cache_file}"
                    if not args.output:
                        # Print to console only if not writing to file
                        print(options_log_line)
                # Apply placeholder resolution
                sql_script = options.replace_options(sql_script)

        # Collect output parts
        output_parts = []

        # Add options file path to output if -O and -e are both used
        if args.output and options_log_line:
            output_parts.append(options_log_line)

        # Echo input if requested (print resolved SQL before execution)
        if args.echo:
            echo_footer = "--"
            if args.output:
                # Write to output file only (no console output)
                output_parts.append(sql_script)
                output_parts.append(echo_footer)
            else:
                # Print to console only
                print(sql_script)
                print(echo_footer)

        # Execute the SQL command via native compiler (tsql)
        success, output = execute_sql_native(
            host, port, username, password, database, platform,
            sql_script,
            output_file=None,  # We handle output ourselves
            echo_input=False   # We handle echo ourselves
        )

        if success:
            if output and output.strip():
                output_parts.append(output)
        else:
            # Append error to output
            output_parts.append(f"ERROR: {output}")

        # Write output to file or console
        if args.output:
            # Write all output to file
            try:
                with open(args.output, 'w', encoding='utf-8') as f:
                    f.write("\n".join(output_parts) + "\n")
            except IOError as e:
                print(f"ERROR: Failed to write output file: {e}", file=sys.stderr)
                return 1
        else:
            # Print to console
            if success:
                if output and output.strip():
                    print(output)
            else:
                print(f"ERROR: {output}", file=sys.stderr)

        return 0 if success else 1

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
