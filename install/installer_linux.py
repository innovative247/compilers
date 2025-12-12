#!/usr/bin/env python3
"""
IBS Compilers Linux/Ubuntu Installer

This script handles installation of all dependencies for the IBS Python Compilers on Linux:
- FreeTDS (via apt)
- Python packages
- vim (via apt)
- Configuration files

Usage:
    python3 installer_linux.py [options]

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
        """Write to log file."""
        with open(self.log_file, "a", encoding="utf-8") as f:
            f.write(f"[{level}] {message}\n")

    def log(self, message: str, level: str = "INFO"):
        """Log a message to console and file."""
        self._write_log(level, message)

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

    def section(self, title: str):
        """Print a section header."""
        self._write_log("SECTION", title)
        print()
        print("=" * 60)
        print(f"  {title}")
        print("=" * 60)

    def subsection(self, title: str):
        """Print a subsection header."""
        self._write_log("SUBSECTION", title)
        print()
        print(f"  --- {title} ---")


# Initialize logger (will append to existing log from bootstrap.sh)
log = Logger(LOG_FILE)

# =============================================================================
# UTILITY FUNCTIONS
# =============================================================================

def get_platform() -> str:
    """Get platform name."""
    system = platform.system().lower()
    if system not in ("linux", "darwin"):
        raise RuntimeError(f"This installer is for Linux/macOS. Current platform: {system}")
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


def has_sudo() -> bool:
    """Check if user can run sudo."""
    try:
        result = subprocess.run(
            ["sudo", "-n", "true"],
            capture_output=True,
            check=False
        )
        return result.returncode == 0
    except Exception:
        return False


# =============================================================================
# FREETDS INSTALLATION
# =============================================================================

def check_freetds_installed() -> bool:
    """Check if FreeTDS is installed."""
    return command_exists("tsql")


def get_freetds_version() -> str:
    """Get FreeTDS version info."""
    try:
        result = subprocess.run(
            ["tsql", "-C"],
            capture_output=True,
            text=True,
            check=True
        )
        return result.stdout
    except Exception:
        return "Unknown"


def install_freetds(force: bool = False) -> bool:
    """Install FreeTDS via apt."""
    log.section("FreeTDS Installation")

    if not force and check_freetds_installed():
        log.log("FreeTDS already installed", "SUCCESS")
        log.log("Running: tsql -C", "INFO")
        version_info = get_freetds_version()
        log.log(f"FreeTDS version info:\n{version_info}", "INFO")
        return True

    log.log("Installing FreeTDS via apt...", "STEP")

    try:
        run_command(["sudo", "apt", "update"])
        run_command(["sudo", "apt", "install", "-y", "freetds-bin", "freetds-dev", "freetds-common"])

        if check_freetds_installed():
            log.log("FreeTDS installed successfully", "SUCCESS")
            version_info = get_freetds_version()
            log.log(f"FreeTDS version info:\n{version_info}", "INFO")
            return True
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
    """Install vim via apt."""
    log.section("Vim Installation")

    if not force and check_vim_installed():
        vim_path = get_vim_path()
        log.log(f"vim already installed: {vim_path}", "SUCCESS")
        return True

    if not prompt_yes_no("vim not found. Install vim via apt?", default=True):
        log.log("Skipping vim installation", "INFO")
        return False

    log.log("Installing vim via apt...", "STEP")

    try:
        run_command(["sudo", "apt", "update"])
        run_command(["sudo", "apt", "install", "-y", "vim"])

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

def ensure_pip_installed() -> bool:
    """Ensure pip is installed, installing it via apt if missing."""
    try:
        result = subprocess.run(
            [sys.executable, "-m", "pip", "--version"],
            capture_output=True,
            text=True
        )
        if result.returncode == 0:
            log.log(f"pip is available: {result.stdout.strip()}", "SUCCESS")
            return True
    except Exception:
        pass

    log.log("pip not found, installing via apt...", "STEP")
    try:
        run_command(["sudo", "apt", "install", "-y", "python3-pip"])
        # Verify installation
        result = subprocess.run(
            [sys.executable, "-m", "pip", "--version"],
            capture_output=True,
            text=True
        )
        if result.returncode == 0:
            log.log("pip installed successfully", "SUCCESS")
            return True
    except Exception as e:
        log.log(f"Failed to install pip: {e}", "ERROR")

    return False


def install_python_packages() -> bool:
    """Install Python packages from src/."""
    log.section("Python Packages Installation")

    # Ensure pip is available first
    if not ensure_pip_installed():
        log.log("Cannot proceed without pip", "ERROR")
        return False

    if not SRC_DIR.exists():
        log.log(f"Source directory not found: {SRC_DIR}", "ERROR")
        return False

    pyproject = SRC_DIR / "pyproject.toml"

    if not pyproject.exists():
        log.log("No pyproject.toml found in src/", "ERROR")
        return False

    log.subsection("Upgrading pip")

    try:
        run_command([sys.executable, "-m", "pip", "install", "--upgrade", "pip", "--break-system-packages"])
    except Exception as e:
        log.log(f"pip upgrade failed (continuing anyway): {e}", "WARN")

    log.subsection("Installing IBS Compilers package")

    try:
        # Install in editable mode (--break-system-packages for PEP 668 compliance on Ubuntu 23.04+)
        run_command([sys.executable, "-m", "pip", "install", "-e", str(SRC_DIR), "--break-system-packages"])
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

def configure_shell_path() -> bool:
    """Add PATH export to shell config if not present."""
    log.section("Shell Configuration")

    # Determine shell config file
    shell = os.environ.get("SHELL", "/bin/bash")
    if "zsh" in shell:
        config_file = Path.home() / ".zshrc"
    else:
        config_file = Path.home() / ".bashrc"

    log.log(f"Shell: {shell}", "INFO")
    log.log(f"Config file: {config_file}", "INFO")

    user_bin = Path.home() / ".local" / "bin"
    path_export = 'export PATH="$PATH:$HOME/.local/bin"'
    marker = "# IBS Compilers PATH"

    # Check if already configured
    if config_file.exists():
        content = config_file.read_text()
        if marker in content or str(user_bin) in content:
            log.log("PATH already configured in shell config", "SUCCESS")
            return True

    # Add PATH export to shell config
    try:
        with open(config_file, "a") as f:
            f.write(f"\n{marker}\n{path_export}\n")
        log.log(f"Added PATH export to {config_file}", "SUCCESS")
        return True
    except Exception as e:
        log.log(f"Failed to update {config_file}: {e}", "ERROR")
        return False


def verify_installation() -> bool:
    """Source shell config and verify set_profile is available."""
    log.section("Verifying Installation")

    shell = os.environ.get("SHELL", "/bin/bash")
    if "zsh" in shell:
        config_file = Path.home() / ".zshrc"
    else:
        config_file = Path.home() / ".bashrc"

    # Source the config and check if set_profile exists
    try:
        result = subprocess.run(
            [shell, "-c", f"source {config_file} && which set_profile"],
            capture_output=True,
            text=True
        )
        if result.returncode == 0 and result.stdout.strip():
            log.log(f"set_profile found at: {result.stdout.strip()}", "SUCCESS")
            return True
        else:
            log.log("set_profile not found in PATH after sourcing config", "WARN")
            return False
    except Exception as e:
        log.log(f"Verification failed: {e}", "ERROR")
        return False


# =============================================================================
# SUMMARY
# =============================================================================

def show_summary():
    """Show installation summary."""
    log.section("Installation Summary")

    print()
    print(f"  {'Component':<15} {'Status':<20} {'Path':<40}")
    print(f"  {'-'*15} {'-'*20} {'-'*40}")

    # FreeTDS
    if check_freetds_installed():
        tsql_path = shutil.which("tsql") or "N/A"
        print(f"  {'FreeTDS':<15} {'Installed':<20} {tsql_path:<40}")
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
        description="IBS Compilers Linux/Ubuntu Installer",
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

    # Header
    print()
    print("=" * 60)
    print("  IBS Compilers - Linux/Ubuntu Installer")
    print("=" * 60)
    print()
    print(f"  Platform: {platform.system()} {platform.release()}")
    print(f"  Python: {sys.version.split()[0]}")
    print(f"  Project: {SCRIPT_DIR}")
    print(f"  Log file: {LOG_FILE}")
    print()

    if not prompt_yes_no("Continue with installation?"):
        log.log("User cancelled installation", "INFO")
        return 1

    # Step 1: FreeTDS
    if not args.skip_freetds:
        if not install_freetds(args.force):
            if not prompt_yes_no("FreeTDS installation had issues. Continue anyway?"):
                show_summary()
                return 1
    else:
        log.log("Skipping FreeTDS installation (user flag)", "SKIP")

    # Step 2: vim
    if not args.skip_vim:
        if not install_vim(args.force):
            log.log("vim installation skipped or failed", "WARN")
    else:
        log.log("Skipping vim installation (user flag)", "SKIP")

    # Step 3: Python packages
    if not args.skip_packages:
        if not install_python_packages():
            log.log("Python package installation had issues", "WARN")
    else:
        log.log("Skipping Python package installation (user flag)", "SKIP")

    # Step 4: Initialize settings.json
    initialize_settings_json(args.force)

    # Step 5: Configure shell PATH
    configure_shell_path()

    # Step 6: Verify installation
    verify_installation()

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
