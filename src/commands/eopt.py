"""
eopt.py: Edit and compile options.

This command allows editing and importing options configuration files into the database.
Options control runtime behavior of the IBS/SBN system, including database mappings,
feature flags, and configuration values.

SOURCE FILES:
    Options are stored in a hierarchy of source files:

    1. options.def
       - Master template containing all available options with default values
       - Located at: {SQL_SOURCE}/CSS/Setup/options.def
       - Used in "Add mode" to propagate new options to company/profile files

    2. options.{COMPANY} (e.g., options.101)
       - Company-wide options that apply to all servers for a company
       - Located at: {SQL_SOURCE}/CSS/Setup/options.{COMPANY}

    3. options.{COMPANY}.{PROFILE} (e.g., options.101.GONZO)
       - Server/profile-specific options that override company options
       - Located at: {SQL_SOURCE}/CSS/Setup/options.{COMPANY}.{PROFILE}

    4. options.{PLATFORM} (e.g., options.SYBASE, options.MSSQL)
       - Platform-specific options (Sybase vs MS SQL Server)
       - These are excluded when merging from options.def

OPTION FORMAT:
    Each option line follows one of these formats:

    Value options (v:/V:):
        v:option_name <<value>> description
        V:option_name <<value>> description   (V: = dynamic/user-changeable)

        Example: v:dbtbl <<sbnmaster>> Main tables database
        Example: V:timeout <<30>> Connection timeout (dynamic)

    Condition options (c:/C:):
        c:option_name +/- description
        C:option_name +/- description   (C: = dynamic/user-changeable)

        Example: c:debug + Enable debug mode
        Example: C:feature_x - Feature X disabled (dynamic)

TARGET TABLES:
    Options are imported through a two-step process:

    1. w#options (work table) - nvarchar(2000) single column
       - Temporary staging table for raw option lines
       - Format: :>name     - - + - <<value>> description

    2. options (final table) - via i_import_options stored procedure
       - id          varchar(8)      Option identifier
       - act_flg     char(1)         Active flag (+/-)
       - if_flg      char(1)         If-condition flag
       - val_flg     char(1)         Value flag
       - value       nvarchar(2000)  Option value
       - dyn_flg     char(1)         Dynamic flag (+/-)
       - description varchar(255)    Description

MODES:
    1. Edit Mode (default):
       - Prompts to edit profile options file (default: No)
       - Prompts to edit company options file (default: No)
       - Profile options override company options when compiled

    2. Add Mode (-d flag or menu option 1):
       - Opens options.def for editing
       - Finds NEW options not in company/profile files
       - Merges new options with "NEW->" prefix for review
       - Opens merged files for editing

PROCESS:
    1. Select mode (Add or Edit)
    2. Edit source files as needed
    3. Prompt to import into database (default: Yes)
    4. Parse company and profile options files
    5. Convert to :> format for database import
    6. Delete from w#options work table
    7. Insert all options using SQL INSERT statements
    8. Execute i_import_options stored procedure
    9. Also update table_locations (since options may affect database mappings)

USAGE:
    eopt PROFILE [-d] [-O output_file]

ARGUMENTS:
    PROFILE     Configuration profile name (e.g., GONZO, PROD)
    -d          Developer/Add mode: merge new options from options.def
    -O          Optional output file for messages (not currently used)

EXAMPLES:
    eopt GONZO          Edit options for GONZO profile (interactive mode selection)
    eopt GONZO -d       Add mode: merge new options from options.def
    eopt PROD           Edit options for PROD profile

RELATED:
    eloc - Edit and compile table_locations only

NOTE:
    This command uses SQL INSERT statements instead of BCP (Bulk Copy Program)
    to avoid the 255 character limit in freebcp. This allows option values
    up to 2000 characters (the w#options.line column width).
"""

import argparse
import sys
import os
from datetime import datetime

from .ibs_common import (
    get_config,
    Options,
    get_options_company_path,
    get_options_profile_path,
    compile_options,
    console_yes_no,
    launch_editor,
)


def get_options_def_path(config: dict) -> str:
    """
    Get path to the master options.def template file.

    Args:
        config: Configuration dictionary with SQL_SOURCE

    Returns:
        Full path to {SQL_SOURCE}/CSS/Setup/options.def
    """
    sql_source = config.get('SQL_SOURCE', os.getcwd())
    return os.path.join(sql_source, 'CSS', 'Setup', 'options.def')


