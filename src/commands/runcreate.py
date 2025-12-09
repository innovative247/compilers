"""
runcreate.py: Orchestrate multi-step database builds from a master script.

This script reads a create file (e.g., create_all, create_pro, create_tbl) and
executes each line by dispatching to the appropriate command (runsql, runcreate, etc.).

CREATE FILE FORMAT:
    # Comment lines start with #
    #NT Windows-only lines start with #NT (strip #NT and execute)
    #UNIX Unix-only lines start with #UNIX (skip on Windows)

    Lines starting with &option& are conditional:
        &if_mssql& runsql ...  -> Only execute if mssql option is enabled
        &ifn_mssql& runsql ... -> Only execute if mssql option is NOT enabled

    Path format: $ir>css>ss>ba>file -> {SQL_SOURCE}/css/ss/ba/file

    Commands: runsql, runcreate, i_run_upgrade, isqlline,
              install_msg, install_required_fields, import_options,
              create_tbl_locations, compile_actions

OPTIONS:
    -O output.log   Creates/overwrites file at start, then appends all output
    -e              Echo input (verbose mode - maps to tsql -v)

OUTPUT FILE BEHAVIOR:
    When -O is specified:
    - File is created/overwritten at runcreate start
    - All runsql, isqlline output appends to this file
    - Nested runcreate calls also append to the same file
    - Console output is suppressed (goes to file only)

USAGE:
    runcreate create_file PROFILE
    runcreate create_all GONZO
    runcreate create_all GONZO -O build.log
    runcreate create_all GONZO -e
    runcreate css/ss/ba/create_pro GONZO

CHG 241208 Rewritten to match C# runcreate functionality
CHG 241208 Added -O output file and -e echo flag support
"""

import argparse
import sys
import re
import os

from .ibs_common import (
    get_config,
    execute_sql_native,
    find_file,
    Options,
    convert_non_linked_paths,
    create_symbolic_links,
)


def write_output(message: str, output_handle=None):
    """
    Write a message to stdout or to an open file handle.

    Args:
        message: The message to write
        output_handle: Open file handle, or None for stdout
    """
    if output_handle:
        output_handle.write(message + '\n')
        output_handle.flush()
    else:
        print(message)


def convert_ir_path(line: str, sql_source: str) -> str:
    """
    Convert $ir>path>to>file or $ir/path/to/file to {SQL_SOURCE}/path/to/file.

    Handles both formats found in create files:
        $ir>css>ss>ub>filename (> separators)
        $ir/css/ss/ub/filename (/ separators)

    Args:
        line: Line containing $ir... path
        sql_source: Base SQL_SOURCE directory

    Returns:
        Line with $ir path converted to full path
    """
    # Check for $ir followed by either > or /
    if '$ir' not in line.lower():
        return line

    # Match $ir followed by > or / and then the path
    # Pattern: $ir>path>path>file OR $ir/path/path/file
    match = re.search(r'\$ir[>/]([^\s]+)', line, re.IGNORECASE)
    if match:
        ir_path = match.group(0)  # Full match including $ir> or $ir/
        path_part = match.group(1)  # Just the path after $ir> or $ir/
        # Convert > to / (handles both formats uniformly)
        converted_path = path_part.replace('>', '/')
        # Apply symbolic path conversion (e.g., ss/ub -> SQL_Sources/US_Basics)
        converted_path = convert_non_linked_paths(converted_path)
        # Now convert to OS separator and join with sql_source
        converted_path = converted_path.replace('/', os.sep)
        full_path = os.path.join(sql_source, converted_path)
        line = line.replace(ir_path, full_path)

    return line


