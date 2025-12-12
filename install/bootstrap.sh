#!/bin/bash
#
# IBS Compilers Bootstrap Script for Ubuntu/Linux
#
# Lightweight bootstrap script that ensures Python is installed,
# then hands off to installer_linux.py for cross-platform installation.
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
INSTALLER_SCRIPT="$SCRIPT_DIR/installer_linux.py"
LOG_FILE="$SCRIPT_DIR/installer.log"
REQUIRED_PYTHON_VERSION="3.8"

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
    echo -e "${CYAN}IBS Compilers Bootstrap Script (Ubuntu/Linux)${NC}"
    echo "==============================================="
    echo ""
    echo "This script verifies Python is installed, then runs installer_linux.py"
    echo "to complete the Linux installation."
    echo ""
    echo "Usage: ./bootstrap.sh [options]"
    echo ""
    echo "Options:"
    echo "  --skip-python    Don't attempt to install Python if missing"
    echo "  --help, -h       Show this help message"
    echo ""
    echo "The installer_linux.py script supports additional options:"
    echo "  --skip-freetds   Skip FreeTDS installation"
    echo "  --skip-packages  Skip Python package installation"
    echo "  --force          Force reinstallation of components"
    echo ""
}

get_python_cmd() {
    # Try python3 first, then python
    for cmd in python3 python; do
        if command -v "$cmd" &> /dev/null; then
            version=$("$cmd" --version 2>&1 | grep -oP 'Python \K[0-9]+\.[0-9]+')
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
    "$cmd" --version 2>&1 | grep -oP 'Python \K[0-9]+\.[0-9]+\.[0-9]+'
}

version_ge() {
    # Returns 0 if $1 >= $2
    [ "$(printf '%s\n' "$2" "$1" | sort -V | head -n1)" = "$2" ]
}

install_python() {
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
    echo "Platform: $(uname -s) $(uname -r)" >> "$LOG_FILE"
    echo "============================================================" >> "$LOG_FILE"
    echo "" >> "$LOG_FILE"

    # Header
    clear
    echo ""
    echo -e "  ${CYAN}=============================================${NC}"
    echo -e "  ${CYAN}  IBS Compilers - Ubuntu/Linux Bootstrap${NC}"
    echo -e "  ${CYAN}=============================================${NC}"
    echo ""
    echo "  Project: $PROJECT_ROOT"
    echo "  Log file: $LOG_FILE"
    echo ""

    # Check for installer script
    if [ ! -f "$INSTALLER_SCRIPT" ]; then
        log_error "installer_linux.py not found at: $INSTALLER_SCRIPT"
        echo ""
        log_info "Please ensure installer_linux.py exists in the install directory."
        echo ""
        exit 1
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
        read -p "Python is required. Install Python via apt? [Y/n] " response
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
        read -p "Install newer Python via apt? [Y/n] " response
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

    # Step 2: Run installer_linux.py
    echo ""
    echo "  --- Launching Linux Installer ---"
    echo ""

    log_step "Launching installer_linux.py with $PYTHON_CMD"
    log_info "installer_linux.py will append to the same log file"
    echo ""

    log_info "Executing: $PYTHON_CMD $INSTALLER_SCRIPT"

    "$PYTHON_CMD" "$INSTALLER_SCRIPT"
    EXIT_CODE=$?

    if [ $EXIT_CODE -ne 0 ]; then
        log_error "installer_linux.py exited with code $EXIT_CODE"
        echo ""
        log_info "Check the log file for details: $LOG_FILE"
        echo ""
        exit $EXIT_CODE
    fi

    log_success "Bootstrap completed - installer_linux.py succeeded"
    log_info "See $LOG_FILE for full installation log"
}

# Run main
main "$@"
