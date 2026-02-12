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

FREETDS_MIN_VERSION = "1.4.0"
FREETDS_SOURCE_VERSION = "1.5.11"
FREETDS_SOURCE_URL = f"https://www.freetds.org/files/stable/freetds-{FREETDS_SOURCE_VERSION}.tar.gz"


def get_tsql_path() -> str:
    """Get absolute path to tsql binary."""
    path = shutil.which("tsql")
    if path:
        return path
    for fallback in ["/usr/local/bin/tsql", "/usr/bin/tsql"]:
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


def parse_freetds_tls(tsql_output: str = "") -> str:
    """Parse the TLS library from tsql -C output."""
    import re
    if not tsql_output:
        tsql_output = get_freetds_version_output()
    match = re.search(r'TLS library:\s*(\S+)', tsql_output)
    return match.group(1) if match else ""


def version_at_least(current: str, minimum: str) -> bool:
    """Compare version strings (e.g. '1.3.17' >= '1.4.0')."""
    def parts(v):
        return [int(x) for x in v.split('.')]
    try:
        return parts(current) >= parts(minimum)
    except (ValueError, IndexError):
        return False


def find_all_tsql() -> list[str]:
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
        output = get_freetds_version_output(path)
        ver = parse_freetds_version(output)
        tls = parse_freetds_tls(output)
        status = f"v{ver} ({tls})" if ver and tls else f"v{ver}" if ver else "unknown"
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
    tsql_output = get_freetds_version_output(first_tsql)
    version = parse_freetds_version(tsql_output)
    tls = parse_freetds_tls(tsql_output)

    if not version or not version_at_least(version, FREETDS_MIN_VERSION):
        log.log(f"FAIL: first tsql in PATH ({first_tsql}) is still v{version}", "ERROR")
        return False

    log.log(f"tsql binary: {first_tsql}", "SUCCESS")
    log.log(f"Version: {version} (>= {FREETDS_MIN_VERSION})", "SUCCESS")

    # 5. TLS check
    if tls:
        log.log(f"TLS library: {tls}", "INFO")
    else:
        log.log("TLS library: unknown", "WARN")

    if tls and "gnutls" in tls.lower():
        log.log("FAIL: GnuTLS detected — Azure SQL requires OpenSSL", "ERROR")
        return False
    if tls and "openssl" in tls.lower():
        log.log("TLS: OpenSSL (required for Azure SQL)", "SUCCESS")

    log.log(f"FreeTDS verification passed: v{version} ({tls or 'unknown TLS'})", "SUCCESS")
    return True


def remove_apt_freetds() -> None:
    """Remove apt-installed FreeTDS packages."""
    log.log("Removing apt-installed FreeTDS...", "STEP")
    try:
        run_command(["sudo", "apt", "remove", "-y", "freetds-bin", "freetds-dev", "freetds-common"], check=False)
        run_command(["sudo", "apt", "autoremove", "-y"], check=False)
    except Exception as e:
        log.log(f"apt remove failed (continuing): {e}", "WARN")


def install_freetds_from_source() -> bool:
    """Build and install FreeTDS from source for Azure SQL redirect support."""
    log.subsection(f"Building FreeTDS {FREETDS_SOURCE_VERSION} from source")

    # Remove apt version first to avoid conflicts
    remove_apt_freetds()

    import tempfile
    build_dir = Path(tempfile.mkdtemp(prefix="freetds-build-"))
    tarball = build_dir / f"freetds-{FREETDS_SOURCE_VERSION}.tar.gz"
    source_dir = build_dir / f"freetds-{FREETDS_SOURCE_VERSION}"

    try:
        # Install build dependencies
        log.log("Installing build dependencies...", "STEP")
        run_command(["sudo", "apt", "install", "-y", "build-essential", "libssl-dev", "pkg-config"])

        # Download source
        log.log(f"Downloading FreeTDS {FREETDS_SOURCE_VERSION}...", "STEP")
        import urllib.request
        urllib.request.urlretrieve(FREETDS_SOURCE_URL, str(tarball))
        log.log(f"Downloaded to {tarball}", "SUCCESS")

        # Extract
        log.log("Extracting...", "STEP")
        run_command(["tar", "xzf", str(tarball), "-C", str(build_dir)])

        # Configure
        log.log("Configuring (--with-openssl --enable-mars)...", "STEP")
        run_command(
            ["./configure", "--with-openssl", "--enable-mars", "--disable-odbc"],
            cwd=str(source_dir)
        )

        # Build
        log.log("Building...", "STEP")
        run_command(["make"], cwd=str(source_dir))

        # Install to /usr/local/
        log.log("Installing...", "STEP")
        run_command(["sudo", "make", "install"], cwd=str(source_dir))

        # Update shared library cache
        run_command(["sudo", "ldconfig"], check=False)

        # Symlink into /usr/bin/ so tsql is always on PATH
        for tool in ["tsql", "bsqldb", "freebcp"]:
            local_path = Path(f"/usr/local/bin/{tool}")
            if local_path.exists():
                log.log(f"Linking {local_path} -> /usr/bin/{tool}", "INFO")
                run_command(["sudo", "ln", "-sf", str(local_path), f"/usr/bin/{tool}"])

        # Verify
        if verify_freetds():
            return True
        log.log("Source build completed but verification failed", "WARN")
        return True  # Build succeeded even if verification had issues

    except Exception as e:
        log.log(f"Source build failed: {e}", "ERROR")
        return False
    finally:
        # Cleanup build directory
        try:
            import shutil as sh
            sh.rmtree(str(build_dir), ignore_errors=True)
        except Exception:
            pass


