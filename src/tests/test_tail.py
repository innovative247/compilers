"""
Tests for tail.py - Display last N lines of a file.
"""

import pytest
from pathlib import Path
from commands.tail import tail_lines


@pytest.fixture
def tail_test_file(tmp_path):
    """Create a temp file with 20 numbered lines."""
    test_file = tmp_path / "test.txt"
    test_file.write_text("\n".join(f"line {i}" for i in range(1, 21)) + "\n")
    return test_file


@pytest.fixture
def empty_file(tmp_path):
    """Create an empty temp file."""
    test_file = tmp_path / "empty.txt"
    test_file.write_text("")
    return test_file


class TestTailLines:
    def test_default_10_lines(self, tail_test_file):
        """tail_lines should return last 10 lines by default."""
        lines = tail_lines(tail_test_file, 10)
        assert len(lines) == 10
        assert lines[0] == "line 11\n"
        assert lines[-1] == "line 20\n"

    def test_custom_line_count(self, tail_test_file):
        """tail_lines should return specified number of lines."""
        lines = tail_lines(tail_test_file, 5)
        assert len(lines) == 5
        assert lines[0] == "line 16\n"
        assert lines[-1] == "line 20\n"

    def test_single_line(self, tail_test_file):
        """tail_lines with n=1 should return only the last line."""
        lines = tail_lines(tail_test_file, 1)
        assert len(lines) == 1
        assert lines[0] == "line 20\n"

    def test_more_lines_than_file(self, tail_test_file):
        """Requesting more lines than file contains should return all lines."""
        lines = tail_lines(tail_test_file, 100)
        assert len(lines) == 20
        assert lines[0] == "line 1\n"
        assert lines[-1] == "line 20\n"

    def test_empty_file(self, empty_file):
        """tail_lines on empty file should return empty list."""
        lines = tail_lines(empty_file, 10)
        assert len(lines) == 0

    def test_file_not_found(self, tmp_path):
        """tail_lines should exit with code 1 for missing file."""
        with pytest.raises(SystemExit) as exc_info:
            tail_lines(tmp_path / "nonexistent.txt", 10)
        assert exc_info.value.code == 1
