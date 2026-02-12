#!/usr/bin/env python3
"""
IBS Compilers macOS Installer

This script handles installation of all dependencies for the IBS Python Compilers on macOS:
- Homebrew (if not installed)
- FreeTDS (via brew)
- Python packages
- vim (via brew, if not installed)
- Configuration files

Usage:
    python3 installer_macos.py [options]

Options:
    --skip-freetds      Skip FreeTDS installation
    --skip-packages     Skip Python package installation
    --skip-vim          Skip vim installation
    --force             Force reinstallation of components
    --help              Show this help message
"""

import argparse
import os
import platform
import shutil
import subprocess
import sys
from pathlib import Path

# =============================================================================
# CONFIGURATION
# =============================================================================

SCRIPT_DIR = Path(__file__).parent.resolve()
PROJECT_ROOT = SCRIPT_DIR.parent
SRC_DIR = PROJECT_ROOT / "src"
LOG_FILE = SCRIPT_DIR / "installer.log"

# =============================================================================
# LOGGING
# =============================================================================

class Logger:
    """Simple logger that writes to both console and file."""

    COLORS = {
        "INFO": "\033[0m",      # Default
        "WARN": "\033[93m",     # Yellow
        "ERROR": "\033[91m",    # Red
        "SUCCESS": "\033[92m",  # Green
        "STEP": "\033[96m",     # Cyan
        "SKIP": "\033[90m",     # Gray
        "RESET": "\033[0m",
    }

    def __init__(self, log_file: Path):
        self.log_file = log_file

    def _write_log(self, level: str, message: str):
        """Write to log file. Silently fails if file is not writable."""
        try:
            with open(self.log_file, "a", encoding="utf-8") as f:
                f.write(f"[{level}] {message}\n")
        except (IOError, OSError):
            pass

    def log(self, message: str, level: str = "INFO"):
        """Log a message to console and file."""
        prefix = {
            "INFO": "   ",
            "WARN": " ! ",
            "ERROR": " X ",
            "SUCCESS": " + ",
            "STEP": ">> ",
            "SKIP": " - ",
        }.get(level, "   ")

        color = self.COLORS.get(level, self.COLORS["INFO"])
        reset = self.COLORS["RESET"]
        print(f"{color}{prefix}{message}{reset}")
        self._write_log(level, message)

    def section(self, title: str):
        """Print a section header."""
        print()
        print("=" * 60)
        print(f"  {title}")
        print("=" * 60)
        self._write_log("SECTION", title)

    def subsection(self, title: str):
        """Print a subsection header."""
        print()
        print(f"  --- {title} ---")
        self._write_log("SUBSECTION", title)


# Initialize logger (will append to existing log from bootstrap script)
log = Logger(LOG_FILE)

# =============================================================================
# UTILITY FUNCTIONS
# =============================================================================

def get_platform() -> str:
    """Get platform name."""
    system = platform.system().lower()
    if system != "darwin":
        raise RuntimeError(f"This installer is for macOS only. Current platform: {system}")
    return system


def command_exists(cmd: str) -> bool:
    """Check if a command exists in PATH."""
    return shutil.which(cmd) is not None


def run_command(
    cmd: list[str],
    check: bool = True,
    capture_output: bool = False,
    shell: bool = False,
    **kwargs
) -> subprocess.CompletedProcess:
    """Run a command with logging."""
    cmd_str = " ".join(cmd) if isinstance(cmd, list) else cmd
    log.log(f"Running: {cmd_str}", "INFO")

    try:
        result = subprocess.run(
            cmd,
            check=check,
            capture_output=capture_output,
            text=True,
            shell=shell,
            **kwargs
        )
        return result
    except subprocess.CalledProcessError as e:
        log.log(f"Command failed with exit code {e.returncode}", "ERROR")
        if e.stdout:
            log.log(f"stdout: {e.stdout}", "INFO")
        if e.stderr:
            log.log(f"stderr: {e.stderr}", "ERROR")
        raise


