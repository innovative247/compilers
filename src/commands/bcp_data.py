"""
bcp_data.py: Python script to perform bulk copy operations on SQL Server/Sybase databases.

This script replaces the C# bcp_data project and orchestrates
BCP IN/OUT operations for all user tables in user databases.
"""

import argparse
import sys
import os
from pathlib import Path

# Import shared functions from the common module (relative import within commands package)
from .ibs_common import (
    get_config,
    verify_database_tools,
    execute_sql,
    execute_bcp,
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
    # Simplified logic; actual C# logic was more complex
    # filtering out system DBs, numeric names, etc.
    for db in raw_list:
        db_name = db.strip()
        if db_name and db_name not in ["name", "master", "tempdb", "model", "msdb", "mon_db"] \
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

def main(args_list=None):
    if args_list is None:
        args_list = sys.argv[1:]

    parser = argparse.ArgumentParser(description="Perform bulk copy (BCP) operations on database tables.")
    parser.add_argument("direction", choices=["in", "out"], help="Direction of BCP operation (in/out).")
    parser.add_argument("profile_or_server", nargs='?', help="Configuration profile or server name (optional).")
    parser.add_argument("-S", "--server", help="Server name (overrides profile and positional server).")
    parser.add_argument("-U", "--username", help="Database username (overrides profile).")
    parser.add_argument("-P", "--password", help="Database password (overrides profile).")
    parser.add_argument("-O", "--outfile", help="Output file for logging (overrides default logging).")
    parser.add_argument("--truncate-tables", action="store_true", help="Truncate tables before BCP IN.")
    parser.add_argument("--drop-indexes", action="store_true", help="Drop indexes before BCP IN.")
    parser.add_argument("--drop-triggers", action="store_true", help="Drop triggers before BCP IN.")
    
    args = parser.parse_args(args_list)

    config = get_config(args_list=args_list)
    setup_logging(config)
    verify_database_tools(config) # Verify BCP tool availability

    logging.info(f"Starting bcp_data in '{args.direction}' mode for server '{config.get('DSQUERY')}'...")

    if args.direction == "in" and config.get('DSQUERY', '').upper() == "GONZO":
        logging.error("You cannot BCP into GONZO!")
        sys.exit(1)
        
    if not console_yes_no(f"Are you sure you want to BCP all data {args.direction} of {config.get('DSQUERY')}:"): 
        logging.info("BCP operation cancelled by user.")
        sys.exit(0)

    bcp_directory = Path(os.getcwd()) / "bcp"
    bcp_directory.mkdir(exist_ok=True)

    # Get list of databases
    db_query_result = execute_sql(config, "select name from master..sysdatabases", fetch_results=True)
    lstDatabases = clean_database_list([row[0] for row in db_query_result])
    
    if not lstDatabases:
        logging.warning("No databases found to process.")
        sys.exit(0)

    for db_name in lstDatabases:
        config['DATABASE'] = db_name # Set current database in config
        logging.info(f"*************************************************************")
        logging.info(f"BCP database {db_name}")

        # Get list of tables
        tbl_query_result = execute_sql(config, "select name from sysobjects where type='U'", database=db_name, fetch_results=True)
        lstTables = clean_table_list([row[0] for row in tbl_query_result])

        if not lstTables:
            logging.info(f"No tables found in database {db_name}.")
            continue

        for table_name in lstTables:
            # Handle indexes and triggers if BCP IN
            if args.direction == "in":
                # Placeholder for index/trigger drop logic
                pass 

            # Handle truncate if BCP IN
            if args.direction == "in" and args.truncate_tables:
                logging.info(f"Truncating table {db_name}..{table_name}")
                execute_sql(config, f"truncate table {table_name}", database=db_name)

            # Perform BCP
            bcp_file_path = bcp_directory / f"{table_name}.txt"
            logging.info(f"Bulk Copy {args.direction} {db_name}..{table_name} to {bcp_file_path}")
            if not execute_bcp(config, f"{db_name}..{table_name}", args.direction, bcp_file_path):
                logging.error(f"BCP failed for table {db_name}..{table_name}. Aborting.")
                sys.exit(1)

            # Recreate indexes/triggers if dropped (after BCP IN)
            if args.direction == "in":
                # Placeholder for index/trigger recreate logic
                pass

    logging.info("bcp_data DONE.")

if __name__ == "__main__":
    main()