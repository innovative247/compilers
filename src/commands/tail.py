"""
tail.py: Python script to continuously monitor and display new lines appended to a file.

This script replaces the C# tail project, mimicking the Unix 'tail -f' command.
"""

import argparse
import time
import os
import sys
from pathlib import Path

# Import shared functions for logging setup (relative import within commands package)
from .ibs_common import setup_logging, get_config
import logging

def follow(file_path: Path):
    """
    Generator function that yields new lines from a file as it grows.
    """
    try:
        f = open(file_path, 'r')
        # Seek to the end of the file initially
        f.seek(0, os.SEEK_END)
        
        while True:
            line = f.readline()
            if not line:
                time.sleep(0.1) # Sleep briefly if no new data
                continue
            yield line
    except FileNotFoundError:
        logging.error(f"Error: File not found at {file_path}")
        sys.exit(1)
    except Exception as e:
        logging.error(f"An error occurred while trying to follow file {file_path}: {e}")
        sys.exit(1)

def main(args_list=None):
    if args_list is None:
        args_list = sys.argv[1:]

    parser = argparse.ArgumentParser(description="Continuously display new lines appended to a file (like 'tail -f').")
    parser.add_argument("file", help="The path to the file to tail.")
    parser.add_argument("profile_or_server", nargs='?', help="Configuration profile or server name (optional).")
    parser.add_argument("-O", "--outfile", help="Output file for logging (overrides default logging).")
    
    args = parser.parse_args(args_list)

    # Minimal config to setup logging, tail doesn't use many config options
    config = get_config(args_list=args_list) # Pass args_list for profile/server determination
    setup_logging(config)

    file_to_tail = Path(args.file)
    if not file_to_tail.is_absolute():
        file_to_tail = Path(os.getcwd()) / file_to_tail
        
    logging.info(f"Tailing file: {file_to_tail} (Press Ctrl+C to exit)")
    
    try:
        for line in follow(file_to_tail):
            sys.stdout.write(line)
            sys.stdout.flush()
            
    except KeyboardInterrupt:
        logging.info("\nExiting tail utility.")
        sys.exit(0)
    except Exception as e:
        logging.error(f"An unexpected error occurred: {e}")
        sys.exit(1)

if __name__ == "__main__":
    main()