def prompt_yes_no(question: str, default: bool = True) -> bool:
    """Prompt user for yes/no confirmation."""
    suffix = "[Y/n]" if default else "[y/N]"
    try:
        response = input(f"{question} {suffix} ").strip().lower()
        if not response:
            return default
        return response in ("y", "yes")
    except EOFError:
        return default


# =============================================================================
# HOMEBREW INSTALLATION
# =============================================================================

def check_homebrew_installed() -> bool:
    """Check if Homebrew is installed."""
    return command_exists("brew")


def get_homebrew_prefix() -> str:
    """Get Homebrew prefix path."""
    try:
        result = subprocess.run(
            ["brew", "--prefix"],
            capture_output=True,
            text=True,
            check=True
        )
        return result.stdout.strip()
    except Exception:
        # Default paths
        if platform.machine() == "arm64":
            return "/opt/homebrew"
        return "/usr/local"


def install_homebrew() -> bool:
    """Install Homebrew."""
    log.section("Homebrew Installation")

    if check_homebrew_installed():
        prefix = get_homebrew_prefix()
        log.log(f"Homebrew already installed at {prefix}", "SUCCESS")
        return True

    log.log("Homebrew not found", "WARN")

    if not prompt_yes_no("Install Homebrew? (Required for FreeTDS)", default=True):
        log.log("Skipping Homebrew installation", "SKIP")
        return False

    log.log("Installing Homebrew...", "STEP")

    try:
        # Homebrew install script
        install_cmd = '/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"'
        subprocess.run(install_cmd, shell=True, check=True)

        # Add Homebrew to PATH for this session (Apple Silicon vs Intel)
        if platform.machine() == "arm64":
            brew_path = "/opt/homebrew/bin"
        else:
            brew_path = "/usr/local/bin"

        if brew_path not in os.environ.get("PATH", ""):
            os.environ["PATH"] = f"{brew_path}:{os.environ.get('PATH', '')}"

        if check_homebrew_installed():
            log.log("Homebrew installed successfully", "SUCCESS")
            return True
        else:
            log.log("Homebrew installation completed but brew not found in PATH", "ERROR")
            log.log("You may need to restart your terminal", "INFO")
            return False

    except Exception as e:
        log.log(f"Homebrew installation failed: {e}", "ERROR")
        return False


# =============================================================================
# FREETDS INSTALLATION
# =============================================================================

FREETDS_MIN_VERSION = "1.4.0"


def get_tsql_path() -> str:
    """Get absolute path to tsql binary."""
    path = shutil.which("tsql")
    if path:
        return path
    for fallback in ["/opt/homebrew/bin/tsql", "/usr/local/bin/tsql"]:
        if os.path.isfile(fallback):
            return fallback
    return ""


def check_freetds_installed() -> bool:
    """Check if FreeTDS is installed."""
    return bool(get_tsql_path())


def get_freetds_version_output(tsql_path: str = "") -> str:
    """Run tsql -C and return raw stdout."""
    if not tsql_path:
        tsql_path = get_tsql_path()
    if not tsql_path:
        return ""
    try:
        result = subprocess.run(
            [tsql_path, "-C"],
            capture_output=True,
            text=True,
            check=True
        )
        return result.stdout
    except Exception:
        return ""


def parse_freetds_version(tsql_output: str = "") -> str:
    """Parse the version number from tsql -C output. Returns version string or empty."""
    import re
    if not tsql_output:
        tsql_output = get_freetds_version_output()
    match = re.search(r'Version:\s*freetds\s*v?(\d+\.\d+\.\d+)', tsql_output)
    return match.group(1) if match else ""


def version_at_least(current: str, minimum: str) -> bool:
    """Compare version strings (e.g. '1.3.17' >= '1.4.0')."""
    def parts(v):
        return [int(x) for x in v.split('.')]
    try:
        return parts(current) >= parts(minimum)
    except (ValueError, IndexError):
        return False