def extract_sequence_flags(line: str) -> tuple:
    """
    Extract -F and -L sequence flags from line.

    Returns:
        Tuple of (cleaned_line, seq_first, seq_last)
    """
    seq_first = 1
    seq_last = 1

    # Extract -F{n} -L{n} pattern
    # Pattern like: -F0 -L5 or -F1-L3
    f_match = re.search(r'-F\s*(\d+)', line)
    l_match = re.search(r'-L\s*(\d+)', line)

    if f_match:
        seq_first = int(f_match.group(1))
        line = line[:f_match.start()] + line[f_match.end():]

    if l_match:
        seq_last = int(l_match.group(1))
        line = line[:l_match.start()] + line[l_match.end():]

    return line.strip(), seq_first, seq_last


def extract_databases(line: str, options: Options) -> list:
    """
    Extract database names from -D flags or positional arguments and resolve placeholders.

    Formats supported:
        runsql ... -D&dbpro& -D&dbtbl&  (explicit -D flags)
        runsql ... &dbpro&              (positional after script path)

    Args:
        line: Command line potentially containing database references
        options: Options instance for placeholder resolution

    Returns:
        List of unique resolved database names
    """
    # Normalize -d to -D
    line = line.replace('-d', '-D')

    databases = []

    # First, extract databases from -D flags
    if '-D' in line:
        parts = line.split('-D')
        for i, part in enumerate(parts):
            if i == 0:
                continue  # Skip part before first -D
            # Get the database name (first word after -D)
            db_part = part.strip().split()[0] if part.strip() else ''
            if db_part:
                # Resolve placeholders
                resolved = options.replace_options(db_part)
                if resolved and resolved not in databases:
                    databases.append(resolved)
    else:
        # No -D flags - look for positional database placeholders like &dbpro&, &dbtbl&, etc.
        # These appear after the script path
        import re
        db_placeholders = re.findall(r'&db\w+&', line)
        for placeholder in db_placeholders:
            resolved = options.replace_options(placeholder)
            if resolved and resolved not in databases:
                databases.append(resolved)

    return databases


def clean_line(line: str) -> str:
    """
    Clean a line by removing legacy elements.

    Removes: $1, -o, -S&sv&, &sv&, tabs
    """
    line = line.replace('\t', ' ')
    line = line.replace('$1', '')
    line = re.sub(r'-o\s*', '', line)  # Remove -o flag
    line = line.replace('-S&sv&', '').replace('-S &sv&', '')
    line = line.replace('&sv&', '')
    # Clean up multiple spaces
    line = re.sub(r'\s+', ' ', line).strip()
    return line


def parse_line(line: str, options: Options, sql_source: str) -> dict:
    """
    Parse a single line from a create file.

    Returns:
        Dictionary with: command, args, databases, seq_first, seq_last, skip
    """
    result = {
        'command': None,
        'script_file': None,
        'databases': [],
        'seq_first': 1,
        'seq_last': 1,
        'skip': False,
        'raw_line': line
    }

    line = line.strip()

    # Skip empty lines
    if not line:
        result['skip'] = True
        return result

    # Handle #UNIX lines (skip on Windows)
    if line.startswith('#UNIX'):
        result['skip'] = True
        return result

    # Handle #NT lines (Windows-specific, strip prefix and process)
    if line.startswith('#NT'):
        line = line[3:].strip()

    # Skip comment lines
    if line.startswith('#'):
        result['skip'] = True
        return result

    # Handle conditional options at start of line: &if_xxx& or &ifn_xxx&
    if line.startswith('&'):
        # Find the closing &
        end_idx = line.index('&', 1) if '&' in line[1:] else -1
        if end_idx > 0:
            opt_placeholder = line[:end_idx + 1]
            resolved = options.replace_options(opt_placeholder).strip()
            # If resolved to empty or the placeholder itself, skip line
            # &if_xxx& resolves to '' if option is ON (execute line)
            # &if_xxx& resolves to '/*' or similar if OFF (skip line)
            if resolved and resolved != '':
                # Option resolved to something (like /* comment), skip line
                result['skip'] = True
                return result
            # Option resolved to empty, continue processing
            line = line[end_idx + 1:].strip()

    # Clean the line
    line = clean_line(line)

    if not line:
        result['skip'] = True
        return result

    # Extract command type (first word)
    parts = line.split(None, 1)
    if not parts:
        result['skip'] = True
        return result

    result['command'] = parts[0].lower()
    remaining = parts[1] if len(parts) > 1 else ''

    # Convert $ir> paths
    remaining = convert_ir_path(remaining, sql_source)

    # Extract -F/-L sequence flags
    remaining, result['seq_first'], result['seq_last'] = extract_sequence_flags(remaining)

    # Extract databases from -D flags or positional &db*& placeholders
    result['databases'] = extract_databases(remaining, options)

    # Remove -D flags and database names from remaining to get script file
    # Pattern: remove -D followed by word
    script_line = re.sub(r'-D\s*\S+', '', remaining).strip()

    # Also remove positional database placeholders (&dbpro&, &dbtbl&, etc.)
    script_line = re.sub(r'&db\w+&', '', script_line).strip()

    # The script file should be the first remaining path
    script_parts = script_line.split()
    if script_parts:
        result['script_file'] = script_parts[0]

    return result


