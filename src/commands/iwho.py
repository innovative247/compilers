#!/usr/bin/env python3
"""
iwho.py: Show process list from master..sysprocesses.

Standalone replacement for the Sybase-only `z` stored procedure.
Works cross-platform (Windows/Ubuntu/Mac) against both Sybase and MSSQL.

Usage:
    iwho PROFILE              # all processes
    iwho PROFILE sbn0         # exact username filter
    iwho PROFILE sbn%         # username wildcard filter
    iwho PROFILE 52           # SPID filter (numeric, no %)
    iwho PROFILE 1234%        # username wildcard (has %, even though starts with digit)
    iwho -S PROFILE [filter]
    iwho -H host -p port -U user [--platform SYBASE|MSSQL] [filter]
"""

import argparse
import getpass
import logging
import sys
import time

from .ibs_common import (
    load_profile,
    list_profiles,
    execute_sql_native,
    Icons, Fore, Style,
    print_error, print_info,
)


def parse_filter(filter_arg):
    """Parse the optional filter argument.

    Returns (filter_type, value) where filter_type is one of:
        'none'           - no filter
        'spid'           - numeric SPID
        'username_exact' - exact username match
        'username_like'  - username LIKE pattern (contains %)
    """
    if filter_arg is None:
        return ('none', None)
    if '%' in filter_arg:
        return ('username_like', filter_arg)
    if filter_arg.isdigit():
        return ('spid', int(filter_arg))
    return ('username_exact', filter_arg)


def build_sql(platform, filter_type, filter_value):
    """Build the sysprocesses query for the given platform and filter."""
    if platform == "MSSQL":
        base = (
            "SELECT spid,\n"
            "    RTRIM(substring(status, 1, 5)) AS status,\n"
            "    RTRIM(loginame) AS login,\n"
            "    RTRIM(substring(hostname, 1, 10)) AS hostname,\n"
            "    blocked AS blk,\n"
            "    RTRIM(substring(cmd, 1, 4)) AS comm,\n"
            "    cpu, physical_io AS io,\n"
            "    RTRIM(substring(hostprocess, 1, 5)) AS host,\n"
            "    RTRIM(program_name) AS proce\n"
            "FROM master..sysprocesses"
        )
        if filter_type == 'spid':
            return f"{base}\nWHERE spid = {filter_value}"
        elif filter_type == 'username_exact':
            return f"{base}\nWHERE loginame = '{filter_value}'"
        elif filter_type == 'username_like':
            return f"{base}\nWHERE loginame LIKE '{filter_value}'"
        else:
            return base
    else:
        # Sybase
        base = (
            "SELECT spid,\n"
            "    substring(status, 1, 5) AS status,\n"
            "    suser_name(suid) AS login,\n"
            "    substring(hostname, 1, 10) AS hostname,\n"
            "    blocked AS blk,\n"
            "    substring(cmd, 1, 4) AS comm,\n"
            "    cpu, physical_io AS io,\n"
            "    substring(hostprocess, 1, 5) AS host,\n"
            "    object_name(id, dbid) AS proce\n"
            "FROM master..sysprocesses"
        )
        if filter_type == 'spid':
            return f"{base}\nWHERE spid = {filter_value}"
        elif filter_type == 'username_exact':
            return f"{base}\nWHERE suser_name(suid) = '{filter_value}'"
        elif filter_type == 'username_like':
            return f"{base}\nWHERE suser_name(suid) LIKE '{filter_value}'"
        else:
            return base


def main():
    """Main entry point for iwho."""
    # Handle --version / -v
    if len(sys.argv) > 1 and sys.argv[1] in ('--version', '-v'):
        from .version import __version__
        print(f"{Fore.GREEN}Innovative247 Compilers {__version__}{Style.RESET_ALL}")
        sys.exit(0)

    # Check for updates (once per day)
    from .version_check import check_for_updates
    if not check_for_updates("iwho"):
        sys.exit(0)

    # Show help if no arguments provided
    if len(sys.argv) == 1:
        sys.argv.append('-h')

    from .version import __version__
    parser = argparse.ArgumentParser(
        description="Show process list from master..sysprocesses.",
        epilog="""
Examples:
  iwho GONZO                # all processes
  iwho GONZO sbn0           # exact username filter
  iwho GONZO sbn%           # username wildcard filter
  iwho GONZO 52             # SPID filter
  iwho -S GONZO sbn%        # -S flag syntax
  iwho -H 10.0.0.1 -p 1433 -U sa --platform MSSQL
""",
        formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument('--version', '-v', action='version', version=f'{__version__}')

    # Positional arguments
    parser.add_argument("profile", nargs="?", help="Profile name from settings.json")
    parser.add_argument("filter", nargs="?", help="Optional filter: username, username%% wildcard, or numeric SPID")

    # Optional overrides
    parser.add_argument("-S", "--server", dest="server_profile",
                        help="Profile/server name (takes precedence over positional profile)")
    parser.add_argument("-H", "--host", help="Database server host/IP (overrides profile)")
    parser.add_argument("-p", "--port", type=int, help="Database server port (overrides profile)")
    parser.add_argument("-U", "--user", help="Username (overrides profile)")
    parser.add_argument("-P", "--password", help="Password (overrides profile, will prompt if not provided)")
    parser.add_argument("--platform", choices=["SYBASE", "MSSQL"], default="SYBASE",
                        help="Database platform (default: SYBASE)")
    parser.add_argument("--debug", action="store_true", help="Enable debug output")
    parser.add_argument("-t", "--timer", type=int, metavar="SEC",
                        help="Repeat every SEC seconds after completion")

    args = parser.parse_args()

    # Configure logging
    if args.debug:
        logging.basicConfig(level=logging.DEBUG, format='[DEBUG] %(message)s')
    else:
        logging.basicConfig(level=logging.ERROR)

    # Parse and validate filter
    filter_type, filter_value = parse_filter(args.filter)
    if args.filter and "'" in args.filter:
        print_error("Filter value must not contain single quotes")
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

            host = args.host
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

        else:
            print("ERROR: Either specify a profile name or provide direct connection parameters", file=sys.stderr)
            print("\nUsage:", file=sys.stderr)
            print("  iwho GONZO", file=sys.stderr)
            print("  iwho GONZO sbn0", file=sys.stderr)
            print("  iwho -S GONZO sbn%", file=sys.stderr)
            print("  iwho -H 10.0.0.1 -U sa", file=sys.stderr)
            return 1

        # Always query master
        database = "master"

        while True:
            # Build SQL
            sql = build_sql(platform, filter_type, filter_value)

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

            if not args.timer:
                break
            print()
            time.sleep(args.timer)

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
