"""
compile_required_fields.py: Python script to compile and install required field definitions.

This script replaces the C# compile_required_fields project, handling:
- Checking for existence of required field flat files.
- Backing up existing required fields tables.
- Clearing work tables.
- Bulk importing new definitions from flat files.
- Executing a stored procedure to finalize installation.
"""

import argparse
import sys
from pathlib import Path
import datetime
import shutil
import tempfile

# Import shared functions from the common module (relative import within commands package)
from .ibs_common import (
    get_config,
    verify_database_tools,
    execute_sql,
    execute_bcp,
    execute_sql_procedure,
    console_yes_no,
    replace_placeholders,
    setup_logging
)
import logging

def main(args_list=None):
    if args_list is None:
        args_list = sys.argv[1:]

    parser = argparse.ArgumentParser(description="Compile and install required field definitions.")
    parser.add_argument("profile_or_server", nargs='?', help="Configuration profile or server name (optional).")
    parser.add_argument("-S", "--server", help="Server name (overrides profile and positional server).")
    parser.add_argument("-U", "--username", help="Database username (overrides profile).")
    parser.add_argument("-P", "--password", help="Database password (overrides profile).")
    parser.add_argument("-O", "--outfile", help="Output file for logging (overrides default logging).")
    
    args = parser.parse_args(args_list)

    config = get_config(args_list=args_list)
    setup_logging(config)
    verify_database_tools(config)

    logging.info(f"Starting compile_required_fields using profile for server '{config.get('DSQUERY')}'...")

    if config.get('DSQUERY', '').upper() == "GONZO":
        logging.error("You may NOT compile required fields into GONZO!")
        sys.exit(1)

    # Paths for required field files
    main_dir = Path(config.get('PATH_APPEND')) / 'setup' / 'css'
    bup_dir = Path(config.get('PATH_APPEND')) / 'bup' / f"{config.get('DSQUERY')}_css"
    bup_dir.mkdir(parents=True, exist_ok=True) # Ensure backup directory exists

    required_files_map = {
        "required_fields": "i_required_fields",
        "required_fields_dtl": "i_required_fields_dtl"
    }

    # 1. Check for existence of source files
    logging.info("Validating source files...")
    all_files_exist = True
    source_paths = {}
    for key, _ in required_files_map.items():
        file_path = main_dir / f"css.{key}"
        if not file_path.exists():
            logging.error(f"Required file missing: {file_path}")
            all_files_exist = False
        source_paths[key] = file_path
    
    if not all_files_exist:
        sys.exit(1)

    # 2. Backup existing tables
    logging.info("Making backup files for existing required fields...")
    timestamp = datetime.datetime.now().strftime("%Y%m%d%H%M%S")
    
    for key, table_name_placeholder in required_files_map.items():
        processed_table_name = replace_placeholders(f"&{table_name_placeholder}&", config)
        bup_file_path = bup_dir / f"css.{key}.{timestamp}.bcp"
        
        if not execute_bcp(config, processed_table_name, "out", bup_file_path):
            logging.error(f"Failed to backup table {processed_table_name}.")
            sys.exit(1)

    # 3. Clear work tables
    logging.info("Clearing Required Fields work tables...")
    for key, table_name_placeholder in required_files_map.items():
        processed_work_table_name = replace_placeholders(f"&w#{table_name_placeholder}&", config)
        execute_sql(config, f"DELETE FROM {processed_work_table_name}") # C# used DELETE, not TRUNCATE

    # 4. BCP IN the new files into the work tables
    logging.info("Importing Required Fields flat files into database...")
    with tempfile.TemporaryDirectory() as tmp_dir_name:
        tmp_dir = Path(tmp_dir_name)
        for key, table_name_placeholder in required_files_map.items():
            src_file = source_paths[key]
            dest_table_name = replace_placeholders(f"&w#{table_name_placeholder}&", config)

            tmp_bcp_file = tmp_dir / f"css.{key}.tmp"
            with src_file.open('r') as infile, tmp_bcp_file.open('w', newline='\n') as outfile:
                for line in infile:
                    outfile.write(line.strip() + '\n')

            if not execute_bcp(config, dest_table_name, "in", tmp_bcp_file):
                logging.error(f"Failed to import {src_file} into {dest_table_name}.")
                sys.exit(1)
                
    # 5. Run the final installation stored procedure
    logging.info("Installing Required Fields...")
    execute_sql_procedure(config, replace_placeholders("&dbpro&..i_required_fields_install", config))

    logging.info("compile_required_fields DONE.")

if __name__ == "__main__":
    main()