#!/usr/bin/env bash
# ============================================================================
#  APTM Gate Service â€” Ubuntu NUC Installer
#  Run as root: sudo bash setup.sh
#  Designed for offline (USB) deployment on Ubuntu 22.04/24.04 NUC devices.
# ============================================================================
set -e

INSTALL_DIR="/opt/aptm-gate"
SERVICE_NAME="aptm-gate"
DB_NAME="aptm_gate"
DB_USER="aptm"
DB_PASS="AptmGate@2024"
KESTREL_PORT=5000
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Colors
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'

info()  { echo -e "${CYAN}[INFO]${NC}  $1"; }
ok()    { echo -e "${GREEN}[OK]${NC}    $1"; }
warn()  { echo -e "${YELLOW}[WARN]${NC}  $1"; }
fail()  { echo -e "${RED}[FAIL]${NC}  $1"; exit 1; }

echo ""
echo -e "${CYAN}=======================================${NC}"
echo -e "${CYAN}  APTM Gate Service â€” NUC Installer${NC}"
echo -e "${CYAN}=======================================${NC}"
echo ""

# â”€â”€ Pre-flight checks â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
if [ "$EUID" -ne 0 ]; then
    fail "Please run as root: sudo bash setup.sh"
fi

if [ ! -d "$SCRIPT_DIR/backend" ]; then
    fail "backend/ directory not found. Run build-installer.ps1 first."
fi

# â”€â”€ Step 1: Install .NET 10 Runtime â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
echo ""
info "[1/8] Checking .NET runtime..."

if command -v dotnet &>/dev/null && dotnet --list-runtimes 2>/dev/null | grep -q "Microsoft.AspNetCore.App 10"; then
    ok ".NET ASP.NET Core 10 runtime already installed"
else
    warn ".NET 10 runtime not found. Attempting install..."

    # Check for offline tarball first
    DOTNET_TARBALL=$(find "$SCRIPT_DIR/prerequisites" -name "dotnet-runtime-10*linux-x64*" -o -name "aspnetcore-runtime-10*linux-x64*" 2>/dev/null | head -1)

    if [ -n "$DOTNET_TARBALL" ]; then
        info "Installing from offline tarball: $(basename "$DOTNET_TARBALL")"
        mkdir -p /usr/share/dotnet
        tar -xzf "$DOTNET_TARBALL" -C /usr/share/dotnet
        ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
        ok ".NET runtime installed from offline package"
    else
        # Try online install via Microsoft package feed
        info "No offline package found. Trying online install..."
        if ! command -v wget &>/dev/null; then
            apt-get update -qq && apt-get install -y -qq wget
        fi

        # Microsoft package repository
        wget -q "https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb" -O /tmp/packages-microsoft-prod.deb
        dpkg -i /tmp/packages-microsoft-prod.deb
        apt-get update -qq
        apt-get install -y -qq aspnetcore-runtime-10.0

        if dotnet --list-runtimes 2>/dev/null | grep -q "Microsoft.AspNetCore.App 10"; then
            ok ".NET 10 runtime installed via package manager"
        else
            fail ".NET 10 runtime installation failed. Download aspnetcore-runtime-10.0 manually."
        fi
    fi
fi

# â”€â”€ Step 2: Install PostgreSQL 16 â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
echo ""
info "[2/8] Checking PostgreSQL..."

if command -v psql &>/dev/null && systemctl is-active --quiet postgresql; then
    ok "PostgreSQL is running"
else
    warn "PostgreSQL not found or not running. Attempting install..."

    # Check for offline .deb packages
    OFFLINE_DEBS=$(find "$SCRIPT_DIR/prerequisites" -name "postgresql*.deb" 2>/dev/null | head -1)

    if [ -n "$OFFLINE_DEBS" ]; then
        info "Installing from offline .deb packages..."
        dpkg -i "$SCRIPT_DIR/prerequisites"/postgresql*.deb 2>/dev/null || true
        apt-get -f install -y -qq
        ok "PostgreSQL installed from offline packages"
    else
        info "No offline packages. Trying online install..."
        apt-get update -qq
        apt-get install -y -qq postgresql postgresql-contrib
    fi

    systemctl enable postgresql
    systemctl start postgresql
    ok "PostgreSQL installed and started"
