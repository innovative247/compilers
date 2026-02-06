#!/usr/bin/env python3
"""
iplanext.py: Extended plan viewer - process info + SQL text + execution plan for a SPID.

Replaces the Unix shell script iplanext from IBS_Unix/Command_Library/iplanext.
Works cross-platform (Windows/Ubuntu/Mac) against both Sybase and MSSQL.

Usage:
    iplanext PROFILE SPID
    iplanext -S PROFILE SPID
    iplanext -H host -p port -U user [-P password] [--platform SYBASE|MSSQL] SPID
"""

import argparse
import getpass
import logging
import os
import sys
import tempfile
from datetime import datetime

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
    """Main entry point for iplanext."""
    # Handle --version / -v
    if len(sys.argv) > 1 and sys.argv[1] in ('--version', '-v'):
        from .version import __version__
        print(f"{Fore.GREEN}Innovative247 Compilers {__version__}{Style.RESET_ALL}")
        sys.exit(0)

    # Check for updates (once per day)
    from .version_check import check_for_updates
    if not check_for_updates("iplanext"):
        sys.exit(0)

    # Show help if no arguments provided
    if len(sys.argv) == 1:
        sys.argv.append('-h')

    from .version import __version__
    parser = argparse.ArgumentParser(
        description="Extended plan viewer: process info + SQL text + execution plan for a SPID.",
        epilog="""
Examples:
  iplanext GONZO 52
  iplanext -S GONZO 52
  iplanext -H 10.0.0.1 -p 5000 -U sa -P pass 52
  iplanext -H 10.0.0.1 -p 1433 -U sa --platform MSSQL 52
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
        print("Usage: iplanext PROFILE SPID", file=sys.stderr)
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
            print("  iplanext GONZO 52", file=sys.stderr)
            print("  iplanext -S GONZO 52", file=sys.stderr)
            print("  iplanext -H 10.0.0.1 -U sa 52", file=sys.stderr)
            return 1

        # Resolve database: &dbpro& if available, else profile DATABASE, else "master"
        database = args.db_override or ""
        if not database:
            if not is_raw_mode(config) and profile_name and config.get('COMPANY'):
                options = Options(config)
                if options.generate_option_files():
                    dbpro = options.get_option("dbpro")
                    if dbpro:
                        database = dbpro
            if not database:
                database = config.get('DATABASE') or "master"

        # Build output file path (cwd first, fall back to temp dir)
        # Matches original: PLANTRACE.<server>.<spid>.<pid> (unique per invocation)
        server_name = profile_name.upper() if profile_name else host
        filename = f"PLANTRACE.{server_name}.{spid}.{os.getpid()}"
        tmp_path = os.path.join(os.getcwd(), filename)
        try:
            with open(tmp_path, 'w', encoding='utf-8') as f:
                pass  # test write access
            os.remove(tmp_path)
        except OSError:
            tmp_path = os.path.join(tempfile.gettempdir(), filename)

        # Print file path first (matches original: echo $tmpname)
        print(tmp_path)

        # Open file for appending, write each section as it completes
        # (matches original: date > $tmpname, then >> $tmpname for each query)
        with open(tmp_path, 'w', encoding='utf-8') as f:
            # Timestamp (matches original: date > $tmpname)
            timestamp = datetime.now().strftime("%a %b %d %H:%M:%S %Z %Y").strip()
            f.write(timestamp + "\n")

            # Query 1: Process Info
            if platform == "MSSQL":
                sql_proc = (
                    f"SELECT spid,\n"
                    f"    RTRIM(substring(status, 1, 5)) AS status,\n"
                    f"    RTRIM(loginame) AS login,\n"
                    f"    RTRIM(substring(hostname, 1, 10)) AS hostname,\n"
                    f"    blocked AS blk,\n"
                    f"    RTRIM(substring(cmd, 1, 4)) AS comm,\n"
                    f"    cpu, physical_io AS io,\n"
                    f"    RTRIM(substring(hostprocess, 1, 5)) AS host,\n"
                    f"    RTRIM(program_name) AS proce\n"
                    f"FROM master..sysprocesses\n"
                    f"WHERE spid = {spid}"
                )
            else:
                sql_proc = (
                    f"SELECT spid,\n"
                    f"    substring(status, 1, 5) AS status,\n"
                    f"    suser_name(suid) AS login,\n"
                    f"    substring(hostname, 1, 10) AS hostname,\n"
                    f"    blocked AS blk,\n"
                    f"    substring(cmd, 1, 4) AS comm,\n"
                    f"    cpu, physical_io AS io,\n"
                    f"    substring(hostprocess, 1, 5) AS host,\n"
                    f"    object_name(id, dbid) AS proce\n"
                    f"FROM master..sysprocesses\n"
                    f"WHERE spid = {spid}"
                )

            success, output = execute_sql_native(
                host, port, username, password, database, platform,
                sql_proc, output_file=None, echo_input=False
            )
            # Check if we got actual data rows (not just column headers)
            has_data = False
            if success and output and output.strip():
                lines = [l for l in output.strip().splitlines() if l.strip()]
                # More than 1 line means we have header + data rows
                has_data = len(lines) > 1

            if has_data:
                f.write(output + "\n")
            else:
                msg = f"There is no active server process for the specified spid value '{spid}'.  Possibly the user connection has terminated."
                f.write(msg + "\n")
                if not success and output:
                    f.write(output + "\n")
                f.write("(return status = 1)\n")

            # Query 2: SQL Text
            if platform == "MSSQL":
                sql_text = (
                    f"SELECT\n"
                    f"    SUBSTRING(st.text, (r.statement_start_offset/2) + 1,\n"
                    f"        ((CASE r.statement_end_offset\n"
                    f"            WHEN -1 THEN DATALENGTH(st.text)\n"
                    f"            ELSE r.statement_end_offset\n"
                    f"        END - r.statement_start_offset)/2) + 1) AS current_statement,\n"
                    f"    st.text AS full_text\n"
                    f"FROM sys.dm_exec_requests r\n"
                    f"CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) st\n"
                    f"WHERE r.session_id = {spid}"
                )
            else:
                sql_text = f"dbcc traceon(3604)\ngo\ndbcc sqltext({spid})"

            success, output = execute_sql_native(
                host, port, username, password, database, platform,
                sql_text, output_file=None, echo_input=False
            )
            if success and output and output.strip():
                lines = [l for l in output.strip().splitlines() if l.strip()]
                if platform != "MSSQL":
                    # Sybase: filter for lines containing "SQL" (matches original: | grep SQL)
                    filtered = "\n".join(line for line in lines if "SQL" in line)
                    if filtered:
                        f.write(filtered + "\n")
                elif len(lines) > 1:
                    # MSSQL: only write if we have data rows (not just headers)
                    f.write(output + "\n")
            else:
                if not success and output:
                    f.write(output + "\n")

            # Query 3: Execution Plan
            if platform == "MSSQL":
                sql_plan = (
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
                sql_plan = f"sp_showplan {spid},NULL,NULL,NULL"

            success, output = execute_sql_native(
                host, port, username, password, database, platform,
                sql_plan, output_file=None, echo_input=False,
                include_info_messages=True
            )
            if success and output and output.strip():
                lines = [l for l in output.strip().splitlines() if l.strip()]
                # For MSSQL SELECT queries, skip header-only results
                if platform != "MSSQL" or len(lines) > 1:
                    f.write(output + "\n")
            else:
                if not success and output:
                    f.write(output + "\n")

        # Display the file (matches original: cat $tmpname)
        with open(tmp_path, 'r', encoding='utf-8') as f:
            print(f.read(), end='')

        # Print file path at the end (matches original: echo $tmpname)
        print(tmp_path)

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