def get_options_platform_path(config: dict) -> str:
    """
    Get path to platform-specific options file.

    Platform options (options.SYBASE or options.MSSQL) contain options that
    differ between database platforms. These are excluded when merging
    from options.def to avoid propagating platform-specific settings.

    Args:
        config: Configuration dictionary with SQL_SOURCE and PLATFORM

    Returns:
        Full path to {SQL_SOURCE}/CSS/Setup/options.{PLATFORM}
    """
    sql_source = config.get('SQL_SOURCE', os.getcwd())
    platform = config.get('PLATFORM', 'SYBASE')
    return os.path.join(sql_source, 'CSS', 'Setup', f'options.{platform}')


def load_options_file(file_path: str) -> list:
    """
    Load options file into a list of lines.

    Args:
        file_path: Path to options file

    Returns:
        List of lines with line endings stripped, or empty list if file doesn't exist
    """
    if not os.path.exists(file_path):
        return []
    with open(file_path, 'r', encoding='utf-8') as f:
        return [line.rstrip('\r\n') for line in f]


def save_options_file(file_path: str, lines: list):
    """
    Save list of lines to options file.

    Args:
        file_path: Path to options file
        lines: List of lines to write
    """
    with open(file_path, 'w', encoding='utf-8', newline='\n') as f:
        for line in lines:
            f.write(line + '\n')


def extract_option_name(line: str) -> str:
    """
    Extract option name from a v:/V:/c:/C: line.

    Option lines have the format:
        v:option_name <<value>> description
        c:option_name +/- description

    Args:
        line: A line from an options file

    Returns:
        Lowercase option name, or empty string if not a valid option line
    """
    line = line.strip()
    if len(line) < 3:
        return ''

    prefix = line[:2].lower()
    if prefix not in ('v:', 'c:'):
        return ''

    # Get content after prefix
    content = line[2:].strip()

    # Option name is everything before first space
    space_idx = content.find(' ')
    if space_idx == -1:
        return content.lower()

    return content[:space_idx].strip().lower()


def find_new_options(def_lines: list, target_lines: list) -> list:
    """
    Find options in def_lines that are not in target_lines.

    Used in Add mode to identify which options from options.def
    need to be added to company or profile files.

    Args:
        def_lines: Lines from options.def (master template)
        target_lines: Lines from options.{company} or options.{company}.{profile}

    Returns:
        List of new option lines from def_lines that don't exist in target
    """
    # Build set of existing option names in target
    existing_names = set()
    for line in target_lines:
        name = extract_option_name(line)
        if name:
            existing_names.add(name)

    # Find options in def that aren't in target
    new_options = []
    for line in def_lines:
        name = extract_option_name(line)
        if name and name not in existing_names:
            new_options.append(line)

    return new_options


def remove_options(base_lines: list, lines_to_remove: list) -> list:
    """
    Remove options from base_lines that exist in lines_to_remove.

    Used to filter out platform-specific options (from options.SYBASE/options.MSSQL)
    before merging default options into company/profile files. This prevents
    platform-specific options from being incorrectly propagated.

    Args:
        base_lines: Lines from options.def
        lines_to_remove: Lines from options.{PLATFORM}

    Returns:
        Filtered list with matching options removed
    """
    # Build set of option names to remove
    remove_names = set()
    for line in lines_to_remove:
        name = extract_option_name(line)
        if name:
            remove_names.add(name)

    # Filter out lines whose option name is in the remove set
    result = []
    for line in base_lines:
        name = extract_option_name(line)
        # Keep non-option lines and options not in remove set
        if not name or name not in remove_names:
            result.append(line)

    return result


def insert_new_options(target_lines: list, new_options: list, mod_num: str = None) -> list:
    """
    Insert new options into target file with MOD markers.

    New options are appended at the end, wrapped with MOD # markers
    so users can easily identify which options were just added.

    Args:
        target_lines: Existing lines in target file
        new_options: New option lines to add
        mod_num: MOD number for markers (e.g., "07.95.27649")

    Returns:
        Combined list with new options appended (wrapped in MOD markers)
    """
    result = target_lines.copy()

    if mod_num:
        result.append(f"# {mod_num} -->")
        for line in new_options:
            result.append(line)
        result.append(f"# {mod_num} <--")
    else:
        # Fallback if no mod_num provided
        for line in new_options:
            result.append(line)

    return result