def find_all_tsql() -> list:
    """Find all tsql binaries in PATH order (like 'which -a tsql')."""
    paths = []
    for dir_path in os.environ.get("PATH", "").split(":"):
        if not dir_path:
            continue
        tsql_path = os.path.join(dir_path, "tsql")
        if os.path.isfile(tsql_path) and os.access(tsql_path, os.X_OK):
            paths.append(tsql_path)
    return paths


def verify_freetds() -> bool:
    """Comprehensive post-install FreeTDS verification.

    Finds ALL tsql binaries in PATH, identifies the correct version,
    and symlinks any old versions so the correct one is always used.
    """
    log.subsection("FreeTDS Verification")

    # 1. Find all tsql binaries in PATH
    all_tsql = find_all_tsql()
    if not all_tsql:
        # Check fallback locations not in PATH
        tsql_path = get_tsql_path()
        if not tsql_path:
            log.log("FAIL: tsql binary not found", "ERROR")
            return False
        all_tsql = [tsql_path]

    # 2. Check each instance — find the correct one
    correct_path = None
    log.log(f"Found {len(all_tsql)} tsql binary(ies) in PATH:", "INFO")
    for path in all_tsql:
        ver = parse_freetds_version(get_freetds_version_output(path))
        status = f"v{ver}" if ver else "unknown"
        log.log(f"  {path}: {status}", "INFO")
        if ver and version_at_least(ver, FREETDS_MIN_VERSION) and not correct_path:
            correct_path = path

    if not correct_path:
        log.log(f"FAIL: no tsql binary with version >= {FREETDS_MIN_VERSION}", "ERROR")
        return False

    # 3. Ensure correct version is first in PATH — symlink old copies
    if all_tsql[0] != correct_path:
        log.log(f"Wrong tsql is first in PATH: {all_tsql[0]}", "WARN")
        log.log(f"Correct version at: {correct_path}", "INFO")
        real_correct = os.path.realpath(correct_path)
        for path in all_tsql:
            if os.path.realpath(path) == real_correct:
                continue
            log.log(f"Symlinking {path} -> {correct_path}", "STEP")
            try:
                run_command(["sudo", "ln", "-sf", correct_path, path])
            except Exception as e:
                log.log(f"Failed to symlink {path}: {e}", "WARN")

    # 4. Final verification — the first tsql in PATH must be correct
    first_tsql = get_tsql_path()
    version = parse_freetds_version(get_freetds_version_output(first_tsql))

    if not version or not version_at_least(version, FREETDS_MIN_VERSION):
        log.log(f"FAIL: first tsql in PATH ({first_tsql}) is still v{version}", "ERROR")
        return False

    log.log(f"tsql binary: {first_tsql}", "SUCCESS")
    log.log(f"Version: {version} (>= {FREETDS_MIN_VERSION})", "SUCCESS")

    log.log(f"FreeTDS verification passed: v{version}", "SUCCESS")
    return True


def install_freetds(force: bool = False) -> bool:
    """Install FreeTDS via Homebrew with version enforcement."""
    log.section("FreeTDS Installation")

    # Check existing installation
    if check_freetds_installed():
        version = parse_freetds_version(get_freetds_version_output())

        if version and version_at_least(version, FREETDS_MIN_VERSION):
            if not force:
                log.log(f"FreeTDS {version} already installed (>= {FREETDS_MIN_VERSION})", "SUCCESS")
                return verify_freetds()
            else:
                log.log(f"FreeTDS {version} installed but --force specified, reinstalling...", "INFO")
        else:
            log.log(f"FreeTDS {version or 'unknown'} is too old (need >= {FREETDS_MIN_VERSION})", "WARN")
            force = True  # Force upgrade

    if not check_homebrew_installed():
        log.log("Homebrew not installed - cannot install FreeTDS", "ERROR")
        return False

    # Install or upgrade via Homebrew
    try:
        if force and check_freetds_installed():
            log.log("Upgrading FreeTDS via Homebrew...", "STEP")
            try:
                run_command(["brew", "upgrade", "freetds"])
            except Exception:
                log.log("brew upgrade failed, trying reinstall...", "WARN")
                run_command(["brew", "reinstall", "freetds"])
        else:
            log.log("Installing FreeTDS via Homebrew...", "STEP")
            run_command(["brew", "install", "freetds"])

        if check_freetds_installed():
            return verify_freetds()
        else:
            log.log("FreeTDS installation completed but tsql not found", "ERROR")
            return False

    except Exception as e:
        log.log(f"FreeTDS installation failed: {e}", "ERROR")
        return False


