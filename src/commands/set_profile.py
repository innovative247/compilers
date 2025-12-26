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
    validate_profile_aliases,
    find_profile_by_name_or_alias,
    create_symbolic_links,
)


# =============================================================================
# STARTUP VALIDATION
# =============================================================================

def check_settings_integrity(settings: dict) -> list:
    """
    Check settings.json for duplicate profile names and alias conflicts.

    Returns:
        List of error messages (empty list = no issues)
    """
    errors = []
    profiles = settings.get("Profiles", {})

    # Check for duplicate profile names (case-insensitive)
    profile_names_seen = {}  # uppercase name -> original name
    for name in profiles.keys():
        name_upper = name.upper()
        if name_upper in profile_names_seen:
            errors.append(f"Profile name '{name}' is used more than once (also exists as '{profile_names_seen[name_upper]}')")
        else:
            profile_names_seen[name_upper] = name

    # Check for alias conflicts using existing validation
    alias_errors = validate_profile_aliases(settings)
    errors.extend(alias_errors)

    return errors


def prompt_settings_fix(errors: list, settings_path) -> bool:
    """
    Display integrity errors and offer to open settings.json for manual fix.

    Args:
        errors: List of error messages
        settings_path: Path to settings.json

    Returns:
        True if user wants to continue anyway, False to exit
    """
    print_error("Settings.json has integrity issues:")
    print()
    for err in errors:
        print(f"  - {err}")
    print()

    choice = input("Would you like to open settings.json to fix these issues? [Y/n]: ").strip().lower()

    if choice in ('y', 'yes', ''):
        # Open settings.json in editor
        import subprocess
        import platform

        try:
            if platform.system() == 'Windows':
                os.startfile(str(settings_path))
            elif platform.system() == 'Darwin':
                subprocess.run(['open', str(settings_path)])
            else:
                # Try common editors
                for editor in ['code', 'vim', 'nano', 'vi']:
                    try:
                        subprocess.run([editor, str(settings_path)])
                        break
                    except FileNotFoundError:
                        continue

            print(f"\nOpened: {settings_path}")
            print("Please fix the issues and restart set_profile.")
            return False
        except Exception as e:
            print(f"Could not open editor: {e}")
            print(f"Please manually edit: {settings_path}")
            return False

    # User chose not to open - ask if they want to continue anyway
    continue_choice = input("Continue anyway (not recommended)? [y/N]: ").strip().lower()
    return continue_choice in ('y', 'yes')


# =============================================================================
# ALIAS INPUT HELPER
# =============================================================================

def prompt_for_aliases(settings: dict, profile_name: str, current_aliases: list = None) -> list:
    """
    Prompt user for aliases with validation and retry on conflict.

    Args:
        settings: Current settings dictionary
        profile_name: Name of the profile being edited/created
        current_aliases: Current aliases (for edit mode)

    Returns:
        List of valid aliases, or empty list if none/cleared
    """
    while True:
        if current_aliases:
            current_str = ", ".join(current_aliases)
            print(f"  Current aliases: {current_str}")
            print("  (Enter new aliases, 'clear' to remove all, or blank to keep current)")
        else:
            print("  Aliases allow shortcuts for this profile (e.g., 'G' for 'GONZO').")
            print("  (Enter comma-separated aliases, or blank to skip)")

        aliases_input = input("  Aliases: ").strip()

        # Handle blank input
        if not aliases_input:
            return current_aliases if current_aliases else []

        # Handle clear
        if aliases_input.lower() == 'clear':
            print("  Aliases cleared.")
            return []

        # Parse and uppercase aliases
        new_aliases = [a.strip().upper() for a in aliases_input.split(',') if a.strip()]

        if not new_aliases:
            return current_aliases if current_aliases else []

        # Validate aliases by building temporary settings
        temp_settings = settings.copy()
        temp_settings["Profiles"] = settings.get("Profiles", {}).copy()

        # Create/update the profile entry with new aliases
        if profile_name in temp_settings["Profiles"]:
            temp_profile = temp_settings["Profiles"][profile_name].copy()
        else:
            temp_profile = {}
        temp_profile["ALIASES"] = new_aliases
        temp_settings["Profiles"][profile_name] = temp_profile

        # Validate
        errors = validate_profile_aliases(temp_settings)

        if errors:
            print("\n  Alias validation errors:")
            for err in errors:
                print(f"    - {err}")
            print("  Please enter different aliases.\n")
            # Loop continues - user can retry
        else:
            print(f"  Aliases set: {', '.join(new_aliases)}")
            return new_aliases


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

