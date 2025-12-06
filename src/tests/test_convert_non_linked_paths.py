"""
Tests for convert_non_linked_paths() function in ibs_common.py.

This function converts symbolic directory paths to real directory paths.
It mirrors the C# NonLinkedFilename() function in common.cs.
"""

import pytest
import os
import sys

# Add the src directory to the path for imports
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..'))

from commands.ibs_common import convert_non_linked_paths


class TestConvertNonLinkedPathsBasic:
    """Basic conversion tests."""

    def test_unix_style_ba_path(self):
        """Test /ss/ba/ -> /SQL_Sources/Basics/ conversion."""
        result = convert_non_linked_paths("css/ss/ba/pro_users.sql")
        assert "SQL_Sources" in result
        assert "Basics" in result
        assert "/ss/ba/" not in result.replace("\\", "/")

    def test_windows_style_ba_path(self):
        """Test \\ss\\ba\\ -> \\SQL_Sources\\Basics\\ conversion."""
        result = convert_non_linked_paths("css\\ss\\ba\\pro_users.sql")
        assert "SQL_Sources" in result
        assert "Basics" in result
        assert "\\ss\\ba\\" not in result

    def test_preserves_filename(self):
        """Ensure the filename is preserved after conversion."""
        result = convert_non_linked_paths("css/ss/ba/pro_users.sql")
        assert result.endswith("pro_users.sql")

    def test_preserves_prefix(self):
        """Ensure path prefix is preserved."""
        result = convert_non_linked_paths("C:/projects/css/ss/ba/file.sql")
        assert result.startswith("C:")


class TestConvertNonLinkedPathsNoConversion:
    """Tests for paths that should NOT be converted."""

    def test_no_css_or_ibs_in_path(self):
        """Paths without css or ibs should not be modified."""
        original = "some/other/path/file.sql"
        result = convert_non_linked_paths(original)
        assert result == original

    def test_random_path_unchanged(self):
        """Random paths should pass through unchanged."""
        original = "/home/user/documents/script.sql"
        result = convert_non_linked_paths(original)
        assert result == original

    def test_partial_match_not_converted(self):
        """Partial matches like 'mycss' should not trigger conversion."""
        original = "mycss/ss/ba/file.sql"  # 'mycss' not '/css/'
        result = convert_non_linked_paths(original)
        # Should not convert because it's 'mycss' not 'css'
        assert result == original


class TestConvertNonLinkedPathsCaseInsensitive:
    """Case-insensitivity tests."""

    def test_uppercase_css(self):
        """CSS in uppercase should still convert."""
        result = convert_non_linked_paths("CSS/ss/ba/file.sql")
        assert "SQL_Sources" in result

    def test_uppercase_ss_ba(self):
        """SS/BA in uppercase should still convert."""
        result = convert_non_linked_paths("css/SS/BA/file.sql")
        assert "SQL_Sources" in result
        assert "Basics" in result

    def test_mixed_case(self):
        """Mixed case should still convert."""
        result = convert_non_linked_paths("Css/Ss/Ba/file.sql")
        assert "SQL_Sources" in result
        assert "Basics" in result


