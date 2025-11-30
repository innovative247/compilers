"""
compile_msg.py: Python script to compile and install messages into the database.

This script replaces the C# compile_msg project, handling:
- Checking for existence of message flat files.
- Backing up existing message tables.
- Truncating work tables.
- Bulk importing new messages from flat files.
- Executing stored procedures to finalize message compilation.
"""

import argparse
import sys
from pathlib import Path
import datetime
import shutil
import tempfile
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
    setup_logging
)
import logging

def main(args_list=None):
    if args_list is None:
        args_list = sys.argv[1:]

    parser = argparse.ArgumentParser(description="Compile and install messages into the database.")
    parser.add_argument("profile_or_server", nargs='?', help="Configuration profile or server name (optional).")
    parser.add_argument("-S", "--server", help="Server name (overrides profile and positional server).")
    parser.add_argument("-U", "--username", help="Database username (overrides profile).")
    parser.add_argument("-P", "--password", help="Database password (overrides profile).")
    parser.add_argument("-O", "--outfile", help="Output file for logging (overrides default logging).")
    
    args = parser.parse_args(args_list)

    config = get_config(args_list=args_list)
    setup_logging(config)
    verify_database_tools(config)

    logging.info(f"Starting compile_msg using profile for server '{config.get('DSQUERY')}'...")

    if config.get('DSQUERY', '').upper() == "GONZO":
        logging.error("You may NOT install messages into GONZO!")
        sys.exit(1)

    # Define message types and their corresponding files/tables
    message_types = {
        "ibs": {"files": [".ibs_msg", ".ibs_msgrp"], "tables": ["ibs_messages", "ibs_message_groups"]},
        "jam": {"files": [".jam_msg", ".jam_msgrp"], "tables": ["jam_messages", "jam_message_groups"]},
        "sqr": {"files": [".sqr_msg", ".sqr_msgrp"], "tables": ["sqr_messages", "sqr_message_groups"]},
        "sql": {"files": [".sql_msg", ".sql_msgrp"], "tables": ["sql_messages", "sql_message_groups"]},
        "gui": {"files": [".gui_msg", ".gui_msgrp"], "tables": ["gui_messages", "gui_message_groups"]},
    }
    
    # Paths for message files
    main_dir = Path(config.get('PATH_APPEND')) / 'setup' / 'css'
    bup_dir = Path(config.get('PATH_APPEND')) / 'bup' / f"{config.get('DSQUERY')}_css"
    bup_dir.mkdir(parents=True, exist_ok=True) # Ensure backup directory exists

    # 1. Check for existence of source files
    logging.info("Validating source files...")
    all_files_exist = True
    for msg_type, data in message_types.items():
        for file_ext in data["files"]:
            file_path = main_dir / f"css{file_ext}"
            if not file_path.exists():
                logging.error(f"Message file missing: {file_path}")
                all_files_exist = False
    if not all_files_exist:
        sys.exit(1)

    # 2. Preserve translated messages (e.g., in gui_messages_save)
    logging.info("Preserving translated messages into table gui_messages_save")
    execute_sql_procedure(config, "ba_compile_gui_messages_save")

    # 3. Backup existing message tables using BCP OUT
    logging.info("Making backup files for existing messages...")
    timestamp = datetime.datetime.now().strftime("%Y%m%d%H%M%S")
    
    for msg_type, data in message_types.items():
        for i, table_name in enumerate(data["tables"]):
            processed_table_name = replace_placeholders(f"&{table_name}&", config)
            bup_file_path = bup_dir / f"css.{msg_type}_{i}.{timestamp}.bcp"
            
            if not execute_bcp(config, processed_table_name, "out", bup_file_path):
                logging.error(f"Failed to backup table {processed_table_name}.")
                sys.exit(1)

            if not bup_file_path.exists() or bup_file_path.stat().st_size == 0:
                logging.warning(f"Backup file {bup_file_path} is missing or 0 bytes.")

    # 4. Clear temporary message tables (w#...)
    logging.info("Clearing temporary message tables...")
    for msg_type, data in message_types.items():
        for table_name in data["tables"]:
            processed_work_table_name = replace_placeholders(f"&w#{table_name}&", config)
            execute_sql(config, f"TRUNCATE TABLE {processed_work_table_name}")

    # 5. BCP IN the new flat files into the temporary tables
    logging.info("Importing message flat files into database...")
    with tempfile.TemporaryDirectory() as tmp_dir_name:
        tmp_dir = Path(tmp_dir_name)
        for msg_type, data in message_types.items():
            for i, file_ext in enumerate(data["files"]):
                src_file = main_dir / f"css{file_ext}"
                dest_table_name = replace_placeholders(f"&w#{data['tables'][i]}&", config)
                tmp_bcp_file = tmp_dir / src_file.name
                shutil.copy(src_file, tmp_bcp_file)

                if not execute_bcp(config, dest_table_name, "in", tmp_bcp_file):
                    logging.error(f"Failed to import {src_file} into {dest_table_name}.")
                    sys.exit(1)
                
    # 6. Run the final compile stored procedures
    logging.info("Running final message compilation stored procedures...")
    execute_sql_procedure(config, replace_placeholders("&dbpro&..i_compile_messages", config))
    execute_sql_procedure(config, replace_placeholders("&dbpro&..i_compile_jam_messages", config))
    execute_sql_procedure(config, replace_placeholders("&dbpro&..i_compile_jrw_messages", config))

    logging.info("compile_msg DONE.")

if __name__ == "__main__":
    main()