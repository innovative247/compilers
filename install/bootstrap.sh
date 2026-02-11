#!/bin/bash
#
# IBS Compilers Bootstrap Script for Linux and macOS
#
# Lightweight bootstrap script that ensures Python is installed,
# then hands off to the platform-specific installer.
#
# Usage:
#   ./bootstrap.sh
#   ./bootstrap.sh --skip-python
#   ./bootstrap.sh --help
#

set -e

# =============================================================================
# CONFIGURATION
# =============================================================================

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
LOG_FILE="$SCRIPT_DIR/installer.log"
REQUIRED_PYTHON_VERSION="3.8"

# Detect platform
PLATFORM="$(uname -s)"
ARCH="$(uname -m)"

case "$PLATFORM" in
    Linux*)
        INSTALLER_SCRIPT="$SCRIPT_DIR/installer_linux.py"
        PLATFORM_NAME="Linux"
        ;;
    Darwin*)
        INSTALLER_SCRIPT="$SCRIPT_DIR/installer_macos.py"
        PLATFORM_NAME="macOS"
        ;;
    *)
        echo "Unsupported platform: $PLATFORM"
        echo "For Windows, use: .\\bootstrap.ps1"
        exit 1
        ;;
esac

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# =============================================================================
# HELPER FUNCTIONS
# =============================================================================

log_info() {
    echo -e "   $1"
}

log_success() {
    echo -e " ${GREEN}+${NC} $1"
}

log_warn() {
    echo -e " ${YELLOW}!${NC} $1"
}

log_error() {
    echo -e " ${RED}X${NC} $1"
}

log_step() {
    echo -e "${CYAN}>>${NC} $1"
}

show_help() {
    echo ""
    echo -e "${CYAN}IBS Compilers Bootstrap Script ($PLATFORM_NAME)${NC}"
    echo "================================================"
    echo ""
    echo "This script verifies Python is installed, then runs the"
    echo "platform-specific installer ($INSTALLER_SCRIPT)."
    echo ""
    echo "Usage: ./bootstrap.sh [options]"
    echo ""
    echo "Options:"
    echo "  --skip-python    Don't attempt to install Python if missing"
    echo "  --help, -h       Show this help message"
    echo ""
    echo "The installer script supports additional options:"
    echo "  --skip-freetds   Skip FreeTDS installation"
    echo "  --skip-packages  Skip Python package installation"
    echo "  --skip-vim       Skip vim installation"
    echo "  --force          Force reinstallation of components"
    echo ""
}

get_python_cmd() {
    # Try python3 first, then python
    for cmd in python3 python; do
        if command -v "$cmd" &> /dev/null; then
            # Use sed for cross-platform compatibility (macOS grep lacks -P)
            version=$("$cmd" --version 2>&1 | sed -n 's/Python \([0-9]*\.[0-9]*\).*/\1/p')
            if [ -n "$version" ]; then
                echo "$cmd"
                return 0
            fi
        fi
    done
    return 1
}

get_python_version() {
    local cmd=$1
    "$cmd" --version 2>&1 | sed -n 's/Python \([0-9]*\.[0-9]*\.[0-9]*\).*/\1/p'
}

version_ge() {
    # Returns 0 if $1 >= $2
    [ "$(printf '%s\n' "$2" "$1" | sort -V | head -n1)" = "$2" ]
}

# =============================================================================
# PLATFORM-SPECIFIC: MACOS
# =============================================================================

check_xcode_cli() {
    if xcode-select -p &> /dev/null; then
        return 0
    fi
    return 1
}

install_xcode_cli() {
    log_step "Installing Xcode Command Line Tools..."
    log_info "This may open a dialog - please follow the prompts"

    xcode-select --install 2>/dev/null || true

    echo ""
    read -p "Press Enter after Xcode Command Line Tools installation completes... "

    if check_xcode_cli; then
        log_success "Xcode Command Line Tools installed"
        return 0
    else
        log_error "Xcode Command Line Tools installation may have failed"
        return 1
    fi
}

