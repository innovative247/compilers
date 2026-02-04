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
import urllib.request
from pathlib import Path

# =============================================================================
# CONFIGURATION
# =============================================================================

SCRIPT_DIR = Path(__file__).parent.resolve()
PROJECT_ROOT = SCRIPT_DIR.parent
SRC_DIR = PROJECT_ROOT / "src"
LOG_FILE = SCRIPT_DIR / "installer.log"

# Windows-specific paths
WINDOWS_MSYS2_PATH = Path(os.environ.get("LOCALAPPDATA", "C:/msys64")) / "msys64"
WINDOWS_MSYS2_BIN = WINDOWS_MSYS2_PATH / "ucrt64/bin"
WINDOWS_MSYS2_USR_BIN = WINDOWS_MSYS2_PATH / "usr/bin"  # For tail, grep, etc.

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
    """Install MSYS2 on Windows via direct download."""
    log.section("MSYS2 Installation")

    if check_msys2_installed():
        log.log(f"MSYS2 already installed at {WINDOWS_MSYS2_PATH}", "SUCCESS")
        return True

    # MSYS2 latest installer URL (always points to current version)
    installer_url = "https://github.com/msys2/msys2-installer/releases/download/nightly-x86_64/msys2-x86_64-latest.exe"
    installer_path = Path(os.environ.get("TEMP", ".")) / "msys2-x86_64-latest.exe"

    log.log(f"Downloading MSYS2 from GitHub...", "STEP")
    print()
    print(f"  URL: {installer_url}")
    print()

    try:
        # Download with progress indicator
        def report_progress(block_num, block_size, total_size):
            if total_size > 0:
                percent = min(100, (block_num * block_size * 100) // total_size)
                if block_num % 100 == 0:  # Update every 100 blocks
                    print(f"\r  Downloading: {percent}%", end="", flush=True)

        urllib.request.urlretrieve(installer_url, installer_path, report_progress)
        print()  # Newline after progress

        if not installer_path.exists():
            raise FileNotFoundError(f"Download completed but file not found at {installer_path}")

        file_size_mb = installer_path.stat().st_size / (1024 * 1024)
        log.log(f"Downloaded: {file_size_mb:.2f} MB", "SUCCESS")

    except Exception as e:
        log.log(f"Failed to download MSYS2 installer: {e}", "ERROR")
        print()
        print("  MANUAL INSTALLATION REQUIRED:")
        print("  1. Download from: https://www.msys2.org/")
        print("  2. Run the installer")
        print(f"  3. Install to {WINDOWS_MSYS2_PATH}")
        print("  4. Re-run this installer")
        print()
        print("  If download fails repeatedly, check:")
        print("  - Network connectivity to github.com")
        print("  - Firewall/proxy settings")
        print("  - Try downloading manually in a browser")
        print()

        if prompt_yes_no("Open MSYS2 download page in browser?"):
            import webbrowser
            webbrowser.open("https://www.msys2.org/")

        return False

    log.log(f"Installing MSYS2 (silent install to {WINDOWS_MSYS2_PATH})...", "STEP")
    print()
    print("  This may take several minutes. Please wait...")
    print()

    try:
        # Silent install to LOCALAPPDATA
        # See: https://www.msys2.org/docs/installer/
        result = subprocess.run(
            [str(installer_path), "install", "--root", str(WINDOWS_MSYS2_PATH), "--confirm-command"],
            capture_output=True,
            text=True,
            timeout=600  # 10 minute timeout
        )

        if result.returncode != 0:
            log.log(f"MSYS2 installer exited with code {result.returncode}", "ERROR")
            if result.stderr:
                log.log(f"Error output: {result.stderr[:500]}", "ERROR")
            raise RuntimeError(f"Installer failed with exit code {result.returncode}")

        # Clean up installer
        try:
            installer_path.unlink()
        except Exception:
            pass

    except subprocess.TimeoutExpired:
        log.log("MSYS2 installation timed out after 10 minutes", "ERROR")
        print()
        print("  MANUAL INSTALLATION REQUIRED:")
        print(f"  1. Run the installer manually: {installer_path}")
        print(f"  2. Install to {WINDOWS_MSYS2_PATH}")
        print("  3. Re-run this installer")
        print()
        return False

    except Exception as e:
        log.log(f"MSYS2 installation failed: {e}", "ERROR")
        print()
        print("  MANUAL INSTALLATION REQUIRED:")
        print(f"  1. Run the installer manually: {installer_path}")
        print(f"  2. Install to {WINDOWS_MSYS2_PATH}")
        print("  3. Re-run this installer")
        print()
        print("  Common issues:")
        print("  - Run as Administrator")
        print("  - Antivirus may block the installer")
        print("  - Insufficient disk space (needs ~2GB)")
        print()
        return False

    if check_msys2_installed():
        log.log("MSYS2 installation verified", "SUCCESS")
        return True
    else:
        log.log(f"MSYS2 not found at {WINDOWS_MSYS2_PATH} after installation", "ERROR")
        print()
        print("  Installation may have succeeded but to a different location.")
        print(f"  This installer expects MSYS2 at {WINDOWS_MSYS2_PATH}")
        print()
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
        print()
        print("  MANUAL INSTALLATION REQUIRED:")
        print("  1. Install MSYS2 first from https://www.msys2.org/")
        print("  2. Re-run this installer")
        print()
        return False

    msys2_shell = WINDOWS_MSYS2_PATH / "msys2_shell.cmd"

    if not msys2_shell.exists():
        log.log(f"MSYS2 shell not found at {msys2_shell}", "ERROR")
        print()
        print("  MSYS2 appears to be installed but shell script is missing.")
        print("  Try reinstalling MSYS2 from https://www.msys2.org/")
        print()
        return False

    log.log("Installing FreeTDS via pacman (automatic)...", "INFO")

    try:
        pacman_cmd = "pacman -S --noconfirm mingw-w64-ucrt-x86_64-freetds"

        result = subprocess.run(
            [str(msys2_shell), "-ucrt64", "-defterm", "-no-start", "-c", pacman_cmd],
            capture_output=True,
            text=True,
            timeout=300  # 5 minute timeout
        )

        log.log(f"pacman exited with code: {result.returncode}", "INFO")

        if result.returncode != 0:
            log.log("pacman command failed", "WARN")
            if result.stderr:
                log.log(f"Error: {result.stderr[:300]}", "ERROR")

    except subprocess.TimeoutExpired:
        log.log("FreeTDS installation timed out after 5 minutes", "ERROR")
        print()
        print("  MANUAL INSTALLATION REQUIRED:")
        print("  1. Open 'MSYS2 UCRT64' from Start Menu")
        print("  2. Run: pacman -S mingw-w64-ucrt-x86_64-freetds")
        print("  3. Re-run this installer")
        print()
        return False

    except Exception as e:
        log.log(f"FreeTDS installation failed: {e}", "ERROR")
        print()
        print("  MANUAL INSTALLATION REQUIRED:")
        print("  1. Open 'MSYS2 UCRT64' from Start Menu")
        print("  2. Run: pacman -S mingw-w64-ucrt-x86_64-freetds")
        print("  3. Re-run this installer")
        print()
        return False

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
    """Add MSYS2 bin directories to Windows PATH."""
    log.subsection("Configuring MSYS2 PATH")

    # Both directories to add: ucrt64/bin (FreeTDS) and usr/bin (tail, grep, etc.)
    paths_to_add = [
        (WINDOWS_MSYS2_BIN, "ucrt64/bin (FreeTDS)"),
        (WINDOWS_MSYS2_USR_BIN, "usr/bin (tail, grep, etc.)"),
    ]

    current_path = os.environ.get("PATH", "")
    success = True

    for path_obj, description in paths_to_add:
        path_str = str(path_obj)

        if not path_obj.exists():
            log.log(f"MSYS2 path does not exist: {path_str}", "WARN")
            continue

        # Check if already in current PATH
        if path_str.lower() in current_path.lower():
            log.log(f"PATH already contains {description}", "SUCCESS")
            continue

        # Add to current session
        os.environ["PATH"] = f"{path_str};{os.environ.get('PATH', '')}"
        log.log(f"Added to current session PATH: {path_str}", "SUCCESS")

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

                if path_str.lower() not in user_path.lower():
                    new_path = f"{user_path};{path_str}" if user_path else path_str
                    winreg.SetValueEx(key, "Path", 0, winreg.REG_EXPAND_SZ, new_path)
                    log.log(f"Added to User PATH (permanent): {path_str}", "SUCCESS")
                else:
                    log.log(f"User PATH already contains {description}", "SUCCESS")

        except Exception as e:
            log.log(f"Could not modify user PATH for {description}: {e}", "WARN")
            success = False

    return success


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


def install_vim_via_msys2() -> bool:
    """Install vim using MSYS2 pacman."""
    if not check_msys2_installed():
        log.log("MSYS2 not installed - cannot install vim via pacman", "ERROR")
        return False

    msys2_shell = WINDOWS_MSYS2_PATH / "msys2_shell.cmd"
    if not msys2_shell.exists():
        log.log(f"MSYS2 shell not found at {msys2_shell}", "ERROR")
        return False

    log.log("Installing vim via MSYS2 pacman...", "STEP")

    try:
        # Install vim to usr/bin (available system-wide via MSYS2)
        pacman_cmd = "pacman -S --noconfirm vim"

        result = subprocess.run(
            [str(msys2_shell), "-ucrt64", "-defterm", "-no-start", "-c", pacman_cmd],
            capture_output=True,
            text=True,
            timeout=300  # 5 minute timeout
        )

        log.log(f"pacman exited with code: {result.returncode}", "INFO")

        # Check if vim is now available in MSYS2 usr/bin
        vim_path = WINDOWS_MSYS2_USR_BIN / "vim.exe"
        if vim_path.exists():
            log.log(f"vim installed at {vim_path}", "SUCCESS")
            return True
        else:
            log.log("vim not found after pacman install", "WARN")
            return False

    except subprocess.TimeoutExpired:
        log.log("vim installation timed out", "ERROR")
        return False
    except Exception as e:
        log.log(f"vim installation failed: {e}", "ERROR")
        return False


def configure_vim_path() -> bool:
    """
    Ensure vim.exe is available in PATH on Windows.

    Checks common installation locations. If not found, installs via MSYS2 pacman.
    MSYS2's usr/bin is already added to PATH by configure_path(), so vim will
    be available once installed there.
    """
    log.section("VIM Installation")

    # Check if vim is already in PATH
    vim_path = shutil.which("vim")
    if vim_path:
        log.log(f"vim already in PATH: {vim_path}", "SUCCESS")
        return True

    # Check if vim exists in MSYS2 usr/bin (may not be in PATH yet)
    msys2_vim = WINDOWS_MSYS2_USR_BIN / "vim.exe"
    if msys2_vim.exists():
        log.log(f"vim found at {msys2_vim}", "SUCCESS")
        log.log("vim will be available after PATH is configured", "INFO")
        return True

    # Common vim installation locations on Windows (check before installing)
    vim_locations = [
        # Standard Vim installations (check multiple versions)
        Path("C:/Program Files/Vim/vim91"),
        Path("C:/Program Files/Vim/vim90"),
        Path("C:/Program Files/Vim/vim82"),
        Path("C:/Program Files/Vim/vim81"),
        Path("C:/Program Files (x86)/Vim/vim91"),
        Path("C:/Program Files (x86)/Vim/vim90"),
        Path("C:/Program Files (x86)/Vim/vim82"),
        Path("C:/Program Files (x86)/Vim/vim81"),
        # Git for Windows includes vim
        Path("C:/Program Files/Git/usr/bin"),
        Path("C:/Program Files (x86)/Git/usr/bin"),
        # User-local installations
        Path(os.environ.get("LOCALAPPDATA", ""), "Programs/Git/usr/bin"),
    ]

    # Find vim.exe in known locations
    for location in vim_locations:
        if not location.exists():
            continue
        vim_exe = location / "vim.exe"
        if vim_exe.exists():
            log.log(f"Found vim at: {vim_exe}", "SUCCESS")
            # Add this directory to PATH
            vim_dir_str = str(location)
            current_path = os.environ.get("PATH", "")

            if vim_dir_str.lower() not in current_path.lower():
                os.environ["PATH"] = f"{vim_dir_str};{current_path}"
                log.log(f"Added to current session PATH: {vim_dir_str}", "SUCCESS")

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

                        if vim_dir_str.lower() not in user_path.lower():
                            new_path = f"{user_path};{vim_dir_str}" if user_path else vim_dir_str
                            winreg.SetValueEx(key, "Path", 0, winreg.REG_EXPAND_SZ, new_path)
                            log.log(f"Added to User PATH (permanent): {vim_dir_str}", "SUCCESS")
                except Exception as e:
                    log.log(f"Could not modify user PATH: {e}", "WARN")

            return True

    # vim not found anywhere - install via MSYS2 pacman
    log.log("vim not found in common locations", "INFO")

    if not check_msys2_installed():
        log.log("MSYS2 not installed - cannot auto-install vim", "ERROR")
        print()
        print("  MANUAL INSTALLATION REQUIRED:")
        print("  Option 1: Install vim from https://www.vim.org/download.php")
        print("  Option 2: Install Git for Windows (includes vim)")
        print("            https://git-scm.com/download/win")
        print()
        return False

    log.log(f"Installing vim via MSYS2 (will be available in {WINDOWS_MSYS2_USR_BIN})...", "STEP")

    if install_vim_via_msys2():
        log.log("vim installed successfully via MSYS2", "SUCCESS")
        log.log(f"vim will be available via {WINDOWS_MSYS2_USR_BIN} (already in PATH)", "INFO")
        return True
    else:
        log.log("vim installation via MSYS2 failed", "ERROR")
        print()
        print("  MANUAL INSTALLATION REQUIRED:")
        print("  Open MSYS2 terminal and run: pacman -S vim")
        print("  Or install from: https://www.vim.org/download.php")
        print()
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

    settings_file = PROJECT_ROOT / "settings.json"
    needs_creation = False

    if settings_file.exists():
        if force:
            log.log("Force flag set - will recreate settings.json", "INFO")
            needs_creation = True
        else:
            # Validate existing file
            log.log(f"Settings file exists: {settings_file}", "SUCCESS")
            try:
                import json
                with open(settings_file, 'r', encoding='utf-8') as f:
                    data = json.load(f)

                if "Profiles" in data:
                    profile_count = len(data["Profiles"])
                    log.log(f"Found {profile_count} profile(s) in settings.json", "SUCCESS")
                    return True
                else:
                    log.log("settings.json missing 'Profiles' key - will recreate", "WARN")
                    needs_creation = True
            except json.JSONDecodeError as e:
                log.log(f"settings.json is not valid JSON: {e}", "ERROR")
                log.log("Backing up invalid file and creating new one", "INFO")
                needs_creation = True
            except Exception as e:
                log.log(f"Could not read settings.json: {e}", "ERROR")
                return False
    else:
        needs_creation = True

    if not needs_creation:
        return True

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

    # Check VIM
    vim_path = shutil.which("vim")
    vim_status = "Installed" if vim_path else "Not Found"
    results.append(("VIM", vim_status, vim_path or "Not in PATH"))

    # Check tail (from MSYS2 usr/bin)
    tail_path = shutil.which("tail")
    tail_status = "Available" if tail_path else "Not Found"
    results.append(("tail", tail_status, tail_path or "Not in PATH"))

    # Check IBS Commands (pip-installed .exe wrappers)
    runsql_path = shutil.which("runsql")
    runsql_status = "Available" if runsql_path else "Not Found"
    results.append(("IBS Commands", runsql_status, runsql_path or "N/A"))

    # Check settings.json
    settings_file = PROJECT_ROOT / "settings.json"
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
    settings_file = PROJECT_ROOT / "settings.json"
    runsql_available = shutil.which("runsql") is not None
    vim_available = shutil.which("vim") is not None
    tail_available = shutil.which("tail") is not None
    all_good = (
        check_msys2_installed() and
        check_freetds_installed() and
        runsql_available and
        settings_file.exists() and
        vim_available and
        tail_available
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
        if not vim_available:
            print("  Missing: VIM - Install vim or add its directory to PATH")
        if not tail_available:
            print(f"  Missing: tail - Ensure {WINDOWS_MSYS2_USR_BIN} is in PATH")


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

    # Step 1: MSYS2
    msys2_ok = install_msys2()
    if not msys2_ok:
        log.log("MSYS2 installation failed - continuing with remaining steps", "WARN")

    # Step 2: FreeTDS (requires MSYS2)
    freetds_ok = False
    if not args.skip_freetds:
        if msys2_ok:
            freetds_ok = install_freetds(args.force)
            if not freetds_ok:
                log.log("FreeTDS installation failed - continuing with remaining steps", "WARN")
        else:
            log.log("Skipping FreeTDS - MSYS2 not available", "SKIP")
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

    # Step 6: Configure vim PATH (Windows only)
    if platform.system() == "Windows":
        configure_vim_path()

    # Show summary
    show_summary()

    return 0


if __name__ == "__main__":
    sys.exit(main())
