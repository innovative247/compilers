"""
set_profile.py: Interactive profile setup wizard for IBS Compilers

This script guides users through creating and managing profiles in settings.json.
It also automatically synchronizes these profiles to the system's freetds.conf
file, allowing native tools like 'tsql' to use the same profile names.

Usage:
    set_profile
"""

import sys
import os
import json
import getpass
import re
from pathlib import Path
import subprocess
import platform

# Import from ibs_common for profile management
from .ibs_common import (
    find_settings_file,
    load_settings as ibs_load_settings,
    load_profile,
    save_profile,
    list_profiles as ibs_list_profiles
)


# =============================================================================
# PROFILE VALIDATION AND SAVING
# =============================================================================

def validate_profile_name(name: str) -> bool:
    """
    Validate that a profile name contains only safe characters.

    Args:
        name: The profile name to validate

    Returns:
        True if valid, False otherwise
    """
    # Only allow alphanumeric characters and underscores
    # No spaces, no special characters
    return bool(re.match(r'^[a-zA-Z0-9_]+$', name))


def save_profile_to_settings(profile_name: str, host: str, port: int,
                             username: str, password: str, platform_type: str) -> bool:
    """
    Save a new profile to settings.json using ibs_common functions.

    Args:
        profile_name: Name for the new profile
        host: Database host
        port: Database port
        username: Database username
        password: Database password
        platform_type: Database platform (SYBASE or MSSQL)

    Returns:
        True if successfully saved, False otherwise
    """
    profile_data = {
        "COMPANY": 101,
        "DEFAULT_LANGUAGE": 1,
        "PLATFORM": platform_type,
        "HOST": host,
        "PORT": port,
        "USERNAME": username,
        "PASSWORD": password,
        "SQL_SOURCE": None
    }

    return save_profile(profile_name, profile_data)


def prompt_to_save_profile(host: str, port: int, username: str,
                           password: str, platform_type: str) -> None:
    """
    Prompt user to save a successful manual connection to settings.json.

    Args:
        host: Database host
        port: Database port
        username: Database username
        password: Database password
        platform_type: Database platform
    """
    print()
    save_choice = input("Connection successful! Would you like to save this profile to settings.json? [Y/n]: ").strip().lower()

    if save_choice in ['n', 'no']:
        return

    # Load existing profiles once to check for duplicates
    try:
        existing_profiles = ibs_list_profiles()
    except Exception as e:
        print(f"ERROR: Could not load existing profiles: {e}")
        return

    settings_file = find_settings_file()

    # Default to 'yes' for empty input or 'y'/'yes'
    while True:
        print()
        profile_name = input("Enter a profile name (alphanumeric and underscores only): ").strip()

        # Empty input = cancel/quit
        if not profile_name:
            print("Profile not saved.")
            return

        # Validate characters
        if not validate_profile_name(profile_name):
            print("Invalid name. Use only letters, numbers, and underscores.")
            continue

        # Check if profile already exists
        if profile_name in existing_profiles:
            overwrite = input(f"Profile '{profile_name}' already exists. Overwrite? [y/N] ").strip().lower()

            if overwrite in ['y', 'yes']:
                # User wants to overwrite - break out and save
                break
            else:
                # User declined to overwrite - show existing names and ask for new name
                print()
                print("Existing profiles:")
                for name in sorted(existing_profiles):
                    print(f"  - {name}")
                print()
                print("Enter a different name, or leave blank to cancel.")
                continue
        else:
            # New valid name - break out and save
            break

    # Save the profile
    if save_profile_to_settings(profile_name, host, port, username, password, platform_type):
        print()
        print(f"SUCCESS: Profile '{profile_name}' saved to settings.json")
        print(f"Location: {settings_file}")
    else:
        print()
        print("FAILED: Could not save profile to settings.json")


# =============================================================================
# DISPLAY HELPERS
# =============================================================================

def print_header(text):
    """Print formatted header"""
    print("\n" + "=" * 70)
    print(f"  {text}")
    print("=" * 70 + "\n")


def print_success(text):
    """Print success message"""
    print(f"[OK] {text}")


def print_error(text):
    """Print error message"""
    print(f"[ERROR] {text}")


