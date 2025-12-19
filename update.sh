#!/bin/bash
set -euo pipefail

# ============================================================
# UltimateServer – In-Place Update Script
# ============================================================

# -------------------------
# Configuration
# -------------------------
REPO="VoidbornGames/UltimateServer"
INSTALL_DIR="/var/UltimateServer"
SERVICE_NAME="ultimateserver"
RELEASE_FILE="UltimateServer.zip"
SERVER_BINARY="Server"
BACKUP_DIR="/var/UltimateServer_backup_$(date +%Y%m%d_%H%M%S)"

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
# Root check
# -------------------------
if [[ "$EUID" -ne 0 ]]; then
  error "Please run as root (sudo ./update.sh)"
fi

# -------------------------
# Sanity checks
# -------------------------
[[ -f "${INSTALL_DIR}/${SERVER_BINARY}" ]] || error "Server binary not found"
systemctl is-enabled "${SERVICE_NAME}" >/dev/null 2>&1 || error "Service not installed"

# -------------------------
# Stop service
# -------------------------
log "Stopping ${SERVICE_NAME}..."
systemctl stop "${SERVICE_NAME}"
success "Service stopped"

# -------------------------
# Backup current version
# -------------------------
log "Creating backup at ${BACKUP_DIR}..."
mkdir -p "${BACKUP_DIR}"
cp -a "${INSTALL_DIR}/${SERVER_BINARY}" "${BACKUP_DIR}/" || true
cp -a "${INSTALL_DIR}/"*.so "${BACKUP_DIR}/" 2>/dev/null || true
success "Backup created"

# -------------------------
# Download latest release
# -------------------------
log "Fetching latest release info..."
API_JSON=$(wget -qO- "https://api.github.com/repos/${REPO}/releases/latest" || true)

LATEST_URL=$(echo "$API_JSON" | jq -r ".assets[] | select(.name==\"${RELEASE_FILE}\") | .browser_download_url" 2>/dev/null || true)

[[ -n "$LATEST_URL" && "$LATEST_URL" != "null" ]] || error "Release asset not found"

TMP_DIR=$(mktemp -d)
log "Downloading update..."
wget -qO "${TMP_DIR}/${RELEASE_FILE}" "$LATEST_URL"

[[ -s "${TMP_DIR}/${RELEASE_FILE}" ]] || error "Downloaded file is empty"

# -------------------------
# Extract update
# -------------------------
log "Extracting update..."
unzip -o "${TMP_DIR}/${RELEASE_FILE}" -d "${TMP_DIR}" >/dev/null

[[ -f "${TMP_DIR}/${SERVER_BINARY}" ]] || error "Updated binary missing"

# -------------------------
# Install update
# -------------------------
log "Installing update..."
cp -f "${TMP_DIR}/${SERVER_BINARY}" "${INSTALL_DIR}/"
cp -f "${TMP_DIR}/"*.so "${INSTALL_DIR}/" 2>/dev/null || true
chmod +x "${INSTALL_DIR}/${SERVER_BINARY}"

success "Update installed"

# -------------------------
# Restart service
# -------------------------
log "Starting ${SERVICE_NAME}..."
systemctl start "${SERVICE_NAME}"

sleep 2
if ! systemctl is-active --quiet "${SERVICE_NAME}"; then
  warn "Service failed to start, rolling back..."
  cp -f "${BACKUP_DIR}/${SERVER_BINARY}" "${INSTALL_DIR}/"
  cp -f "${BACKUP_DIR}/"*.so "${INSTALL_DIR}/" 2>/dev/null || true
  systemctl start "${SERVICE_NAME}" || error "Rollback failed"
  error "Update failed and rollback applied"
fi

success "Service restarted successfully"

# -------------------------
# Cleanup
# -------------------------
rm -rf "${TMP_DIR}"
success "Temporary files cleaned"

echo
echo "============================================================"
echo " UltimateServer updated successfully"
echo " Backup stored at: ${BACKUP_DIR}"
echo "============================================================"
echo
