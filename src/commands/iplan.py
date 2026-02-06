#!/usr/bin/env python3
"""
iplan.py: Show the execution plan for a running SPID.

Replaces the Unix shell script iplan from IBS_Unix/Command_Library/iplan.
Works cross-platform (Windows/Ubuntu/Mac) against both Sybase and MSSQL.

Usage:
    iplan PROFILE SPID
    iplan -S PROFILE SPID
    iplan -H host -p port -U user [-P password] [--platform SYBASE|MSSQL] SPID
"""

import argparse
import getpass
import logging
import os
import sys

from .ibs_common import (
    load_profile,
    list_profiles,
    execute_sql_native,
    Options,
    create_symbolic_links,
    is_raw_mode,
    Icons, Fore, Style,
    print_error, print_info,
)


def main():
    """Main entry point for iplan."""
    # Handle --version / -v
    if len(sys.argv) > 1 and sys.argv[1] in ('--version', '-v'):
        from .version import __version__
        print(f"{Fore.GREEN}Innovative247 Compilers {__version__}{Style.RESET_ALL}")
        sys.exit(0)

    # Check for updates (once per day)
    from .version_check import check_for_updates
    if not check_for_updates("iplan"):
        sys.exit(0)

    # Show help if no arguments provided
    if len(sys.argv) == 1:
        sys.argv.append('-h')

    from .version import __version__
    parser = argparse.ArgumentParser(
        description="Show the execution plan for a running SPID.",
        epilog="""
Examples:
  iplan GONZO 52
  iplan -S GONZO 52
  iplan -H 10.0.0.1 -p 5000 -U sa -P pass 52
  iplan -H 10.0.0.1 -p 1433 -U sa --platform MSSQL 52
""",
        formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument('--version', '-v', action='version', version=f'{__version__}')

    # Positional arguments
    parser.add_argument("profile", nargs="?", help="Profile name from settings.json")
    parser.add_argument("spid", nargs="?", help="SPID to show plan for")

    # Optional overrides
    parser.add_argument("-S", "--server", dest="server_profile",
                        help="Profile/server name (takes precedence over positional profile)")
    parser.add_argument("-H", "--host", help="Database server host/IP (overrides profile)")
    parser.add_argument("-p", "--port", type=int, help="Database server port (overrides profile)")
    parser.add_argument("-U", "--user", help="Username (overrides profile)")
    parser.add_argument("-P", "--password", help="Password (overrides profile, will prompt if not provided)")
    parser.add_argument("-D", "--database-override", dest="db_override",
                        help="Database name override")
    parser.add_argument("--platform", choices=["SYBASE", "MSSQL"], default="SYBASE",
                        help="Database platform (default: SYBASE)")
    parser.add_argument("--debug", action="store_true", help="Enable debug output")

    args = parser.parse_args()

    # Configure logging
    if args.debug:
        logging.basicConfig(level=logging.DEBUG, format='[DEBUG] %(message)s')
    else:
        logging.basicConfig(level=logging.ERROR)

    # Validate SPID was provided
    if not args.spid:
        print_error("SPID is required")
        print("Usage: iplan PROFILE SPID", file=sys.stderr)
        return 1

    # Validate SPID is a positive integer
    try:
        spid = int(args.spid)
        if spid <= 0:
            raise ValueError()
    except ValueError:
        print_error(f"SPID must be a positive integer, got: {args.spid}")
        return 1

    # Determine profile: -S flag takes precedence over positional argument
    profile_name = args.server_profile or args.profile

    # Check if user is attempting direct connection
    attempting_direct = args.host or args.port or args.user

    try:
        config = None

        if attempting_direct:
            if not args.host:
                print("ERROR: -H/--host is required for direct connection", file=sys.stderr)
                return 1
            if not args.user:
                print("ERROR: -U/--user is required for direct connection", file=sys.stderr)
                return 1

            port = args.port
            if not port:
                port = 1433 if args.platform == "MSSQL" else 5000

            password = args.password
            if not password:
                password = getpass.getpass("Password: ")
                if not password:
                    print("ERROR: Password is required", file=sys.stderr)
                    return 1

            config = {
                'HOST': args.host,
                'PORT': port,
                'USERNAME': args.user,
                'PASSWORD': password,
                'PLATFORM': args.platform,
                'DATABASE': args.db_override or ""
            }

            host = args.host
            port = port
            username = args.user
            platform = args.platform

        elif profile_name:
            try:
                profile_data = load_profile(profile_name)
            except KeyError:
                print(f"ERROR: Profile '{profile_name}' not found in settings.json", file=sys.stderr)
                print("\nAvailable profiles:", file=sys.stderr)
                for pname in list_profiles():
                    print(f"  - {pname}", file=sys.stderr)
                return 1

            host = args.host or profile_data["HOST"]
            port = args.port or profile_data["PORT"]
            username = args.user or profile_data["USERNAME"]
            password = args.password or profile_data["PASSWORD"]
            platform = profile_data["PLATFORM"]

            config = profile_data.copy()
            config['HOST'] = host
            config['PORT'] = port
            config['USERNAME'] = username
            config['PASSWORD'] = password
            config['DATABASE'] = args.db_override or ""

            if not config.get('SQL_SOURCE'):
                config['SQL_SOURCE'] = os.getcwd()

            create_symbolic_links(config, prompt=False)

        else:
            print("ERROR: Either specify a profile name or provide direct connection parameters", file=sys.stderr)
            print("\nUsage:", file=sys.stderr)
            print("  iplan GONZO 52", file=sys.stderr)
            print("  iplan -S GONZO 52", file=sys.stderr)
            print("  iplan -H 10.0.0.1 -U sa 52", file=sys.stderr)
            return 1

        # Resolve database: &dbpro& if available, else profile DATABASE, else "master"
        database = args.db_override or ""
        if not database:
            # Try to get &dbpro& from options
            if not is_raw_mode(config) and profile_name and config.get('COMPANY'):
                options = Options(config)
                if options.generate_option_files():
                    dbpro = options.get_option("dbpro")
                    if dbpro:
                        database = dbpro
            if not database:
                database = config.get('DATABASE') or "master"

        # Build platform-specific SQL
        if platform == "MSSQL":
            sql = (
                f"SELECT\n"
                f"    r.session_id AS spid,\n"
                f"    r.status,\n"
                f"    r.command,\n"
                f"    DB_NAME(r.database_id) AS database_name,\n"
                f"    r.cpu_time,\n"
                f"    r.total_elapsed_time,\n"
                f"    CAST(qp.query_plan AS nvarchar(max)) AS query_plan\n"
                f"FROM sys.dm_exec_requests r\n"
                f"CROSS APPLY sys.dm_exec_query_plan(r.plan_handle) qp\n"
                f"WHERE r.session_id = {spid}"
            )
        else:
            sql = f"sp_showplan {spid}, NULL, NULL, NULL"

        # Execute
        success, output = execute_sql_native(
            host, port, username, password, database, platform,
            sql, output_file=None, echo_input=False
        )

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