def prompt_option_value(option_line: str) -> str:
    """
    Prompt user to set/confirm the default value for an option.

    Parses the option line to determine type (value or condition) and
    prompts appropriately.

    Args:
        option_line: Option line from options.def (e.g., "V:myopt <<default>> description")

    Returns:
        Modified option line with user's chosen value
    """
    # Parse the option line
    line = option_line.strip()
    if len(line) < 3:
        return option_line

    prefix = line[:2]
    opt_type = prefix.lower()
    is_dynamic = prefix[0].isupper()

    if opt_type not in ('v:', 'c:'):
        return option_line

    content = line[2:].strip()

    # Extract option name
    space_idx = content.find(' ')
    if space_idx == -1:
        return option_line

    opt_name = content[:space_idx].strip()
    rest = content[space_idx:].strip()

    if opt_type == 'v:':
        # Value option - extract current value and description
        start_idx = rest.find('<<')
        end_idx = rest.find('>>')
        if start_idx == -1 or end_idx == -1:
            return option_line

        current_value = rest[start_idx + 2:end_idx]
        description = rest[end_idx + 2:].strip()

        print(f"\n  Option: {opt_name}")
        print(f"  Type: Value ({'dynamic' if is_dynamic else 'static'})")
        print(f"  Description: {description}")
        print(f"  Default value: <<{current_value}>>")

        new_value = input(f"  Enter value (or press Enter to keep <<{current_value}>>): ").strip()
        if not new_value:
            new_value = current_value

        return f"{prefix}{opt_name} <<{new_value}>> {description}"

    else:
        # Condition option - extract current state and description
        if rest.startswith('-'):
            current_state = '-'
            description = rest[1:].strip()
        elif rest.startswith('+'):
            current_state = '+'
            description = rest[1:].strip()
        else:
            current_state = '+'
            description = rest

        state_text = "On (+)" if current_state == '+' else "Off (-)"

        print(f"\n  Option: {opt_name}")
        print(f"  Type: On/Off ({'dynamic' if is_dynamic else 'static'})")
        print(f"  Description: {description}")
        print(f"  Default state: {state_text}")
        print("  1. Off (-)")
        print("  2. On (+)")
        print("  3. Keep current")

        while True:
            choice = input("  Choose [1-3]: ").strip()
            if choice == "1":
                new_state = '-'
                break
            elif choice == "2":
                new_state = '+'
                break
            elif choice == "3" or choice == "":
                new_state = current_state
                break
            else:
                print("  Please enter 1, 2, or 3")

        return f"{prefix}{opt_name} {new_state} {description}"


def create_new_option_interactive(existing_options: list = None):
    """
    Interactive wizard to create a new option.

    Guides user through:
    1. Option type (value or on/off)
    2. Static or dynamic
    3. Option name (validated against existing options)
    4. Default value
    5. Description
    6. Confirmation

    Args:
        existing_options: List of lines from options.def to check for duplicates

    Returns:
        Option line string (e.g., "V:myopt <<value>> description") or None if cancelled
    """
    # Build set of existing option names for duplicate checking
    existing_names = set()
    if existing_options:
        for line in existing_options:
            name = extract_option_name(line)
            if name:
                existing_names.add(name)

    while True:
        # Step 1: Option type
        print("\n--- New Option ---")
        print("What type of option?")
        print("  1. Value option (stores a value like <<sbnmaster>>)")
        print("  2. On/Off option (condition flag +/-)")
        print("  3. Cancel")

        type_choice = input("\nChoose [1-3]: ").strip()
        if type_choice == "3":
            return None
        if type_choice not in ("1", "2"):
            print("Invalid choice.")
            continue

        is_value_option = (type_choice == "1")

        # Step 2: Static or dynamic
        print("\nIs this option static or dynamic?")
        print("  1. Static (lowercase v:/c: - cannot be changed at runtime)")
        print("  2. Dynamic (uppercase V:/C: - can be changed by users)")
        print("  3. Go back")

        dynamic_choice = input("\nChoose [1-3]: ").strip()
        if dynamic_choice == "3":
            continue  # Go back to option type
        if dynamic_choice not in ("1", "2"):
            print("Invalid choice.")
            continue

        is_dynamic = (dynamic_choice == "2")

        # Step 3: Option name (with validation loop)
        opt_name = None
        while opt_name is None:
            print("\nEnter option name (max 8 characters, e.g., 'myopt'), or 'back' to go back:")
            name_input = input("> ").strip()

            if name_input.lower() == 'back':
                break  # Go back to dynamic selection

            if not name_input:
                print("Option name cannot be empty.")
                continue

            if len(name_input) > 8:
                print("Option name must be 8 characters or less.")
                continue

            # Check if option already exists
            if name_input.lower() in existing_names:
                print(f"Option '{name_input}' already exists in options.def.")
                continue

            opt_name = name_input

        if opt_name is None:
            continue  # User typed 'back', restart from beginning

        # Step 4: Default value
        if is_value_option:
            default_value = None
            while default_value is None:
                print("\nEnter default value (max 2000 characters, will be wrapped in <<>>):")
                value_input = input("> ").strip()

                if len(value_input) > 2000:
                    print("Value must be 2000 characters or less.")
                    continue

                default_value = value_input
        else:
            print("\nDefault state:")
            print("  1. Off (-)")
            print("  2. On (+)")
            value_choice = input("\nChoose [1-2]: ").strip()
            if value_choice == "1":
                default_value = "-"
            elif value_choice == "2":
                default_value = "+"
            else:
                print("Invalid choice.")
                continue

        # Step 5: Description
        print("\nEnter description:")
        description = input("> ").strip()
        if not description:
            description = "No description"

        # Build the option line
        if is_value_option:
            prefix = "V:" if is_dynamic else "v:"
            option_line = f"{prefix}{opt_name} <<{default_value}>> {description}"
        else:
            prefix = "C:" if is_dynamic else "c:"
            option_line = f"{prefix}{opt_name} {default_value} {description}"

        # Step 6: Confirmation
        print("\n--- Review ---")
        print(f"Option line: {option_line}")
        print(f"  Type: {'Value' if is_value_option else 'On/Off'}")
        print(f"  Dynamic: {'Yes' if is_dynamic else 'No'}")
        print(f"  Name: {opt_name}")
        if is_value_option:
            print(f"  Default value: <<{default_value}>>")
        else:
            print(f"  Default state: {'On (+)' if default_value == '+' else 'Off (-)'}")
        print(f"  Description: {description}")

        if console_yes_no("\nIs this correct?", default=True):
            return option_line
        # If not correct, loop back to start