def execute_runsql(config: dict, options: Options, script_file: str, database: str,
                   seq_first: int = 1, seq_last: int = 1, output_handle=None,
                   echo_input: bool = False):
    """
    Execute a SQL script file, looping through sequences if specified.

    The -F and -L flags specify a range of sequence numbers. The script is
    executed once for each sequence, with @sequence@ replaced by the number.

    Example: -F1 -L16 runs the script 16 times with @sequence@ = 1, 2, ... 16
    This creates tables/procs like ma_alarmqueue1, ma_alarmqueue2, etc.

    Args:
        config: Configuration dictionary
        options: Options instance for placeholder resolution
        script_file: Path to the SQL script file (may be full path or relative)
        database: Target database name
        seq_first: First sequence number (default 1)
        seq_last: Last sequence number (default 1)
        output_handle: Open file handle for output, or None for stdout
        echo_input: If True, echo SQL input (maps to tsql -v)
    """
    # Add .sql extension if not present and not a create* file
    if not script_file.endswith('.sql') and not os.path.basename(script_file).startswith('create'):
        script_file = script_file + '.sql'

    # runcreate never searches - paths must be fully specified via $ir conversion
    # This ensures no interactive prompts when running with -O output file
    if os.path.isabs(script_file):
        if os.path.isfile(script_file):
            script_path = script_file
        else:
            write_output(f"  ERROR: File not found: {script_file}", output_handle)
            return False
    else:
        # Relative path not supported in runcreate - log error and continue
        write_output(f"  ERROR: Relative path not supported: {script_file}", output_handle)
        return False

    # Read script content once
    with open(script_path, 'r', encoding='utf-8', errors='replace') as f:
        raw_sql = f.read()

    profile = config.get('PROFILE_NAME', '')

    # Loop through sequences
    for seq in range(seq_first, seq_last + 1):
        if seq_first != seq_last:
            write_output(f"Running {seq} of {seq_last}: {script_path} on {profile}.{database}", output_handle)
        else:
            write_output(f"Running: {script_path} on {profile}.{database}", output_handle)

        # Replace placeholders including @sequence@
        sql_content = options.replace_options(raw_sql, sequence=seq)

        # Echo input if requested
        if echo_input:
            write_output(f"-- Executing SQL (sequence {seq}):", output_handle)
            write_output(sql_content, output_handle)
            write_output("--", output_handle)

        # Execute
        success, output = execute_sql_native(
            host=config.get('HOST'),
            port=config.get('PORT'),
            username=config.get('USERNAME'),
            password=config.get('PASSWORD'),
            database=database,
            platform=config.get('PLATFORM', 'SYBASE'),
            sql_content=sql_content,
            echo_input=echo_input
        )

        if not success:
            write_output(f"    ERROR: {output[:200]}", output_handle)
            return False

        # Write SQL output if any
        if output and output.strip():
            write_output(output.strip(), output_handle)

    return True