# =============================================================================
# VIM INSTALLATION
# =============================================================================

def check_vim_installed() -> bool:
    """Check if vim is installed."""
    return command_exists("vim")


def get_vim_path() -> str:
    """Get vim executable path."""
    return shutil.which("vim") or ""


def install_vim(force: bool = False) -> bool:
    """Install vim via Homebrew."""
    log.section("Vim Installation")

    if not force and check_vim_installed():
        vim_path = get_vim_path()
        log.log(f"vim already installed: {vim_path}", "SUCCESS")
        return True

    if not check_homebrew_installed():
        log.log("Homebrew not installed - cannot install vim", "ERROR")
        return False

    if not prompt_yes_no("vim not found. Install vim via Homebrew?", default=True):
        log.log("Skipping vim installation", "INFO")
        return False

    log.log("Installing vim via Homebrew...", "STEP")

    try:
        run_command(["brew", "install", "vim"])

        if check_vim_installed():
            vim_path = get_vim_path()
            log.log(f"vim installed successfully: {vim_path}", "SUCCESS")
            return True
        else:
            log.log("vim installation completed but vim not found", "ERROR")
            return False

    except Exception as e:
        log.log(f"vim installation failed: {e}", "ERROR")
        return False


# =============================================================================
# PYTHON PACKAGES
# =============================================================================

def pull_latest() -> bool:
    """Pull latest changes from origin."""
    log.section("Pulling Latest Changes")

    if not shutil.which("git"):
        log.log("git not found - skipping pull", "WARN")
        return False

    if not (PROJECT_ROOT / ".git").exists():
        log.log(f"Not a git repository: {PROJECT_ROOT}", "WARN")
        return False

    try:
        run_command(["git", "-C", str(PROJECT_ROOT), "pull"])
        log.log("Repository updated", "SUCCESS")
        return True
    except subprocess.CalledProcessError as e:
        log.log(f"git pull failed (exit code {e.returncode})", "WARN")
        return False
    except Exception as e:
        log.log(f"git pull failed: {e}", "WARN")
        return False


def install_python_packages() -> bool:
    """Install Python packages from src/."""
    log.section("Python Packages Installation")

    if not SRC_DIR.exists():
        log.log(f"Source directory not found: {SRC_DIR}", "ERROR")
        return False

    pyproject = SRC_DIR / "pyproject.toml"

    if not pyproject.exists():
        log.log("No pyproject.toml found in src/", "ERROR")
        return False

    log.subsection("Upgrading pip")

    try:
        run_command([sys.executable, "-m", "pip", "install", "--upgrade", "pip"])
    except Exception as e:
        log.log(f"pip upgrade failed (continuing anyway): {e}", "WARN")

    log.subsection("Installing IBS Compilers package")

    try:
        # Install in editable mode
        run_command([sys.executable, "-m", "pip", "install", "--force-reinstall", "-e", str(SRC_DIR)])
        log.log("IBS Compilers package installed successfully", "SUCCESS")

        # Check if commands are available
        scripts_dir = Path(sys.prefix) / "bin"
        if (scripts_dir / "runsql").exists():
            log.log(f"Commands installed to: {scripts_dir}", "SUCCESS")
        else:
            # Try user scripts directory
            user_scripts = Path.home() / ".local" / "bin"
            if (user_scripts / "runsql").exists():
                log.log(f"Commands installed to: {user_scripts}", "SUCCESS")
                # Check if in PATH
                if str(user_scripts) not in os.environ.get("PATH", ""):
                    log.log(f"Add to PATH: export PATH=\"$PATH:{user_scripts}\"", "WARN")
                    log.log("Add this line to ~/.zshrc or ~/.bash_profile", "WARN")

        return True

    except Exception as e:
        log.log(f"Package installation failed: {e}", "ERROR")
        return False