fi

# â”€â”€ Step 3: Create database and user â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
echo ""
info "[3/8] Setting up database..."

# Create user if not exists
if sudo -u postgres psql -tAc "SELECT 1 FROM pg_roles WHERE rolname='$DB_USER'" | grep -q 1; then
    ok "Database user '$DB_USER' already exists"
else
    sudo -u postgres psql -c "CREATE USER $DB_USER WITH PASSWORD '$DB_PASS';"
    ok "Created database user '$DB_USER'"
fi

# Create database if not exists
if sudo -u postgres psql -tAc "SELECT 1 FROM pg_database WHERE datname='$DB_NAME'" | grep -q 1; then
    ok "Database '$DB_NAME' already exists"
else
    sudo -u postgres psql -c "CREATE DATABASE $DB_NAME OWNER $DB_USER;"
    ok "Created database '$DB_NAME'"
fi

# Grant privileges
sudo -u postgres psql -c "GRANT ALL PRIVILEGES ON DATABASE $DB_NAME TO $DB_USER;" 2>/dev/null
sudo -u postgres psql -d "$DB_NAME" -c "GRANT ALL ON SCHEMA public TO $DB_USER;" 2>/dev/null
ok "Database privileges granted"

# â”€â”€ Step 4: Deploy backend â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
echo ""
info "[4/8] Deploying backend to $INSTALL_DIR..."

# Stop existing service if running
if systemctl is-active --quiet "$SERVICE_NAME" 2>/dev/null; then
    info "Stopping existing $SERVICE_NAME service..."
    systemctl stop "$SERVICE_NAME"
fi

mkdir -p "$INSTALL_DIR"

# Copy backend binaries
cp -r "$SCRIPT_DIR/backend/"* "$INSTALL_DIR/"

# Deploy production config (preserve existing if present)
if [ -f "$INSTALL_DIR/appsettings.Production.json" ]; then
    warn "Existing appsettings.Production.json found â€” preserving (backup at .bak)"
    cp "$INSTALL_DIR/appsettings.Production.json" "$INSTALL_DIR/appsettings.Production.json.bak"
else
    cp "$SCRIPT_DIR/config/appsettings.Production.json" "$INSTALL_DIR/"
    ok "Production config deployed"
fi

# Set permissions
chmod +x "$INSTALL_DIR/APTM.Gate.Api" 2>/dev/null || true
chown -R root:root "$INSTALL_DIR"

ok "Backend deployed to $INSTALL_DIR"

# â”€â”€ Step 5: Interactive configuration â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
echo ""
info "[5/8] Configuration..."

CONFIG_FILE="$INSTALL_DIR/appsettings.Production.json"

echo ""
echo -e "  Current settings (from appsettings.Production.json):"
echo -e "  ${CYAN}Gate DeviceCode${NC}: $(grep -o '"DeviceCode"[^,]*' "$CONFIG_FILE" | head -1 | cut -d'"' -f4)"
echo -e "  ${CYAN}Accepted Token${NC} : $(grep -o '"AcceptedToken"[^,]*' "$CONFIG_FILE" | head -1 | cut -d'"' -f4)"
echo -e "  ${CYAN}Reader Host${NC}    : $(grep -o '"Host"[^,]*' "$CONFIG_FILE" | head -1 | cut -d'"' -f4)"
echo -e "  ${CYAN}Reader Port${NC}    : $(grep -o '"Port"[^,]*' "$CONFIG_FILE" | head -1 | grep -o '[0-9]*')"
echo ""