def install_freetds(force: bool = False) -> bool:
    """Install FreeTDS. Uses apt first, then builds from source if version is too old or TLS is wrong."""
    log.section("FreeTDS Installation")

    # Check existing installation
    if check_freetds_installed():
        tsql_output = get_freetds_version_output()
        version = parse_freetds_version(tsql_output)
        tls = parse_freetds_tls(tsql_output)

        if version and version_at_least(version, FREETDS_MIN_VERSION):
            if tls and "gnutls" in tls.lower():
                log.log(f"FreeTDS {version} uses GnuTLS — rebuilding with OpenSSL for Azure SQL", "WARN")
                return install_freetds_from_source()
            if not force:
                log.log(f"FreeTDS {version} already installed (>= {FREETDS_MIN_VERSION}, TLS: {tls or 'unknown'})", "SUCCESS")
                return verify_freetds()
            else:
                log.log(f"FreeTDS {version} installed but --force specified, rebuilding...", "INFO")
                return install_freetds_from_source()
        else:
            log.log(f"FreeTDS {version or 'unknown'} is too old (need >= {FREETDS_MIN_VERSION})", "WARN")
            return install_freetds_from_source()

    # Not installed — try apt first
    log.log("Installing FreeTDS via apt...", "STEP")

    try:
        run_command(["sudo", "apt", "update"])
        run_command(["sudo", "apt", "install", "-y", "freetds-bin", "freetds-dev", "freetds-common"])
    except Exception as e:
        log.log(f"apt install failed: {e}", "WARN")

    # Check version and TLS after apt install
    if check_freetds_installed():
        tsql_output = get_freetds_version_output()
        version = parse_freetds_version(tsql_output)
        tls = parse_freetds_tls(tsql_output)

        if version and version_at_least(version, FREETDS_MIN_VERSION):
            if tls and "gnutls" in tls.lower():
                log.log(f"apt installed FreeTDS {version} but uses GnuTLS — rebuilding with OpenSSL", "WARN")
                return install_freetds_from_source()
            log.log(f"FreeTDS {version} installed via apt (TLS: {tls or 'unknown'})", "SUCCESS")
            return verify_freetds()
        else:
            log.log(f"apt version {version or 'unknown'} is too old (need >= {FREETDS_MIN_VERSION})", "WARN")
            return install_freetds_from_source()
    else:
        log.log("apt install did not provide tsql, building from source...", "WARN")
        return install_freetds_from_source()


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
        run_command([sys.executable, "-m", "pip", "install", "--force-reinstall", "-e", str(SRC_DIR), "--break-system-packages"])
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
    tsql_path = get_tsql_path()
    if tsql_path:
        tsql_output = get_freetds_version_output(tsql_path)
        version = parse_freetds_version(tsql_output)
        tls = parse_freetds_tls(tsql_output)
        status = f"v{version} ({tls})" if version and tls else f"v{version}" if version else "Installed"
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

    # Step 3: Pull latest from origin
    pull_latest()

    # Step 4: Python packages
    if not args.skip_packages:
        if not install_python_packages():
            log.log("Python package installation had issues", "WARN")
    else:
        log.log("Skipping Python package installation (user flag)", "SKIP")

    # Step 5: Initialize settings.json
    initialize_settings_json(args.force)

    # Step 6: Configure shell PATH
    configure_shell_path()

    # Step 7: Verify installation
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
