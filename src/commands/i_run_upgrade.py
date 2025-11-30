"""
i_run_upgrade.py: Python script to execute database upgrade scripts.

This script replaces the C# i_run_upgrade project, handling:
- Checking upgrade status against a control table.
- Parsing and executing a master upgrade script file.
- Dispatching commands to other Python tools (runsql).
- Handling SQL batches and database context changes.
"""

import argparse
import sys
import re
from pathlib import Path

# Import shared functions and other script runners (relative imports within commands package)
from .ibs_common import (
    get_config,
    verify_database_tools,
    execute_sql,
    execute_sql_procedure,
    replace_placeholders,
    find_file,
    setup_logging
)
from . import runsql # Will call the converted runsql.py script's main function
import logging

def check_upgrade_status(config: dict, upgrade_no: str):
    """
    Checks the ba_upgrades_check procedure to see if the upgrade can be run.
    Returns True if it's safe to proceed, False otherwise.
    """
    logging.info(f"Checking upgrade status for {upgrade_no}...")
    try:
        result = execute_sql_procedure(config, "ba_upgrades_check", (upgrade_no,), fetch_results=True)
        
        status_code = -1
        if result and len(result) > 0 and len(result[0]) > 0:
            status_code = result[0][0]

        if status_code == 1:
            logging.error(replace_placeholders("Upgrade control table &upgrades& does not exist or control record missing on server.", config))
            return False
        elif status_code == 2:
            logging.warning(f"Upgrade {upgrade_no} has already been run. Skipping.")
            return False
        elif status_code == 0:
            logging.info(f"Upgrade {upgrade_no} is new or can be run.")
            return True
        else:
            logging.error(f"Unknown status code from ba_upgrades_check: {status_code}. Aborting.")
            return False

    except Exception as e:
        logging.error(f"Error checking upgrade status: {e}")
        return False

def set_upgrade_end_time(config: dict, upgrade_no: str):
    """Updates the upgrades table to mark the end time of the script."""
    sql = replace_placeholders(f"UPDATE &upgrades& SET end_tm=GETDATE() WHERE upgrade_no='{upgrade_no}' AND ix=0 AND opc=1", config)
    execute_sql(config, sql)
    logging.info(f"Updated end time for upgrade {upgrade_no}.")

def parse_upgrade_script(script_path: Path, config: dict):
    """
    Parses the master upgrade script and executes commands.
    """
    logging.info(f"Parsing upgrade script: {script_path}")
    current_db = config.get('DATABASE', '')
    sql_batch = []
    script_config = config.copy()

    with script_path.open('r') as f:
        for line_num, line in enumerate(f, 1):
            line = line.strip()
            if not line or line.startswith('#'):
                continue

            processed_line = replace_placeholders(line, script_config)

            if processed_line.lower().startswith('use '):
                if sql_batch:
                    execute_sql(script_config, '\n'.join(sql_batch), database=current_db)
                    sql_batch = []

                current_db = processed_line[4:].strip()
                script_config['DATABASE'] = current_db
                logging.info(f"Line {line_num}: Switching database context to: {current_db}")

            elif 'runsql' in processed_line.lower():
                if sql_batch:
                    execute_sql(script_config, '\n'.join(sql_batch), database=current_db)
                    sql_batch = []
                
                logging.info(f"Line {line_num}: Calling runsql for: {processed_line}")
                runsql_line_parts = processed_line.replace('runsql', '').strip().split()
                runsql.main(args_list=runsql_line_parts, existing_config=script_config, database_override=current_db)
                
            elif processed_line.lower() == 'go':
                if sql_batch:
                    execute_sql(script_config, '\n'.join(sql_batch), database=current_db)
                    sql_batch = []
            
            elif processed_line.lower().startswith('sp_renametoold'):
                 if sql_batch:
                    execute_sql(script_config, '\n'.join(sql_batch), database=current_db)
                    sql_batch = []
                 execute_sql(script_config, processed_line, database=current_db)

            else:
                sql_batch.append(processed_line)
        
        if sql_batch:
            execute_sql(script_config, '\n'.join(sql_batch), database=current_db)

def main(args_list=None, existing_config=None):
    if args_list is None:
        args_list = sys.argv[1:]

    parser = argparse.ArgumentParser(description="Execute database upgrade scripts.")
    parser.add_argument("upgrade_no", help="The upgrade number to run.")
    parser.add_argument("command", help="Path to the master upgrade script file.")
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

    logging.info(f"Starting i_run_upgrade for upgrade {args.upgrade_no} using profile for server '{config.get('DSQUERY')}'...")

    config['upgrade_no'] = args.upgrade_no
    config['command'] = args.command

    if not check_upgrade_status(config, args.upgrade_no):
        sys.exit(0)

    script_path_str = find_file(args.command, config)
    if not script_path_str:
        logging.error(f"Upgrade script file '{args.command}' not found.")
        sys.exit(1)
    script_path = Path(script_path_str)

    try:
        parse_upgrade_script(script_path, config)
    except Exception as e:
        logging.error(f"An error occurred during script execution: {e}")
        sys.exit(1)

    set_upgrade_end_time(config, args.upgrade_no)
    
    logging.info(f"Upgrade {args.upgrade_no} DONE.")

if __name__ == "__main__":
    main()