def check_and_create_symbolic_links(profile: dict) -> None:
    """
    Check if symbolic links exist for the profile's SQL_SOURCE and prompt to create them.

    This is called when a profile is added or modified, so we always check
    (bypassing the IBS_SYMLINKS_CHECKED environment variable).

    Also clears the IBS_CHANGELOG_STATUS cache so the next runsql call will
    re-check changelog availability for the new/modified profile.

    Args:
        profile: Profile dictionary containing SQL_SOURCE
    """
    # Clear changelog cache when profile is modified
    os.environ.pop('IBS_CHANGELOG_STATUS', None)

    sql_source = profile.get('SQL_SOURCE')
    if not sql_source:
        return

    # Build a minimal config for create_symbolic_links
    config = {'SQL_SOURCE': sql_source}

    # Check if any links need to be created by importing the config function
    from .ibs_common import _get_symbolic_links_config

    base_path = Path(sql_source)
    if not base_path.exists():
        return

    symbolic_links = _get_symbolic_links_config()
    links_needed = []

    for link_rel, target_name in symbolic_links:
        link_path = base_path / link_rel
        target_path = base_path / target_name

        # Check if link needs to be created
        if not link_path.exists() and not link_path.is_symlink():
            if target_path.exists():
                links_needed.append(link_rel)

    if not links_needed:
        return  # All links exist or no targets found

    print(f"\n{len(links_needed)} symbolic links need to be created in {sql_source}")
    response = input("Would you like to create them now? [Y/n]: ").strip().lower()

    if response in ('', 'y', 'yes'):
        # Clear the env var to force symbolic link creation check
        # (profile add/modify should always check, regardless of session state)
        os.environ.pop('IBS_SYMLINKS_CHECKED', None)

        if create_symbolic_links(config):
            print_success(f"Symbolic links created successfully.")
        else:
            print_error("Failed to create some symbolic links.")


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
    import platform as plat

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
            print()
            choice = input("Would you like to open settings.json to fix the error? [Y/n]: ").strip().lower()
            if choice in ('y', 'yes', ''):
                try:
                    if plat.system() == 'Windows':
                        os.startfile(str(settings_path))
                    elif plat.system() == 'Darwin':
                        subprocess.run(['open', str(settings_path)])
                    else:
                        for editor in ['code', 'vim', 'nano', 'vi']:
                            try:
                                subprocess.run([editor, str(settings_path)])
                                break
                            except FileNotFoundError:
                                continue
                    print(f"\nOpened: {settings_path}")
                    print("Please fix the JSON error and restart set_profile.")
                    sys.exit(0)
                except Exception as open_err:
                    print(f"Could not open editor: {open_err}")
                    print(f"Please manually edit: {settings_path}")
                    sys.exit(1)
            else:
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


def display_single_profile(name, profile):
    """Display a single profile's details."""
    company = profile.get("COMPANY", "unknown")
    platform = profile.get("PLATFORM", "unknown")
    host = profile.get("HOST", "unknown")
    port = profile.get("PORT", "unknown")
    path_append = profile.get("SQL_SOURCE", "unknown")
    aliases = profile.get("ALIASES", [])

    raw_mode = profile.get("RAW_MODE", False)

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
    if raw_mode:
        print(f"  Raw Mode: Yes")


