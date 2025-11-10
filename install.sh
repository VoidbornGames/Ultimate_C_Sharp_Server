#!/bin/bash
set -e

# === CONFIG ===
REPO="VoidbornGames/Ultimate_C_Sharp_Server"
INSTALL_DIR="/var/UltimateServer"
RELEASE_FILE="UltimateServer.zip"
DOTNET_CMD="dotnet Server.dll 11001 11002 11003"

echo "==== 🧩 UltimateServer Auto Installer ===="

# 1️⃣ Root check
if [ "$EUID" -ne 0 ]; then
  echo "❌ Please run as root (sudo ./install.sh)"
  exit 1
fi

# 2️⃣ Update and install dependencies
echo "[1/6] Updating system packages..."
apt update -y
apt install -y wget unzip ufw dotnet-sdk-8.0 php8.3 php8.3-cli php8.3-fpm certbot nginx

# 3️⃣ Create installation directory
echo "[2/6] Creating installation directory..."
mkdir -p "$INSTALL_DIR"
cd "$INSTALL_DIR"

# 4️⃣ Fetch latest release download URL
echo "[3/6] Downloading latest release from GitHub..."
LATEST_URL=$(wget -qO- "https://api.github.com/repos/$REPO/releases/latest" \
  | grep "browser_download_url" \
  | grep "$RELEASE_FILE" \
  | cut -d '"' -f 4)

if [ -z "$LATEST_URL" ]; then
  echo "❌ Could not find release file '$RELEASE_FILE' in the latest release."
  echo "Make sure you uploaded it as a GitHub release asset."
  exit 1
fi

wget -O "$RELEASE_FILE" "$LATEST_URL"

# 5️⃣ Extract and clean up
echo "[4/6] Extracting files..."
unzip -o "$RELEASE_FILE" -d "$INSTALL_DIR" >/dev/null
rm -f "$RELEASE_FILE"

# 6️⃣ Configure firewall
echo "[5/6] Configuring firewall rules..."
ufw allow 11001/tcp
ufw allow 11002
ufw allow 11003/udp
ufw allow 11004
ufw reload

# 7️⃣ Create systemd service (auto-start on boot)
echo "[6/6] Creating systemd service..."

SERVICE_FILE="/etc/systemd/system/ultimateserver.service"

cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=UltimateServer
After=network.target

[Service]
WorkingDirectory=$INSTALL_DIR
ExecStart=/usr/bin/dotnet $INSTALL_DIR/Server.dll 11001 11002 11003
Restart=always
RestartSec=5
User=root
Environment=ASPNETCORE_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable ultimateserver
systemctl start ultimateserver

echo "✅ UltimateServer installation complete!"
echo "----------------------------------------"
echo "Installed at: $INSTALL_DIR"
echo "Service:      systemctl status ultimateserver"
echo "Ports:        11001, 11002, 11003, 11004"
echo "----------------------------------------"
