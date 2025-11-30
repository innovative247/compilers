#!/usr/bin/env python3
"""
Test database connectivity using FreeTDS.

This script can use a profile from settings.json or prompt for manual connection details.
It uses the FreeTDS `tsql` command to test connectivity, which is the most reliable way
to verify that FreeTDS is configured correctly before using it with Python scripts.

Usage:
    python test_connection.py                               # Interactive mode
    python test_connection.py SBNA                          # Test profile SBNA (positional)
    python test_connection.py --profile SBNA                # Test profile SBNA (with flag)
    python test_connection.py -H 10.0.0.1 -p 5000 -U sa     # Direct connection (prompts for password)
    python test_connection.py -H 10.0.0.1 -p 5000 -U sa --password secret --platform MSSQL
"""

import argparse
import getpass
import sys
from pathlib import Path

# Import consolidated functions from ibs_common (relative import within commands package)
from .ibs_common import (
    load_settings,
    load_profile,
    list_profiles,
    check_freetds_installed,
    test_connection
)

# Import profile saving function from setup_profile
from .setup_profile import prompt_to_save_profile


# =============================================================================
# PROFILE SELECTION
# =============================================================================

def display_profiles(settings: dict) -> dict:
    """
    Display available profiles and return them.

    Args:
        settings: Settings dictionary loaded from settings.json

    Returns:
        Dictionary of profile names to profile data
    """
    profiles = settings.get("Profiles", {})

    if not profiles:
        print("No profiles found in settings.json")
        return {}

    print()
    print("Available profiles:")
    print("-" * 60)

    for i, (profile_name, profile_data) in enumerate(profiles.items(), 1):
        platform = profile_data.get("PLATFORM", "UNKNOWN")
        host = profile_data.get("HOST", "N/A")
        port = profile_data.get("PORT", "N/A")
        username = profile_data.get("USERNAME", "N/A")

        print(f"{i}. {profile_name}")
        print(f"   Platform: {platform}")
        print(f"   Host: {host}:{port}")
        print(f"   Username: {username}")
        print()

    return profiles


def get_profile_choice(profiles: dict) -> tuple:
    """
    Prompt user to select a profile or enter manual connection details.

    Args:
        profiles: Dictionary of available profiles

    Returns:
        Tuple of (profile_name, host, port, username, password, platform, port_defaulted, platform_defaulted)
        or None if user cancels
    """
    print()
    print("Options:")
    print(f"  1-{len(profiles)}: Select a profile by number")
    print("  m: Enter connection details manually")
    print("  q: Quit")
    print()

    choice = input("Your choice: ").strip().lower()

    if choice == 'q':
        return None

    if choice == 'm':
        return get_manual_connection()

    # Try to parse as number
    try:
        profile_num = int(choice)
        if 1 <= profile_num <= len(profiles):
            profile_name = list(profiles.keys())[profile_num - 1]
            profile_data = profiles[profile_name]

            host = profile_data.get("HOST")
            port = profile_data.get("PORT")
            username = profile_data.get("USERNAME")
            password = profile_data.get("PASSWORD")
            platform = profile_data.get("PLATFORM", "SYBASE")

            # Validate required fields
            if not all([host, port, username, password]):
                print()
                print(f"ERROR: Profile '{profile_name}' is missing required connection fields.")
                print("Required: HOST, PORT, USERNAME, PASSWORD")
                return None

            # Profile values are explicit, not defaulted
            return (profile_name, host, port, username, password, platform, False, False)
        else:
            print(f"Invalid choice: {choice}")
            return None

    except ValueError:
        print(f"Invalid choice: {choice}")
        return None