def list_profiles(settings):
    """Display all profiles"""
    if not settings.get("Profiles"):
        print("No profiles configured yet.")
        return

    print("\nConfigured profiles:")
    print("-" * 70)

    for name, profile in settings["Profiles"].items():
        display_single_profile(name, profile)

    print("-" * 70)


def view_profile(settings):
    """View a specific profile or all profiles."""
    if not settings.get("Profiles"):
        print("No profiles configured yet.")
        return

    input_name = input("\nEnter profile name (or blank for all): ").strip()

    if not input_name:
        # Show all profiles
        list_profiles(settings)
    else:
        # Find profile by name or alias
        profile_name, profile_data = find_profile_by_name_or_alias(settings, input_name.upper())

        if profile_name is None:
            print_error(f"Profile '{input_name}' not found.")
            return

        print("-" * 70)
        display_single_profile(profile_name, profile_data)
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
    """
    Test options loading for a profile. Returns True on success, False on failure.

    This function checks for an existing options cache file:
    - If cache exists and is valid: prompts user to use it or rebuild
    - If cache doesn't exist or is expired: builds it

    The full path of the options file is ALWAYS printed, ensuring there is no
    ambiguity about which file is being used by runsql, isqlline, runcreate, etc.
    """
    print("\nTesting options loading...")

    # Import Options class
    from .ibs_common import Options

    try:
        # Build config dictionary for Options class
        config = profile.copy()
        config['PROFILE_NAME'] = profile_name

        # Create Options instance
        options = Options(config)

        # Get cache file path FIRST - always show this
        cache_file = options.get_cache_filepath()
        print(f"\n  Options file: {cache_file}")

        # Check if cache exists and is valid
        force_rebuild = False
        if options._is_cache_valid():
            # Cache exists and is valid - prompt user
            import datetime
            mtime = os.path.getmtime(cache_file)
            age_minutes = (datetime.datetime.now().timestamp() - mtime) / 60
            age_hours = age_minutes / 60

            print(f"  Status: EXISTS (age: {age_hours:.1f} hours)")
            print()
            choice = input("  Use existing options file or rebuild? [U]se / [R]ebuild: ").strip().lower()
            if choice in ('r', 'rebuild'):
                force_rebuild = True
                print("  Rebuilding options file...")
            else:
                print("  Using existing options file...")
        else:
            # Cache doesn't exist or is expired
            if os.path.exists(cache_file):
                print("  Status: EXPIRED (will rebuild)")
            else:
                print("  Status: NOT FOUND (will create)")
            force_rebuild = True

        # Generate/load option files
        success = options.generate_option_files(force_rebuild=force_rebuild)

        if success:
            print_success("Options loaded successfully!")

            # Show setup directory and files found
            setup_dir = options.get_setup_directory()
            loaded_files = options.get_loaded_files()

            if options.was_rebuilt():
                print(f"\n  Setup directory: {setup_dir}")
                print(f"  Files loaded ({len(loaded_files)}):")
                for filepath in loaded_files:
                    print(f"    - {Path(filepath).name}")

            # Always show the cache file path prominently
            print(f"\n  >>> Options file: {cache_file}")

            # Test a common placeholder
            test_value = options.replace_options("&users&")
            if test_value != "&users&":
                print(f"  Resolution test: &users& -> {test_value}")
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