def load_settings():
    """Load existing settings.json or create new"""
    # Use find_settings_file from ibs_common (returns src/settings.json by default)
    settings_path = find_settings_file()

    if settings_path.exists():
        try:
            with open(settings_path, 'r') as f:
                settings = json.load(f)
                print_success(f"Loaded existing settings from: {settings_path}")
                return settings, settings_path
        except json.JSONDecodeError as e:
            print_error(f"Invalid JSON in settings file: {e}")
            print("Starting with empty settings...")
            return {"Profiles": {}}, settings_path
        except Exception as e:
            print_error(f"Could not read settings file: {e}")
            return {"Profiles": {}}, settings_path
    else:
        print(f"[INFO] Creating new settings file: {settings_path}")
        return {"Profiles": {}}, settings_path


def save_settings(settings, settings_path):
    """Save settings.json"""
    try:
        with open(settings_path, 'w') as f:
            json.dump(settings, f, indent=2)
        print_success(f"Settings saved to: {settings_path}")
        return True
    except Exception as e:
        print_error(f"Failed to save settings: {e}")
        return False


def get_freetds_conf_path():
    """
    Gets the platform-specific path to freetds.conf.
    Returns None if the OS is not supported or the path is not found.
    """
    system = platform.system()
    if system == "Windows":
        # Path as defined in bootstrap.ps1 for the MSYS2 installation
        return Path("C:/msys64/ucrt64/etc/freetds.conf")
    elif system in ["Linux", "Darwin"]:
        # Common paths for Linux/macOS
        paths_to_check = [
            Path("/etc/freetds/freetds.conf"),
            Path("/usr/local/etc/freetds.conf"),
            Path("/etc/freetds.conf"),
        ]
        for path in paths_to_check:
            if path.exists():
                return path
    # Unsupported OS or path not found
    return None


def update_freetds_conf(settings):
    """
    Dynamically updates the freetds.conf file from settings.json profiles.
    This enables native tools like 'tsql' to use profile names.
    """
    system = platform.system()
    if system != "Windows":
        print(f"\n[INFO] Skipping freetds.conf update on non-Windows OS ({system}).")
        return

    conf_path = get_freetds_conf_path()
    if not conf_path:
        print("\n[ERROR] Could not find freetds.conf, skipping update.")
        return

    if not conf_path.parent.exists():
        print(f"\n[ERROR] freetds.conf directory not found at {conf_path.parent}, skipping update.")
        print("  Please ensure MSYS2 and FreeTDS are installed correctly via bootstrap.ps1.")
        return

    print(f"\nSynchronizing profiles to {conf_path}...")

    # Backup existing file
    if conf_path.exists():
        try:
            # Use a timestamp for the backup file name
            backup_path = conf_path.with_suffix(f".conf.backup_{Path(conf_path).stat().st_mtime:.0f}")
            if backup_path.exists():
                backup_path.unlink() # Remove old backup if it exists
            conf_path.rename(backup_path)
            print_success(f"Backed up existing config to: {backup_path.name}")
        except Exception as e:
            print_error(f"Could not back up existing freetds.conf: {e}")
            return

    content = [
        "# This file is auto-generated by set_profile.py from settings.json.",
        "# Manual edits will be overwritten.",
        "",
        "[global]",
        "    # Global settings that apply to all connections.",
        "    tds version = auto",
        "    text size = 64512",
        "",
    ]

    profiles = settings.get("Profiles", {})
    if not profiles:
        print("No profiles in settings.json to write.")
        return

    for name, profile in profiles.items():
        platform_val = profile.get("PLATFORM", "").upper()
        tds_version = "5.0" if platform_val == "SYBASE" else "7.4"

        content.append(f"[{name}]")
        content.append(f"    host = {profile.get('HOST', '')}")
        content.append(f"    port = {profile.get('PORT', '')}")
        content.append(f"    tds version = {tds_version}")
        content.append("")

    try:
        with open(conf_path, 'w', encoding='utf-8') as f:
            f.write("\n".join(content))
        print_success(f"Successfully wrote {len(profiles)} profiles to {conf_path.name}")
    except PermissionError:
        print_error(f"Permission denied writing to {conf_path}.")
        print("  Try running this script as an Administrator.")
    except Exception as e:
        print_error(f"Failed to write to {conf_path}: {e}")


