#!/bin/bash
set -e

# === CONFIG ===
REPO="VoidbornGames/Ultimate_C_Sharp_Server"
INSTALL_DIR="/var/UltimateServer"
RELEASE_FILE="UltimateServer.zip"
DOTNET_CMD="dotnet Server.dll 11001 11002 11003"

echo "==== UltimateServer Auto Installer ===="

# 1️⃣ Update system
echo "[1/6] Updating system packages..."
apt update -y && apt install -y wget unzip ufw dotnet-sdk-8.0 php8.3 php8.3-cli php8.3-fpm

# 2️⃣ Create directory
echo "[2/6] Creating installation directory..."
mkdir -p "$INSTALL_DIR"
cd "$INSTALL_DIR"

# 3️⃣ Download latest release zip from GitHub
echo "[3/6] Downloading latest release from GitHub..."
LATEST_URL=$(wget -qO- "https://api.github.com/repos/$REPO/releases/latest" | grep "browser_download_url" | grep "$RELEASE_FILE" | cut -d '"' -f 4)

if [ -z "$LATEST_URL" ]; then
  echo "❌ Could not find release file '$RELEASE_FILE' in latest release."
  exit 1
fi

wget -O "$RELEASE_FILE" "$LATEST_URL"

# 4️⃣ Unzip and clean up
echo "[4/6] Extracting files..."
unzip -o "$RELEASE_FILE" -d "$INSTALL_DIR"
rm -f "$RELEASE_FILE"

# 5️⃣ Open firewall ports
echo "[5/6] Configuring firewall..."
ufw allow 11001/tcp
ufw allow 11002
ufw allow 11003/udp
ufw reload

# 6️⃣ Run the server
echo "[6/6] Starting UltimateServer..."
cd "$INSTALL_DIR"
$DOTNET_CMD