class TestConvertNonLinkedPathsAllMappings:
    """Test all symbolic path mappings."""

    @pytest.mark.parametrize("symbolic,expected", [
        ("/ss/api/", "/SQL_Sources/Application_Program_Interface/"),
        ("/ss/api2/", "/SQL_Sources/Application_Program_Interface_V2/"),
        ("/ss/api3/", "/SQL_Sources/Application_Program_Interface_V3/"),
        ("/ss/at/", "/SQL_Sources/Alarm_Treatment/"),
        ("/ss/ba/", "/SQL_Sources/Basics/"),
        ("/ss/bl/", "/SQL_Sources/Billing/"),
        ("/ss/ct/", "/SQL_Sources/Create_Temp/"),
        ("/ss/cv/", "/SQL_Sources/Conversions/"),
        ("/ss/da/", "/SQL_Sources/da/"),
        ("/ss/dv/", "/SQL_Sources/IBS_Development/"),
        ("/ss/fe/", "/SQL_Sources/Front_End/"),
        ("/ss/in/", "/SQL_Sources/Internal/"),
        ("/ss/ma/", "/SQL_Sources/Co_Monitoring/"),
        ("/ss/mb/", "/SQL_Sources/Mobile/"),
        ("/ss/mo/", "/SQL_Sources/Monitoring/"),
        ("/ss/mobile/", "/SQL_Sources/Mobile/"),
        ("/ss/sdi/", "/SQL_Sources/SDI_App/"),
        ("/ss/si/", "/SQL_Sources/System_Init/"),
        ("/ss/sv/", "/SQL_Sources/Service/"),
        ("/ss/tm/", "/SQL_Sources/Telemarketing/"),
        ("/ss/test/", "/SQL_Sources/Test/"),
        ("/ss/ub/", "/SQL_Sources/US_Basics/"),
        ("/ibs/ss/", "/IBS/SQL_Sources/"),
    ])
    def test_mapping(self, symbolic, expected):
        """Test each symbolic path mapping."""
        input_path = f"css{symbolic}file.sql"
        result = convert_non_linked_paths(input_path)
        # Normalize for comparison
        result_normalized = result.replace("\\", "/")
        expected_normalized = expected.replace("\\", "/")
        assert expected_normalized in result_normalized, \
            f"Expected '{expected_normalized}' in '{result_normalized}'"


class TestConvertNonLinkedPathsCrossPlatform:
    """Cross-platform behavior tests."""

    def test_output_uses_os_separator(self):
        """Output should use OS-appropriate path separator."""
        result = convert_non_linked_paths("css/ss/ba/file.sql")
        if os.sep == '\\':
            # On Windows, result should have backslashes
            assert '\\' in result or '/' not in result.replace("C:/", "C:\\")
        else:
            # On Unix, result should have forward slashes
            assert '\\' not in result

    def test_mixed_separators_input(self):
        """Input with mixed separators should still work."""
        result = convert_non_linked_paths("css/ss\\ba/file.sql")
        assert "SQL_Sources" in result
        assert "Basics" in result


class TestConvertNonLinkedPathsIBS:
    """Tests for IBS-specific paths."""

    def test_ibs_ss_conversion(self):
        """Test /ibs/ss/ -> /IBS/SQL_Sources/ conversion."""
        result = convert_non_linked_paths("ibs/ss/pro_common.sql")
        assert "IBS" in result
        assert "SQL_Sources" in result

    def test_ibs_path_preserved(self):
        """IBS paths should preserve the IBS prefix."""
        result = convert_non_linked_paths("C:/projects/ibs/ss/file.sql")
        result_normalized = result.replace("\\", "/")
        assert "IBS/SQL_Sources" in result_normalized


class TestConvertNonLinkedPathsEdgeCases:
    """Edge case tests."""

    def test_empty_string(self):
        """Empty string should return empty string."""
        result = convert_non_linked_paths("")
        assert result == ""

    def test_just_css(self):
        """Path with just 'css' should not crash."""
        result = convert_non_linked_paths("css")
        assert result == "css"

    def test_trailing_no_file(self):
        """Path ending with symbolic dir should still convert."""
        result = convert_non_linked_paths("css/ss/ba/")
        assert "SQL_Sources" in result
        assert "Basics" in result

    def test_multiple_symbolic_paths(self):
        """Only the first symbolic path should be converted."""
        # This is an unusual case but should handle gracefully
        result = convert_non_linked_paths("css/ss/ba/ss/bl/file.sql")
        # First /ss/ba/ should convert, the second /ss/bl/ may or may not
        assert "SQL_Sources" in result
        assert "Basics" in result