# =============================================================================
# SETTINGS CONFIGURATION
# =============================================================================

def initialize_settings_json(force: bool = False) -> bool:
    """Initialize settings.json if it doesn't exist."""
    log.section("Settings Configuration")

    settings_path = PROJECT_ROOT / "settings.json"

    if settings_path.exists() and not force:
        log.log(f"Settings file exists: {settings_path}", "SUCCESS")

        # Count profiles
        try:
            import json
            with open(settings_path, "r") as f:
                settings = json.load(f)
            profile_count = len(settings.get("Profiles", {}))
            log.log(f"Found {profile_count} profile(s) in settings.json", "SUCCESS")
        except Exception as e:
            log.log(f"Could not read settings.json: {e}", "WARN")

        return True

    # Create default settings
    log.log("Creating default settings.json...", "STEP")

    default_settings = {
        "Profiles": {},
        "GlobalSettings": {
            "DefaultProfile": "",
            "LogLevel": "INFO"
        }
    }

    try:
        import json
        with open(settings_path, "w") as f:
            json.dump(default_settings, f, indent=4)
        log.log(f"Created settings.json: {settings_path}", "SUCCESS")
        return True
    except Exception as e:
        log.log(f"Failed to create settings.json: {e}", "ERROR")
        return False


# =============================================================================
# SHELL CONFIGURATION
# =============================================================================

def check_shell_config() -> bool:
    """Check and suggest shell configuration updates."""
    log.section("Shell Configuration")

    # Determine shell config file
    shell = os.environ.get("SHELL", "/bin/zsh")
    if "zsh" in shell:
        config_file = Path.home() / ".zshrc"
    else:
        config_file = Path.home() / ".bash_profile"

    log.log(f"Shell: {shell}", "INFO")
    log.log(f"Config file: {config_file}", "INFO")

    # Check if ~/.local/bin is in PATH
    user_bin = Path.home() / ".local" / "bin"
    current_path = os.environ.get("PATH", "")

    if str(user_bin) not in current_path:
        log.log(f"~/.local/bin not in PATH", "WARN")
        log.log(f"Add to {config_file}:", "INFO")
        log.log('  export PATH="$PATH:$HOME/.local/bin"', "INFO")

    # Check Homebrew path for Apple Silicon
    if platform.machine() == "arm64":
        brew_path = "/opt/homebrew/bin"
        if brew_path not in current_path:
            log.log(f"Homebrew path not in PATH (Apple Silicon)", "WARN")
            log.log(f"Add to {config_file}:", "INFO")
            log.log('  eval "$(/opt/homebrew/bin/brew shellenv)"', "INFO")

    return True


# =============================================================================
# SUMMARY
# =============================================================================

def show_summary():
    """Show installation summary."""
    log.section("Installation Summary")

    print()
    print(f"  {'Component':<15} {'Status':<20} {'Path':<40}")
    print(f"  {'-'*15} {'-'*20} {'-'*40}")

    # Homebrew
    if check_homebrew_installed():
        brew_path = shutil.which("brew") or "N/A"
        print(f"  {'Homebrew':<15} {'Installed':<20} {brew_path:<40}")
    else:
        print(f"  {'Homebrew':<15} {'Not Installed':<20} {'':<40}")

    # FreeTDS
    tsql_path = get_tsql_path()
    if tsql_path:
        version = parse_freetds_version(get_freetds_version_output(tsql_path))
        status = f"v{version}" if version else "Installed"
        print(f"  {'FreeTDS':<15} {status:<20} {tsql_path:<40}")
    else:
        print(f"  {'FreeTDS':<15} {'Not Installed':<20} {'':<40}")

    # vim
    if check_vim_installed():
        vim_path = get_vim_path()
        print(f"  {'vim':<15} {'Installed':<20} {vim_path:<40}")
    else:
        print(f"  {'vim':<15} {'Not Installed':<20} {'':<40}")

    # Python
    python_version = f"{sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}"
    print(f"  {'Python':<15} {'Installed (' + python_version + ')':<20} {sys.executable:<40}")

    # IBS Commands
    runsql_path = shutil.which("runsql")
    if runsql_path:
        print(f"  {'IBS Commands':<15} {'Available':<20} {runsql_path:<40}")
    else:
        print(f"  {'IBS Commands':<15} {'Not in PATH':<20} {'Check ~/.local/bin':<40}")

    # Settings
    settings_path = PROJECT_ROOT / "settings.json"
    if settings_path.exists():
        print(f"  {'settings.json':<15} {'Exists':<20} {str(settings_path):<40}")
    else:
        print(f"  {'settings.json':<15} {'Not Found':<20} {'':<40}")

    # Log file
    print(f"  {'Log File':<15} {'Exists':<20} {str(LOG_FILE):<40}")

    print()

    # Overall status
    all_good = (
        check_homebrew_installed() and
        check_freetds_installed() and
        check_vim_installed() and
        shutil.which("runsql") is not None
    )

    if all_good:
        log.log("All components installed successfully!", "SUCCESS")
    else:
        log.log("Some components may need attention - see above", "WARN")