def list_profiles(settings):
    """Display all profiles"""
    if not settings.get("Profiles"):
        print("No profiles configured yet.")
        return

    print("\nConfigured profiles:")
    print("-" * 70)

    for name, profile in settings["Profiles"].items():
        company = profile.get("COMPANY", "unknown")
        platform = profile.get("PLATFORM", "unknown")
        host = profile.get("HOST", "unknown")
        port = profile.get("PORT", "unknown")
        path_append = profile.get("SQL_SOURCE", "unknown")

        print(f"\nProfile: {name}")
        print(f"  Company: {company}")
        print(f"  Platform: {platform}")
        print(f"  Server: {host}:{port}")
        print(f"  SQL Source: {path_append}")

    print("-" * 70)


def test_connection(profile):
    """Test connection for a profile. Returns True on success, False on failure."""
    print("\nTesting connection...")

    platform = profile.get("PLATFORM")
    host = profile.get("HOST")
    port = profile.get("PORT")
    username = profile.get("USERNAME")
    password = profile.get("PASSWORD")

    # Import and use ibs_common's test_connection directly
    from .ibs_common import test_connection as ibs_test_connection

    try:
        success, message = ibs_test_connection(
            platform=platform,
            host=host,
            port=port,
            username=username,
            password=password
        )
        if success:
            print_success("Connection successful!")
        else:
            print_error(f"Connection failed: {message}")
        return success
    except Exception as e:
        print_error(f"Connection test failed: {e}")
        return False


def test_options(profile, profile_name):
    """Test options loading for a profile. Returns True on success, False on failure."""
    print("\nTesting options loading...")

    # Import Options class
    from .ibs_common import Options

    try:
        # Build config dictionary for Options class
        config = profile.copy()
        config['PROFILE_NAME'] = profile_name

        # Create Options instance
        options = Options(config)

        # Generate/load option files
        success = options.generate_option_files(force_rebuild=True)

        if success:
            print_success("Options loaded successfully!")
            print(f"  Cache file: {options.get_cache_filepath()}")
            # Test a common placeholder
            test_value = options.replace_options("&users&")
            if test_value != "&users&":
                print(f"  Resolution: &users& -> {test_value}")
        else:
            print_error("Failed to load options files.")

        return success
    except Exception as e:
        print_error(f"Options test failed: {e}")
        return False


def test_option_value(profile, profile_name):
    """Test a specific option value for a profile."""
    # Import Options class
    from .ibs_common import Options

    # Prompt for option value
    option_input = input("\nEnter option to test [&users&]: ").strip()
    if not option_input:
        option_input = "&users&"

    print(f"\nTesting option: {option_input}")

    try:
        # Build config dictionary for Options class
        config = profile.copy()
        config['PROFILE_NAME'] = profile_name

        # Create Options instance
        options = Options(config)

        # Generate/load option files
        success = options.generate_option_files()

        if success:
            # Resolve the option
            result = options.replace_options(option_input)
            if result != option_input:
                print_success(f"{option_input} -> {result}")
            else:
                print_error(f"Option '{option_input}' was not resolved (no match found)")
        else:
            print_error("Failed to load options files.")

    except Exception as e:
        print_error(f"Options test failed: {e}")


def print_profile_summary(name, profile):
    """Print a summary of a profile."""
    print("\n" + "=" * 70)
    print("Profile Summary:")
    print("-" * 70)
    print(f"  Profile Name: {name}")
    print(f"  Company: {profile.get('COMPANY', 'NOT SET')}")
    print(f"  Platform: {profile.get('PLATFORM', 'NOT SET')}")
    print(f"  Server: {profile.get('HOST', 'NOT SET')}:{profile.get('PORT', 'NOT SET')}")
    print(f"  Username: {profile.get('USERNAME', 'NOT SET')}")
    print(f"  Language: {profile.get('DEFAULT_LANGUAGE', 'NOT SET')}")
    print(f"  SQL Source: {profile.get('SQL_SOURCE', 'NOT SET')}")
    print("=" * 70)


