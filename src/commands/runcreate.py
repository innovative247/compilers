"""
runcreate.py: Python script to orchestrate multi-step database processes from a master script.

This script replaces the C# runcreate project, acting as a master dispatcher
that reads a script file and calls other Python tools (e.g., runsql, i_run_upgrade).
"""

import argparse
import sys
from pathlib import Path

# Import shared functions from the common module (relative import within commands package)
from .ibs_common import (
    get_config,
    verify_database_tools,
    replace_placeholders,
    find_file,
    setup_logging
)
import logging

# Import the main functions of other Python tools that runcreate might call (relative imports)
from . import runsql
from . import i_run_upgrade
from . import eopt
from . import eloc
from . import compile_msg
from . import compile_required_fields

def parse_and_dispatch(script_path: Path, config: dict):
    """
    Parses the master script file and calls the appropriate Python tool.
    """
    logging.info(f"Parsing master script: {script_path}")
    
    script_config = config.copy()

    with script_path.open('r') as f:
        for line_num, line in enumerate(f, 1):
            line = line.strip()
            if not line or line.startswith('#'):
                continue

            processed_line = replace_placeholders(line, script_config)
            if not processed_line.strip():
                continue

            parts = processed_line.split(maxsplit=1)
            command_keyword = parts[0].lower()
            command_args_str = parts[1] if len(parts) > 1 else ""
            command_args_list = command_args_str.split()

            logging.info(f"Line {line_num}: Dispatching command '{command_keyword}' with args: {command_args_str}")

            try:
                if command_keyword == "runsql":
                    runsql.main(args_list=command_args_list, existing_config=script_config)
                elif command_keyword == "runcreate":
                    main(args_list=[command_args_str], existing_config=script_config)
                elif command_keyword == "i_run_upgrade":
                    i_run_upgrade.main(args_list=command_args_list, existing_config=script_config)
                elif command_keyword == "import_options":
                    eopt.main(args_list=command_args_list, existing_config=script_config)
                elif command_keyword == "create_tbl_locations":
                    eloc.main(args_list=command_args_list, existing_config=script_config)
                elif command_keyword == "install_msg":
                    compile_msg.main(args_list=command_args_list, existing_config=script_config)
                elif command_keyword == "install_required_fields":
                    compile_required_fields.main(args_list=command_args_list, existing_config=script_config)
                else:
                    logging.warning(f"Line {line_num}: Unknown command '{command_keyword}' skipped.")
            except SystemExit as e:
                if e.code != 0:
                    logging.error(f"Sub-command '{command_keyword}' failed with exit code {e.code} at line {line_num}.")
                    sys.exit(e.code)
            except Exception as e:
                logging.error(f"Error executing command '{command_keyword}' at line {line_num}: {e}")
                sys.exit(1)

def main(args_list=None, existing_config=None):
    if args_list is None:
        args_list = sys.argv[1:]

    parser = argparse.ArgumentParser(description="Orchestrate multi-step database processes from a master script.")
    parser.add_argument("script_file", help="Path to the master script file (e.g., create_all.sct).")
    parser.add_argument("profile_or_server", nargs='?', help="Configuration profile or server name (optional).")
    parser.add_argument("-S", "--server", help="Server name (overrides profile and positional server).")
    parser.add_argument("-U", "--username", help="Database username (overrides profile).")
    parser.add_argument("-P", "--password", help="Database password (overrides profile).")
    parser.add_argument("-D", "--database", help="Database name (overrides profile).")
    parser.add_argument("-O", "--outfile", help="Output file for logging (overrides default logging).")
    
    args = parser.parse_args(args_list)

    config = get_config(args_list=args_list, existing_config=existing_config)
    setup_logging(config)
    verify_database_tools(config)

    logging.info(f"Starting runcreate for script '{args.script_file}' using profile for server '{config.get('DSQUERY')}'...")

    # Find the master script file.
    script_path = Path(args.script_file)
    found_script_path_str = find_file(str(script_path), config)
    if not found_script_path_str:
        logging.error(f"Master script file '{args.script_file}' not found.")
        sys.exit(1)

    try:
        parse_and_dispatch(Path(found_script_path_str), config)
    except Exception as e:
        logging.error(f"An error occurred during master script execution: {e}")
        sys.exit(1)

    logging.info("runcreate DONE.")

if __name__ == "__main__":
    main()