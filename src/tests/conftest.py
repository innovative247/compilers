"""
Pytest configuration and shared fixtures for IBS Compilers tests.

This module provides common fixtures used across multiple test files.
"""

import pytest
import tempfile
import os
import json
from pathlib import Path


@pytest.fixture
def temp_dir():
    """Create a temporary directory for test files."""
    with tempfile.TemporaryDirectory() as tmpdir:
        yield Path(tmpdir)


@pytest.fixture
def sample_config():
    """Provide a sample configuration dictionary for testing."""
    return {
        'COMPANY': 101,
        'DEFAULT_LANGUAGE': 1,
        'SQL_SOURCE': 'C:\\_innovative\\_source\\current.sql',
        'PLATFORM': 'SYBASE',
        'HOST': '127.0.0.1',
        'PORT': 5000,
        'USERNAME': 'sa',
        'PASSWORD': 'testpass',
        'PROFILE_NAME': 'TEST'
    }


@pytest.fixture
def sample_settings_file(temp_dir):
    """Create a temporary settings.json file for testing."""
    settings = {
        "Profiles": {
            "TEST_PROFILE": {
                "COMPANY": 101,
                "DEFAULT_LANGUAGE": 1,
                "SQL_SOURCE": str(temp_dir),
                "PLATFORM": "SYBASE",
                "HOST": "127.0.0.1",
                "PORT": 5000,
                "USERNAME": "sa",
                "PASSWORD": "testpass"
            },
            "ANOTHER_PROFILE": {
                "COMPANY": 102,
                "DEFAULT_LANGUAGE": 1,
                "SQL_SOURCE": str(temp_dir),
                "PLATFORM": "MSSQL",
                "HOST": "localhost",
                "PORT": 1433,
                "USERNAME": "admin",
                "PASSWORD": "adminpass"
            }
        }
    }
    settings_path = temp_dir / "settings.json"
    with open(settings_path, 'w') as f:
        json.dump(settings, f, indent=2)
    yield settings_path


@pytest.fixture
def sample_options_files(temp_dir):
    """Create sample option files for testing Options class."""
    setup_dir = temp_dir / "CSS" / "Setup"
    setup_dir.mkdir(parents=True, exist_ok=True)

    # options.def - default options
    options_def = setup_dir / "options.def"
    options_def.write_text("""# Default options file
v: dbtbl <<sbnmaster>>
v: dbpro <<sbnpro>>
v: dbsta <<sbnstatic>>
v: dbwrk <<sbnwork>>
c: mssql -
c: sybase +
""")

    # options.101 - company-specific overrides
    options_101 = setup_dir / "options.101"
    options_101.write_text("""# Company 101 options
v: dbtbl <<sbnmaster_101>>
""")

    # table_locations
    table_locations = setup_dir / "table_locations"
    table_locations.write_text("""# Table locations
-> users &dbtbl&
-> addresses &dbtbl&
-> invoices &dbpro&
""")

    yield {
        'setup_dir': setup_dir,
        'options_def': options_def,
        'options_101': options_101,
        'table_locations': table_locations
    }


@pytest.fixture
def sample_sql_files(temp_dir):
    """Create sample SQL files for testing find_file()."""
    # Create CSS/SQL_Sources/Basics structure
    basics_dir = temp_dir / "CSS" / "SQL_Sources" / "Basics"
    basics_dir.mkdir(parents=True, exist_ok=True)

    # Create sample SQL file
    pro_users = basics_dir / "pro_users.sql"
    pro_users.write_text("-- Sample procedure\nCREATE PROCEDURE pro_users AS\nBEGIN\n  SELECT 1\nEND\n")

    # Create another file
    tbl_users = basics_dir / "tbl_users.sql"
    tbl_users.write_text("-- Sample table\nCREATE TABLE users (id INT)\n")

    # Create billing directory
    billing_dir = temp_dir / "CSS" / "SQL_Sources" / "Billing"
    billing_dir.mkdir(parents=True, exist_ok=True)

    pro_invoices = billing_dir / "pro_invoices.sql"
    pro_invoices.write_text("-- Invoice procedure\nCREATE PROCEDURE pro_invoices AS\nBEGIN\n  SELECT 1\nEND\n")

    yield {
        'base_dir': temp_dir,
        'basics_dir': basics_dir,
        'billing_dir': billing_dir,
        'pro_users': pro_users,
        'tbl_users': tbl_users,
        'pro_invoices': pro_invoices
    }