read -p "  Edit configuration now? [y/N]: " EDIT_CONFIG
if [[ "$EDIT_CONFIG" =~ ^[Yy]$ ]]; then
    read -p "  Gate DeviceCode [$GateDeviceCode]: " NEW_CODE
    if [ -n "$NEW_CODE" ]; then
        sed -i "s/\"DeviceCode\": \"[^\"]*\"/\"DeviceCode\": \"$NEW_CODE\"/" "$CONFIG_FILE"
    fi

    read -p "  Accepted Token [$AcceptedToken]: " NEW_TOKEN
    if [ -n "$NEW_TOKEN" ]; then
        sed -i "s/\"AcceptedToken\": \"[^\"]*\"/\"AcceptedToken\": \"$NEW_TOKEN\"/" "$CONFIG_FILE"
    fi

    read -p "  UHF Reader IP [192.168.0.250]: " NEW_READER
    if [ -n "$NEW_READER" ]; then
        sed -i "s/\"Host\": \"[^\"]*\"/\"Host\": \"$NEW_READER\"/" "$CONFIG_FILE"
    fi

    ok "Configuration updated"
fi

# â”€â”€ Step 6: Create systemd service â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
echo ""
info "[6/8] Creating systemd service..."

cat > "/etc/systemd/system/$SERVICE_NAME.service" << EOF
[Unit]
Description=APTM Gate Service
After=network.target postgresql.service
Wants=postgresql.service

[Service]
Type=notify
WorkingDirectory=$INSTALL_DIR
ExecStart=$INSTALL_DIR/APTM.Gate.Api
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_CONTENTROOT=$INSTALL_DIR
Restart=always
RestartSec=10
KillSignal=SIGINT
SyslogIdentifier=$SERVICE_NAME
User=root
TimeoutStopSec=30

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "$SERVICE_NAME"
ok "Systemd service created and enabled"

# â”€â”€ Step 7: Firewall â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
echo ""
info "[7/8] Configuring firewall..."

if command -v ufw &>/dev/null; then
    ufw allow "$KESTREL_PORT/tcp" comment "APTM Gate API" 2>/dev/null || true
    ok "UFW rule added for port $KESTREL_PORT"
else
    warn "UFW not found â€” ensure port $KESTREL_PORT is accessible"
fi

# â”€â”€ Step 8: Start service and verify â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
echo ""
info "[8/8] Starting service..."

systemctl start "$SERVICE_NAME"

# Wait for health check
MAX_WAIT=30
for i in $(seq 1 $MAX_WAIT); do
    if curl -sf "http://localhost:$KESTREL_PORT/gate/health" > /dev/null 2>&1; then
        ok "Service is healthy!"
        break
    fi
    if [ $i -eq $MAX_WAIT ]; then
        warn "Service not responding after ${MAX_WAIT}s. Check logs: journalctl -u $SERVICE_NAME -f"
    fi
    sleep 1
done

# Get health status
HEALTH=$(curl -sf "http://localhost:$KESTREL_PORT/gate/health" 2>/dev/null || echo '{"status":"unknown"}')

echo ""
echo -e "${GREEN}=======================================${NC}"
echo -e "${GREEN}  APTM Gate Service â€” Installed!${NC}"
echo -e "${GREEN}=======================================${NC}"
echo ""
echo -e "  Service    : $SERVICE_NAME"
echo -e "  Status     : $(echo "$HEALTH" | grep -o '"status":"[^"]*"' | cut -d'"' -f4)"
echo -e "  API        : http://localhost:$KESTREL_PORT"
echo -e "  Swagger    : http://localhost:$KESTREL_PORT/swagger"
echo -e "  Health     : http://localhost:$KESTREL_PORT/gate/health"
echo -e "  Display    : http://localhost:$KESTREL_PORT/start-display.html"
echo -e "  Install Dir: $INSTALL_DIR"
echo -e "  Database   : $DB_NAME (PostgreSQL)"
echo -e "  Logs       : journalctl -u $SERVICE_NAME -f"
echo ""
echo -e "  ${YELLOW}Commands:${NC}"
echo -e "    sudo systemctl status $SERVICE_NAME"
echo -e "    sudo systemctl restart $SERVICE_NAME"
echo -e "    sudo journalctl -u $SERVICE_NAME -f"
echo -e "    nano $INSTALL_DIR/appsettings.Production.json"
echo ""