# =============================================================================
# MAIN
# =============================================================================

def main():
    parser = argparse.ArgumentParser(
        description="IBS Compilers macOS Installer",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__
    )
    parser.add_argument(
        "--skip-freetds",
        action="store_true",
        help="Skip FreeTDS installation"
    )
    parser.add_argument(
        "--skip-packages",
        action="store_true",
        help="Skip Python package installation"
    )
    parser.add_argument(
        "--skip-vim",
        action="store_true",
        help="Skip vim installation"
    )
    parser.add_argument(
        "--force",
        action="store_true",
        help="Force reinstallation of components"
    )

    args = parser.parse_args()

    # Verify platform
    try:
        get_platform()
    except RuntimeError as e:
        print(f"Error: {e}")
        print("This installer is for macOS only.")
        print("For Linux, use: ./bootstrap_linux.sh")
        print("For Windows, use: .\\bootstrap.ps1")
        return 1

    # Header
    print()
    print("=" * 60)
    print("  IBS Compilers - macOS Installer")
    print("=" * 60)
    print()
    print(f"  Platform: {platform.system()} {platform.release()}")
    print(f"  Architecture: {platform.machine()}")
    print(f"  Python: {sys.version.split()[0]}")
    print(f"  Project: {PROJECT_ROOT}")
    print(f"  Log file: {LOG_FILE}")
    print()

    if not prompt_yes_no("Continue with installation?"):
        log.log("User cancelled installation", "INFO")
        return 1

    # Step 1: Homebrew
    if not install_homebrew():
        if not prompt_yes_no("Homebrew installation had issues. Continue anyway?"):
            show_summary()
            return 1

    # Step 2: FreeTDS
    if not args.skip_freetds:
        if not install_freetds(args.force):
            if not prompt_yes_no("FreeTDS installation had issues. Continue anyway?"):
                show_summary()
                return 1
    else:
        log.log("Skipping FreeTDS installation (user flag)", "SKIP")

    # Step 3: vim
    if not args.skip_vim:
        if not install_vim(args.force):
            log.log("vim installation skipped or failed", "WARN")
    else:
        log.log("Skipping vim installation (user flag)", "SKIP")

    # Step 4: Pull latest from origin
    pull_latest()

    # Step 5: Python packages
    if not args.skip_packages:
        if not install_python_packages():
            log.log("Python package installation had issues", "WARN")
    else:
        log.log("Skipping Python package installation (user flag)", "SKIP")

    # Step 6: Initialize settings.json
    initialize_settings_json(args.force)

    # Step 7: Shell configuration hints
    check_shell_config()

    # Show summary
    show_summary()

    print()
    print("=" * 60)
    print("  Setup complete! Run 'set_profile' to configure a database profile.")
    print("=" * 60)
    print()

    return 0


if __name__ == "__main__":
    sys.exit(main())