def test_profile_menu(settings):
    """Menu for testing a profile."""
    if not settings.get("Profiles"):
        print("No profiles to test.")
        return

    list_profiles(settings)

    profile_name = input("\nEnter profile name to test: ").strip()

    if profile_name not in settings["Profiles"]:
        print_error(f"Profile '{profile_name}' not found.")
        return

    profile = settings["Profiles"][profile_name]

    while True:
        print(f"\nTesting profile: {profile_name}")
        print("  1. Test SQL Source path")
        print("  2. Test connection")
        print("  3. Test options")
        print("  4. Return to main menu")

        choice = input("\nChoose [1-4]: ").strip()

        if choice == "1":
            # Test SQL Source path
            sql_source = profile.get('SQL_SOURCE', '')
            print(f"\nSQL SOURCE: {sql_source}")
            if sql_source:
                if Path(sql_source).exists():
                    print_success("Path exists!")
                else:
                    print_error("Path does NOT exist!")
            else:
                print_error("SQL SOURCE is not set!")

        elif choice == "2":
            test_connection(profile)

        elif choice == "3":
            test_option_value(profile, profile_name)

        elif choice == "4":
            return

        else:
            print("Invalid choice. Please enter 1-4.")


def edit_profile_inline(profile, current_name=None):
    """Edit profile fields inline. Returns new profile name if changed, otherwise None."""
    print("\nEdit profile (press Enter to keep current value):")

    new_name = None

    # Allow renaming profile if current_name is provided
    if current_name:
        new_name_input = input(f"  PROFILE NAME [{current_name}]: ").strip()
        if new_name_input and new_name_input != current_name:
            if validate_profile_name(new_name_input):
                new_name = new_name_input
                print(f"    Profile will be renamed to: {new_name}")
            else:
                print("    Invalid name. Use only letters, numbers, and underscores. Name not changed.")

    # Fields in requested order: COMPANY, PLATFORM, HOST, PORT, USERNAME, PASSWORD, SQL_SOURCE
    for key in ["COMPANY", "PLATFORM", "HOST", "PORT", "USERNAME", "PASSWORD", "SQL_SOURCE"]:
        current = profile.get(key, "")

        if key == "PASSWORD":
            new_value = getpass.getpass(f"  {key} [****]: ")
        elif key == "SQL_SOURCE":
            print(f"  {key} [{current}]")
            print(f"    (Enter '.' for current directory: {os.getcwd()})")
            new_value = input(f"    New value: ").strip()
            if new_value in ['.', './', '.\\']:
                new_value = os.getcwd()
                print(f"    Using: {new_value}")
        else:
            new_value = input(f"  {key} [{current}]: ").strip()

        if new_value:
            # Convert to appropriate type
            if key in ["PORT", "COMPANY", "DEFAULT_LANGUAGE"]:
                profile[key] = int(new_value) if str(new_value).isdigit() else new_value
            else:
                profile[key] = new_value

    print_success("Profile updated.")
    return new_name


def create_profile():
    """Interactive profile creation wizard. All fields are required."""
    print_header("Create New Profile")

    profile = {}

    # Profile name (required)
    while True:
        name = input("Enter profile name (e.g., GONZO, S123_SBNA, TEST): ").strip()
        if name:
            break
        print("Profile name is required.")

    # Company (required)
    while True:
        cmpy_input = input("\nEnter company number (COMPANY): ").strip()
        if cmpy_input:
            profile["COMPANY"] = int(cmpy_input) if cmpy_input.isdigit() else cmpy_input
            break
        print("Company is required.")

    # Platform (required)
    print("\nWhat database platform does this server use?")
    print("  1. Sybase ASE")
    print("  2. Microsoft SQL Server")

    while True:
        choice = input("\nChoose [1-2]: ").strip()
        if choice == "1":
            profile["PLATFORM"] = "SYBASE"
            break
        elif choice == "2":
            profile["PLATFORM"] = "MSSQL"
            break
        else:
            print("Invalid choice. Please enter 1 or 2.")

    # Host (required)
    while True:
        host = input("\nEnter server hostname or IP: ").strip()
        if host:
            profile["HOST"] = host
            break
        print("Server hostname is required.")

    # Port (required, with default)
    default_port = 5000 if profile["PLATFORM"] == "SYBASE" else 1433
    while True:
        port_input = input(f"Enter port number [{default_port}]: ").strip()
        if not port_input:
            profile["PORT"] = default_port
            break
        if port_input.isdigit():
            profile["PORT"] = int(port_input)
            break
        print("Port must be a number.")

    # Username (required)
    while True:
        username = input("Enter database username: ").strip()
        if username:
            profile["USERNAME"] = username
            break
        print("Username is required.")

    # Password (required)
    while True:
        password = getpass.getpass("Enter password: ")
        if password:
            profile["PASSWORD"] = password
            break
        print("Password is required.")

    # SQL source location (required)
    print("\nWhere is your SQL source code located?")
    print(f"  (Enter '.' or './' to use current directory: {os.getcwd()})")

    while True:
        choice = input("\nEnter path: ").strip()

        if not choice:
            print("SQL source path is required.")
            continue

        # Handle current directory shortcut
        if choice in ['.', './', '.\\']:
            profile["SQL_SOURCE"] = os.getcwd()
            print(f"Using current directory: {profile['SQL_SOURCE']}")
            break

        # Treat as custom path
        if Path(choice).exists():
            profile["SQL_SOURCE"] = choice
            break
        else:
            print(f"Path does not exist: {choice}")
            use_anyway = input("Use anyway? [y/n]: ").strip().lower()
            if use_anyway == 'y':
                profile["SQL_SOURCE"] = choice
                break

    # Language (set default - user can change via edit if needed)
    profile["DEFAULT_LANGUAGE"] = 1

    return name, profile


