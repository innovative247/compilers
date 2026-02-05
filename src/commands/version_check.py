"""
Version check and upgrade functionality for compilers.

Checks for updates once per day on first command execution.
"""

import os
import sys
import json
import subprocess
from datetime import datetime, date
from pathlib import Path
from typing import Tuple, Optional

from .version import __version__

# GitHub raw URL for version file
GITHUB_VERSION_URL = "https://raw.githubusercontent.com/innovative247/compilers/main/src/commands/version.py"

# Local state file location
def _get_state_file() -> Path:
    """Get path to the state file that tracks last check date."""
    if sys.platform == 'win32':
        base = Path(os.environ.get('LOCALAPPDATA', os.path.expanduser('~')))
    else:
        base = Path(os.path.expanduser('~'))

    state_dir = base / '.compilers'
    state_dir.mkdir(exist_ok=True)
    return state_dir / 'version_state.json'


def _get_compilers_dir() -> Path:
    """Get the root directory of the compilers installation."""
    # version_check.py is in src/commands/, so go up two levels
    return Path(__file__).parent.parent.parent


def _load_state() -> dict:
    """Load the state file."""
    state_file = _get_state_file()
    if state_file.exists():
        try:
            with open(state_file, 'r') as f:
                return json.load(f)
        except (json.JSONDecodeError, IOError):
            pass
    return {}


def _save_state(state: dict) -> None:
    """Save the state file."""
    state_file = _get_state_file()
    try:
        with open(state_file, 'w') as f:
            json.dump(state, f, indent=2)
    except IOError:
        pass  # Silently fail if we can't write state


def _needs_check() -> bool:
    """Check if we need to check for updates (first command of the day)."""
    state = _load_state()
    last_check = state.get('last_check_date')
    today = date.today().isoformat()
    return last_check != today


def _mark_checked() -> None:
    """Mark that we've checked for updates today."""
    state = _load_state()
    state['last_check_date'] = date.today().isoformat()
    _save_state(state)


def _fetch_remote_version() -> Optional[str]:
    """Fetch the latest version from GitHub."""
    try:
        import urllib.request
        import ssl

        # Create SSL context that doesn't verify (for corporate proxies)
        ctx = ssl.create_default_context()
        ctx.check_hostname = False
        ctx.verify_mode = ssl.CERT_NONE

        req = urllib.request.Request(
            GITHUB_VERSION_URL,
            headers={'User-Agent': 'compilers-version-check'}
        )

        with urllib.request.urlopen(req, timeout=5, context=ctx) as response:
            content = response.read().decode('utf-8')

        # Parse version from the file content
        for line in content.split('\n'):
            if line.startswith('__version__'):
                # Extract version string: __version__ = "1.0.0"
                parts = line.split('=', 1)
                if len(parts) == 2:
                    version_str = parts[1].strip().strip('"\'')
                    return version_str
        return None
    except Exception:
        return None  # Silently fail on network errors


def _compare_versions(current: str, remote: str) -> int:
    """
    Compare version strings.
    Returns: -1 if current < remote, 0 if equal, 1 if current > remote
    """
    def parse_version(v: str) -> Tuple[int, ...]:
        try:
            return tuple(int(x) for x in v.split('.'))
        except ValueError:
            return (0,)

    curr_parts = parse_version(current)
    remote_parts = parse_version(remote)

    # Pad to same length
    max_len = max(len(curr_parts), len(remote_parts))
    curr_parts = curr_parts + (0,) * (max_len - len(curr_parts))
    remote_parts = remote_parts + (0,) * (max_len - len(remote_parts))

    if curr_parts < remote_parts:
        return -1
    elif curr_parts > remote_parts:
        return 1
    return 0


def _perform_upgrade() -> Tuple[bool, str]:
    """
    Perform the upgrade by pulling latest from git.
    Returns: (success, message)
    """
    compilers_dir = _get_compilers_dir()

    try:
        # Check if it's a git repository
        git_dir = compilers_dir / '.git'
        if not git_dir.exists():
            return False, "Not a git repository. Manual upgrade required."

        # Perform git pull
        result = subprocess.run(
            ['git', 'pull', 'origin', 'main'],
            cwd=str(compilers_dir),
            capture_output=True,
            text=True,
            timeout=60
        )

        if result.returncode == 0:
            output = result.stdout.strip()
            if 'Already up to date' in output:
                return True, "Already up to date."
            return True, f"Upgrade successful!\n{output}"
        else:
            return False, f"Git pull failed:\n{result.stderr}"

    except subprocess.TimeoutExpired:
        return False, "Upgrade timed out."
    except FileNotFoundError:
        return False, "Git not found. Please install git or upgrade manually."
    except Exception as e:
        return False, f"Upgrade failed: {str(e)}"


def check_for_updates(command_name: str = "command") -> bool:
    """
    Check for updates if this is the first command of the day.

    Args:
        command_name: Name of the command being run (for display)

    Returns:
        True if the command should proceed, False if it should exit
        (e.g., after an upgrade that requires restart)
    """
    # Skip if not the first check of the day
    if not _needs_check():
        return True

    # Mark as checked (so we don't check again today regardless of outcome)
    _mark_checked()

    # Try to fetch remote version
    remote_version = _fetch_remote_version()

    if remote_version is None:
        # Couldn't check - proceed silently
        return True

    # Compare versions
    comparison = _compare_versions(__version__, remote_version)

    if comparison >= 0:
        # Current version is same or newer
        return True

    # Newer version available - prompt user
    try:
        from colorama import Fore, Style
        has_colorama = True
    except ImportError:
        has_colorama = False

    print()
    if has_colorama:
        print(f"{Style.BRIGHT}{'=' * 60}{Style.RESET_ALL}")
        print(f"{Fore.YELLOW}  A new version of compilers is available!{Style.RESET_ALL}")
        print(f"{Style.BRIGHT}{'=' * 60}{Style.RESET_ALL}")
        print(f"  Current version: {Fore.RED}{__version__}{Style.RESET_ALL}")
        print(f"  Latest version:  {Fore.GREEN}{remote_version}{Style.RESET_ALL}")
    else:
        print("=" * 60)
        print("  A new version of compilers is available!")
        print("=" * 60)
        print(f"  Current version: {__version__}")
        print(f"  Latest version:  {remote_version}")
    print()

    choice = input("  Would you like to upgrade now? [Y/n]: ").strip().lower()

    if choice in ('', 'y', 'yes'):
        print()
        print("  Upgrading...")
        success, message = _perform_upgrade()
        print()

        if success:
            if has_colorama:
                print(f"  {Fore.GREEN}{message}{Style.RESET_ALL}")
            else:
                print(f"  {message}")
            print()

            if 'Already up to date' not in message:
                print("  Please restart the command to use the new version.")
                print()
                return False  # Exit so user restarts with new version
        else:
            if has_colorama:
                print(f"  {Fore.RED}{message}{Style.RESET_ALL}")
            else:
                print(f"  {message}")
            print()
            print(f"  Continuing with current version ({__version__})...")
            print()
    else:
        print()
        print(f"  Continuing with current version ({__version__})...")
        print("  (You will not be prompted again until tomorrow)")
        print()

    return True


def get_version() -> str:
    """Get the current version string."""
    return __version__