def prompt_modification_info() -> dict:
    """
    Prompt user for modification/change log information.

    Returns:
        Dictionary with keys: date, name, mod_num, reason
        Or None if user cancels
    """
    print("\n--- Modification Information ---")
    print("This will be added to the options.def header.")

    # Auto-generate date in YYMMDD format
    date_str = datetime.now().strftime("%y%m%d")
    print(f"\nDate: {date_str} (auto-generated)")

    # Prompt for name
    print("\nEnter your name:")
    name = input("> ").strip().upper()
    if not name:
        print("Name is required.")
        return None

    # Prompt for MOD #
    print("\nEnter MOD # (e.g., 07.95.27639):")
    mod_num = input("> ").strip()
    if not mod_num:
        print("MOD # is required.")
        return None

    # Prompt for reason
    print("\nEnter reason/description:")
    reason = input("> ").strip()
    if not reason:
        print("Reason is required.")
        return None

    # Show summary
    chg_line = f"CHG {date_str} {name}    {mod_num}    {reason}"
    print(f"\nChange log entry: {chg_line}")

    if not console_yes_no("Is this correct?", default=True):
        return None

    return {
        'date': date_str,
        'name': name,
        'mod_num': mod_num,
        'reason': reason,
        'chg_line': chg_line
    }


def add_chg_to_header(lines: list, chg_line: str) -> list:
    """
    Add a CHG line to the header section of an options file.

    Looks for existing CHG lines and adds the new one after the last one.
    If no CHG lines exist, adds after the first line.

    Args:
        lines: List of file lines
        chg_line: The CHG line to add

    Returns:
        Modified list with CHG line inserted
    """
    result = lines.copy()

    # Find the last CHG line in the header
    last_chg_idx = -1
    for i, line in enumerate(result):
        if line.strip().startswith('CHG '):
            last_chg_idx = i
        # Stop looking after we hit actual option lines
        if line.strip().startswith(('v:', 'V:', 'c:', 'C:')):
            break

    if last_chg_idx >= 0:
        # Insert after last CHG line
        result.insert(last_chg_idx + 1, chg_line)
    elif result:
        # No CHG lines found, insert after first line
        result.insert(1, chg_line)
    else:
        # Empty file
        result.append(chg_line)

    return result


