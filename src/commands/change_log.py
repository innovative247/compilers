"""
change_log.py: Audit trail logging for IBS Compilers

This module provides change logging functionality that records database
modifications to the ba_gen_chg_log table for compliance and audit purposes.

The change log is injected as SQL before script execution and conditionally
executes based on the gclog12 option flag.

CHG 241124 Python implementation ported from C# change_log.cs
CHG 221110 SPORSGAARD 08.93.12266 General changelog support (original C#)
"""

import os
import logging
from typing import Generator, Optional


class ChangeLog:
    """
    Generates SQL statements for audit trail logging.

    When enabled via --changelog flag, injects SQL that:
    1. Checks if changelog is enabled (gclog12 option = '+')
    2. Verifies ba_gen_chg_log_new stored procedure exists
    3. Logs user, command, server, company, and reference info
    """

    def __init__(self, config: dict):
        """
        Initialize change log generator.

        Args:
            config: Configuration dictionary containing:
                - CHANGELOG: Boolean flag
                - USERNAME: User executing the command
                - COMMAND: Command being executed
                - DATABASE: Database name
                - SERVER: Server name
                - COMPANY: Company number
                - UPGRADE_NO: Upgrade reference number (optional)
        """
        self.config = config
        self.logger = logging.getLogger(__name__)
        self._who_am_i = ""

    def lines(self) -> Generator[str, None, None]:
        """
        Generates SQL lines for change logging.

        Yields SQL statements that:
        - Check if changelog option is enabled
        - Execute ba_gen_chg_log_new stored procedure
        - Include user, command, server, company, reference info

        Yields:
            SQL statement strings to inject before script execution

        Example output:
            if exists (select * from &options& where id = 'gclog12' and act_flg = '+')
            if exists (select * from &dbpro&..sysobjects where name = 'ba_gen_chg_log_new')
            exec &dbpro&..ba_gen_chg_log_new '', 'User `jake` recompiled sproc...', ...
            go
        """
        if not self.config.get('CHANGELOG', False):
            # Change logging not enabled
            return

        # Get username
        if not self._who_am_i:
            self._who_am_i = self.config.get('USERNAME', os.environ.get('USERNAME', 'unknown'))

        # Get command details
        command = self.config.get('COMMAND', '').replace("'", "''")  # SQL escape
        database = self.config.get('DATABASE', '')
        server = self.config.get('SERVER', '')
        company = self.config.get('COMPANY', '')
        ref_no = self.config.get('UPGRADE_NO', '')

        # Construct command string
        cmd_str = f"runsql {command} {database} {server} {company}"

        # Generate SQL statements
        yield "if exists (select * from &options& where id = 'gclog12' and act_flg = '+')"
        yield "if exists (select * from &dbpro&..sysobjects where name = 'ba_gen_chg_log_new')"
        yield f"exec &dbpro&..ba_gen_chg_log_new '', 'User `{self._who_am_i}` recompiled sproc or ran sql', 'RUNSQL', '', '{cmd_str}', '{ref_no}', 'X'"
        yield "go"
        yield ""  # Blank line for readability


def get_change_log_lines(config: dict) -> list:
    """
    Convenience function to get change log lines as a list.

    Args:
        config: Configuration dictionary

    Returns:
        List of SQL statement strings
    """
    change_log = ChangeLog(config)
    return list(change_log.lines())


def inject_change_log(sql_lines: list, config: dict) -> list:
    """
    Injects change log SQL at the beginning of a SQL script.

    Args:
        sql_lines: List of SQL statements
        config: Configuration dictionary with CHANGELOG flag

    Returns:
        SQL lines with change log statements prepended (if enabled)

    Example:
        original_sql = ['create procedure foo', 'as', 'select 1', 'go']
        result = inject_change_log(original_sql, {'CHANGELOG': True, ...})
        # Result will have change log statements at the beginning
    """
    if not config.get('CHANGELOG', False):
        return sql_lines

    change_log_lines = get_change_log_lines(config)

    if not change_log_lines:
        return sql_lines

    # Prepend change log lines
    return change_log_lines + sql_lines


# Module-level function for backward compatibility
def lines(config: dict) -> Generator[str, None, None]:
    """
    Module-level function that returns change log lines.

    Args:
        config: Configuration dictionary

    Yields:
        SQL statement strings for change logging

    Example:
        from change_log import lines

        for sql_line in lines(config):
            print(sql_line)
    """
    change_log = ChangeLog(config)
    yield from change_log.lines()
