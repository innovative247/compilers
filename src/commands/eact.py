"""
eact.py: Python script for interactive editing and compilation of database actions.

This script replaces the C# eact project and the compile_actions logic, allowing
users to edit action definition files and then compile them into the database.
"""

import argparse
import sys
from pathlib import Path
import subprocess
import tempfile
import shutil
import re

# Import shared functions from the common module (relative import within commands package)
from .ibs_common import (
    get_config,
    verify_database_tools,
    execute_sql,
    execute_bcp,
    execute_sql_procedure,
    console_yes_no,
    replace_placeholders,
    launch_editor,
    setup_logging
)
import logging

def parse_and_clean_action_files(source_path_header: Path, source_path_detail: Path, config: dict, temp_dir: Path):
    """
    Reads the source action files, replaces placeholders, and writes
    cleaned temp files for BCP.
    """
    temp_header_path = temp_dir / "actions.tmp"
    temp_detail_path = temp_dir / "actions_dtl.tmp"

    # Process actions.dat (header)
    logging.info(f"Processing action header file: {source_path_header}")
    try:
        with source_path_header.open('r') as infile, temp_header_path.open('w', newline='\n') as outfile:
            i = 0
            for line in infile:
                line = line.strip()
                if not line:
                    continue
                
                processed_line = replace_placeholders(line, config)
                
                if processed_line.startswith('&') or processed_line.startswith(':>'):
                    if processed_line.startswith(':>'):
                        i += 1
                        outfile.write(f"{i}\t{processed_line}\n")
                
    except Exception as e:
        logging.error(f"Error processing action header file {source_path_header}: {e}")
        raise

    # Process actions_dtl.dat (detail)
    logging.info(f"Processing action detail file: {source_path_detail}")
    try:
        with source_path_detail.open('r') as infile, temp_detail_path.open('w', newline='\n') as outfile:
            for line in infile:
                line = line.strip()
                if not line:
                    continue
                
                processed_line = replace_placeholders(line, config)

                if len(processed_line) > 2 and (processed_line.startswith('&') or processed_line.startswith(':>')):
                    try:
                        part1 = processed_line[2:6].strip()
                        part2 = processed_line[7:10].strip()
                        part3 = processed_line[11:14].strip()
                        part4 = processed_line[15:20].strip()
                        part5 = processed_line[21:24].strip()
                        part6 = processed_line[24:].strip()
                        outfile.write(f"{part1}\t{part2}\t{part3}\t{part4}\t{part5}\t{part6}\n")
                    except IndexError:
                        logging.warning(f"Skipping malformed detail line: {processed_line}")
                
    except Exception as e:
        logging.error(f"Error processing action detail file {source_path_detail}: {e}")
        raise

    return temp_header_path, temp_detail_path


def main(args_list=None):
    if args_list is None:
        args_list = sys.argv[1:]

    parser = argparse.ArgumentParser(description="Interactively edit and compile database actions.")
    parser.add_argument("profile_or_server", nargs='?', help="Configuration profile or server name (optional).")
    parser.add_argument("-S", "--server", help="Server name (overrides profile and positional server).")
    parser.add_argument("-U", "--username", help="Database username (overrides profile).")
    parser.add_argument("-P", "--password", help="Database password (overrides profile).")
    parser.add_argument("-O", "--outfile", help="Output file for logging (overrides default logging).")
    
    args = parser.parse_args(args_list)

    config = get_config(args_list=args_list)
    setup_logging(config)
    verify_database_tools(config)

    logging.info(f"Starting eact using profile for server '{config.get('DSQUERY')}'...")

    actions_header_file = Path(config.get('SQL_SOURCE')) / 'dat' / 'actions.dat'
    actions_detail_file = Path(config.get('SQL_SOURCE')) / 'dat' / 'actions_dtl.dat'
    
    if not actions_header_file.exists():
        logging.error(f"Action header file missing: {actions_header_file}")
        sys.exit(1)
    if not actions_detail_file.exists():
        logging.error(f"Action detail file missing: {actions_detail_file}")
        sys.exit(1)

    logging.info(f"Launching editor for {actions_header_file} and {actions_detail_file}...")
    launch_editor(actions_header_file)
    launch_editor(actions_detail_file)
    
    if not console_yes_no(f"Compile actions into {config.get('DSQUERY')}?"):
        logging.info("Compilation cancelled by user.")
        sys.exit(0)

    logging.info("Proceeding with compile_actions logic...")
    
    with tempfile.TemporaryDirectory() as tmp_dir_name:
        tmp_dir = Path(tmp_dir_name)
        temp_header, temp_detail = parse_and_clean_action_files(
            actions_header_file, actions_detail_file, config, tmp_dir
        )
        
        logging.info("Truncating work tables w_actions and w_actions_dtl...")
        execute_sql(config, replace_placeholders("TRUNCATE TABLE &w#actions&", config))
        execute_sql(config, replace_placeholders("TRUNCATE TABLE &w#actions_dtl&", config))
        
        logging.info(f"Importing {temp_header.name} into &w#actions&...")
        if not execute_bcp(config, replace_placeholders("&w#actions&", config), "in", temp_header):
            logging.error("BCP for action header failed. Aborting.")
            sys.exit(1)

        logging.info(f"Importing {temp_detail.name} into &w#actions_dtl&...")
        if not execute_bcp(config, replace_placeholders("&w#actions_dtl&", config), "in", temp_detail):
            logging.error("BCP for action detail failed. Aborting.")
            sys.exit(1)

        logging.info("Executing final compilation stored procedure ba_compile_actions...")
        execute_sql_procedure(config, replace_placeholders("&dbpro&..ba_compile_actions", config))
    
    logging.info("eact DONE.")

if __name__ == "__main__":
    main()