def execute_isqlline(config: dict, options: Options, sql: str, database: str,
                     output_handle=None, echo_input: bool = False):
    """
    Execute inline SQL.

    Args:
        config: Configuration dictionary
        options: Options instance for placeholder resolution
        sql: SQL command to execute
        database: Target database name
        output_handle: Open file handle for output, or None for stdout
        echo_input: If True, echo SQL input (maps to tsql -v)
    """
    # Resolve placeholders in SQL
    sql = options.replace_options(sql)

    write_output(f"  isqlline -> {database}", output_handle)

    # Echo input if requested
    if echo_input:
        write_output("-- Executing SQL:", output_handle)
        write_output(sql, output_handle)
        write_output("--", output_handle)

    success, output = execute_sql_native(
        host=config.get('HOST'),
        port=config.get('PORT'),
        username=config.get('USERNAME'),
        password=config.get('PASSWORD'),
        database=database,
        platform=config.get('PLATFORM', 'SYBASE'),
        sql_content=sql,
        echo_input=echo_input
    )

    if not success:
        write_output(f"    ERROR: {output[:200]}", output_handle)
        return False

    # Write SQL output if any
    if output and output.strip():
        write_output(output.strip(), output_handle)

    return True


def run_create_file(config: dict, options: Options, create_file_path: str,
                    output_handle=None, echo_input: bool = False):
    """
    Process a create file and execute each command.

    Args:
        config: Configuration dictionary
        options: Options instance
        create_file_path: Path to the create file
        output_handle: Open file handle for output, or None for stdout
        echo_input: If True, echo SQL input (maps to tsql -v)
    """
    sql_source = config.get('SQL_SOURCE', os.getcwd())
    server = config.get('PROFILE_NAME', '')

    write_output(f"RUNCREATE {create_file_path} on {server}...", output_handle)

    with open(create_file_path, 'r', encoding='utf-8', errors='replace') as f:
        lines = f.readlines()

    for line_num, line in enumerate(lines, 1):
        parsed = parse_line(line, options, sql_source)

        if parsed['skip']:
            continue

        command = parsed['command']
        script_file = parsed['script_file']
        databases = parsed['databases']
        seq_first = parsed['seq_first']
        seq_last = parsed['seq_last']

        if command == 'runsql':
            if not script_file:
                write_output(f"  Line {line_num}: runsql missing script file", output_handle)
                continue

            # If no databases specified, use default from config
            if not databases:
                db = config.get('DATABASE') or options.replace_options('&dbtbl&')
                databases = [db] if db else []

            # Execute for each database
            for db in databases:
                execute_runsql(config, options, script_file, db, seq_first, seq_last, output_handle, echo_input)

        elif command == 'runcreate':
            if not script_file:
                write_output(f"  Line {line_num}: runcreate missing script file", output_handle)
                continue

            # runcreate never searches - paths must be fully specified via $ir conversion
            if os.path.isabs(script_file):
                if os.path.isfile(script_file):
                    nested_path = script_file
                else:
                    write_output(f"  ERROR: File not found: {script_file}", output_handle)
                    continue
            else:
                write_output(f"  ERROR: Relative path not supported: {script_file}", output_handle)
                continue

            # Recursive call - passes same output_handle and echo_input
            run_create_file(config, options, nested_path, output_handle, echo_input)

        elif command == 'i_run_upgrade':
            # i_run_upgrade format: i_run_upgrade ... sct_xx.yy.zzzzz...
            # The upgrade_no and script are parsed from the line
            write_output(f"  i_run_upgrade (line {line_num}): {parsed['raw_line'][:60]}...", output_handle)
            # For now, just note it - full implementation would call i_run_upgrade

        elif command == 'isqlline':
            # isqlline has inline SQL in quotes
            # Format: isqlline -o -Usa $1 "SQL here" database server
            match = re.search(r'"([^"]+)"', parsed['raw_line'])
            if match:
                sql = match.group(1)
                # Get database from the line
                db = databases[0] if databases else config.get('DATABASE', '')
                if db:
                    execute_isqlline(config, options, sql, db, output_handle, echo_input)

        elif command == 'install_msg':
            write_output(f"  install_msg (line {line_num})", output_handle)
            # Would call compile_msg

        elif command == 'install_required_fields':
            write_output(f"  install_required_fields (line {line_num})", output_handle)
            # Would call compile_required_fields

        elif command == 'import_options':
            write_output(f"  import_options (line {line_num})", output_handle)
            # Would call eopt

        elif command == 'create_tbl_locations':
            write_output(f"  create_tbl_locations (line {line_num})", output_handle)
            # Would call eloc

        elif command == 'compile_actions':
            write_output(f"  compile_actions (line {line_num})", output_handle)
            # Would call eact

        else:
            # Unknown command - skip
            pass