def get_manual_connection() -> tuple:
    """
    Prompt user for manual connection details.

    Returns:
        Tuple of (profile_name, host, port, username, password, platform, port_defaulted, platform_defaulted)
        or None if user cancels
    """
    print()
    print("Enter connection details:")
    print("-" * 60)

    try:
        host = input("Host (IP address or hostname): ").strip()
        if not host:
            print("Host is required")
            return None

        port_str = input("Port (default: 5000 for Sybase, 1433 for MSSQL): ").strip()
        port_defaulted = False
        if not port_str:
            port_str = "5000"
            port_defaulted = True

        try:
            port = int(port_str)
        except ValueError:
            print(f"Invalid port: {port_str}")
            return None

        username = input("Username: ").strip()
        if not username:
            print("Username is required")
            return None

        # Use getpass for password to hide input
        password = getpass.getpass("Password: ")
        if not password:
            print("Password is required")
            return None

        platform_input = input("Platform (SYBASE or MSSQL, default: SYBASE): ").strip().upper()
        platform_defaulted = False
        if not platform_input:
            platform = "SYBASE"
            platform_defaulted = True
        else:
            platform = platform_input

        if platform not in ["SYBASE", "MSSQL"]:
            print(f"Invalid platform: {platform}. Must be SYBASE or MSSQL.")
            return None

        return ("MANUAL", host, port, username, password, platform, port_defaulted, platform_defaulted)

    except KeyboardInterrupt:
        print()
        print("Cancelled by user")
        return None


# =============================================================================
# MAIN
# =============================================================================

