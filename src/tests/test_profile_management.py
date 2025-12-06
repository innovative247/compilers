"""
Tests for profile management functions in ibs_common.py.

Tests cover:
- load_profile(): Load profile from settings.json
- save_profile(): Save profile to settings.json
- list_profiles(): List all available profiles
"""

import pytest
import os
import sys
import json
from pathlib import Path
from unittest.mock import patch

# Add the src directory to the path for imports
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..'))

from commands import ibs_common


class TestLoadProfile:
    """Tests for load_profile() function."""

    def test_load_existing_profile(self, temp_dir, sample_settings_file):
        """Test loading an existing profile."""
        # Patch the settings file location
        with patch.object(ibs_common, 'get_settings_filepath', return_value=str(sample_settings_file)):
            profile = ibs_common.load_profile("TEST_PROFILE")

            assert profile["COMPANY"] == 101
            assert profile["HOST"] == "127.0.0.1"
            assert profile["PORT"] == 5000
            assert profile["USERNAME"] == "sa"
            assert profile["PASSWORD"] == "testpass"
            assert profile["PLATFORM"] == "SYBASE"

    def test_load_nonexistent_profile(self, temp_dir, sample_settings_file):
        """Test loading a profile that doesn't exist raises KeyError."""
        with patch.object(ibs_common, 'get_settings_filepath', return_value=str(sample_settings_file)):
            with pytest.raises(KeyError) as excinfo:
                ibs_common.load_profile("NONEXISTENT_PROFILE")

            assert "NONEXISTENT_PROFILE" in str(excinfo.value)

    def test_load_profile_missing_required_field(self, temp_dir):
        """Test loading a profile with missing required fields raises ValueError."""
        # Create settings with incomplete profile
        settings = {
            "Profiles": {
                "INCOMPLETE": {
                    "HOST": "127.0.0.1",
                    # Missing PORT, USERNAME, PASSWORD, PLATFORM
                }
            }
        }
        settings_path = temp_dir / "settings.json"
        with open(settings_path, 'w') as f:
            json.dump(settings, f)

        with patch.object(ibs_common, 'get_settings_filepath', return_value=str(settings_path)):
            with pytest.raises(ValueError) as excinfo:
                ibs_common.load_profile("INCOMPLETE")

            assert "missing required fields" in str(excinfo.value)

    def test_load_profile_returns_copy(self, temp_dir, sample_settings_file):
        """Test that load_profile returns a copy, not the original dict."""
        with patch.object(ibs_common, 'get_settings_filepath', return_value=str(sample_settings_file)):
            profile1 = ibs_common.load_profile("TEST_PROFILE")
            profile1["HOST"] = "modified"

            profile2 = ibs_common.load_profile("TEST_PROFILE")
            assert profile2["HOST"] == "127.0.0.1"  # Original unchanged


class TestSaveProfile:
    """Tests for save_profile() function."""

    def test_save_new_profile(self, temp_dir):
        """Test saving a new profile."""
        settings_path = temp_dir / "settings.json"
        # Create empty settings file
        with open(settings_path, 'w') as f:
            json.dump({"Profiles": {}}, f)

        new_profile = {
            "COMPANY": 101,
            "HOST": "192.168.1.1",
            "PORT": 5000,
            "USERNAME": "admin",
            "PASSWORD": "secret",
            "PLATFORM": "SYBASE"
        }

        with patch.object(ibs_common, 'get_settings_filepath', return_value=str(settings_path)):
            result = ibs_common.save_profile("NEW_PROFILE", new_profile)

            assert result is True

            # Verify it was saved
            with open(settings_path, 'r') as f:
                saved_settings = json.load(f)

            assert "NEW_PROFILE" in saved_settings["Profiles"]
            assert saved_settings["Profiles"]["NEW_PROFILE"]["HOST"] == "192.168.1.1"

    def test_save_profile_overwrites_existing(self, temp_dir, sample_settings_file):
        """Test saving over an existing profile."""
        updated_profile = {
            "COMPANY": 999,
            "HOST": "new.host.com",
            "PORT": 1433,
            "USERNAME": "newuser",
            "PASSWORD": "newpass",
            "PLATFORM": "MSSQL"
        }

        with patch.object(ibs_common, 'get_settings_filepath', return_value=str(sample_settings_file)):
            result = ibs_common.save_profile("TEST_PROFILE", updated_profile)

            assert result is True

            # Verify it was updated
            with open(sample_settings_file, 'r') as f:
                saved_settings = json.load(f)

            assert saved_settings["Profiles"]["TEST_PROFILE"]["HOST"] == "new.host.com"
            assert saved_settings["Profiles"]["TEST_PROFILE"]["COMPANY"] == 999

    def test_save_profile_creates_profiles_section(self, temp_dir):
        """Test saving when Profiles section doesn't exist."""
        settings_path = temp_dir / "settings.json"
        # Create settings without Profiles section
        with open(settings_path, 'w') as f:
            json.dump({}, f)

        new_profile = {
            "HOST": "127.0.0.1",
            "PORT": 5000,
            "USERNAME": "sa",
            "PASSWORD": "pass",
            "PLATFORM": "SYBASE"
        }

        with patch.object(ibs_common, 'get_settings_filepath', return_value=str(settings_path)):
            result = ibs_common.save_profile("FIRST_PROFILE", new_profile)

            assert result is True

            with open(settings_path, 'r') as f:
                saved_settings = json.load(f)

            assert "Profiles" in saved_settings
            assert "FIRST_PROFILE" in saved_settings["Profiles"]

    def test_save_profile_preserves_other_profiles(self, temp_dir, sample_settings_file):
        """Test that saving one profile doesn't affect others."""
        new_profile = {
            "HOST": "new.host.com",
            "PORT": 1234,
            "USERNAME": "user",
            "PASSWORD": "pass",
            "PLATFORM": "SYBASE"
        }

        with patch.object(ibs_common, 'get_settings_filepath', return_value=str(sample_settings_file)):
            ibs_common.save_profile("THIRD_PROFILE", new_profile)

            # Verify original profiles still exist
            with open(sample_settings_file, 'r') as f:
                saved_settings = json.load(f)

            assert "TEST_PROFILE" in saved_settings["Profiles"]
            assert "ANOTHER_PROFILE" in saved_settings["Profiles"]
            assert "THIRD_PROFILE" in saved_settings["Profiles"]


