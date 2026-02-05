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

OPTIONS FILE HANDLING:
    When a profile is used, runsql loads options from {SQL_SOURCE}/CSS/Setup/:
    - options.def (required)
    - options.{company} (required)
    - options.{company}.{profile} (optional)
    - table_locations (required)

    The merged options are cached in {SQL_SOURCE}/CSS/Setup/temp/{profile}.options.tmp
    and reused for 24 hours. All option resolution is handled by the Options class
    in ibs_common.py.

    When -e (echo) is used, the options cache file path is logged.

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
    execute_sql_interleaved,
    find_file,
    Options,
    create_symbolic_links,
    is_raw_mode,
    # Styling utilities
    Icons, Fore, Style,
    print_success, print_error, print_warning, print_info,
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
    parser.add_argument("--force-changelog-check", action="store_true",
                        help="Force re-check of changelog status (bypasses session cache)")
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
                print(f"{Fore.RED}{Icons.ERROR} -H/--host is required for direct connection{Style.RESET_ALL}", file=sys.stderr)
                return 1
            if not args.user:
                print(f"{Fore.RED}{Icons.ERROR} -U/--user is required for direct connection{Style.RESET_ALL}", file=sys.stderr)
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
                    print(f"{Fore.RED}{Icons.ERROR} Password is required{Style.RESET_ALL}", file=sys.stderr)
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
                print(f"{Fore.RED}{Icons.ERROR} Profile '{profile_name}' not found in settings.json{Style.RESET_ALL}", file=sys.stderr)
                print(f"\n{Style.BRIGHT}Available profiles:{Style.RESET_ALL}", file=sys.stderr)
                for pname in list_profiles():
                    print(f"  {Icons.ARROW} {pname}", file=sys.stderr)
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

            # Ensure symbolic links exist (fast no-op if already checked this session)
            create_symbolic_links(config, prompt=False)

        else:
            # No profile and no direct connection params
            print(f"{Fore.RED}{Icons.ERROR} Either specify a profile name or provide direct connection parameters{Style.RESET_ALL}", file=sys.stderr)
            print(f"\n{Style.BRIGHT}Usage:{Style.RESET_ALL}", file=sys.stderr)
            print(f"  {Icons.ARROW} runsql script.sql sbnmaster GONZO", file=sys.stderr)
            print(f"  {Icons.ARROW} runsql script.sql sbnmaster -S GONZO", file=sys.stderr)
            print(f"  {Icons.ARROW} runsql script.sql sbnmaster -H 10.0.0.1 -U sa", file=sys.stderr)
            return 1

        # Test connection if requested
        if args.test_connection:
            print(f"{Icons.RUNNING} Testing connection to {platform} at {host}:{port}...", file=sys.stderr)
            success, msg = test_connection(host, port, username, password, platform)
            if not success:
                print(f"{Fore.RED}{Icons.ERROR} Connection test failed: {msg}{Style.RESET_ALL}", file=sys.stderr)
                return 1
            print(f"{Fore.GREEN}{Icons.SUCCESS} Connection successful: {msg}{Style.RESET_ALL}", file=sys.stderr)

        # Find the script file
        script_path = find_file(args.script_file, config)
        if not script_path:
            print(f"{Fore.RED}{Icons.ERROR} '{args.script_file}' not found.{Style.RESET_ALL}", file=sys.stderr)
            return 1

        logging.debug(f"Found script file: {script_path}")

        # Validate file is within profile's SQL_SOURCE directory
        sql_source = config.get('SQL_SOURCE', '')
        if sql_source and profile_name:
            script_abs = os.path.normcase(os.path.abspath(script_path))
            source_abs = os.path.normcase(os.path.abspath(sql_source))

            if not script_abs.startswith(source_abs + os.sep):
                profile_display = config.get('PROFILE_NAME', profile_name or '')
                print(f"{Fore.RED}{Icons.ERROR} You are outside of profile {profile_display}'s path of:{Style.RESET_ALL}", file=sys.stderr)
                print(f"  {Icons.FOLDER} {sql_source}", file=sys.stderr)
                print(f"  Run {Style.BRIGHT}set_profile{Style.RESET_ALL} and create a new profile for this sql path.", file=sys.stderr)
                return 1

        # Read the script content (UTF-8 default, works cross-platform)
        try:
            with open(script_path, 'r', encoding='utf-8') as f:
                script_content = f.read()
        except IOError as e:
            print(f"{Fore.RED}{Icons.ERROR} Could not read script file '{script_path}': {e}{Style.RESET_ALL}", file=sys.stderr)
            return 1

        # ==========================================================================
        # OPTIONS FILE HANDLING
        # ==========================================================================
        # Load/create options file ONCE per runsql execution.
        # Options are cached in {SQL_SOURCE}/CSS/Setup/temp/{profile}.options.tmp
        # and reused for 24 hours. All processing is done by Options class in ibs_common.py.
        #
        # When -e (echo) is used, the options file path is logged.
        # ==========================================================================
        options = None
        options_log_line = None  # Will be added to output later if -O is used
        if not is_raw_mode(config) and profile_name and config.get('COMPANY'):
            options = Options(config)
            if options.generate_option_files():
                # Options loaded successfully - log path if echo is enabled
                logging.debug("Options loaded successfully for placeholder resolution")
                if args.echo:
                    cache_file = options.get_cache_filepath()
                    options_log_line = f"-- Options: {cache_file}"
                    if not args.output:
                        # Print to console only if not writing to file
                        print(options_log_line)
            else:
                # Options failed to load - continue without them
                logging.warning("Failed to generate option files. Continuing without soft-compiler.")
                options = None
        else:
            options = None
            logging.debug("No profile or COMPANY - skipping Options-based placeholder resolution")

        # Loop through the sequence
        current_seq = args.first_sequence
        all_output = []  # Used for console output (no -O flag)
        output_handle = None  # File handle for streaming output

        # Open output file for streaming if specified
        if args.output:
            try:
                output_handle = open(args.output, 'w', encoding='utf-8')
                # Write options file path first if echo is enabled
                if options_log_line:
                    output_handle.write(options_log_line + '\n')
                    output_handle.flush()
            except IOError as e:
                print(f"{Fore.RED}{Icons.ERROR} Failed to open output file: {e}{Style.RESET_ALL}", file=sys.stderr)
                return 1

        try:
            while current_seq <= args.last_sequence:
                logging.info(f"Processing sequence {current_seq} of {args.last_sequence}...")

                # Build the SQL content for this sequence
                # Start with the original file content
                sql_content = script_content

                # Inject change log (ON by default, use --no-changelog to disable)
                if not args.no_changelog:
                    from .ibs_common import is_changelog_enabled, generate_changelog_sql
                    enabled, msg = is_changelog_enabled(config, force_check=args.force_changelog_check)
                    if enabled:
                        logging.info("Change logging enabled. Injecting audit trail SQL...")
                        changelog_user = os.environ.get('USERNAME', os.environ.get('USER', 'unknown'))
                        changelog_lines = generate_changelog_sql(
                            sql_command=script_path,
                            database=database,
                            server=config.get('PROFILE_NAME', host),
                            company=str(config.get('COMPANY', '')),
                            username=changelog_user,
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

                if args.preview:
                    print(f"--- PREVIEW: Sequence {current_seq} ---")
                    print(sql_content)
                    print("--- END PREVIEW ---\n")
                else:
                    # Print "Running..." message to match Unix output
                    profile_display = config.get('PROFILE_NAME', profile_name or '')
                    # Include sequence number when using -F/-L flags
                    if args.first_sequence != args.last_sequence:
                        print(f"Running {script_path} on {profile_display} in {database} {current_seq}")
                    else:
                        print(f"Running {script_path} on {profile_display} in {database}")

                    # Execute SQL using interleaved mode (single tsql process, batch-by-batch)
                    # This gives us correct error placement while avoiding subprocess spawn overhead
                    success = execute_sql_interleaved(
                        host, port, username, password, database, platform,
                        sql_content,
                        echo=args.echo,
                        output_handle=output_handle
                    )

                    # Print return status to match Unix output
                    if success:
                        print("(return status = 0)")
                    else:
                        print("(return status = 1)")

                current_seq += 1

            # Handle console output (when no -O flag)
            if not args.preview and not output_handle:
                combined_output = "\n".join(all_output)
                if combined_output.strip():
                    print(combined_output)

        finally:
            # Always close output file if open
            if output_handle:
                output_handle.close()

        logging.info("runsql completed successfully.")
        return 0

    except KeyboardInterrupt:
        print(f"\n{Fore.YELLOW}{Icons.WARNING} Interrupted by user{Style.RESET_ALL}", file=sys.stderr)
        return 130

    except Exception as e:
        print(f"{Fore.RED}{Icons.ERROR} {e}{Style.RESET_ALL}", file=sys.stderr)
        if args.debug:
            import traceback
            traceback.print_exc()
        return 1


if __name__ == "__main__":
    sys.exit(main())
