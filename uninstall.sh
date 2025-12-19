#!/bin/bash
set -euo pipefail

# ============================================================
# UltimateServer – Uninstall Script
# ============================================================

# -------------------------
# Configuration
# -------------------------
INSTALL_DIR="/var/UltimateServer"
SERVICE_NAME="ultimateserver"

# -------------------------
# Colors & helpers
# -------------------------
RED="\033[1;31m"
GREEN="\033[1;32m"
YELLOW="\033[1;33m"
BLUE="\033[1;34m"
RESET="\033[0m"

log()    { echo -e "${BLUE}[INFO]${RESET} $1"; }
success(){ echo -e "${GREEN}[OK]${RESET}   $1"; }
warn()   { echo -e "${YELLOW}[WARN]${RESET} $1"; }
error()  { echo -e "${RED}[FAIL]${RESET} $1"; exit 1; }

# -------------------------
# Banner
# -------------------------
clear
echo "============================================================"
echo "            UltimateServer Uninstall Utility"
echo "============================================================"
echo

# -------------------------
# Root check
# -------------------------
if [[ "$EUID" -ne 0 ]]; then
  error "Please run as root (sudo ./uninstall.sh)"
fi

# -------------------------
# Confirmation
# -------------------------
echo -e "${YELLOW}This will permanently remove UltimateServer.${RESET}"
read -rp "Are you sure you want to continue? [y/N]: " CONFIRM

if [[ ! "$CONFIRM" =~ ^[Yy]$ ]]; then
  echo "Uninstall cancelled."
  exit 0
fi

# -------------------------
# Stop and disable service
# -------------------------
if systemctl list-unit-files | grep -q "^${SERVICE_NAME}.service"; then
  log "Stopping service..."
  systemctl stop "${SERVICE_NAME}" || true

  log "Disabling service..."
  systemctl disable "${SERVICE_NAME}" || true

  log "Removing systemd service file..."
  rm -f "/etc/systemd/system/${SERVICE_NAME}.service"

  systemctl daemon-reload
  systemctl reset-failed || true

  success "Service removed"
else
  warn "Service not found, skipping"
fi

# -------------------------
# Remove application files
# -------------------------
if [[ -d "$INSTALL_DIR" ]]; then
  log "Removing installation directory: ${INSTALL_DIR}"
  rm -rf "$INSTALL_DIR"
  success "Application files removed"
else
  warn "Installation directory not found, skipping"
fi

# -------------------------
# Firewall cleanup (optional)
# -------------------------
log "Removing firewall rules..."
ufw delete allow 11001/tcp || true
ufw delete allow 11002 || true
ufw delete allow 11003/udp || true
ufw delete allow 11004 || true
ufw reload || true
success "Firewall rules cleaned"

# -------------------------
# Optional package cleanup (COMMENTED BY DEFAULT)
# -------------------------
: <<'OPTIONAL_REMOVAL'
# Uncomment ONLY if UltimateServer was the sole reason these were installed

apt remove -y nginx certbot php8.3 php8.3-fpm
apt autoremove -y
OPTIONAL_REMOVAL

# -------------------------
# Done
# -------------------------
echo
echo "============================================================"
echo " UltimateServer has been successfully uninstalled"
echo "============================================================"
echo