install_python_macos() {
    log_step "Installing Python..."

    # Check for Homebrew first
    if command -v brew &> /dev/null; then
        log_info "Installing Python via Homebrew..."
        brew install python3

        if command -v python3 &> /dev/null; then
            log_success "Python installed successfully via Homebrew"
            return 0
        fi
    fi

    # Fallback: suggest manual installation
    log_error "Could not auto-install Python"
    echo ""
    echo "  Please install Python manually:"
    echo "  Option 1: Install Homebrew first, then run: brew install python3"
    echo "            /bin/bash -c \"\$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)\""
    echo "  Option 2: Download from https://www.python.org/downloads/macos/"
    echo ""
    return 1
}

# =============================================================================
# PLATFORM-SPECIFIC: LINUX
# =============================================================================

install_python_linux() {
    log_step "Installing Python via apt..."

    if ! command -v apt &> /dev/null; then
        log_error "apt not found - cannot auto-install Python"
        log_info "Please install Python $REQUIRED_PYTHON_VERSION or later manually"
        return 1
    fi

    sudo apt update
    sudo apt install -y python3 python3-pip python3-venv

    if command -v python3 &> /dev/null; then
        log_success "Python installed successfully"
        return 0
    else
        log_error "Python installation failed"
        return 1
    fi
}

# =============================================================================
# CROSS-PLATFORM
# =============================================================================

install_python() {
    case "$PLATFORM" in
        Darwin*)
            install_python_macos
            ;;
        Linux*)
            install_python_linux
            ;;
    esac
}

# =============================================================================
# MAIN
# =============================================================================

