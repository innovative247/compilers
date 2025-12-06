"""
eopt.py: Python script for interactive editing, merging, and compilation of configuration options.

This script replaces the C# eopt project and the compile_options logic, providing
a workflow for managing and deploying database configuration options.
"""

import argparse
import sys
from pathlib import Path
import subprocess
import tempfile
import re
import json

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

def load_options_file(file_path: Path):
    """
    Loads an options file into a list of lines, ignoring comments and empty lines.
    Returns an empty list if file does not exist.
    """
    if not file_path.exists():
        return []
    with file_path.open('r') as f:
        return [line.strip() for line in f if line.strip() and not line.strip().startswith('#')]

def save_options_file(file_path: Path, options_list: list):
    """Saves a list of options to a file."""
    try:
        with file_path.open('w', newline='\n') as f:
            for line in options_list:
                f.write(line + '\n')
        logging.info(f"Saved options to {file_path}")
    except IOError as e:
        logging.error(f"Error saving options to {file_path}: {e}")
        raise

def extract_option_key(option_line: str):
    """Extracts the option key from a line (e.g., 'KEY=VALUE' -> 'KEY')."""
    parts = option_line.split('=', 1)
    return parts[0].strip() if parts else ""

def find_new_options(source_options: list, target_options: list):
    """Finds options in source_options that are not present (by key) in target_options."""
    target_keys = {extract_option_key(opt) for opt in target_options if opt}
    new_options = [opt for opt in source_options if opt and extract_option_key(opt) not in target_keys]
    return new_options

def run_compile_options(config: dict):
    """
    Implements the logic from the C# compile_options project:
    Merges various option files, BCPs them into the database, and runs a final SP.
    """
    logging.info("Starting import of options into database...")
    
    opt_file_sql_type = Path(config.get('SQL_SOURCE')) / 'setup' / 'css' / f"options.{config.get('PLATFORM').lower()}"
    opt_file_company = Path(config.get('SQL_SOURCE')) / 'setup' / 'css' / 'options.company'
    opt_file_server = Path(config.get('SQL_SOURCE')) / 'setup' / 'css' / f"options.{config.get('DSQUERY')}"

    tmp_opt_file_sql = load_options_file(opt_file_sql_type)
    tmp_opt_file_company = load_options_file(opt_file_company)
    tmp_opt_file_server = load_options_file(opt_file_server)

    final_options_dict = {}
    
    def add_options_to_dict(options_list, current_dict):
        for opt_line in options_list:
            key = extract_option_key(opt_line)
            if key:
                current_dict[key] = opt_line
    
    add_options_to_dict(tmp_opt_file_sql, final_options_dict)
    add_options_to_dict(tmp_opt_file_company, final_options_dict)
    add_options_to_dict(tmp_opt_file_server, final_options_dict)
    
    _arr_options = list(final_options_dict.values())

    if not _arr_options:
        logging.warning("No options found to import.")
        return

    with tempfile.TemporaryDirectory() as tmp_dir_name:
        opt_file_final = Path(tmp_dir_name) / "options_final.tmp"
        save_options_file(opt_file_final, _arr_options)

        processed_work_table = replace_placeholders("&w#options&", config)
        logging.info(f"Clearing work table {processed_work_table}...")
        execute_sql(config, f"DELETE FROM {processed_work_table}")

        logging.info(f"Importing {opt_file_final.name} into {processed_work_table}...")
        if not execute_bcp(config, processed_work_table, "in", opt_file_final):
            logging.error("BCP for options failed. Aborting.")
            sys.exit(1)

    logging.info("Executing stored procedure i_import_options...")
    execute_sql_procedure(config, replace_placeholders("&dbpro&..i_import_options", config))

    logging.info("Import of options completed.")


def main(args_list=None):
    if args_list is None:
        args_list = sys.argv[1:]

    parser = argparse.ArgumentParser(description="Interactively manage and compile database configuration options.")
    parser.add_argument("profile_or_server", nargs='?', help="Configuration profile or server name (optional).")
    parser.add_argument("-d", "--developer", action="store_true", 
                        help="Run in developer mode to merge new options from options.def.")
    parser.add_argument("-S", "--server", help="Server name (overrides profile and positional server).")
    parser.add_argument("-U", "--username", help="Database username (overrides profile).")
    parser.add_argument("-P", "--password", help="Database password (overrides profile).")
    parser.add_argument("-O", "--outfile", help="Output file for logging (overrides default logging).")
    
    args = parser.parse_args(args_list)

    config = get_config(args_list=args_list)
    setup_logging(config)
    verify_database_tools(config)

    logging.info(f"Starting eopt using profile for server '{config.get('DSQUERY')}'...")

    opt_file_default = Path(config.get('SQL_SOURCE')) / 'setup' / 'css' / 'options.def'
    opt_file_company = Path(config.get('SQL_SOURCE')) / 'setup' / 'css' / 'options.company'
    opt_file_server = Path(config.get('SQL_SOURCE')) / 'setup' / 'css' / f"options.{config.get('DSQUERY')}"

    if args.developer:
        logging.info(f"Developer mode: Launching editor for default options: {opt_file_default}")
        launch_editor(opt_file_default)
        
        def_opts = load_options_file(opt_file_default)
        
        if console_yes_no(f"Upgrade company options ({opt_file_company}) with new defaults?"):
            comp_opts = load_options_file(opt_file_company)
            new_opts = find_new_options(def_opts, comp_opts)
            if new_opts:
                logging.info(f"{len(new_opts)} new options found for company file.")
                comp_opts.extend(new_opts)
                save_options_file(opt_file_company, comp_opts)
                launch_editor(opt_file_company)
            else:
                logging.info("No new options found for company file.")

        if console_yes_no(f"Upgrade server options ({opt_file_server}) with new defaults?"):
            serv_opts = load_options_file(opt_file_server)
            new_opts = find_new_options(def_opts, serv_opts)
            if new_opts:
                logging.info(f"{len(new_opts)} new options found for server file.")
                serv_opts.extend(new_opts)
                save_options_file(opt_file_server, serv_opts)
                launch_editor(opt_file_server)
            else:
                logging.info("No new options found for server file.")

    else:
        if console_yes_no(f"Edit server options ({opt_file_server})?"):
            launch_editor(opt_file_server)

        if console_yes_no(f"Edit company options ({opt_file_company})?"):
            launch_editor(opt_file_company)

    if console_yes_no(f"Import options into {config.get('DSQUERY')}?"):
        run_compile_options(config)
    else:
        logging.info("Option compilation cancelled by user.")

    logging.info("eopt DONE.")

if __name__ == "__main__":
    main()