class TestListProfiles:
    """Tests for list_profiles() function."""

    def test_list_profiles_returns_all(self, temp_dir, sample_settings_file):
        """Test listing all profiles."""
        with patch.object(ibs_common, 'get_settings_filepath', return_value=str(sample_settings_file)):
            profiles = ibs_common.list_profiles()

            assert len(profiles) == 2
            assert "TEST_PROFILE" in profiles
            assert "ANOTHER_PROFILE" in profiles

    def test_list_profiles_empty(self, temp_dir):
        """Test listing when no profiles exist."""
        settings_path = temp_dir / "settings.json"
        with open(settings_path, 'w') as f:
            json.dump({"Profiles": {}}, f)

        with patch.object(ibs_common, 'get_settings_filepath', return_value=str(settings_path)):
            profiles = ibs_common.list_profiles()

            assert profiles == []

    def test_list_profiles_no_profiles_section(self, temp_dir):
        """Test listing when Profiles section doesn't exist."""
        settings_path = temp_dir / "settings.json"
        with open(settings_path, 'w') as f:
            json.dump({}, f)

        with patch.object(ibs_common, 'get_settings_filepath', return_value=str(settings_path)):
            profiles = ibs_common.list_profiles()

            assert profiles == []

    def test_list_profiles_returns_list_type(self, temp_dir, sample_settings_file):
        """Test that list_profiles returns a list."""
        with patch.object(ibs_common, 'get_settings_filepath', return_value=str(sample_settings_file)):
            profiles = ibs_common.list_profiles()

            assert isinstance(profiles, list)


class TestProfileRoundTrip:
    """Integration tests for save and load together."""

    def test_save_then_load(self, temp_dir):
        """Test saving a profile then loading it back."""
        settings_path = temp_dir / "settings.json"
        with open(settings_path, 'w') as f:
            json.dump({"Profiles": {}}, f)

        original_profile = {
            "COMPANY": 101,
            "DEFAULT_LANGUAGE": 1,
            "SQL_SOURCE": "/path/to/sql",
            "HOST": "db.example.com",
            "PORT": 5000,
            "USERNAME": "testuser",
            "PASSWORD": "testpass",
            "PLATFORM": "SYBASE"
        }

        with patch.object(ibs_common, 'get_settings_filepath', return_value=str(settings_path)):
            # Save
            ibs_common.save_profile("ROUNDTRIP_TEST", original_profile)

            # Load
            loaded_profile = ibs_common.load_profile("ROUNDTRIP_TEST")

            # Verify all fields match
            for key, value in original_profile.items():
                assert loaded_profile[key] == value

    def test_multiple_saves_then_list(self, temp_dir):
        """Test saving multiple profiles then listing them."""
        settings_path = temp_dir / "settings.json"
        with open(settings_path, 'w') as f:
            json.dump({"Profiles": {}}, f)

        base_profile = {
            "HOST": "host",
            "PORT": 5000,
            "USERNAME": "user",
            "PASSWORD": "pass",
            "PLATFORM": "SYBASE"
        }

        with patch.object(ibs_common, 'get_settings_filepath', return_value=str(settings_path)):
            ibs_common.save_profile("PROFILE_A", base_profile)
            ibs_common.save_profile("PROFILE_B", base_profile)
            ibs_common.save_profile("PROFILE_C", base_profile)

            profiles = ibs_common.list_profiles()

            assert len(profiles) == 3
            assert set(profiles) == {"PROFILE_A", "PROFILE_B", "PROFILE_C"}
