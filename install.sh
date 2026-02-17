#!/bin/bash
# install.sh — IBS Compilers installer for Linux/macOS
# One-liner: curl -fsSL https://raw.githubusercontent.com/innovative247/compilers/main/install.sh | bash

set -e

REPO="innovative247/compilers"
INSTALL_DIR="$HOME/ibs-compilers"

echo "IBS Compilers Installer"
echo ""

# --- Detect and remove existing Python installation ---
detect_python_install() {
    # Check for the pip package
    if python3 -m pip show ibs_compilers >/dev/null 2>&1; then
        return 0
    fi
    # Check for Python-based entry points in ~/.local/bin
    if [ -f "$HOME/.local/bin/runsql" ] && head -1 "$HOME/.local/bin/runsql" 2>/dev/null | grep -q python; then
        return 0
    fi
    return 1
}

# Skip Python check if .NET compilers are already installed
if [ -f "$INSTALL_DIR/runsql" ] || [ -f "$INSTALL_DIR/set_profile" ]; then
    SKIP_PYTHON_CHECK=true
else
    SKIP_PYTHON_CHECK=false
fi

if [ "$SKIP_PYTHON_CHECK" = false ] && detect_python_install; then
    echo "=== Existing Python installation detected ==="
    echo ""

    # Show what we found
    PIP_LOCATION=$(python3 -m pip show ibs_compilers 2>/dev/null | grep "^Location:" | cut -d' ' -f2- || true)
    PIP_VERSION=$(python3 -m pip show ibs_compilers 2>/dev/null | grep "^Version:" | cut -d' ' -f2- || true)
    PIP_EDITABLE=$(python3 -m pip show ibs_compilers 2>/dev/null | grep "^Editable project location:" | cut -d' ' -f4- || true)
    if [ -n "$PIP_VERSION" ]; then
        echo "  Python package: ibs_compilers v${PIP_VERSION}"
        [ -n "$PIP_LOCATION" ] && echo "  Location:       $PIP_LOCATION"
    fi

    # Find existing settings.json from Python installation
    PYTHON_SETTINGS=""
    # Try 1: Ask Python directly
    if [ -z "$PYTHON_SETTINGS" ] && command -v python3 >/dev/null 2>&1; then
        PYTHON_SETTINGS=$(python3 -c "
try:
    from commands.ibs_common import find_settings_file
    f = find_settings_file()
    if f.exists(): print(f)
except: pass
" 2>/dev/null || true)
    fi
    # Try 2: Editable project location (settings.json is at project root)
    if [ -z "$PYTHON_SETTINGS" ] && [ -n "$PIP_EDITABLE" ]; then
        if [ -f "$PIP_EDITABLE/settings.json" ]; then
            PYTHON_SETTINGS="$PIP_EDITABLE/settings.json"
        fi
    fi
    # Try 3: Location field — for editable installs this is the src/ dir, settings.json is one level up
    if [ -z "$PYTHON_SETTINGS" ] && [ -n "$PIP_LOCATION" ]; then
        PARENT=$(dirname "$PIP_LOCATION")
        if [ -f "$PARENT/settings.json" ]; then
            PYTHON_SETTINGS="$PARENT/settings.json"
        fi
    fi

    if [ -n "$PYTHON_SETTINGS" ]; then
        echo "  Settings:       $PYTHON_SETTINGS"
    fi

    # Count Python entry points in ~/.local/bin
    PYTHON_CMDS=0
    for cmd in runsql isqlline set_profile set_actions eact compile_actions \
               set_table_locations eloc create_tbl_locations set_options eopt \
               import_options set_messages compile_msg install_msg extract_msg \
               set_required_fields ereq install_required_fields i_run_upgrade \
               runcreate transfer_data iplan iplanext iwho; do
        if [ -f "$HOME/.local/bin/$cmd" ] && head -1 "$HOME/.local/bin/$cmd" 2>/dev/null | grep -q python; then
            PYTHON_CMDS=$((PYTHON_CMDS + 1))
        fi
    done
    if [ "$PYTHON_CMDS" -gt 0 ]; then
        echo "  Entry points:   $PYTHON_CMDS commands in ~/.local/bin/"
    fi

    echo ""
    echo "  The .NET 8 compilers replace the Python compilers."
    echo "  The Python compilers package must be removed to avoid conflicts."
    echo "  (This does not remove Python itself.)"
    echo ""
    read -p "  Remove Python compilers? [Y/n]: " REMOVE_PYTHON < /dev/tty
    REMOVE_PYTHON=${REMOVE_PYTHON:-Y}

    if [ "${REMOVE_PYTHON,,}" != "n" ] && [ "${REMOVE_PYTHON,,}" != "no" ]; then
        echo ""

        # Step 1: Uninstall pip package
        if python3 -m pip show ibs_compilers >/dev/null 2>&1; then
            echo "  Uninstalling pip package ibs_compilers..."
            python3 -m pip uninstall ibs_compilers -y 2>/dev/null || true
        fi

        # Step 2: Remove any leftover entry points in ~/.local/bin
        REMOVED=0
        for cmd in runsql isqlline set_profile set_actions eact compile_actions \
                   set_table_locations eloc create_tbl_locations set_options eopt \
                   import_options set_messages compile_msg install_msg extract_msg \
                   set_required_fields ereq install_required_fields i_run_upgrade \
                   runcreate transfer_data iplan iplanext iwho; do
            if [ -f "$HOME/.local/bin/$cmd" ] && head -1 "$HOME/.local/bin/$cmd" 2>/dev/null | grep -q python; then
                rm -f "$HOME/.local/bin/$cmd"
                REMOVED=$((REMOVED + 1))
            fi
        done
        [ "$REMOVED" -gt 0 ] && echo "  Removed $REMOVED Python entry points from ~/.local/bin/"

        # Step 3: Remove FreeTDS if installed via apt
        if dpkg -l freetds-bin >/dev/null 2>&1; then
            echo ""
            echo "  FreeTDS (freetds-bin) is installed. The .NET 8 compilers don't need it."
            read -p "  Remove FreeTDS packages? [y/N]: " REMOVE_FREETDS < /dev/tty
            if [ "${REMOVE_FREETDS,,}" = "y" ] || [ "${REMOVE_FREETDS,,}" = "yes" ]; then
                echo "  Removing FreeTDS..."
                sudo apt remove -y freetds-bin freetds-dev 2>/dev/null || echo "  (requires sudo — you can remove manually: sudo apt remove freetds-bin freetds-dev)"
            fi
        fi

        echo ""
        echo "  Python compilers removed."
        echo ""
    else
        echo ""
        echo "  WARNING: Python compilers left in place. Commands in ~/.local/bin/ may"
        echo "  shadow the .NET 8 versions depending on your PATH order."
        echo ""
    fi
fi

# --- Detect platform ---
OS="$(uname -s)"
case "$OS" in
    Linux*)  ASSET_NAME="compilers-net8-linux-x64.tar.gz" ;;
    Darwin*) ASSET_NAME="compilers-net8-osx-x64.tar.gz" ;;
    *)       echo "Unsupported platform: $OS"; exit 1 ;;
