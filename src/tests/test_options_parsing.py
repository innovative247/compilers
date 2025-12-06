"""
Tests for Options class parsing methods in ibs_common.py.

Tests cover:
- _parse_v_option(): v: value lines
- _parse_c_option(): c: conditional lines
- _parse_table_option(): -> table location lines
- replace_options(): placeholder substitution
"""

import pytest
import os
import sys

# Add the src directory to the path for imports
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..'))

from commands.ibs_common import Options


@pytest.fixture
def options_instance(sample_config):
    """Create an Options instance for testing."""
    return Options(sample_config)


class TestParseVOption:
    """Tests for _parse_v_option() method."""

    def test_basic_v_option(self, sample_config):
        """Test basic v: line parsing."""
        options = Options(sample_config)
        placeholder, value = options._parse_v_option("v: dbsta <<sbnstatic>>")
        assert placeholder == "&dbsta&"
        assert value == "sbnstatic"

    def test_v_option_with_spaces(self, sample_config):
        """Test v: line with extra spaces."""
        options = Options(sample_config)
        placeholder, value = options._parse_v_option("v:  myvar  << myvalue >>")
        assert placeholder == "&myvar&"
        assert value == "myvalue"

    def test_v_option_with_description(self, sample_config):
        """Test v: line with trailing description."""
        options = Options(sample_config)
        placeholder, value = options._parse_v_option("v: dbtbl <<sbnmaster>> Main database for tables")
        assert placeholder == "&dbtbl&"
        assert value == "sbnmaster"

    def test_v_option_empty_value(self, sample_config):
        """Test v: line with empty value."""
        options = Options(sample_config)
        placeholder, value = options._parse_v_option("v: emptyvar <<>>")
        assert placeholder == "&emptyvar&"
        assert value == ""

    def test_v_option_invalid_no_markers(self, sample_config):
        """Test v: line without << >> markers returns None."""
        options = Options(sample_config)
        placeholder, value = options._parse_v_option("v: invalid line")
        assert placeholder is None
        assert value is None

    def test_v_option_invalid_only_start_marker(self, sample_config):
        """Test v: line with only << marker returns None."""
        options = Options(sample_config)
        placeholder, value = options._parse_v_option("v: invalid <<value")
        assert placeholder is None
        assert value is None

    def test_v_option_invalid_reversed_markers(self, sample_config):
        """Test v: line with >> before << returns None."""
        options = Options(sample_config)
        placeholder, value = options._parse_v_option("v: invalid >>value<<")
        assert placeholder is None
        assert value is None

    def test_v_option_numeric_value(self, sample_config):
        """Test v: line with numeric value."""
        options = Options(sample_config)
        placeholder, value = options._parse_v_option("v: company <<101>>")
        assert placeholder == "&company&"
        assert value == "101"

    def test_v_option_value_with_dots(self, sample_config):
        """Test v: line with value containing dots."""
        options = Options(sample_config)
        placeholder, value = options._parse_v_option("v: server <<db.server.com>>")
        assert placeholder == "&server&"
        assert value == "db.server.com"


