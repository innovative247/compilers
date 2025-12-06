"""
eloc.py: Python script for interactive editing and compilation of table location definitions.

This script replaces the C# eloc project and the compile_table_locations logic, allowing
users to edit table location files and then compile them into the database.
"""

import argparse
import sys
from pathlib import Path
import subprocess
import tempfile
import re

# Import shared functions from the common module (relative import within commands package)
from .ibs_common import (
    get_config,
    verify_database_tools,
    execute_sql,
    execute_bcp,
    console_yes_no,
    replace_placeholders,
    launch_editor,
    setup_logging
)
import logging

def parse_and_clean_locations_file(source_path: Path, config: dict, temp_dir: Path):
    """
    Reads table_locations.dat, replaces placeholders, and creates a
    temporary file suitable for BCP.
    """
    temp_locations_path = temp_dir / "table_locations.tmp"

    logging.info(f"Processing table locations file: {source_path}")
    try:
        with source_path.open('r') as infile, temp_locations_path.open('w', newline='\n') as outfile:
            for line in infile:
                line = line.strip()
                if not line or not line.startswith('->'):
                    continue
                
                try:
                    first_ampersand_idx = line.find('&', 2)
                    if first_ampersand_idx == -1:
                        logging.warning(f"Malformed line (no first '&'): {line}")
                        continue
                        
                    tbl_name = line[2:first_ampersand_idx].replace('\t', ' ').strip()
                    
                    second_ampersand_idx = line.find('&', first_ampersand_idx + 1)
                    if second_ampersand_idx == -1:
                        logging.warning(f"Malformed line (no second '&'): {line}")
                        continue
                    
                    opt_name_raw = line[first_ampersand_idx : second_ampersand_idx + 1]
                    
                    db_name = replace_placeholders(opt_name_raw, config, remove_ampersands=True)
                    full_name = f"{db_name}..{tbl_name}"
                    
                    outfile.write(f"{tbl_name}\t{opt_name_raw.strip('&')}\t{db_name}\t{full_name}\n")
                    
                except Exception as e:
                    logging.warning(f"Error parsing line '{line}': {e}. Skipping.")
                
    except Exception as e:
        logging.error(f"Error processing table locations file {source_path}: {e}")
        raise

    return temp_locations_path


def main(args_list=None):
    if args_list is None:
        args_list = sys.argv[1:]
        
    parser = argparse.ArgumentParser(description="Interactively edit and compile table location definitions.")
    parser.add_argument("profile_or_server", nargs='?', help="Configuration profile or server name (optional).")
    parser.add_argument("-S", "--server", help="Server name (overrides profile and positional server).")
    parser.add_argument("-U", "--username", help="Database username (overrides profile).")
    parser.add_argument("-P", "--password", help="Database password (overrides profile).")
    parser.add_argument("-O", "--outfile", help="Output file for logging (overrides default logging).")
    
    args = parser.parse_args(args_list)

    config = get_config(args_list=args_list)
    setup_logging(config)
    verify_database_tools(config)

    logging.info(f"Starting eloc using profile for server '{config.get('DSQUERY')}'...")

    locations_file = Path(config.get('SQL_SOURCE')) / 'dat' / 'table_locations.dat'
    
    if not locations_file.exists():
        logging.error(f"Table locations file missing: {locations_file}")
        sys.exit(1)

    logging.info(f"Launching editor for {locations_file}...")
    launch_editor(locations_file)
    
    if not console_yes_no(f"Compile table locations into {config.get('DSQUERY')}?"):
        logging.info("Compilation cancelled by user.")
        sys.exit(0)

    logging.info("Proceeding with compile_table_locations logic...")
    
    with tempfile.TemporaryDirectory() as tmp_dir_name:
        tmp_dir = Path(tmp_dir_name)
        temp_bcp_file = parse_and_clean_locations_file(locations_file, config, tmp_dir)
        
        logging.info("Truncating table ibs..table_locations...")
        execute_sql(config, "TRUNCATE TABLE table_locations", database="ibs")
        
        logging.info(f"Importing {temp_bcp_file.name} into ibs..table_locations...")
        if not execute_bcp(config, "ibs..table_locations", "in", temp_bcp_file):
            logging.error("BCP for table locations failed. Aborting.")
            sys.exit(1)
        
    logging.info("eloc DONE.")

if __name__ == "__main__":
    main()