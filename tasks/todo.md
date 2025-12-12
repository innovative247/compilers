# Task: Fix installer to ensure pip and PATH are configured

## Plan
- [x] In installer_linux.py, add check for pip and install if missing before running pip install
- [x] Add function to auto-add PATH export to .bashrc if not present
- [x] Add verification step that sources .bashrc and tests set_profile exists

## Root Cause
- Python was pre-installed but python3-pip was not
- Installer only installs python3-pip when Python itself is missing
- PATH was never automatically added to .bashrc (only warned)

## Review
Changes made to `install/installer_linux.py`:

1. **Added `ensure_pip_installed()`** - Checks if pip is available, installs via `apt install python3-pip` if missing
2. **Replaced `check_shell_config()` with `configure_shell_path()`** - Actually adds PATH export to .bashrc instead of just warning
3. **Added `verify_installation()`** - Sources .bashrc and verifies `set_profile` is in PATH
4. **Added `--break-system-packages`** to pip install commands for PEP 668 compliance (Ubuntu 23.04+)

Changes made to `src/commands/ibs_common.py`:

5. **Fixed symlink timing bug** - Re-check if path exists before creating each symlink (parent symlink creation can make child paths exist)
6. **Added lowercase symlinks** - `css` -> `CSS` and `ibs` -> `IBS` for Linux case-insensitive navigation