def run_add_mode(config: dict, company_file: str, profile_file: str):
    """
    Add mode: Interactively create new options and merge into company/profile files.

    This mode guides users through creating new options:
    1. Prompt for modification information (CHG line)
    2. Interactive wizard to create new options one at a time
    3. Each option is saved to options.def (with CHG in header)
    4. When done, prompts to merge new options into company file
    5. Optionally merge into profile file

    Args:
        config: Configuration dictionary
        company_file: Path to options.{COMPANY} file
        profile_file: Path to options.{COMPANY}.{PROFILE} file
    """
    def_file = get_options_def_path(config)

    if not os.path.exists(def_file):
        print(f"ERROR: options.def not found: {def_file}")
        sys.exit(1)

    # Load existing def file
    def_lines = load_options_file(def_file)
    new_options_added = []

    print(f"\nAdding new options to: {def_file}")

    # Prompt for modification information first
    mod_info = prompt_modification_info()
    if mod_info is None:
        print("Cancelled.")
        return

    # Interactive option creation loop
    while True:
        option_line = create_new_option_interactive(def_lines)

        if option_line is None:
            # User cancelled creating this option
            break

        # Add to tracking list (also add to def_lines for duplicate checking)
        new_options_added.append(option_line)
        def_lines.append(option_line)
        print(f"\nOption added: {option_line}")

        # Ask if user wants to add another
        if not console_yes_no("Add another option?", default=True):
            break

    # Save all new options with MOD markers
    if new_options_added:
        # Reload def_lines fresh (without our temp additions for dupe checking)
        def_lines = load_options_file(def_file)

        # Add CHG to header
        def_lines = add_chg_to_header(def_lines, mod_info['chg_line'])

        # Add MOD start marker, options, and end marker
        mod_num = mod_info['mod_num']
        def_lines.append(f"# {mod_num} -->")
        for opt in new_options_added:
            def_lines.append(opt)
        def_lines.append(f"# {mod_num} <--")

        save_options_file(def_file, def_lines)
        print(f"\n{len(new_options_added)} option(s) added to options.def")
    else:
        print("No options added.")

    # Prompt to merge options from options.def into company file
    # This compares ALL options in options.def against company file
    if console_yes_no(f"\nMerge options from options.def into {company_file}?", default=True):
        # Reload def_lines to get the latest (including any just added)
        def_lines = load_options_file(def_file)
        company_lines = load_options_file(company_file)

        # Check for platform-specific options to exclude
        platform_file = get_options_platform_path(config)
        merge_options = def_lines.copy()

        if os.path.exists(platform_file):
            platform = config.get('PLATFORM', 'SYBASE')
            print(f"Excluding {platform}-specific options...")
            platform_lines = load_options_file(platform_file)
            merge_options = remove_options(merge_options, platform_lines)

        # Find which options from def don't exist in company file
        options_to_add = find_new_options(merge_options, company_lines)

        if not options_to_add:
            print("No new options to add. Company file is up to date.")
        else:
            print(f"\n{len(options_to_add)} new option(s) found. We will go through each one.")

            # Loop through each option and ask user if they want to add it
            customized_options = []
            for i, opt in enumerate(options_to_add, 1):
                # Parse and display option details
                line = opt.strip()
                prefix = line[:2]
                opt_type = prefix.lower()
                is_dynamic = prefix[0].isupper()
                content = line[2:].strip()

                # Extract name
                space_idx = content.find(' ')
                if space_idx == -1:
                    opt_name = content
                    rest = ""
                else:
                    opt_name = content[:space_idx].strip()
                    rest = content[space_idx:].strip()

                # Extract value/state and description
                if opt_type == 'v:':
                    start_idx = rest.find('<<')
                    end_idx = rest.find('>>')
                    if start_idx != -1 and end_idx != -1:
                        value = rest[start_idx + 2:end_idx]
                        description = rest[end_idx + 2:].strip()
                    else:
                        value = ""
                        description = rest
                    type_str = "Value"
                    value_str = f"<<{value}>>"
                else:
                    if rest.startswith('-'):
                        value = "Off (-)"
                        description = rest[1:].strip()
                    elif rest.startswith('+'):
                        value = "On (+)"
                        description = rest[1:].strip()
                    else:
                        value = "On (+)"
                        description = rest
                    type_str = "On/Off"
                    value_str = value

                print(f"\n--- Option {i} of {len(options_to_add)} ---")
                print(f"  Name: {opt_name}")
                print(f"  Type: {type_str} ({'dynamic' if is_dynamic else 'static'})")
                print(f"  Default: {value_str}")
                print(f"  Description: {description}")

                if console_yes_no("Add this option?", default=True):
                    # Let user customize the value
                    customized_opt = prompt_option_value(opt)
                    customized_options.append(customized_opt)
                else:
                    print("  Skipped.")

            if customized_options:
                # Merge with MOD markers
                merged = insert_new_options(company_lines, customized_options, mod_info['mod_num'])
                # Add CHG to header
                merged = add_chg_to_header(merged, mod_info['chg_line'])
                save_options_file(company_file, merged)
                print(f"\n{len(customized_options)} option(s) merged into {company_file}")

                # Optionally open for editing
                if console_yes_no(f"Edit {company_file}?", default=False):
                    launch_editor(company_file)
            else:
                print("\nNo options were added.")

    # Prompt to merge into profile file
    if console_yes_no(f"\nMerge options from options.def into {profile_file}?", default=False):
        # Reload def_lines
        def_lines = load_options_file(def_file)
        profile_lines = load_options_file(profile_file)

        # Find which options from def don't exist in profile file
        options_to_add = find_new_options(def_lines, profile_lines)

        if not options_to_add:
            print("No new options to add. Profile file is up to date.")
        else:
            print(f"\n{len(options_to_add)} new option(s) found. We will go through each one.")

            # Loop through each option and ask user if they want to add it
            customized_options = []
            for i, opt in enumerate(options_to_add, 1):
                # Parse and display option details
                line = opt.strip()
                prefix = line[:2]
                opt_type = prefix.lower()
                is_dynamic = prefix[0].isupper()
                content = line[2:].strip()

                # Extract name
                space_idx = content.find(' ')
                if space_idx == -1:
                    opt_name = content
                    rest = ""
                else:
                    opt_name = content[:space_idx].strip()
                    rest = content[space_idx:].strip()

                # Extract value/state and description
                if opt_type == 'v:':
                    start_idx = rest.find('<<')
                    end_idx = rest.find('>>')
                    if start_idx != -1 and end_idx != -1:
                        value = rest[start_idx + 2:end_idx]
                        description = rest[end_idx + 2:].strip()
                    else:
                        value = ""
                        description = rest
                    type_str = "Value"
                    value_str = f"<<{value}>>"
                else:
                    if rest.startswith('-'):
                        value = "Off (-)"
                        description = rest[1:].strip()
                    elif rest.startswith('+'):
                        value = "On (+)"
                        description = rest[1:].strip()
                    else:
                        value = "On (+)"
                        description = rest
                    type_str = "On/Off"
                    value_str = value

                print(f"\n--- Option {i} of {len(options_to_add)} ---")
                print(f"  Name: {opt_name}")
                print(f"  Type: {type_str} ({'dynamic' if is_dynamic else 'static'})")
                print(f"  Default: {value_str}")
                print(f"  Description: {description}")

                if console_yes_no("Add this option?", default=True):
                    # Let user customize the value
                    customized_opt = prompt_option_value(opt)
                    customized_options.append(customized_opt)
                else:
                    print("  Skipped.")

            if customized_options:
                # Merge with MOD markers
                merged = insert_new_options(profile_lines, customized_options, mod_info['mod_num'])
                # Add CHG to header
                merged = add_chg_to_header(merged, mod_info['chg_line'])
                save_options_file(profile_file, merged)
                print(f"\n{len(customized_options)} option(s) merged into {profile_file}")

                # Optionally open for editing
                if console_yes_no(f"Edit {profile_file}?", default=False):
                    launch_editor(profile_file)
            else:
                print("\nNo options were added.")