def edit_profile(settings, settings_path):
    """Edit existing profile and save."""
    if not settings.get("Profiles"):
        print("No profiles to edit.")
        return False

    list_profiles(settings)

    profile_name = input("\nEnter profile name to edit: ").strip()

    if profile_name not in settings["Profiles"]:
        print_error(f"Profile '{profile_name}' not found.")
        return False

    # Make a copy to edit
    profile = settings["Profiles"][profile_name].copy()

    # Edit inline
    new_name = edit_profile_inline(profile, profile_name)

    # Determine final name
    final_name = new_name if new_name else profile_name

    # Handle rename
    if new_name and new_name != profile_name:
        if new_name in settings["Profiles"]:
            print_error(f"Profile '{new_name}' already exists. Cannot rename.")
            return False
        del settings["Profiles"][profile_name]
        print(f"Profile renamed from '{profile_name}' to '{final_name}'")

    # Save
    settings["Profiles"][final_name] = profile
    if save_settings(settings, settings_path):
        update_freetds_conf(settings)
        print_success(f"Profile '{final_name}' saved!")
        return True
    return False


def delete_profile(settings):
    """Delete a profile"""
    if not settings.get("Profiles"):
        print("No profiles to delete.")
        return

    list_profiles(settings)

    profile_name = input("\nEnter profile name to delete: ").strip()

    if profile_name not in settings["Profiles"]:
        print_error(f"Profile '{profile_name}' not found.")
        return

    confirm = input(f"Are you sure you want to delete '{profile_name}'? [y/n]: ").strip().lower()

    if confirm == 'y':
        del settings["Profiles"][profile_name]
        print_success(f"Profile '{profile_name}' deleted.")
    else:
        print("Delete cancelled.")


def main_menu():
    """Main menu loop"""
    print_header("IBS Compiler Profile Setup Wizard")

    settings, settings_path = load_settings()

    while True:
        print("\nWhat would you like to do?")
        print("  1. Create a new profile")
        print("  2. Edit existing profile")
        print("  3. Delete a profile")
        print("  4. View all profiles")
        print("  5. Test a profile")
        print("  6. Exit")

        choice = input("\nChoose [1-6]: ").strip()

        if choice == "1":
            # Create new profile
            name, profile = create_profile()
            if name and profile:
                # Save immediately
                settings.setdefault("Profiles", {})[name] = profile
                if save_settings(settings, settings_path):
                    update_freetds_conf(settings)
                    print_success(f"Profile '{name}' created!")

        elif choice == "2":
            # Edit profile
            edit_profile(settings, settings_path)

        elif choice == "3":
            # Delete profile
            delete_profile(settings)
            if save_settings(settings, settings_path):
                update_freetds_conf(settings)

        elif choice == "4":
            # View profiles
            list_profiles(settings)

        elif choice == "5":
            # Test a profile
            test_profile_menu(settings)

        elif choice == "6":
            # Exit
            print("\nExiting profile setup wizard.")
            break

        else:
            print("Invalid choice. Please enter 1-6.")

    print("\n" + "=" * 70)
    print_success("Profile configuration complete!")
    print("=" * 70)
    print(f"\nYour profiles are saved in: {settings_path}")


def main():
    """Entry point for the set_profile command."""
    try:
        main_menu()
    except KeyboardInterrupt:
        print("\n\nSetup cancelled by user.")
        sys.exit(0)
    except Exception as e:
        print_error(f"Unexpected error: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)


if __name__ == "__main__":
    main()