esac

# Get latest release
echo "Checking latest release..."
RELEASE_URL="https://api.github.com/repos/$REPO/releases/latest"
RELEASE_JSON=$(curl -fsSL -H "User-Agent: IBS-Compilers-Installer" "$RELEASE_URL")

# Parse JSON — try jq first, fall back to python3, then grep
parse_json_field() {
    local json="$1" field="$2"
    if command -v jq >/dev/null 2>&1; then
        echo "$json" | jq -r ".$field"
    elif command -v python3 >/dev/null 2>&1; then
        echo "$json" | python3 -c "import sys,json; print(json.load(sys.stdin)['${field}'])"
    else
        echo "$json" | grep -o "\"${field}\": *\"[^\"]*\"" | head -1 | cut -d'"' -f4
    fi
}

parse_asset_url() {
    local json="$1" name="$2"
    if command -v jq >/dev/null 2>&1; then
        echo "$json" | jq -r ".assets[] | select(.name == \"$name\") | .browser_download_url"
    elif command -v python3 >/dev/null 2>&1; then
        echo "$json" | python3 -c "
import sys, json
data = json.load(sys.stdin)
for a in data.get('assets', []):
    if a['name'] == '$name':
        print(a['browser_download_url'])
        break
"
    else
        echo "$json" | grep -o "\"browser_download_url\": *\"[^\"]*${name}\"" | head -1 | cut -d'"' -f4
    fi
}

