"""
tail.py: Display the last N lines of a file, optionally following for new content.

Mimics Unix 'tail' command:
  tail <file>        - Show last 10 lines
  tail -n 20 <file>  - Show last 20 lines
  tail -f <file>     - Follow file for new content
"""

import argparse
import time
import os
import sys
from pathlib import Path
from collections import deque


def tail_lines(file_path: Path, num_lines: int) -> list:
    """
    Read and return the last N lines of a file.
    Uses a deque for memory-efficient reading of large files.
    """
    try:
        with open(file_path, 'r', encoding='utf-8', errors='replace') as f:
            return list(deque(f, maxlen=num_lines))
    except FileNotFoundError:
        print(f"tail: cannot open '{file_path}' for reading: No such file", file=sys.stderr)
        sys.exit(1)
    except PermissionError:
        print(f"tail: cannot open '{file_path}' for reading: Permission denied", file=sys.stderr)
        sys.exit(1)
    except Exception as e:
        print(f"tail: error reading '{file_path}': {e}", file=sys.stderr)
        sys.exit(1)


def follow(file_path: Path):
    """
    Generator function that yields new lines from a file as it grows.
    """
    with open(file_path, 'r', encoding='utf-8', errors='replace') as f:
        # Seek to the end of the file initially
        f.seek(0, os.SEEK_END)

        while True:
            line = f.readline()
            if not line:
                time.sleep(0.1)  # Sleep briefly if no new data
                continue
            yield line


def main(args_list=None):
    if args_list is None:
        args_list = sys.argv[1:]

    parser = argparse.ArgumentParser(
        description="Display the last part of a file.",
        usage="tail [-n NUM] [-f] <file>"
    )
    parser.add_argument("file", help="The file to display.")
    parser.add_argument("-n", "--lines", type=int, default=10,
                        help="Number of lines to display (default: 10).")
    parser.add_argument("-f", "--follow", action="store_true",
                        help="Output appended data as the file grows.")

    args = parser.parse_args(args_list)

    file_path = Path(args.file)
    if not file_path.is_absolute():
        file_path = Path(os.getcwd()) / file_path

    # Show last N lines
    lines = tail_lines(file_path, args.lines)
    for line in lines:
        sys.stdout.write(line)
    sys.stdout.flush()

    # If follow mode, continue watching for new content
    if args.follow:
        try:
            for line in follow(file_path):
                sys.stdout.write(line)
                sys.stdout.flush()
        except KeyboardInterrupt:
            sys.exit(0)
        except Exception as e:
            print(f"tail: error following '{file_path}': {e}", file=sys.stderr)
            sys.exit(1)


if __name__ == "__main__":
    main()