class TestParseCOption:
    """Tests for _parse_c_option() method."""

    def test_c_option_enabled(self, sample_config):
        """Test c: line with + (enabled)."""
        options = Options(sample_config)
        results = options._parse_c_option("c: sybase +")

        assert len(results) == 4

        # Convert to dict for easier assertions
        result_dict = dict(results)

        # When enabled: if blocks are active, ifn blocks are commented
        assert result_dict["&if_sybase&"] == ""
        assert result_dict["&endif_sybase&"] == ""
        assert result_dict["&ifn_sybase&"] == "/*"
        assert result_dict["&endifn_sybase&"] == "*/"

    def test_c_option_disabled(self, sample_config):
        """Test c: line with - (disabled)."""
        options = Options(sample_config)
        results = options._parse_c_option("c: mssql -")

        assert len(results) == 4

        result_dict = dict(results)

        # When disabled: if blocks are commented, ifn blocks are active
        assert result_dict["&if_mssql&"] == "/*"
        assert result_dict["&endif_mssql&"] == "*/"
        assert result_dict["&ifn_mssql&"] == ""
        assert result_dict["&endifn_mssql&"] == ""

    def test_c_option_with_description(self, sample_config):
        """Test c: line with trailing description."""
        options = Options(sample_config)
        results = options._parse_c_option("c: debug + Enable debug mode")

        assert len(results) == 4
        result_dict = dict(results)
        assert "&if_debug&" in result_dict

    def test_c_option_invalid_no_flag(self, sample_config):
        """Test c: line without +/- flag returns empty list."""
        options = Options(sample_config)
        results = options._parse_c_option("c: noflag")
        assert results == []

    def test_c_option_invalid_wrong_flag(self, sample_config):
        """Test c: line with invalid flag returns empty list."""
        options = Options(sample_config)
        results = options._parse_c_option("c: wrongflag x")
        assert results == []

    def test_c_option_empty(self, sample_config):
        """Test empty c: line returns empty list."""
        options = Options(sample_config)
        results = options._parse_c_option("c:")
        assert results == []

    def test_c_option_underscore_in_name(self, sample_config):
        """Test c: line with underscore in condition name."""
        options = Options(sample_config)
        results = options._parse_c_option("c: use_feature +")

        assert len(results) == 4
        result_dict = dict(results)
        assert "&if_use_feature&" in result_dict


class TestParseTableOption:
    """Tests for _parse_table_option() method."""

    def test_table_option_basic(self, sample_config):
        """Test basic -> line parsing."""
        options = Options(sample_config)
        # First add a database option so it can be resolved
        options._options["&dbtbl&"] = "sbnmaster"

        results = options._parse_table_option("-> users &dbtbl&")

        assert len(results) == 2
        result_dict = dict(results)

        assert result_dict["&users&"] == "sbnmaster..users"
        assert result_dict["&db-users&"] == "sbnmaster"

    def test_table_option_with_description(self, sample_config):
        """Test -> line with trailing description."""
        options = Options(sample_config)
        options._options["&dbpro&"] = "sbnpro"

        results = options._parse_table_option("-> invoices &dbpro& Invoice table")

        assert len(results) == 2
        result_dict = dict(results)
        assert result_dict["&invoices&"] == "sbnpro..invoices"

    def test_table_option_unresolved_db(self, sample_config):
        """Test -> line with unresolved database variable returns empty."""
        options = Options(sample_config)
        # Don't add the database option, so it can't be resolved

        results = options._parse_table_option("-> users &unknown_db&")
        assert results == []

    def test_table_option_empty(self, sample_config):
        """Test empty -> line returns empty list."""
        options = Options(sample_config)
        results = options._parse_table_option("->")
        assert results == []

    def test_table_option_no_db_var(self, sample_config):
        """Test -> line without database variable returns empty."""
        options = Options(sample_config)
        results = options._parse_table_option("-> tablename")
        assert results == []


