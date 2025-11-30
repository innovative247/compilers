"""
setup_profile.py: Interactive profile setup wizard for IBS Compilers

This script guides users through creating and managing profiles in settings.json.
It also automatically synchronizes these profiles to the system's freetds.conf
file, allowing native tools like 'tsql' to use the same profile names.

Usage:
    python setup_profile.py
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
        "CMPY": 101,
        "IBSLANG": 1,
        "BCPJ": None,
        "PLATFORM": platform_type,
        "HOST": host,
        "PORT": port,
        "USERNAME": username,
        "PASSWORD": password,
        "PATH_APPEND": None
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
    print(f"✓ {text}")


def print_error(text):
    """Print error message"""
    print(f"✗ {text}")


def get_settings_path():
    """Determine where settings.json should be located"""
    # Look in current directory first
    if Path("settings.json").exists():
        return Path("settings.json")

    # Look in parent directory (if running from src/)
    if Path("../settings.json").exists():
        return Path("../settings.json").resolve()

    # Default: create in current directory
    return Path("settings.json")


def load_settings():
    """Load existing settings.json or create new"""
    settings_path = get_settings_path()

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
        print(f"ℹ Creating new settings file: {settings_path}")
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
        print(f"\nℹ Skipping freetds.conf update on non-Windows OS ({system}).")
        return

    conf_path = get_freetds_conf_path()
    if not conf_path:
        print("\n✗ Could not find freetds.conf, skipping update.")
        return

    if not conf_path.parent.exists():
        print(f"\n✗ freetds.conf directory not found at {conf_path.parent}, skipping update.")
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
        "# This file is auto-generated by setup_profile.py from settings.json.",
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
        platform = profile.get("PLATFORM", "unknown")
        host = profile.get("HOST", "unknown")
        port = profile.get("PORT", "unknown")
        cmpy = profile.get("CMPY", "unknown")
        path_append = profile.get("PATH_APPEND", "unknown")

        print(f"\nProfile: {name}")
        print(f"  Platform: {platform}")
        print(f"  Server: {host}:{port}")
        print(f"  Company: {cmpy}")
        print(f"  SQL Source: {path_append}")

    print("-" * 70)


def test_connection(profile):
    """Test connection for a profile"""
    print("\nTesting connection...")

    platform = profile.get("PLATFORM")
    host = profile.get("HOST")
    port = profile.get("PORT")
    username = profile.get("USERNAME")
    password = profile.get("PASSWORD")

    # Run test_connection.py
    try:
        cmd = [
            sys.executable,
            "test_connection.py",
            "--platform", platform,
            "--host", host,
            "--port", str(port),
            "--username", username,
            "--password", password
        ]

        result = subprocess.run(cmd, capture_output=False, text=True)
        return result.returncode == 0

    except FileNotFoundError:
        print_error("test_connection.py not found")
        print("Skipping connection test...")
        return None
    except Exception as e:
        print_error(f"Connection test failed: {e}")
        return False


def create_profile():
    """Interactive profile creation wizard"""
    print_header("Create New Profile")

    profile = {}

    # Profile name
    while True:
        name = input("Enter profile name (e.g., GONZO, S123_SBNA, TEST): ").strip()
        if name:
            break
        print("Profile name cannot be empty.")

    # Platform
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

    # Server details
    profile["HOST"] = input("\nEnter server hostname or IP: ").strip()

    # Port with default
    default_port = 5000 if profile["PLATFORM"] == "SYBASE" else 1433
    port_input = input(f"Enter port number [{default_port}]: ").strip()
    profile["PORT"] = int(port_input) if port_input else default_port

    # Credentials
    profile["USERNAME"] = input("Enter database username: ").strip()
    profile["PASSWORD"] = getpass.getpass("Enter password: ")

    # Company and language
    cmpy_input = input("\nEnter company number (CMPY): ").strip()
    profile["CMPY"] = int(cmpy_input) if cmpy_input.isdigit() else cmpy_input

    lang_input = input("Enter language ID (IBSLANG) [1]: ").strip()
    profile["IBSLANG"] = int(lang_input) if lang_input else 1

    # SQL source location
    print("\nWhere is your SQL source code located?")

    common_paths = [
        "C:\\_innovative\\_source\\current.sql",
        "C:\\_innovative\\_source\\sql_v94",
        "C:\\_innovative\\_source\\sql_v93",
    ]

    for i, path in enumerate(common_paths, 1):
        exists = " ✓" if Path(path).exists() else ""
        print(f"  {i}. {path}{exists}")

    print(f"  {len(common_paths) + 1}. Custom path")

    while True:
        choice = input(f"\nChoose [1-{len(common_paths) + 1}] or enter custom path: ").strip()

        if choice.isdigit():
            idx = int(choice) - 1
            if 0 <= idx < len(common_paths):
                profile["PATH_APPEND"] = common_paths[idx]
                break
            elif idx == len(common_paths):
                # Custom path
                custom = input("Enter custom path: ").strip()
                if Path(custom).exists():
                    profile["PATH_APPEND"] = custom
                    break
                else:
                    print(f"Warning: Path does not exist: {custom}")
                    use_anyway = input("Use anyway? [y/n]: ").strip().lower()
                    if use_anyway == 'y':
                        profile["PATH_APPEND"] = custom
                        break
        else:
            # Treat as custom path
            if Path(choice).exists():
                profile["PATH_APPEND"] = choice
                break
            else:
                print(f"Path does not exist: {choice}")
                use_anyway = input("Use anyway? [y/n]: ").strip().lower()
                if use_anyway == 'y':
                    profile["PATH_APPEND"] = choice
                    break

    # Defaults
    profile["BCPJ"] = None

    # Summary
    print("\n" + "=" * 70)
    print("Profile Summary:")
    print("-" * 70)
    print(f"  Profile Name: {name}")
    print(f"  Platform: {profile['PLATFORM']}")
    print(f"  Server: {profile['HOST']}:{profile['PORT']}")
    print(f"  Username: {profile['USERNAME']}")
    print(f"  Company: {profile['CMPY']}")
    print(f"  Language: {profile['IBSLANG']}")
    print(f"  SQL Source: {profile['PATH_APPEND']}")
    print("=" * 70)

    # Test connection
    print("\nWould you like to test the connection?")
    test_choice = input("[y/n]: ").strip().lower()

    if test_choice == 'y':
        test_result = test_connection(profile)
        if test_result is False:
            print("\n⚠ Connection test failed!")
            continue_anyway = input("Save profile anyway? [y/n]: ").strip().lower()
            if continue_anyway != 'y':
                print("Profile not saved.")
                return None, None

    return name, profile


def edit_profile(settings):
    """Edit existing profile"""
    if not settings.get("Profiles"):
        print("No profiles to edit.")
        return

    list_profiles(settings)

    profile_name = input("\nEnter profile name to edit: ").strip()

    if profile_name not in settings["Profiles"]:
        print_error(f"Profile '{profile_name}' not found.")
        return

    profile = settings["Profiles"][profile_name]

    print(f"\nEditing profile: {profile_name}")
    print("(Press Enter to keep current value)")

    # Edit each field
    for key in ["PLATFORM", "HOST", "PORT", "USERNAME", "PASSWORD", "CMPY", "IBSLANG", "PATH_APPEND"]:
        current = profile.get(key, "")

        if key == "PASSWORD":
            new_value = getpass.getpass(f"{key} [****]: ")
        else:
            new_value = input(f"{key} [{current}]: ").strip()

        if new_value:
            # Convert to appropriate type
            if key in ["PORT", "CMPY", "IBSLANG"]:
                profile[key] = int(new_value) if new_value.isdigit() else new_value
            else:
                profile[key] = new_value

    settings["Profiles"][profile_name] = profile
    print_success(f"Profile '{profile_name}' updated.")


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
        print("  5. Exit")

        choice = input("\nChoose [1-5]: ").strip()

        if choice == "1":
            # Create new profile
            name, profile = create_profile()
            if name and profile:
                settings.setdefault("Profiles", {})[name] = profile
                if save_settings(settings, settings_path):
                    print_success(f"Profile '{name}' created successfully!")
                    update_freetds_conf(settings)

                    # Offer to create another
                    another = input("\nCreate another profile? [y/n]: ").strip().lower()
                    if another != 'y':
                        break

        elif choice == "2":
            # Edit profile
            edit_profile(settings)
            if save_settings(settings, settings_path):
                update_freetds_conf(settings)

        elif choice == "3":
            # Delete profile
            delete_profile(settings)
            if save_settings(settings, settings_path):
                update_freetds_conf(settings)

        elif choice == "4":
            # View profiles
            list_profiles(settings)

        elif choice == "5":
            # Exit
            print("\nExiting profile setup wizard.")
            break

        else:
            print("Invalid choice. Please enter 1-5.")

    print("\n" + "=" * 70)
    print_success("Profile configuration complete!")
    print("=" * 70)
    print(f"\nYour profiles are saved in: {settings_path}")
    print("\nYou can now use these profiles with:")
    print("  runsql <script> <profile-name>")
    print("  isqlline \"<query>\" <profile-name>")
    print("  tsql -S <profile-name> -U <user> -P <pass> (for native testing)")
    print("\nFor more help, see: GETTING_STARTED.md")


if __name__ == "__main__":
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
