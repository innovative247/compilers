"""
check_tables.py: Python script to identify and manage 'old' tables in a database.

This script replaces the C# check_tables project,
logging information about tables ending with '_old' and
optionally generating a script to drop them.
"""

import argparse
import sys
import re
from pathlib import Path

# Import shared functions from the common module (relative import within commands package)
from .ibs_common import (
    get_config,
    verify_database_tools,
    execute_sql,
    console_yes_no,
    setup_logging
)
import logging

def clean_database_list(raw_list):
    """
    Cleans and filters the raw database list returned from SQL query.
    Replicates the C# CleanDatabaseList logic.
    """
    cleaned = []
    for db in raw_list:
        db_name = db.strip()
        # Simplified filtering based on common system databases and patterns
        if db_name and db_name not in ["physical_db", "master", "tempdb", "model", "msdb"] \
           and not db_name.startswith("-") and not db_name.startswith("syb") \
           and not db_name[0].isdigit():
            cleaned.append(db_name)
    return cleaned

def clean_table_list(raw_list):
    """
    Cleans and filters the raw table list returned from SQL query.
    Replicates the C# CleanTableList logic.
    """
    cleaned = []
    for tbl in raw_list:
        tbl_name = tbl.strip()
        if tbl_name and tbl_name not in ["name"] \
           and not tbl_name.startswith("-") and not tbl_name[0].isdigit():
            cleaned.append(tbl_name)
    return cleaned

def extract_row_count(config, db_name, table_name):
    """
    Executes sp_spaceused and parses the output to get the row count.
    Replicates the C# ExtractRowCount logic.
    """
    # This is highly dependent on SQL Server/Sybase sp_spaceused output format
    # which can vary. This is a simplified placeholder.
    try:
        results = execute_sql(config, f"EXEC {db_name}..sp_spaceused '{table_name}'", fetch_results=True)
        # This will need careful parsing based on actual sp_spaceused output
        if results and len(results) > 0 and len(results[0]) > 2: # A common format
             return results[0][2] # e.g. third column 'rows'
        return "N/A"
    except Exception as e:
        logging.warning(f"Could not get row count for {db_name}..{table_name}: {e}")
        return "ERROR"

def main(args_list=None):
    if args_list is None:
        args_list = sys.argv[1:]

    parser = argparse.ArgumentParser(description="Check for and manage 'old' tables in databases.")
    parser.add_argument("profile_or_server", nargs='?', help="Configuration profile or server name (optional).")
    parser.add_argument("-S", "--server", help="Server name (overrides profile and positional server).")
    parser.add_argument("-U", "--username", help="Database username (overrides profile).")
    parser.add_argument("-P", "--password", help="Database password (overrides profile).")
    parser.add_argument("-O", "--outfile", help="Output file for logging (overrides default logging).")
    
    args = parser.parse_args(args_list)

    config = get_config(args_list=args_list)
    setup_logging(config)
    verify_database_tools(config)

    logging.info(f"Starting check_tables using profile for server '{config.get('DSQUERY')}'...")

    drop_tables_script = []

    # Get list of databases from ibs..table_locations
    db_query_result = execute_sql(config, "select distinct physical_db from ibs..table_locations", fetch_results=True)
    lstDatabases = clean_database_list([row[0] for row in db_query_result])

    if not lstDatabases:
        logging.warning("No databases found in ibs..table_locations to process.")
        sys.exit(0)

    for db_name in lstDatabases:
        config['DATABASE'] = db_name # Set current database in config for execute_sql
        logging.info(f"*************************************************************")
        logging.info(f"Checking database {db_name}")

        old_tables_query = f"select name from {db_name}..sysobjects where type='U' and name like '%_old'"
        old_tables_result = execute_sql(config, old_tables_query, fetch_results=True)
        lstOldTables = clean_table_list([row[0] for row in old_tables_result])
        
        if not lstOldTables:
            logging.info("No old tables found.")
            continue
            
        drop_tables_script.append(f"USE {db_name}")
        drop_tables_script.append("GO")

        base_tables = set() # To store unique base table names
        for old_tbl in lstOldTables:
            drop_tables_script.append(f"TRUNCATE TABLE {old_tbl}")
            drop_tables_script.append("GO")
            drop_tables_script.append(f"DROP TABLE {old_tbl}")
            drop_tables_script.append("GO")

            # Extract base table name (e.g., 'my_table_old' -> 'my_table')
            match = re.match(r"^(.*?)_x*old$", old_tbl)
            if match:
                base_tables.add(match.groups()[0])
            else: # Fallback for just '_old'
                base_tables.add(old_tbl) # Fallback if no match

        # Log sizes of base and old tables
        for base_tbl in sorted(list(base_tables)):
            logging.info(f"\n{base_tbl}")
            logging.info(f"  Row count: {extract_row_count(config, db_name, base_tbl)}")
            
            for old_tbl in lstOldTables:
                # Check if this old_tbl corresponds to the current base_tbl
                if old_tbl.startswith(base_tbl) and ('_old' in old_tbl):
                    logging.info(f"  {old_tbl}")
                    logging.info(f"    Row count: {extract_row_count(config, db_name, old_tbl)}")

    # Offer to create drop script
    if drop_tables_script and console_yes_no("Create 'drop table' script?"):
        script_file_path = Path("drop_old_tables.sql") # Current directory or configurable
        try:
            with open(script_file_path, "w") as f:
                f.write("\n".join(drop_tables_script))
            logging.info(f"drop_old_tables.sql created at {script_file_path}.")
        except IOError as e:
            logging.error(f"Failed to write drop_old_tables.sql: {e}")

    logging.info("check_tables DONE.")

if __name__ == "__main__":
    main()