class TestReplaceOptions:
    """Tests for replace_options() method."""

    def test_replace_single_placeholder(self, sample_config):
        """Test replacing a single placeholder."""
        options = Options(sample_config)
        options._options["&dbtbl&"] = "sbnmaster"

        result = options.replace_options("SELECT * FROM &dbtbl&..users")
        assert result == "SELECT * FROM sbnmaster..users"

    def test_replace_multiple_placeholders(self, sample_config):
        """Test replacing multiple placeholders."""
        options = Options(sample_config)
        options._options["&dbtbl&"] = "sbnmaster"
        options._options["&dbpro&"] = "sbnpro"

        result = options.replace_options("use &dbtbl&; exec &dbpro&..proc")
        assert result == "use sbnmaster; exec sbnpro..proc"

    def test_replace_same_placeholder_multiple_times(self, sample_config):
        """Test replacing the same placeholder multiple times."""
        options = Options(sample_config)
        options._options["&db&"] = "mydb"

        result = options.replace_options("&db&..table1, &db&..table2")
        assert result == "mydb..table1, mydb..table2"

    def test_replace_no_placeholders(self, sample_config):
        """Test text without placeholders is unchanged."""
        options = Options(sample_config)
        text = "SELECT * FROM users"
        result = options.replace_options(text)
        assert result == text

    def test_replace_unknown_placeholder(self, sample_config):
        """Test unknown placeholders are not replaced."""
        options = Options(sample_config)
        options._options["&known&"] = "value"

        result = options.replace_options("&known& and &unknown&")
        assert result == "value and &unknown&"

    def test_replace_empty_text(self, sample_config):
        """Test empty text returns empty."""
        options = Options(sample_config)
        result = options.replace_options("")
        assert result == ""

    def test_replace_none_text(self, sample_config):
        """Test None text returns None."""
        options = Options(sample_config)
        result = options.replace_options(None)
        assert result is None

    def test_replace_sequence_lowercase(self, sample_config):
        """Test @sequence@ replacement."""
        options = Options(sample_config)
        result = options.replace_options("seq_@sequence@", sequence=5)
        assert result == "seq_5"

    def test_replace_sequence_uppercase(self, sample_config):
        """Test @SEQUENCE@ replacement."""
        options = Options(sample_config)
        result = options.replace_options("seq_@SEQUENCE@", sequence=10)
        assert result == "seq_10"

    def test_replace_sequence_negative_ignored(self, sample_config):
        """Test negative sequence is not replaced."""
        options = Options(sample_config)
        result = options.replace_options("seq_@sequence@", sequence=-1)
        assert result == "seq_@sequence@"

    def test_replace_sequence_zero(self, sample_config):
        """Test sequence=0 is valid and replaced."""
        options = Options(sample_config)
        result = options.replace_options("seq_@sequence@", sequence=0)
        assert result == "seq_0"

    def test_replace_mixed_placeholders_and_sequence(self, sample_config):
        """Test replacing both &placeholders& and @sequence@."""
        options = Options(sample_config)
        options._options["&db&"] = "mydb"

        result = options.replace_options("&db&..table_@sequence@", sequence=3)
        assert result == "mydb..table_3"


class TestReplaceOptionsInList:
    """Tests for replace_options_in_list() method."""

    def test_replace_in_list(self, sample_config):
        """Test replacing placeholders in a list of strings."""
        options = Options(sample_config)
        options._options["&db&"] = "mydb"

        lines = ["use &db&", "select * from &db&..users"]
        result = options.replace_options_in_list(lines)

        assert result == ["use mydb", "select * from mydb..users"]

    def test_replace_in_empty_list(self, sample_config):
        """Test replacing in empty list returns empty list."""
        options = Options(sample_config)
        result = options.replace_options_in_list([])
        assert result == []

    def test_replace_in_list_with_sequence(self, sample_config):
        """Test replacing with sequence in list."""
        options = Options(sample_config)
        options._options["&db&"] = "mydb"

        lines = ["table_@sequence@", "&db&..table"]
        result = options.replace_options_in_list(lines, sequence=7)

        assert result == ["table_7", "mydb..table"]


class TestOptionsIntegration:
    """Integration tests combining multiple parsing methods."""

    def test_full_options_workflow(self, sample_config):
        """Test parsing v:, c:, and -> options together."""
        options = Options(sample_config)

        # Parse v: options first (database definitions)
        placeholder, value = options._parse_v_option("v: dbtbl <<sbnmaster>>")
        options._options[placeholder] = value

        placeholder, value = options._parse_v_option("v: dbpro <<sbnpro>>")
        options._options[placeholder] = value

        # Parse c: options
        for ph, val in options._parse_c_option("c: sybase +"):
            options._options[ph] = val

        # Parse -> options (now database vars are available)
        for ph, val in options._parse_table_option("-> users &dbtbl&"):
            options._options[ph] = val

        # Verify all options are set correctly
        assert options._options["&dbtbl&"] == "sbnmaster"
        assert options._options["&dbpro&"] == "sbnpro"
        assert options._options["&if_sybase&"] == ""
        assert options._options["&users&"] == "sbnmaster..users"
        assert options._options["&db-users&"] == "sbnmaster"

        # Test replacement
        sql = "&if_sybase& SELECT * FROM &users& &endif_sybase&"
        result = options.replace_options(sql)
        assert result == " SELECT * FROM sbnmaster..users "
