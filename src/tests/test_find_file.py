"""
Tests for find_file() function in ibs_common.py.

Tests cover:
- Finding files in current directory
- Finding files in SQL_SOURCE directory
- Automatic .sql extension appending
- Symbolic path resolution integration
- Absolute path handling
"""

import pytest
import os
import sys
from pathlib import Path

# Add the src directory to the path for imports
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..'))

from commands.ibs_common import find_file


class TestFindFileCurrentDirectory:
    """Tests for finding files in current directory."""

    def test_find_file_in_current_dir(self, temp_dir, sample_sql_files, monkeypatch):
        """Test finding a file in the current directory."""
        # Change to temp directory
        monkeypatch.chdir(sample_sql_files['basics_dir'])

        config = {'SQL_SOURCE': ''}
        result = find_file("pro_users.sql", config)

        assert result is not None
        assert result.endswith("pro_users.sql")

    def test_find_file_with_relative_path(self, temp_dir, sample_sql_files, monkeypatch):
        """Test finding a file with relative path."""
        monkeypatch.chdir(sample_sql_files['base_dir'])

        config = {'SQL_SOURCE': ''}
        result = find_file("CSS/SQL_Sources/Basics/pro_users.sql", config)

        assert result is not None
        assert "pro_users.sql" in result

    def test_file_not_found_returns_none(self, temp_dir, monkeypatch):
        """Test that missing file returns None."""
        monkeypatch.chdir(temp_dir)

        config = {'SQL_SOURCE': ''}
        result = find_file("nonexistent_file.sql", config)

        assert result is None


class TestFindFilePathAppend:
    """Tests for finding files in SQL_SOURCE directory."""

    def test_find_file_in_path_append(self, temp_dir, sample_sql_files, monkeypatch):
        """Test finding a file in SQL_SOURCE directory."""
        # Change to a different directory
        monkeypatch.chdir(temp_dir)

        config = {'SQL_SOURCE': str(sample_sql_files['base_dir'])}
        result = find_file("CSS/SQL_Sources/Basics/pro_users.sql", config)

        assert result is not None
        assert "pro_users.sql" in result

    def test_find_file_path_append_priority(self, temp_dir, sample_sql_files, monkeypatch):
        """Test that current dir is checked before SQL_SOURCE."""
        # Create a file in temp_dir (current dir)
        local_file = temp_dir / "local_file.sql"
        local_file.write_text("-- local version")

        # Also create in SQL_SOURCE location
        path_append_dir = temp_dir / "path_append"
        path_append_dir.mkdir()
        remote_file = path_append_dir / "local_file.sql"
        remote_file.write_text("-- remote version")

        monkeypatch.chdir(temp_dir)

        config = {'SQL_SOURCE': str(path_append_dir)}
        result = find_file("local_file.sql", config)

        assert result is not None
        # Should find the local version (current dir priority)
        with open(result, 'r') as f:
            content = f.read()
        assert "local version" in content

    def test_find_file_empty_path_append(self, temp_dir, sample_sql_files, monkeypatch):
        """Test finding file when SQL_SOURCE is empty."""
        monkeypatch.chdir(sample_sql_files['basics_dir'])

        config = {'SQL_SOURCE': ''}
        result = find_file("pro_users.sql", config)

        assert result is not None

    def test_find_file_missing_path_append_key(self, temp_dir, sample_sql_files, monkeypatch):
        """Test finding file when SQL_SOURCE key doesn't exist."""
        monkeypatch.chdir(sample_sql_files['basics_dir'])

        config = {}  # No SQL_SOURCE key
        result = find_file("pro_users.sql", config)

        assert result is not None


class TestFindFileAutoExtension:
    """Tests for automatic .sql extension appending."""

    def test_auto_append_sql_extension(self, temp_dir, sample_sql_files, monkeypatch):
        """Test that .sql is auto-appended when file not found."""
        monkeypatch.chdir(sample_sql_files['basics_dir'])

        config = {'SQL_SOURCE': ''}
        # Search for "pro_users" without extension
        result = find_file("pro_users", config)

        assert result is not None
        assert result.endswith("pro_users.sql")

    def test_no_double_extension(self, temp_dir, sample_sql_files, monkeypatch):
        """Test that .sql isn't appended when already present."""
        monkeypatch.chdir(sample_sql_files['basics_dir'])

        config = {'SQL_SOURCE': ''}
        result = find_file("pro_users.sql", config)

        assert result is not None
        assert not result.endswith(".sql.sql")

    def test_auto_extension_in_path_append(self, temp_dir, sample_sql_files, monkeypatch):
        """Test auto extension works with SQL_SOURCE too."""
        monkeypatch.chdir(temp_dir)

        config = {'SQL_SOURCE': str(sample_sql_files['base_dir'])}
        result = find_file("CSS/SQL_Sources/Basics/tbl_users", config)

        assert result is not None
        assert result.endswith("tbl_users.sql")