VERSION=$(parse_json_field "$RELEASE_JSON" "tag_name")
echo "Latest version: $VERSION"

# Find download URL
DOWNLOAD_URL=$(parse_asset_url "$RELEASE_JSON" "$ASSET_NAME")
if [ -z "$DOWNLOAD_URL" ]; then
    echo "Could not find $ASSET_NAME in release assets."
    exit 1
fi

# Download
TEMP_FILE=$(mktemp)
echo "Downloading $ASSET_NAME..."
curl -fsSL -o "$TEMP_FILE" "$DOWNLOAD_URL"

# Extract
echo "Copying to $INSTALL_DIR..."
mkdir -p "$INSTALL_DIR"
tar -xzf "$TEMP_FILE" -C "$INSTALL_DIR"
rm -f "$TEMP_FILE"

# Make binaries executable (skip non-binary files like .json)
find "$INSTALL_DIR" -maxdepth 1 -type f ! -name "*.json" ! -name "*.example" -exec chmod +x {} +

# Migrate or create settings.json
if [ ! -f "$INSTALL_DIR/settings.json" ]; then
    if [ -n "$PYTHON_SETTINGS" ] && [ -f "$PYTHON_SETTINGS" ]; then
        echo ""
        echo "  Found existing settings.json with your profiles:"
        echo "    $PYTHON_SETTINGS"
        read -p "  Copy to new installation? [Y/n]: " MIGRATE_SETTINGS < /dev/tty
        MIGRATE_SETTINGS=${MIGRATE_SETTINGS:-Y}
        if [ "${MIGRATE_SETTINGS,,}" != "n" ] && [ "${MIGRATE_SETTINGS,,}" != "no" ]; then
            cp "$PYTHON_SETTINGS" "$INSTALL_DIR/settings.json"
            echo "  Migrated settings.json with your existing profiles."
        elif [ -f "$INSTALL_DIR/settings.json.example" ]; then
            cp "$INSTALL_DIR/settings.json.example" "$INSTALL_DIR/settings.json"
            echo "  Created settings.json from example."
        fi
    elif [ -f "$INSTALL_DIR/settings.json.example" ]; then
        cp "$INSTALL_DIR/settings.json.example" "$INSTALL_DIR/settings.json"
        echo "Created settings.json from example."
    fi
fi

echo ""
echo "Copied $VERSION to $INSTALL_DIR"
echo ""

# --- Prompt to run configure ---
read -p "Run configure now? (adds to PATH and verifies setup) [Y/n]: " RUN_CONFIGURE < /dev/tty
RUN_CONFIGURE=${RUN_CONFIGURE:-Y}

if [ "${RUN_CONFIGURE,,}" != "n" ] && [ "${RUN_CONFIGURE,,}" != "no" ]; then
    "$INSTALL_DIR/set_profile" configure
    echo ""
    echo "To activate PATH, restart your terminal or run:"
    echo "  source ~/.bashrc && hash -r"
    echo ""
    echo "Then run: set_profile"
    echo "  (configure database connections)"
else
    echo "Next steps:"
    echo "  1. Run: $INSTALL_DIR/set_profile configure"
    echo "  2. Restart your terminal (or: source ~/.bashrc && hash -r)"
    echo "  3. Run: set_profile"
    echo "     (configure database connections)"
fi