def test_symbolic_links(profile):
    """Test if symbolic links exist, create if needed."""
    from .ibs_common import _get_symbolic_links_config

    print("\nTesting symbolic links...")

    sql_source = profile.get('SQL_SOURCE', '')
    if not sql_source:
        print_error("SQL_SOURCE is not set!")
        return

    base_path = Path(sql_source)
    if not base_path.exists():
        print_error(f"SQL_SOURCE directory does not exist: {sql_source}")
        return

    symbolic_links = _get_symbolic_links_config()

    # Check which links exist and which are missing
    existing = []
    missing = []

    for link_rel, target_name in symbolic_links:
        link_path = base_path / link_rel
        target_path = base_path / target_name

        # Skip if target doesn't exist (link not needed)
        if not target_path.exists():
            continue

        if link_path.exists() or link_path.is_symlink():
            existing.append(link_rel)
        else:
            missing.append((link_rel, target_name))

    print(f"\n  Existing links: {len(existing)}")
    print(f"  Missing links:  {len(missing)}")

    if not missing:
        print_success("\n  All symbolic links exist!")
        return

    # Show missing links
    print("\n  Missing links:")
    for link_rel, target_name in missing[:10]:  # Show first 10
        print(f"    {link_rel} -> {target_name}")
    if len(missing) > 10:
        print(f"    ... and {len(missing) - 10} more")

    # Try to create them
    print("\n  Attempting to create missing links...")
    if create_symbolic_links(profile, prompt=False):
        print_success("  Symbolic links created successfully!")
    else:
        print_error("  Failed to create symbolic links.")
        print("  Please run set_profile as Administrator and try again.")


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

    input_name = input("\nEnter profile name to test: ").strip().upper()

    # Find profile by name or alias
    profile_name, profile = find_profile_by_name_or_alias(settings, input_name)

    if profile_name is None:
        print_error(f"Profile '{input_name}' not found.")
        return

    raw_mode = profile.get('RAW_MODE', False)

    while True:
        print(f"\nTesting profile: {profile_name}")
        if raw_mode:
            print("  (RAW MODE - preprocessing tests not available)")
            print("  1. Test SQL Source path")
            print("  2. Test connection")
            print("  3. Return to main menu")
            max_choice = 3
        else:
            print("  1. Test SQL Source path")
            print("  2. Test connection")
            print("  3. Test options")
            print("  4. Test changelog")
            print("  5. Test table locations")
            print("  6. Test symbolic links")
            print("  7. Return to main menu")
            max_choice = 7

        choice = input(f"\nChoose [1-{max_choice}]: ").strip()

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

        elif choice == "3" and raw_mode:
            return

        elif choice == "3" and not raw_mode:
            test_option_value(profile, profile_name)

        elif choice == "4" and not raw_mode:
            test_changelog(profile, profile_name)

        elif choice == "5" and not raw_mode:
            test_table_locations(profile, profile_name)

        elif choice == "6" and not raw_mode:
            # Test symbolic links
            test_symbolic_links(profile)

        elif choice == "7" and not raw_mode:
            return

        else:
            print(f"Invalid choice. Please enter 1-{max_choice}.")


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

    # Aliases - with retry on conflict (right after profile name)
    profile_name = new_name if new_name else current_name
    current_aliases = profile.get("ALIASES", [])
    settings = ibs_load_settings()
    # Remove the current profile from settings so we can validate against other profiles only
    if profile_name in settings.get("Profiles", {}):
        del settings["Profiles"][profile_name]
    new_aliases = prompt_for_aliases(settings, profile_name, current_aliases)
    if new_aliases:
        profile["ALIASES"] = new_aliases
    elif "ALIASES" in profile and not new_aliases:
        # User cleared aliases
        del profile["ALIASES"]

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

    # RAW_MODE setting
    current_raw = profile.get("RAW_MODE", False)
    current_raw_display = "y" if current_raw else "n"
    print(f"\n  Raw mode skips SBN-specific preprocessing (options files, symlinks, changelog).")
    raw_input = input(f"  Raw mode (y/N) [{current_raw_display}]: ").strip().lower()
    if raw_input == 'y':
        profile["RAW_MODE"] = True
    elif raw_input == 'n':
        profile["RAW_MODE"] = False
    # If empty, keep current value

    # DATABASE - only ask if RAW_MODE is True
    is_raw_mode = profile.get("RAW_MODE", False)
    if is_raw_mode:
        current_db = profile.get("DATABASE", "")
        print(f"\n  Default database for raw mode (used when -D not specified).")
        new_db = input(f"  DATABASE [{current_db}]: ").strip()
        if new_db:
            profile["DATABASE"] = new_db
    else:
        # Remove DATABASE if not in raw mode
        if "DATABASE" in profile:
            del profile["DATABASE"]

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

    # Aliases (optional) - with retry on conflict
    print()
    settings = ibs_load_settings()
    aliases = prompt_for_aliases(settings, name, current_aliases=None)
    if aliases:
        profile["ALIASES"] = aliases

    # Company (required, default 101)
    cmpy_input = input("\nEnter company number (COMPANY) [101]: ").strip()
    if cmpy_input:
        profile["COMPANY"] = int(cmpy_input) if cmpy_input.isdigit() else cmpy_input
    else:
        profile["COMPANY"] = 101

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

    # Raw mode (optional, defaults to No)
    print("\nRaw mode skips SBN-specific preprocessing (options files, symlinks, changelog).")
    print("Use this for projects that don't have the css/setup/ directory structure.")
    raw_choice = input("Enable raw mode? [y/N]: ").strip().lower()
    if raw_choice == 'y':
        profile["RAW_MODE"] = True
        # Ask for default database when in raw mode
        print("\nDefault database for raw mode (used when -D not specified).")
        db_input = input("Enter default database: ").strip()
        if db_input:
            profile["DATABASE"] = db_input

    # Language (set default - user can change via edit if needed)
    profile["DEFAULT_LANGUAGE"] = 1

    return name, profile