class TestFindFileSymbolicPaths:
    """Tests for symbolic path resolution integration."""

    def test_symbolic_path_ss_ba(self, temp_dir, sample_sql_files, monkeypatch):
        """Test /ss/ba/ symbolic path resolution."""
        monkeypatch.chdir(temp_dir)

        config = {'SQL_SOURCE': str(sample_sql_files['base_dir'])}
        # Use symbolic path
        result = find_file("css/ss/ba/pro_users.sql", config)

        assert result is not None
        assert "SQL_Sources" in result
        assert "Basics" in result
        assert "pro_users.sql" in result

    def test_symbolic_path_ss_bl(self, temp_dir, sample_sql_files, monkeypatch):
        """Test /ss/bl/ symbolic path resolution."""
        monkeypatch.chdir(temp_dir)

        config = {'SQL_SOURCE': str(sample_sql_files['base_dir'])}
        # Use symbolic path for billing
        result = find_file("css/ss/bl/pro_invoices.sql", config)

        assert result is not None
        assert "SQL_Sources" in result
        assert "Billing" in result

    def test_symbolic_path_case_insensitive(self, temp_dir, sample_sql_files, monkeypatch):
        """Test symbolic paths work case-insensitively."""
        monkeypatch.chdir(temp_dir)

        config = {'SQL_SOURCE': str(sample_sql_files['base_dir'])}
        # Use uppercase
        result = find_file("CSS/SS/BA/pro_users.sql", config)

        assert result is not None
        assert "pro_users.sql" in result


class TestFindFileAbsolutePaths:
    """Tests for absolute path handling."""

    def test_absolute_path_exists(self, temp_dir, sample_sql_files):
        """Test finding file with absolute path."""
        absolute_path = str(sample_sql_files['pro_users'])

        config = {'SQL_SOURCE': ''}
        result = find_file(absolute_path, config)

        assert result is not None
        assert result == str(sample_sql_files['pro_users'].resolve())

    def test_absolute_path_not_exists(self, temp_dir):
        """Test absolute path that doesn't exist returns None."""
        absolute_path = str(temp_dir / "nonexistent" / "file.sql")

        config = {'SQL_SOURCE': ''}
        result = find_file(absolute_path, config)

        assert result is None


class TestFindFileEdgeCases:
    """Edge case tests."""

    def test_empty_filename(self, temp_dir, monkeypatch):
        """Test empty filename returns None."""
        monkeypatch.chdir(temp_dir)

        config = {'SQL_SOURCE': ''}
        result = find_file("", config)

        assert result is None

    def test_directory_path_not_file(self, temp_dir, sample_sql_files, monkeypatch):
        """Test that directories are not returned as found files."""
        monkeypatch.chdir(sample_sql_files['base_dir'])

        config = {'SQL_SOURCE': ''}
        # Try to find a directory (not a file)
        result = find_file("CSS/SQL_Sources/Basics", config)

        # Directories should not be returned as found files
        # (the function is for finding files, not directories)
        # Note: Current implementation may or may not handle this
        # This test documents expected behavior
        if result is not None:
            assert Path(result).is_file()

    def test_special_characters_in_path(self, temp_dir, monkeypatch):
        """Test paths with spaces work correctly."""
        # Create a directory with spaces
        space_dir = temp_dir / "path with spaces"
        space_dir.mkdir()
        space_file = space_dir / "test file.sql"
        space_file.write_text("-- test")

        monkeypatch.chdir(temp_dir)

        config = {'SQL_SOURCE': ''}
        result = find_file("path with spaces/test file.sql", config)

        assert result is not None
        assert "test file.sql" in result

    def test_returns_resolved_path(self, temp_dir, sample_sql_files, monkeypatch):
        """Test that returned path is fully resolved (absolute)."""
        monkeypatch.chdir(sample_sql_files['basics_dir'])

        config = {'SQL_SOURCE': ''}
        result = find_file("pro_users.sql", config)

        assert result is not None
        assert Path(result).is_absolute()


class TestFindFileIntegration:
    """Integration tests combining multiple features."""

    def test_symbolic_path_with_auto_extension(self, temp_dir, sample_sql_files, monkeypatch):
        """Test symbolic path resolution combined with auto extension."""
        monkeypatch.chdir(temp_dir)

        config = {'SQL_SOURCE': str(sample_sql_files['base_dir'])}
        # Symbolic path without .sql extension
        result = find_file("css/ss/ba/pro_users", config)

        assert result is not None
        assert "SQL_Sources" in result
        assert "Basics" in result
        assert result.endswith("pro_users.sql")

    def test_symbolic_path_in_path_append(self, temp_dir, sample_sql_files, monkeypatch):
        """Test symbolic path works with SQL_SOURCE."""
        # Change to completely different directory
        other_dir = temp_dir / "other"
        other_dir.mkdir()
        monkeypatch.chdir(other_dir)

        config = {'SQL_SOURCE': str(sample_sql_files['base_dir'])}
        result = find_file("css/ss/ba/tbl_users.sql", config)

        assert result is not None
        assert "tbl_users.sql" in result