def main(args_list=None):
    """Main entry point for runcreate."""
    if args_list is None:
        args_list = sys.argv[1:]

    # Handle help
    if len(args_list) == 0 or '-h' in args_list or '--help' in args_list:
        print("Usage:")
        print("  runcreate create_file profile [-O output_file] [-e]")
        print()
        print("Options:")
        print("  -O, --output   Output file (creates/overwrites at start, then appends)")
        print("  -e, --echo     Echo input commands (maps to tsql -v)")
        print()
        print("Examples:")
        print("  runcreate create_all GONZO")
        print("  runcreate create_all GONZO -O build.log")
        print("  runcreate create_all GONZO -e")
        print("  runcreate css/ss/ba/create_pro GONZO")
        print("  runcreate create_tbl GONZO -O output.txt")
        sys.exit(0)

    # Parse arguments manually to support -O and -e flags
    output_file = None
    echo_input = False
    positional_args = []
    i = 0
    while i < len(args_list):
        arg = args_list[i]
        if arg in ('-O', '--output'):
            if i + 1 < len(args_list):
                output_file = args_list[i + 1]
                i += 2
            else:
                print("ERROR: -O requires an output file path")
                sys.exit(1)
        elif arg.startswith('-O'):
            # Handle -Ofilename (no space)
            output_file = arg[2:]
            i += 1
        elif arg in ('-e', '--echo'):
            echo_input = True
            i += 1
        else:
            positional_args.append(arg)
            i += 1

    if len(positional_args) < 2:
        print("ERROR: Expected 2 arguments: create_file profile")
        sys.exit(1)

    create_file = positional_args[0]
    profile = positional_args[1]

    # Open output file handle if -O specified (kept open for entire run)
    output_handle = None
    if output_file:
        if os.path.exists(output_file):
            os.remove(output_file)
        output_handle = open(output_file, 'w', encoding='utf-8')

    # Load configuration
    try:
        config = get_config(args_list=[], profile_name=profile, allow_create=False)
    except KeyError:
        print(f"Profile '{profile}' does not exist. Run `set_profile`, then retry this command.")
        sys.exit(1)
    # PROFILE_NAME is already set by get_config (resolves aliases to real profile name)

    # Initialize options
    options = Options(config)
    if not options.generate_option_files():
        print("ERROR: Failed to load options files")
        sys.exit(1)

    # Ensure symbolic links exist before processing create files
    if not create_symbolic_links(config, prompt=False):
        print("ERROR: Failed to create symbolic links. Run `set_profile` to create them with Administrator privileges.")
        sys.exit(1)

    # Find the create file
    create_path = find_file(create_file, config)
    if not create_path:
        print(f"ERROR: Create file not found: {create_file}")
        sys.exit(1)

    # Run the create file
    run_create_file(config, options, create_path, output_handle, echo_input)

    write_output("runcreate DONE.", output_handle)

    # Close output file if open
    if output_handle:
        output_handle.close()


if __name__ == "__main__":
    main()