def edit_profile(settings, settings_path):
    """Edit existing profile and save."""
    if not settings.get("Profiles"):
        print("No profiles to edit.")
        return False

    list_profiles(settings)

    input_name = input("\nEnter profile name to edit: ").strip().upper()

    # Find profile by name or alias
    profile_name, profile_data = find_profile_by_name_or_alias(settings, input_name)

    if profile_name is None:
        print_error(f"Profile '{input_name}' not found.")
        return False

    # Make a copy to edit
    profile = profile_data.copy()

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
        # Check for symbolic links after saving
        check_and_create_symbolic_links(profile)
        return True
    return False


def delete_profile(settings):
    """Delete a profile"""
    if not settings.get("Profiles"):
        print("No profiles to delete.")
        return

    list_profiles(settings)

    input_name = input("\nEnter profile name to delete: ").strip().upper()

    # Find profile by name or alias
    profile_name, profile_data = find_profile_by_name_or_alias(settings, input_name)

    if profile_name is None:
        print_error(f"Profile '{input_name}' not found.")
        return

    confirm = input(f"Are you sure you want to delete '{profile_name}'? [y/n]: ").strip().lower()

    if confirm == 'y':
        del settings["Profiles"][profile_name]
        print_success(f"Profile '{profile_name}' deleted.")
    else:
        print("Delete cancelled.")


def main_menu():
    """Main menu loop"""
    print_header("Innovative247 Compiler Profile Setup Wizard")

    settings, settings_path = load_settings()

    # Check for integrity issues on startup
    integrity_errors = check_settings_integrity(settings)
    if integrity_errors:
        if not prompt_settings_fix(integrity_errors, settings_path):
            return  # User chose to fix manually or exit

    while True:
        print("\nWhat would you like to do?")
        print("  1. Create a new profile")
        print("  2. Edit existing profile")
        print("  3. Delete a profile")
        print("  4. View profile")
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
                    # Check for symbolic links after saving
                    check_and_create_symbolic_links(profile)

        elif choice == "2":
            # Edit profile
            edit_profile(settings, settings_path)

        elif choice == "3":
            # Delete profile
            delete_profile(settings)
            save_settings(settings, settings_path)

        elif choice == "4":
            # View profile(s)
            view_profile(settings)

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
