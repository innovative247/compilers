"""
set_profile.py: Interactive profile setup wizard for IBS Compilers

This script guides users through creating and managing profiles in settings.json.

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

# Import from ibs_common for profile management
from .ibs_common import (
    find_settings_file,
    load_settings as ibs_load_settings,
    load_profile,
    save_profile,
    list_profiles as ibs_list_profiles,
    validate_profile_aliases
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
        profile_name = input("Enter a profile name (alphanumeric and underscores only): ").strip().upper()

        # Empty input = cancel/quit
        if not profile_name:
            print("Profile not saved.")
            return

        # Validate characters
        if not validate_profile_name(profile_name):
            print("Invalid name. Use only letters, numbers, and underscores.")
            continue

        # Check if profile already exists (profiles are stored uppercase)
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
        aliases = profile.get("ALIASES", [])

        # Format profile name with aliases if present
        if aliases:
            aliases_str = ", ".join(aliases)
            print(f"\nProfile: {name} (aliases: {aliases_str})")
        else:
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

            # Show setup directory and files found
            setup_dir = options.get_setup_directory()
            loaded_files = options.get_loaded_files()

            print(f"\n  Setup directory: {setup_dir}")
            print(f"  Files loaded ({len(loaded_files)}):")
            for filepath in loaded_files:
                print(f"    - {Path(filepath).name}")

            print(f"\n  Cache file: {options.get_cache_filepath()}")

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

        # Check if cache file exists and prompt for rebuild
        force_rebuild = False
        cache_file = options.get_cache_filepath()
        if os.path.exists(cache_file):
            print(f"\n  Existing cache file found:")
            print(f"  {cache_file}")
            rebuild_choice = input("\n  Rebuild cache? [y/N]: ").strip().lower()
            force_rebuild = rebuild_choice in ['y', 'yes']

        # Generate/load option files
        success = options.generate_option_files(force_rebuild=force_rebuild)

        if success:
            # Show cache status
            if options.was_rebuilt():
                print(f"\n  Cache: REBUILT")
            else:
                print(f"\n  Cache: Reused (not rebuilt)")
            print(f"  Cache file: {cache_file}")

            # Show setup directory and files found
            setup_dir = options.get_setup_directory()
            loaded_files = options.get_loaded_files()

            print(f"\n  Setup directory: {setup_dir}")
            print(f"  Files loaded ({len(loaded_files)}):")
            for filepath in loaded_files:
                print(f"    - {Path(filepath).name}")

            # Resolve the option
            result = options.replace_options(option_input)
            if result != option_input:
                source_file = options.get_option_source(option_input)
                print_success(f"\n  {option_input} -> {result}")
                if source_file:
                    print(f"  Source: {source_file}")
            else:
                # Check if this might be a dynamic option (V: or C:)
                print_error(f"\n  Option '{option_input}' was not resolved")
                print(f"\n  Note: If this is a V: or C: option, it is DYNAMIC and")
                print(f"  queried from the &options& database table at runtime,")
                print(f"  not compiled into SQL. Only v: and c: options are static.")
                print(f"\n  For c: options, use &if_name& / &endif_name& placeholders.")
        else:
            print_error("Failed to load options files.")

    except Exception as e:
        print_error(f"Options test failed: {e}")


def test_changelog(profile, profile_name):
    """Test changelog functionality for a profile."""
    print("\nTesting changelog...")

    # Import changelog functions
    from .ibs_common import is_changelog_enabled, insert_changelog_entry

    # Build config dictionary
    config = profile.copy()
    config['PROFILE_NAME'] = profile_name

    # Step 1: Check if changelog is enabled
    print("\n  Step 1: Checking if changelog is enabled...")
    enabled, message = is_changelog_enabled(config)

    if enabled:
        print_success(f"  {message}")
    else:
        print_error(f"  {message}")
        print("\n  Changelog test cannot continue.")
        print("  To enable changelog:")
        print("    1. Ensure &options& table exists with gclog12 row")
        print("    2. Set gclog12 act_flg = '+' in the database")
        print("    3. Ensure ba_gen_chg_log_new stored procedure exists")
        return

    # Step 2: Ask if user wants to insert a test entry
    print("\n  Step 2: Insert test changelog entry")
    insert_choice = input("  Insert a test entry into changelog? [y/N]: ").strip().lower()

    if insert_choice not in ['y', 'yes']:
        print("  Skipping insert test.")
        return

    # Insert test entry
    print("\n  Inserting test changelog entry...")
    success, message = insert_changelog_entry(
        config=config,
        command_type='TEST',
        command='set_profile changelog test',
        description=f"User `{config.get('USERNAME', 'unknown')}` tested changelog from set_profile"
    )

    if success:
        print_success(f"  {message}")

        # Step 3: Verify by querying the changelog
        print("\n  Step 3: Verifying changelog entries...")
        from .ibs_common import Options, execute_sql_native

        options = Options(config)
        options.generate_option_files()

        # Resolve &dbpro& for the database (ba_gen_chg_log is in dbpro)
        dbpro = options.replace_options("&dbpro&")
        ba_gen_chg_log = options.replace_options("&ba_gen_chg_log&")

        if dbpro != "&dbpro&" and ba_gen_chg_log != "&ba_gen_chg_log&":
            query = f"select top 10 dateadd(ss, tm, '800101') as 'server time', descr from {ba_gen_chg_log} where prgno='TEST' order by tm desc"

            query_success, output = execute_sql_native(
                host=config.get('HOST', ''),
                port=config.get('PORT', 5000),
                username=config.get('USERNAME', ''),
                password=config.get('PASSWORD', ''),
                database=dbpro,
                platform=config.get('PLATFORM', 'SYBASE'),
                sql_content=query
            )

            if query_success and output:
                print("\n  Recent TEST changelog entries:")
                print("  " + "-" * 66)
                for line in output.strip().split('\n'):
                    if line.strip():
                        print(f"  {line}")
            else:
                print("\n  Could not retrieve changelog entries.")
        else:
            print("\n  Could not resolve placeholders to verify entries.")
    else:
        print_error(f"  {message}")


def test_table_locations(profile, profile_name):
    """Test table locations parsing and optionally compile."""
    print("\nTesting table locations...")

    # Import table locations functions
    from .ibs_common import (
        Options,
        get_table_locations_path,
        parse_table_locations,
        compile_table_locations
    )

    # Build config dictionary
    config = profile.copy()
    config['PROFILE_NAME'] = profile_name

    # Step 1: Check if table_locations file exists
    locations_file = get_table_locations_path(config)
    print(f"\n  File: {locations_file}")

    if not os.path.exists(locations_file):
        print_error("  table_locations file not found!")
        return

    print_success("  File exists")

    # Step 2: Load options
    print("\n  Loading options...")
    options = Options(config)
    if not options.generate_option_files():
        print_error("  Failed to load options files")
        return

    # Step 3: Resolve target table
    target_table = options.replace_options("&table_locations&")
    if target_table == "&table_locations&":
        print_error("  Could not resolve &table_locations& placeholder")
        return

    print(f"  Target table: {target_table}")

    # Step 4: Parse and show count
    print("\n  Parsing table_locations file...")
    rows = parse_table_locations(locations_file, options)
    print(f"  Found {len(rows)} table location entries")

    # Show a few sample entries
    if rows:
        print("\n  Sample entries (first 5):")
        for row in rows[:5]:
            parts = row.split('\t')
            if len(parts) >= 4:
                print(f"    {parts[0]} -> {parts[3]}")

    # Step 5: Ask if user wants to compile
    print()
    compile_choice = input("  Compile table locations into database? [y/N]: ").strip().lower()

    if compile_choice not in ['y', 'yes']:
        print("  Skipping compile.")
        return

    # Compile
    print("\n  Compiling table locations...")
    success, message, row_count = compile_table_locations(config, options)

    if success:
        print_success(f"  {row_count} rows imported. {message}")
    else:
        print_error(f"  {message}")


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

    profile_name = input("\nEnter profile name to test: ").strip().upper()

    if profile_name not in settings["Profiles"]:
        print_error(f"Profile '{profile_name}' not found.")
        return

    profile = settings["Profiles"][profile_name]

    while True:
        print(f"\nTesting profile: {profile_name}")
        print("  1. Test SQL Source path")
        print("  2. Test connection")
        print("  3. Test options")
        print("  4. Test changelog")
        print("  5. Test table locations")
        print("  6. Return to main menu")

        choice = input("\nChoose [1-6]: ").strip()

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
            test_changelog(profile, profile_name)

        elif choice == "5":
            test_table_locations(profile, profile_name)

        elif choice == "6":
            return

        else:
            print("Invalid choice. Please enter 1-6.")


def edit_profile_inline(profile, current_name=None):
    """Edit profile fields inline. Returns new profile name if changed, otherwise None."""
    print("\nEdit profile (press Enter to keep current value):")

    new_name = None

    # Allow renaming profile if current_name is provided
    if current_name:
        new_name_input = input(f"  PROFILE NAME [{current_name}]: ").strip().upper()
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

    # Aliases
    current_aliases = profile.get("ALIASES", [])
    current_aliases_str = ", ".join(current_aliases) if current_aliases else "(none)"
    print(f"  ALIASES [{current_aliases_str}]")
    print(f"    (Enter comma-separated aliases, or 'clear' to remove all)")
    aliases_input = input(f"    New value: ").strip()

    if aliases_input:
        if aliases_input.lower() == 'clear':
            if "ALIASES" in profile:
                del profile["ALIASES"]
            print("    Aliases cleared.")
        else:
            # Parse and uppercase aliases
            new_aliases = [a.strip().upper() for a in aliases_input.split(',') if a.strip()]
            if new_aliases:
                # Validate before setting
                profile_name = new_name if new_name else current_name
                temp_settings = ibs_load_settings()
                temp_profile = profile.copy()
                temp_profile["ALIASES"] = new_aliases
                # Remove old profile entry if renaming
                if profile_name in temp_settings.get("Profiles", {}):
                    del temp_settings["Profiles"][profile_name]
                temp_settings.setdefault("Profiles", {})[profile_name] = temp_profile

                errors = validate_profile_aliases(temp_settings)
                if errors:
                    print("\n    Alias validation errors:")
                    for err in errors:
                        print(f"      {err}")
                    print("    Aliases not changed.")
                else:
                    profile["ALIASES"] = new_aliases
                    print(f"    Aliases set: {', '.join(new_aliases)}")

    print_success("Profile updated.")
    return new_name


def create_profile():
    """Interactive profile creation wizard. All fields are required."""
    print_header("Create New Profile")

    profile = {}

    # Profile name (required, stored uppercase)
    while True:
        name = input("Enter profile name (e.g., GONZO, S123_SBNA, TEST): ").strip().upper()
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

    # Aliases (optional)
    print("\nAliases allow shortcuts for this profile (e.g., 'G' for 'GONZO').")
    aliases_input = input("Enter aliases (comma-separated, or blank to skip): ").strip()

    if aliases_input:
        # Parse, clean, and uppercase aliases
        aliases = [a.strip().upper() for a in aliases_input.split(',') if a.strip()]

        if aliases:
            # Validate aliases before adding
            # Build temporary settings to validate
            temp_settings = ibs_load_settings()
            temp_profile = profile.copy()
            temp_profile["ALIASES"] = aliases
            temp_settings.setdefault("Profiles", {})[name] = temp_profile

            errors = validate_profile_aliases(temp_settings)
            if errors:
                print("\nAlias validation errors:")
                for err in errors:
                    print(f"  {err}")
                print("Aliases not saved. You can add them later via edit.")
            else:
                profile["ALIASES"] = aliases
                print(f"Aliases set: {', '.join(aliases)}")

    return name, profile


def edit_profile(settings, settings_path):
    """Edit existing profile and save."""
    if not settings.get("Profiles"):
        print("No profiles to edit.")
        return False

    list_profiles(settings)

    profile_name = input("\nEnter profile name to edit: ").strip().upper()

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
        print_success(f"Profile '{final_name}' saved!")
        return True
    return False


def delete_profile(settings):
    """Delete a profile"""
    if not settings.get("Profiles"):
        print("No profiles to delete.")
        return

    list_profiles(settings)

    profile_name = input("\nEnter profile name to delete: ").strip().upper()

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
                    print_success(f"Profile '{name}' created!")

        elif choice == "2":
            # Edit profile
            edit_profile(settings, settings_path)

        elif choice == "3":
            # Delete profile
            delete_profile(settings)
            save_settings(settings, settings_path)

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
