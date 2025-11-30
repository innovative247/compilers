#!/usr/bin/env python3
"""
test_options.py: Test the Options class and placeholder resolution.

This script tests the soft-compiler Options class by loading option files
and resolving a placeholder. It reports whether the options file was built
or reused from cache, and shows the resolved value.

Usage:
    test_options GONZO "&users&"
    test_options GONZO "&dbpro&" --force-rebuild
    test_options GONZO "&users&" --forceRebuild
"""

import argparse
import logging
import sys

# Import from ibs_common (relative import within commands package)
from .ibs_common import (
    load_profile,
    list_profiles,
    Options
)


def main():
    """Main entry point for test_options."""

    # Show help if no arguments provided
    if len(sys.argv) == 1:
        sys.argv.append('-h')

    parser = argparse.ArgumentParser(
        description="Test the Options class and placeholder resolution.",
        epilog="""
Examples:
  test_options GONZO "&users&"
  test_options GONZO "&dbpro&" --force-rebuild
  test_options GONZO "&users&" --forceRebuild

Notes:
  - Loads option files from {PATH_APPEND}\\CSS\\Setup
  - Reports whether options file was rebuilt or reused from cache
  - Shows the location of the cached options file
  - Displays the input placeholder and resolved value
""",
        formatter_class=argparse.RawDescriptionHelpFormatter
    )

    # Positional arguments
    parser.add_argument("profile", help="Profile name from settings.json")
    parser.add_argument("placeholder", help="Placeholder to resolve (e.g., '&users&')")

    # Optional flags
    parser.add_argument("--force-rebuild", "--forceRebuild", action="store_true",
                        help="Force rebuild of options file (ignore cache)")
    parser.add_argument("--debug", action="store_true", help="Enable debug output")

    args = parser.parse_args()

    # Configure logging
    if args.debug:
        logging.basicConfig(level=logging.DEBUG, format='[DEBUG] %(message)s')
    else:
        logging.basicConfig(level=logging.ERROR)

    try:
        # Load the profile
        try:
            profile_data = load_profile(args.profile)
        except KeyError:
            print(f"ERROR: Profile '{args.profile}' not found in settings.json", file=sys.stderr)
            print("\nAvailable profiles:", file=sys.stderr)
            for pname in list_profiles():
                print(f"  - {pname}", file=sys.stderr)
            return 1

        # Build config dictionary for Options class
        config = profile_data.copy()
        config['PROFILE_NAME'] = args.profile

        # Create Options instance
        options = Options(config)

        # Generate/load option files
        success = options.generate_option_files(force_rebuild=args.force_rebuild)

        if not success:
            # Options class already prints detailed error with search context
            return 1

        # Determine if cache was rebuilt or reused
        if options.was_rebuilt():
            cache_status = "REBUILT (cache was stale or missing)"
        else:
            cache_status = "REUSED (loaded from cache)"

        # Get cache location
        cache_location = options.get_cache_filepath()

        # Resolve the placeholder
        input_value = args.placeholder
        resolved_value = options.replace_options(input_value)

        # Output results
        print(f"Options file: {cache_status}")
        print(f"Cache location: {cache_location}")
        print(f"Input:    {input_value}")
        print(f"Resolved: {resolved_value}")

        return 0

    except KeyboardInterrupt:
        print("\nInterrupted by user", file=sys.stderr)
        return 130

    except Exception as e:
        print(f"ERROR: {e}", file=sys.stderr)
        if args.debug:
            import traceback
            traceback.print_exc()
        return 1


if __name__ == "__main__":
    sys.exit(main())