def run_edit_mode(config: dict, company_file: str, profile_file: str):
    """
    Edit mode: Edit existing profile and company options files.

    This mode is for editing existing options without adding new ones:
    1. Prompts to edit profile options file (default: No)
    2. Prompts to edit company options file (default: No)

    Note: Profile (server-specific) file is prompted first, then company file.
    This matches the original C# eopt behavior.

    Args:
        config: Configuration dictionary
        company_file: Path to options.{COMPANY} file
        profile_file: Path to options.{COMPANY}.{PROFILE} file
    """
    # Prompt to edit profile options file FIRST (matches C# order)
    if os.path.exists(profile_file):
        if console_yes_no(f"Edit {profile_file}?", default=False):
            launch_editor(profile_file)
    else:
        print(f"Profile options file not found: {profile_file}")

    # Prompt to edit company options file
    if os.path.exists(company_file):
        if console_yes_no(f"Edit {company_file}?", default=False):
            launch_editor(company_file)
    else:
        print(f"Company options file not found: {company_file}")


def run_merge_mode(config: dict, company_file: str, profile_file: str):
    """
    Merge mode: Merge new options from options.def into company/profile files.

    This mode compares options.def against company/profile files and offers
    to add any missing options. No new options are created in options.def.
    No modification info is prompted since options.def header is not modified.

    Args:
        config: Configuration dictionary
        company_file: Path to options.{COMPANY} file
        profile_file: Path to options.{COMPANY}.{PROFILE} file
    """
    def_file = get_options_def_path(config)

    if not os.path.exists(def_file):
        print(f"ERROR: options.def not found: {def_file}")
        sys.exit(1)

    # Load options.def
    def_lines = load_options_file(def_file)

    # Merge into company file
    if console_yes_no(f"\nMerge options from options.def into {company_file}?", default=True):
        company_lines = load_options_file(company_file)

        # Check for platform-specific options to exclude
        platform_file = get_options_platform_path(config)
        merge_options = def_lines.copy()

        if os.path.exists(platform_file):
            platform = config.get('PLATFORM', 'SYBASE')
            print(f"Excluding {platform}-specific options...")
            platform_lines = load_options_file(platform_file)
            merge_options = remove_options(merge_options, platform_lines)

        # Find which options from def don't exist in company file
        options_to_add = find_new_options(merge_options, company_lines)

        if not options_to_add:
            print("No new options to add. Company file is up to date.")
        else:
            print(f"\n{len(options_to_add)} new option(s) found. We will go through each one.")

            # Loop through each option and ask user if they want to add it
            customized_options = []
            for i, opt in enumerate(options_to_add, 1):
                # Parse and display option details
                line = opt.strip()
                prefix = line[:2]
                opt_type = prefix.lower()
                is_dynamic = prefix[0].isupper()
                content = line[2:].strip()

                # Extract name
                space_idx = content.find(' ')
                if space_idx == -1:
                    opt_name = content
                    rest = ""
                else:
                    opt_name = content[:space_idx].strip()
                    rest = content[space_idx:].strip()

                # Extract value/state and description
                if opt_type == 'v:':
                    start_idx = rest.find('<<')
                    end_idx = rest.find('>>')
                    if start_idx != -1 and end_idx != -1:
                        value = rest[start_idx + 2:end_idx]
                        description = rest[end_idx + 2:].strip()
                    else:
                        value = ""
                        description = rest
                    type_str = "Value"
                    value_str = f"<<{value}>>"
                else:
                    if rest.startswith('-'):
                        value = "Off (-)"
                        description = rest[1:].strip()
                    elif rest.startswith('+'):
                        value = "On (+)"
                        description = rest[1:].strip()
                    else:
                        value = "On (+)"
                        description = rest
                    type_str = "On/Off"
                    value_str = value

                print(f"\n--- Option {i} of {len(options_to_add)} ---")
                print(f"  Name: {opt_name}")
                print(f"  Type: {type_str} ({'dynamic' if is_dynamic else 'static'})")
                print(f"  Default: {value_str}")
                print(f"  Description: {description}")

                if console_yes_no("Add this option?", default=True):
                    # Let user customize the value
                    customized_opt = prompt_option_value(opt)
                    customized_options.append(customized_opt)
                else:
                    print("  Skipped.")

            if customized_options:
                # Merge without MOD markers (no header modification in merge mode)
                merged = insert_new_options(company_lines, customized_options)
                save_options_file(company_file, merged)
                print(f"\n{len(customized_options)} option(s) merged into {company_file}")

                # Optionally open for editing
                if console_yes_no(f"Edit {company_file}?", default=False):
                    launch_editor(company_file)
            else:
                print("\nNo options were added.")

    # Merge into profile file
    if console_yes_no(f"\nMerge options from options.def into {profile_file}?", default=False):
        profile_lines = load_options_file(profile_file)

        # Find which options from def don't exist in profile file
        options_to_add = find_new_options(def_lines, profile_lines)

        if not options_to_add:
            print("No new options to add. Profile file is up to date.")
        else:
            print(f"\n{len(options_to_add)} new option(s) found. We will go through each one.")

            # Loop through each option and ask user if they want to add it
            customized_options = []
            for i, opt in enumerate(options_to_add, 1):
                # Parse and display option details
                line = opt.strip()
                prefix = line[:2]
                opt_type = prefix.lower()
                is_dynamic = prefix[0].isupper()
                content = line[2:].strip()

                # Extract name
                space_idx = content.find(' ')
                if space_idx == -1:
                    opt_name = content
                    rest = ""
                else:
                    opt_name = content[:space_idx].strip()
                    rest = content[space_idx:].strip()

                # Extract value/state and description
                if opt_type == 'v:':
                    start_idx = rest.find('<<')
                    end_idx = rest.find('>>')
                    if start_idx != -1 and end_idx != -1:
                        value = rest[start_idx + 2:end_idx]
                        description = rest[end_idx + 2:].strip()
                    else:
                        value = ""
                        description = rest
                    type_str = "Value"
                    value_str = f"<<{value}>>"
                else:
                    if rest.startswith('-'):
                        value = "Off (-)"
                        description = rest[1:].strip()
                    elif rest.startswith('+'):
                        value = "On (+)"
                        description = rest[1:].strip()
                    else:
                        value = "On (+)"
                        description = rest
                    type_str = "On/Off"
                    value_str = value

                print(f"\n--- Option {i} of {len(options_to_add)} ---")
                print(f"  Name: {opt_name}")
                print(f"  Type: {type_str} ({'dynamic' if is_dynamic else 'static'})")
                print(f"  Default: {value_str}")
                print(f"  Description: {description}")

                if console_yes_no("Add this option?", default=True):
                    # Let user customize the value
                    customized_opt = prompt_option_value(opt)
                    customized_options.append(customized_opt)
                else:
                    print("  Skipped.")

            if customized_options:
                # Merge without MOD markers (no header modification in merge mode)
                merged = insert_new_options(profile_lines, customized_options)
                save_options_file(profile_file, merged)
                print(f"\n{len(customized_options)} option(s) merged into {profile_file}")

                # Optionally open for editing
                if console_yes_no(f"Edit {profile_file}?", default=False):
                    launch_editor(profile_file)
            else:
                print("\nNo options were added.")