def main():
    """Main entry point for connection testing."""

    # Handle command line arguments
    parser = argparse.ArgumentParser(
        description="Test database connectivity using FreeTDS.",
        epilog="""
Examples:
  test_connection                                           Interactive mode
  test_connection SBNA                                      Test profile SBNA
  test_connection --profile SBNA                            Test profile SBNA
  test_connection -H 10.0.0.1 -p 5000 -U sa -P pass         Direct connection
  test_connection -H 10.0.0.1 -p 5000 -U sa --password pass --platform MSSQL
""",
        formatter_class=argparse.RawDescriptionHelpFormatter
    )

    # Positional argument (optional) - profile name
    parser.add_argument("profile", nargs="?", help="Profile name from settings.json")

    # Named arguments
    parser.add_argument("--profile", dest="profile_flag", help="Profile name (alternative to positional)")
    parser.add_argument("-H", "--host", help="Database server host/IP")
    parser.add_argument("-p", "--port", type=int, help="Database server port")
    parser.add_argument("-U", "--user", help="Username")
    parser.add_argument("-P", "--password", help="Password (will prompt if not provided with direct connection)")
    parser.add_argument("--platform", choices=["SYBASE", "MSSQL"], default="SYBASE",
                        help="Database platform (default: SYBASE)")

    args = parser.parse_args()

    print()
    print("=" * 60)
    print("  IBS Compilers - Database Connection Tester")
    print("=" * 60)
    print()
    print("This script tests database connectivity using FreeTDS.")
    print()

    # Check if FreeTDS (tsql) is installed before proceeding
    if not check_freetds_installed():
        print("ERROR: FreeTDS (tsql) not found in PATH.")
        print()
        print("Please ensure FreeTDS is installed:")
        print("  Windows: Run install\\bootstrap.ps1 or install MSYS2 FreeTDS manually")
        print()
        print("The tsql command should be available at: C:\\msys64\\ucrt64\\bin\\tsql.exe")
        print("Make sure C:\\msys64\\ucrt64\\bin is in your PATH.")
        print()
        return 1

    # Determine profile name (positional takes precedence, then --profile flag)
    profile_name = args.profile or args.profile_flag

    # Check if user is attempting direct connection (any direct param provided)
    attempting_direct = args.host or args.port or args.user

    # Direct connection mode - use provided parameters
    if attempting_direct:
        # Validate required params
        missing = []
        if not args.host:
            missing.append("-H/--host")
        if not args.user:
            missing.append("-U/--user")

        if missing:
            print(f"ERROR: Missing required parameters: {', '.join(missing)}")
            print()
            print("For direct connection, provide at minimum: -H <host> -U <user>")
            print("Port defaults to 5000 (SYBASE) or 1433 (MSSQL) if not specified.")
            print()
            print("Examples:")
            print("  test_connection -H 54.235.236.130 -U sbn0")
            print("  test_connection -H 54.235.236.130 -U sbn0 -p 5000")
            print("  test_connection -H 10.0.0.1 -U sa --platform MSSQL")
            return 1

        print("Using direct connection parameters...")
        print()

        # Use default port if not provided
        port = args.port
        port_defaulted = False
        if not port:
            port = 1433 if args.platform == "MSSQL" else 5000
            port_defaulted = True
            print(f"Using default port: {port} ({args.platform})")
            print()

        # Check if platform was explicitly provided
        # argparse sets default to "SYBASE", so we need to check sys.argv to know if user specified it
        platform_defaulted = '--platform' not in sys.argv

        # Prompt for password if not provided
        password = args.password
        if not password:
            password = getpass.getpass("Password: ")
            if not password:
                print("ERROR: Password is required")
                return 1

        # Test the connection using ibs_common.test_connection()
        success, msg = test_connection(args.host, port, args.user, password, args.platform)

        print()
        print("=" * 60)
        if success:
            print(f"SUCCESS: Connected to {args.host}:{port} as {args.user}")
        else:
            print(f"FAILED: {msg}")
        print("=" * 60)

        if success:
            # Offer to save successful direct connections to settings.json
            prompt_to_save_profile(args.host, port, args.user, password, args.platform)
            print()
            return 0
        else:
            return 1

    # Profile mode - load from settings.json and test
    elif profile_name:
        # Load settings
        try:
            profile_data = load_profile(profile_name)
        except KeyError:
            print(f"ERROR: Profile '{profile_name}' not found in settings.json")
            print()
            print("Available profiles:")
            for pname in list_profiles():
                print(f"  - {pname}")
            return 1
        except Exception as e:
            print(f"ERROR: Could not load profile: {e}")
            return 1

        host = profile_data["HOST"]
        port = profile_data["PORT"]
        username = profile_data["USERNAME"]
        password = profile_data["PASSWORD"]
        platform = profile_data["PLATFORM"]

        print(f"Using profile: {profile_name}")

        # Test the connection using ibs_common.test_connection()
        success, msg = test_connection(host, port, username, password, platform)

        print()
        print("=" * 60)
        if success:
            print(f"SUCCESS: Connected to {host}:{port} as {username}")
        else:
            print(f"FAILED: {msg}")
        print("=" * 60)
        print()

        return 0 if success else 1

    # Interactive mode - no profile or direct params specified
    else:
        # Interactive mode - loop until successful connection or user quits
        while True:
            # Reload settings on each iteration to pick up any changes
            try:
                settings = load_settings()
            except Exception as e:
                print(f"ERROR: Could not load settings: {e}")
                return 1

            profiles = settings.get("Profiles", {})

            # Show profiles and get user choice
            display_profiles(settings)

            connection_info = get_profile_choice(profiles)

            if not connection_info:
                print("Connection test cancelled.")
                return 0

            profile_name, host, port, username, password, platform, port_defaulted, platform_defaulted = connection_info

            if profile_name != "MANUAL":
                print(f"Using profile: {profile_name}")

            # Test the connection using ibs_common.test_connection()
            success, msg = test_connection(host, port, username, password, platform)

            print()
            print("=" * 60)
            if success:
                print(f"SUCCESS: Connected to {host}:{port} as {username}")
            else:
                print(f"FAILED: {msg}")
            print("=" * 60)

            # If this was a successful manual entry, offer to save it
            if success and profile_name == "MANUAL":
                prompt_to_save_profile(host, port, username, password, platform)

            # Ask if user wants to test another connection
            print()
            another = input("Would you like to test another connection? [Y/n]: ").strip().lower()

            if another in ['n', 'no']:
                print()
                return 0 if success else 1

            # Default to 'yes' - loop back to show profiles again
            print()


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print()
        print()
        print("Interrupted by user")
        sys.exit(1)