main() {
    # Parse arguments
    SKIP_PYTHON=false

    while [[ $# -gt 0 ]]; do
        case $1 in
            --skip-python)
                SKIP_PYTHON=true
                shift
                ;;
            --help|-h)
                show_help
                exit 0
                ;;
            *)
                log_error "Unknown option: $1"
                show_help
                exit 1
                ;;
        esac
    done

    # Initialize log file
    echo "IBS Compilers Installer Log" > "$LOG_FILE"
    echo "Platform: $PLATFORM_NAME ($PLATFORM $ARCH)" >> "$LOG_FILE"
    echo "============================================================" >> "$LOG_FILE"
    echo "" >> "$LOG_FILE"

    # Header
    clear
    echo ""
    echo -e "  ${CYAN}=============================================${NC}"
    echo -e "  ${CYAN}    IBS Compilers - $PLATFORM_NAME Bootstrap${NC}"
    echo -e "  ${CYAN}=============================================${NC}"
    echo ""
    echo "  Project: $PROJECT_ROOT"
    echo "  Log file: $LOG_FILE"
    echo "  Architecture: $ARCH"
    echo ""

    # Check for installer script
    if [ ! -f "$INSTALLER_SCRIPT" ]; then
        log_error "Installer not found at: $INSTALLER_SCRIPT"
        echo ""
        log_info "Please ensure the installer script exists in the install directory."
        echo ""
        exit 1
    fi

    # macOS only: Check Xcode Command Line Tools
    if [ "$PLATFORM" = "Darwin" ]; then
        echo ""
        echo "  --- Checking Xcode Command Line Tools ---"
        echo ""

        if ! check_xcode_cli; then
            log_warn "Xcode Command Line Tools not found"
            echo ""
            read -p "Install Xcode Command Line Tools? (Required for Homebrew) [Y/n] " response
            response=${response:-Y}

            if [[ "$response" =~ ^[Yy] ]]; then
                install_xcode_cli
            else
                log_warn "Skipping Xcode CLI - Homebrew installation may fail"
            fi
        else
            log_success "Xcode Command Line Tools installed"
        fi
    fi

    # Step 1: Check Python
    echo ""
    echo "  --- Checking Python Installation ---"
    echo ""
    log_step "Checking for Python installation"

    PYTHON_CMD=$(get_python_cmd || echo "")

    if [ -z "$PYTHON_CMD" ]; then
        log_warn "Python not found"

        if [ "$SKIP_PYTHON" = true ]; then
            log_error "Cannot proceed without Python"
            exit 1
        fi

        echo ""
        read -p "Python is required. Attempt to install Python? [Y/n] " response
        response=${response:-Y}

        if [[ "$response" =~ ^[Yy] ]]; then
            if ! install_python; then
                log_error "Please install Python manually and re-run this script"
                exit 1
            fi
            PYTHON_CMD=$(get_python_cmd || echo "")
        else
            log_error "Cannot proceed without Python"
            exit 1
        fi
    fi

    # Python found - check version
    PYTHON_VERSION=$(get_python_version "$PYTHON_CMD")
    PYTHON_PATH=$(which "$PYTHON_CMD")

    log_success "Found: Python $PYTHON_VERSION"
    log_info "Path: $PYTHON_PATH"
    log_info "Command: $PYTHON_CMD"

    if ! version_ge "$PYTHON_VERSION" "$REQUIRED_PYTHON_VERSION"; then
        log_warn "Python version $PYTHON_VERSION is below minimum requirement ($REQUIRED_PYTHON_VERSION)"

        if [ "$SKIP_PYTHON" = true ]; then
            log_error "Cannot proceed with outdated Python"
            exit 1
        fi

        echo ""
        read -p "Attempt to install newer Python? [Y/n] " response
        response=${response:-Y}

        if [[ "$response" =~ ^[Yy] ]]; then
            install_python
            PYTHON_CMD=$(get_python_cmd || echo "")
            PYTHON_VERSION=$(get_python_version "$PYTHON_CMD")
        fi
    fi

    log_success "Python $PYTHON_VERSION meets requirements"

    # Update log with Python info
    echo "Python: $PYTHON_VERSION" >> "$LOG_FILE"
    echo "" >> "$LOG_FILE"

    # Step 2: Pull latest code (ensures installer has newest FreeTDS logic, etc.)
    echo ""
    echo "  --- Pulling Latest Code ---"
    echo ""

    if command -v git &> /dev/null && [ -d "$PROJECT_ROOT/.git" ]; then
        log_step "Pulling latest changes..."
        if git -C "$PROJECT_ROOT" pull; then
            log_success "Repository updated"
        else
            log_warn "git pull failed (continuing with current code)"
        fi
    else
        log_warn "git not available or not a git repository - skipping pull"
    fi

    # Step 3: Run platform-specific installer
    echo ""
    echo "  --- Launching $PLATFORM_NAME Installer ---"
    echo ""

    log_step "Launching installer with $PYTHON_CMD"
    log_info "Installer will append to the same log file"
    echo ""

    log_info "Executing: $PYTHON_CMD $INSTALLER_SCRIPT"

    "$PYTHON_CMD" "$INSTALLER_SCRIPT"
    EXIT_CODE=$?

    if [ $EXIT_CODE -ne 0 ]; then
        log_error "Installer exited with code $EXIT_CODE"
        echo ""
        log_info "Check the log file for details: $LOG_FILE"
        echo ""
        exit $EXIT_CODE
    fi

    log_success "Bootstrap completed successfully"
    log_info "See $LOG_FILE for full installation log"

    # Remind about shell restart
    echo ""
    echo -e "  ${YELLOW}Note: You may need to restart your terminal or run:${NC}"
    if [ "$PLATFORM" = "Darwin" ]; then
        echo -e "  ${YELLOW}  source ~/.zshrc${NC}"
    else
        echo -e "  ${YELLOW}  source ~/.bashrc${NC}"
    fi
    echo -e "  ${YELLOW}for PATH changes to take effect.${NC}"
    echo ""
}

# Run main
main "$@"