def main(args_list=None):
    """
    Main entry point for the eopt command.

    Workflow:
        1. Load configuration for the specified profile
        2. Determine mode (Add or Edit)
        3. Run appropriate mode to edit source files
        4. Prompt to import into database (default: Yes)
        5. Call compile_options() to parse and insert data
        6. compile_options() also updates table_locations

    Args:
        args_list: Command line arguments (defaults to sys.argv[1:])
    """
    if args_list is None:
        args_list = sys.argv[1:]

    parser = argparse.ArgumentParser(
        description="Edit and compile options.",
        usage="eopt PROFILE [-d] [-O output_file]"
    )
    parser.add_argument("profile", help="Configuration profile (required)")
    parser.add_argument("-d", "--developer", action="store_true",
                        help="Add mode: merge new options from options.def")
    parser.add_argument("-O", "--outfile", help="Output file for messages")

    args = parser.parse_args(args_list)

    # Load config from profile (contains HOST, PORT, USERNAME, PASSWORD, SQL_SOURCE, etc.)
    # PROFILE_NAME is set by get_config (resolves aliases to real profile name)
    config = get_config(profile_name=args.profile)

    # Get paths to options files
    company_file = get_options_company_path(config)
    profile_file = get_options_profile_path(config)

    # Determine mode
    if args.developer:
        # -d flag: Add mode
        run_add_mode(config, company_file, profile_file)
    else:
        # No flag: Ask user
        print("\nWhat do you want to do?")
        print("  1. Add new options (create in options.def)")
        print("  2. Edit existing options")
        print("  3. Merge new options (from options.def into company/profile)")

        choice = input("\nChoose [1-3]: ").strip()

        if choice == "1":
            run_add_mode(config, company_file, profile_file)
        elif choice == "2":
            run_edit_mode(config, company_file, profile_file)
        elif choice == "3":
            run_merge_mode(config, company_file, profile_file)
        else:
            print("Invalid choice.")
            sys.exit(1)

    # Prompt to compile/import into database (default: Yes)
    if not console_yes_no(f"Import options into {args.profile.upper()}?", default=True):
        print("Finished.")
        sys.exit(0)

    # Collect output messages
    output_lines = []

    def output(msg):
        """Write message to output file or console."""
        if args.outfile:
            output_lines.append(msg)
        else:
            print(msg)

    # Compile: parse options files and insert into database
    # This also updates table_locations since options may affect database mappings
    output("Compiling options...")
    success, message = compile_options(config)

    if success:
        output(f"SUCCESS: {message}")
    else:
        output(f"ERROR: {message}")

    # Write output file if specified
    if args.outfile:
        try:
            with open(args.outfile, 'w', encoding='utf-8') as f:
                f.write("\n".join(output_lines) + "\n")
        except IOError as e:
            print(f"ERROR: Failed to write output file: {e}", file=sys.stderr)
            sys.exit(1)

    if not success:
        sys.exit(1)


if __name__ == "__main__":
    main()
