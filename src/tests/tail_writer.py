"""
Helper script to test 'tail -f' functionality.

Terminal 1: python -m tests.tail_writer
Terminal 2: tail -f tail_test_output.txt
"""

import time
from pathlib import Path

OUTPUT_FILE = Path(__file__).parent / "tail_test_output.txt"

def main():
    print(f"Writing to: {OUTPUT_FILE}")
    print("Press Ctrl+C to stop\n")

    # Clear/create the file
    OUTPUT_FILE.write_text("")

    count = 1
    try:
        while True:
            line = f"Line {count} - timestamp {time.strftime('%H:%M:%S')}\n"
            with open(OUTPUT_FILE, "a") as f:
                f.write(line)
            print(f"Wrote: {line.strip()}")
            count += 1
            time.sleep(2)
    except KeyboardInterrupt:
        print("\nStopped.")

if __name__ == "__main__":
    main()
