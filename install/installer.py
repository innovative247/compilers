#!/usr/bin/env python3
"""
IBS Compilers Windows Installer

This script handles installation of all dependencies for the IBS Python Compilers on Windows:
- MSYS2
- FreeTDS (via MSYS2 pacman)
- Python packages
- Configuration files

Usage:
    python installer.py [options]

Options:
    --skip-freetds      Skip FreeTDS installation
    --skip-packages     Skip Python package installation
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

# Windows-specific paths
WINDOWS_MSYS2_PATH = Path("C:/msys64")
WINDOWS_MSYS2_BIN = WINDOWS_MSYS2_PATH / "ucrt64/bin"

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
        self._init_log_file()

    def _init_log_file(self):
        """Initialize the log file with header."""
        with open(self.log_file, "w", encoding="utf-8") as f:
            f.write(f"IBS Compilers Installer Log\n")
            f.write(f"Platform: {platform.system()} {platform.release()}\n")
            f.write(f"Python: {sys.version}\n")
            f.write("=" * 60 + "\n\n")

    def _write_log(self, level: str, message: str):
        """Write to log file."""
        with open(self.log_file, "a", encoding="utf-8") as f:
            f.write(f"[{level}] {message}\n")

    def _supports_color(self) -> bool:
        """Check if terminal supports color."""
        if platform.system() == "Windows":
            return os.environ.get("TERM") or os.environ.get("WT_SESSION")
        return hasattr(sys.stdout, "isatty") and sys.stdout.isatty()

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

        if self._supports_color():
            color = self.COLORS.get(level, self.COLORS["INFO"])
            reset = self.COLORS["RESET"]
            print(f"{color}{prefix}{message}{reset}")
        else:
            print(f"{prefix}{message}")

    def section(self, title: str):
        """Print a section header."""
        separator = "=" * 60
        print()
        if self._supports_color():
            color = self.COLORS["STEP"]
            reset = self.COLORS["RESET"]
            print(f"{color}{separator}{reset}")
            print(f"{color}  {title}{reset}")
            print(f"{color}{separator}{reset}")
        else:
            print(separator)
            print(f"  {title}")
            print(separator)
        self._write_log("STEP", title)

    def subsection(self, title: str):
        """Print a subsection header."""
        print()
        print(f"  --- {title} ---")
        self._write_log("STEP", title)


log = Logger(LOG_FILE)

# =============================================================================
# UTILITY FUNCTIONS
# =============================================================================

def get_platform() -> str:
    """Get platform name - should always be Windows for this installer."""
    system = platform.system().lower()
    if system != "windows":
        raise RuntimeError(f"This installer is for Windows only. Current platform: {system}")
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
    print()
    response = input(f"{question} {suffix} ").strip().lower()

    if not response:
        return default
    return response in ("y", "yes")


# =============================================================================
# WINDOWS: MSYS2 INSTALLATION
# =============================================================================

def check_msys2_installed() -> bool:
    """Check if MSYS2 is installed."""
    return WINDOWS_MSYS2_PATH.exists()


def install_msys2() -> bool:
    """Install MSYS2 on Windows."""
    log.section("MSYS2 Installation")

    if check_msys2_installed():
        log.log(f"MSYS2 already installed at {WINDOWS_MSYS2_PATH}", "SUCCESS")
        return True

    if not command_exists("winget"):
        log.log("winget not found - manual installation required", "WARN")
        print()
        print("  Please install MSYS2 manually:")
        print("  1. Download from: https://www.msys2.org/")
        print("  2. Run the installer")
        print("  3. Install to C:\\msys64 (default)")
        print("  4. Re-run this installer")
        print()

        if prompt_yes_no("Open MSYS2 download page in browser?"):
            import webbrowser
            webbrowser.open("https://www.msys2.org/")

        return False

    log.log("Installing MSYS2 via winget...", "INFO")
    print()
    print("  This will open the MSYS2 installer.")
    print("  Please complete the installation wizard.")
    print("  Use default settings (install to C:\\msys64)")
    print()

    if not prompt_yes_no("Ready to install MSYS2?"):
        log.log("User cancelled MSYS2 installation", "WARN")
        return False

    try:
        # Use -i for interactive installation
        run_command(
            ["winget", "install", "-i", "MSYS2.MSYS2", "--accept-source-agreements"],
            check=False  # Don't fail if user cancels
        )
    except Exception as e:
        log.log(f"MSYS2 installation failed: {e}", "ERROR")
        return False

    if check_msys2_installed():
        log.log("MSYS2 installation verified", "SUCCESS")
        return True
    else:
        log.log(f"MSYS2 not found at {WINDOWS_MSYS2_PATH}", "ERROR")
        return False


# =============================================================================
# FREETDS INSTALLATION
# =============================================================================

def check_freetds_installed() -> bool:
    """Check if FreeTDS is installed on Windows."""
    tsql_path = WINDOWS_MSYS2_BIN / "tsql.exe"
    freebcp_path = WINDOWS_MSYS2_BIN / "freebcp.exe"
    return tsql_path.exists() and freebcp_path.exists()


def install_freetds_windows() -> bool:
    """Install FreeTDS on Windows via MSYS2 pacman."""
    log.subsection("Installing FreeTDS via MSYS2 pacman")

    if not check_msys2_installed():
        log.log("MSYS2 not installed - cannot install FreeTDS", "ERROR")
        return False

    msys2_shell = WINDOWS_MSYS2_PATH / "msys2_shell.cmd"

    if not msys2_shell.exists():
        log.log(f"MSYS2 shell not found at {msys2_shell}", "ERROR")
        return False

    print()
    print("  FreeTDS will be installed from MSYS2 UCRT64.")
    print()
    print("  Option 1: Automatic (recommended)")
    print("  Option 2: Manual - run in MSYS2 UCRT64 terminal:")
    print("            pacman -S mingw-w64-ucrt-x86_64-freetds")
    print()

    if prompt_yes_no("Try automatic installation?"):
        log.log("Attempting automatic FreeTDS installation...", "INFO")

        try:
            pacman_cmd = "pacman -S --noconfirm mingw-w64-ucrt-x86_64-freetds"

            result = subprocess.run(
                [str(msys2_shell), "-ucrt64", "-defterm", "-no-start", "-c", pacman_cmd],
                capture_output=False,
                text=True
            )

            log.log(f"pacman exited with code: {result.returncode}", "INFO")

        except Exception as e:
            log.log(f"Automatic installation failed: {e}", "ERROR")
            print()
            print("  Please install manually:")
            print("  1. Open 'MSYS2 UCRT64' from Start Menu")
            print("  2. Run: pacman -S mingw-w64-ucrt-x86_64-freetds")
            print()
            input("Press Enter after installing FreeTDS...")
    else:
        print()
        print("  Please install FreeTDS manually, then continue.")
        input("Press Enter after installing FreeTDS...")

    return check_freetds_installed()


def install_freetds(force: bool = False) -> bool:
    """Install FreeTDS on Windows via MSYS2."""
    log.section("FreeTDS Installation")

    if check_freetds_installed() and not force:
        log.log("FreeTDS already installed", "SUCCESS")

        # Show version
        try:
            result = run_command(["tsql", "-C"], capture_output=True, check=False)
            if result.stdout:
                log.log("FreeTDS version info:", "INFO")
                for line in result.stdout.strip().split("\n")[:5]:
                    log.log(f"  {line}", "INFO")
        except Exception:
            pass

        return True

    # Install via Windows method
    success = install_freetds_windows()

    if success and check_freetds_installed():
        log.log("FreeTDS installation verified", "SUCCESS")
        return True
    else:
        log.log("FreeTDS installation could not be verified", "ERROR")
        return False


# =============================================================================
# PATH CONFIGURATION
# =============================================================================

def configure_path_windows() -> bool:
    """Add MSYS2 bin directory to Windows PATH."""
    log.subsection("Configuring MSYS2 PATH")

    msys2_bin = str(WINDOWS_MSYS2_BIN)

    if not WINDOWS_MSYS2_BIN.exists():
        log.log(f"MSYS2 bin path does not exist: {msys2_bin}", "ERROR")
        return False

    # Check current PATH
    current_path = os.environ.get("PATH", "")
    if msys2_bin.lower() in current_path.lower():
        log.log("PATH already contains MSYS2 bin directory", "SUCCESS")
        return True

    # Add to current session
    os.environ["PATH"] = f"{msys2_bin};{current_path}"
    log.log(f"Added to current session PATH: {msys2_bin}", "SUCCESS")

    # Add to user PATH permanently (Windows)
    try:
        import winreg

        with winreg.OpenKey(
            winreg.HKEY_CURRENT_USER,
            r"Environment",
            0,
            winreg.KEY_READ | winreg.KEY_WRITE
        ) as key:
            try:
                user_path, _ = winreg.QueryValueEx(key, "Path")
            except FileNotFoundError:
                user_path = ""

            if msys2_bin.lower() not in user_path.lower():
                new_path = f"{user_path};{msys2_bin}" if user_path else msys2_bin
                winreg.SetValueEx(key, "Path", 0, winreg.REG_EXPAND_SZ, new_path)
                log.log(f"Added to User PATH (permanent): {msys2_bin}", "SUCCESS")
            else:
                log.log("User PATH already contains MSYS2 bin directory", "SUCCESS")

    except Exception as e:
        log.log(f"Could not modify user PATH: {e}", "WARN")
        log.log("You may need to add to PATH manually", "WARN")

    return True


def configure_path() -> bool:
    """Configure PATH for Windows."""
    log.section("PATH Configuration")
    return configure_path_windows()


# =============================================================================
# PYTHON PACKAGES
# =============================================================================

def get_python_scripts_dir(user_mode: bool = False) -> Path:
    """Get the Python Scripts directory path."""
    if user_mode:
        # User scripts: %APPDATA%\Python\Python3XX\Scripts
        import site
        user_base = site.getuserbase()
        return Path(user_base) / "Scripts"
    else:
        # System scripts: C:\Python3XX\Scripts
        return Path(sys.prefix) / "Scripts"


def add_scripts_to_path(scripts_dir: Path) -> bool:
    """Add a Scripts directory to user PATH if not already present."""
    scripts_str = str(scripts_dir)

    # Check if already in PATH
    current_path = os.environ.get("PATH", "")
    if scripts_str.lower() in current_path.lower():
        log.log(f"Scripts directory already in PATH: {scripts_str}", "SUCCESS")
        return True

    # Add to current session
    os.environ["PATH"] = f"{scripts_str};{current_path}"
    log.log(f"Added to current session PATH: {scripts_str}", "SUCCESS")

    # Add to user PATH permanently
    try:
        import winreg

        with winreg.OpenKey(
            winreg.HKEY_CURRENT_USER,
            r"Environment",
            0,
            winreg.KEY_READ | winreg.KEY_WRITE
        ) as key:
            try:
                user_path, _ = winreg.QueryValueEx(key, "Path")
            except FileNotFoundError:
                user_path = ""

            if scripts_str.lower() not in user_path.lower():
                new_path = f"{user_path};{scripts_str}" if user_path else scripts_str
                winreg.SetValueEx(key, "Path", 0, winreg.REG_EXPAND_SZ, new_path)
                log.log(f"Added to User PATH (permanent): {scripts_str}", "SUCCESS")
            else:
                log.log("User PATH already contains Scripts directory", "SUCCESS")

        return True

    except Exception as e:
        log.log(f"Could not modify user PATH: {e}", "WARN")
        log.log(f"Please manually add to PATH: {scripts_str}", "WARN")
        return False


def install_python_packages() -> bool:
    """Install Python packages from src/."""
    log.section("Python Packages Installation")

    if not SRC_DIR.exists():
        log.log(f"Source directory not found: {SRC_DIR}", "ERROR")
        return False

    pyproject = SRC_DIR / "pyproject.toml"
    setup_py = SRC_DIR / "setup.py"

    if not pyproject.exists() and not setup_py.exists():
        log.log("No pyproject.toml or setup.py found in src/", "ERROR")
        return False

    log.subsection("Upgrading pip")

    try:
        run_command([sys.executable, "-m", "pip", "install", "--upgrade", "pip"])
    except Exception as e:
        log.log(f"pip upgrade failed (continuing anyway): {e}", "WARN")

    log.subsection("Installing IBS Compilers package")

    # Verify SRC_DIR has proper structure
    if not (SRC_DIR / "pyproject.toml").exists():
        log.log(f"pyproject.toml not found in {SRC_DIR}", "ERROR")
        log.log("Cannot install package without proper Python project structure", "ERROR")
        return False

    # Determine install location
    system_scripts = get_python_scripts_dir(user_mode=False)
    user_scripts = get_python_scripts_dir(user_mode=True)

    # Check if we can write to system Scripts directory
    can_write_system = False
    try:
        test_file = system_scripts / ".write_test"
        test_file.touch()
        test_file.unlink()
        can_write_system = True
    except (PermissionError, OSError):
        pass

    user_mode = not can_write_system

    if user_mode:
        log.log(f"No write access to {system_scripts}", "INFO")
        log.log("Installing to user directory instead", "INFO")

    try:
        # Build pip install command
        pip_cmd = [sys.executable, "-m", "pip", "install", "-e", str(SRC_DIR)]

        if user_mode:
            pip_cmd.insert(4, "--user")  # Insert --user before -e

        run_command(pip_cmd)

        # Determine which Scripts directory was used
        scripts_dir = user_scripts if user_mode else system_scripts
        log.log(f"IBS Compilers package installed to: {scripts_dir}", "SUCCESS")

        # Ensure Scripts directory is in PATH
        add_scripts_to_path(scripts_dir)

    except subprocess.CalledProcessError as e:
        log.log(f"Package installation failed with exit code {e.returncode}", "ERROR")
        log.log("Check that pyproject.toml and all required files are present", "WARN")
        return False
    except Exception as e:
        log.log(f"Package installation failed: {e}", "ERROR")
        return False

    return True


# =============================================================================
# SETTINGS.JSON INITIALIZATION
# =============================================================================

def initialize_settings_json(force: bool = False) -> bool:
    """Initialize or validate settings.json file."""
    log.section("Settings Configuration")

    settings_file = SRC_DIR / "settings.json"

    if settings_file.exists() and not force:
        log.log(f"Settings file exists: {settings_file}", "SUCCESS")

        # Validate it's valid JSON
        try:
            import json
            with open(settings_file, 'r', encoding='utf-8') as f:
                data = json.load(f)

            if "Profiles" in data:
                profile_count = len(data["Profiles"])
                log.log(f"Found {profile_count} profile(s) in settings.json", "SUCCESS")
                return True
            else:
                log.log("settings.json missing 'Profiles' key", "WARN")
                log.log("File may need to be recreated", "WARN")
        except json.JSONDecodeError as e:
            log.log(f"settings.json is not valid JSON: {e}", "ERROR")
            if not prompt_yes_no("Recreate settings.json with sample data?", default=False):
                return False
        except Exception as e:
            log.log(f"Could not read settings.json: {e}", "ERROR")
            return False

    # Create sample settings.json
    log.subsection("Creating sample settings.json")

    sample_settings = """{
  "Profiles": {
    "EXAMPLE_MSSQL": {
      "CMPY": 101,
      "IBSLANG": 1,
      "IR": "%CD%",
      "BCPJ": null,
      "PLATFORM": "MSSQL",
      "HOST": "127.0.0.1",
      "PORT": 1433,
      "USERNAME": "sa",
      "PASSWORD": "your_password_here",
      "PATH_APPEND": null
    },
    "EXAMPLE_SYBASE": {
      "CMPY": 123,
      "IBSLANG": 1,
      "IR": "%CD%",
      "BCPJ": null,
      "PLATFORM": "SYBASE",
      "HOST": "10.0.0.1",
      "PORT": 5000,
      "USERNAME": "sa",
      "PASSWORD": "your_password_here",
      "PATH_APPEND": null
    }
  }
}
"""

    if settings_file.exists():
        if not prompt_yes_no("Overwrite existing settings.json?", default=False):
            log.log("Keeping existing settings.json", "SKIP")
            return True

        # Backup existing
        backup_path = settings_file.with_suffix(".json.backup")
        try:
            shutil.copy(settings_file, backup_path)
            log.log(f"Backed up existing settings to: {backup_path}", "INFO")
        except Exception as e:
            log.log(f"Could not backup settings.json: {e}", "WARN")

    try:
        settings_file.write_text(sample_settings, encoding='utf-8')
        log.log(f"Created sample settings.json at {settings_file}", "SUCCESS")
        print()
        print(f"  IMPORTANT: Edit {settings_file}")
        print("  to configure your database connection profiles.")
        print()
        return True
    except Exception as e:
        log.log(f"Failed to create settings.json: {e}", "ERROR")
        return False


# =============================================================================
# CONNECTION TESTING
# =============================================================================
# Connection testing is now handled by test_connection.py in src/commands/


# =============================================================================
# SUMMARY
# =============================================================================

def show_summary():
    """Show installation summary for Windows."""
    log.section("Installation Summary")

    results = []

    # Check MSYS2
    msys2_status = "Installed" if check_msys2_installed() else "Not Found"
    results.append(("MSYS2", msys2_status, str(WINDOWS_MSYS2_PATH)))

    # Check FreeTDS
    freetds_status = "Installed" if check_freetds_installed() else "Not Found"
    freetds_path = shutil.which("tsql") or "Not in PATH"
    results.append(("FreeTDS", freetds_status, freetds_path))

    # Check Python
    results.append(("Python", f"Installed ({platform.python_version()})", sys.executable))

    # Check IBS Commands (pip-installed .exe wrappers)
    runsql_path = shutil.which("runsql")
    runsql_status = "Available" if runsql_path else "Not Found"
    results.append(("IBS Commands", runsql_status, runsql_path or "N/A"))

    # Check settings.json
    settings_file = SRC_DIR / "settings.json"
    settings_status = "Exists" if settings_file.exists() else "Not Found"
    results.append(("settings.json", settings_status, str(settings_file)))

    # Check log file
    log_status = "Exists" if LOG_FILE.exists() else "Not Found"
    results.append(("Log File", log_status, str(LOG_FILE)))

    # Print table
    print()
    print(f"  {'Component':<15} {'Status':<20} {'Path'}")
    print(f"  {'-'*15} {'-'*20} {'-'*40}")
    for component, status, path in results:
        print(f"  {component:<15} {status:<20} {path}")
    print()

    # Overall status
    settings_file = SRC_DIR / "settings.json"
    runsql_available = shutil.which("runsql") is not None
    all_good = (
        check_msys2_installed() and
        check_freetds_installed() and
        runsql_available and
        settings_file.exists()
    )

    if all_good:
        log.log("All components installed successfully!", "SUCCESS")
    else:
        log.log("Some components are missing or not configured.", "WARN")
        print()
        print(f"  Review the log file for details: {LOG_FILE}")
        print("  You can re-run this installer to retry failed steps.")
        print()
        if not settings_file.exists():
            print("  Missing: settings.json - Run installer with --force to create it")
        if not check_freetds_installed():
            print("  Missing: FreeTDS - Ensure tsql and freebcp are in PATH")
        if not runsql_available:
            print("  Missing: IBS Commands - pip install may have failed or Scripts directory not in PATH")


# =============================================================================
# MAIN
# =============================================================================

def main():
    parser = argparse.ArgumentParser(
        description="IBS Compilers Windows Installer",
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
        "--force",
        action="store_true",
        help="Force reinstallation of components"
    )

    args = parser.parse_args()

    plat = get_platform()

    # Header
    print()
    print("=" * 60)
    print("  IBS Compilers - Windows Installer")
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

    # Step 1: MSYS2
    if not install_msys2():
        if not prompt_yes_no("MSYS2 installation had issues. Continue anyway?"):
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

    # Step 3: PATH configuration
    configure_path()

    # Step 4: Python packages
    if not args.skip_packages:
        if not install_python_packages():
            log.log("Python package installation had issues", "WARN")
    else:
        log.log("Skipping Python package installation (user flag)", "SKIP")

    # Step 5: Initialize settings.json
    initialize_settings_json(args.force)

    # Show summary
    show_summary()

    # Optional connection testing
    print()
    if prompt_yes_no("Would you like to test a database connection?", default=False):
        try:
            log.log("Launching test_connection...", "INFO")
            # Run as a module from the src directory to handle relative imports
            subprocess.run(
                [sys.executable, "-m", "commands.test_connection"],
                cwd=str(PROJECT_ROOT / "src"),
                check=False
            )
        except Exception as e:
            log.log(f"Failed to launch test_connection: {e}", "ERROR")

    print()
    print("=" * 60)
    print("  Setup complete! See src/README.md for usage instructions.")
    print("=" * 60)
    print()

    return 0


if __name__ == "__main__":
    sys.exit(main())
