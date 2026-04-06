#!/usr/bin/env bash
# ============================================================================
#  APTM Gate Service --Ubuntu NUC Installer
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
WIFI_PASS="AptmGate@2024"
WIFI_CHANNEL=6
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Colors
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'

info()  { echo -e "${CYAN}[INFO]${NC}  $1"; }
ok()    { echo -e "${GREEN}[OK]${NC}    $1"; }
warn()  { echo -e "${YELLOW}[WARN]${NC}  $1"; }
fail()  { echo -e "${RED}[FAIL]${NC}  $1"; exit 1; }

echo ""
echo -e "${CYAN}=======================================${NC}"
echo -e "${CYAN}  APTM Gate Service --NUC Installer${NC}"
echo -e "${CYAN}=======================================${NC}"
echo ""

# ── Pre-flight checks ────────────────────────────────────────────────────
if [ "$EUID" -ne 0 ]; then
    fail "Please run as root: sudo bash setup.sh"
fi

if [ ! -d "$SCRIPT_DIR/backend" ]; then
    fail "backend/ directory not found. Run build-installer.ps1 first."
fi

# ── Step 1: Install .NET 10 Runtime ──────────────────────────────────────
echo ""
info "[1/11] Checking .NET runtime..."

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

# ── Step 2: Install PostgreSQL 16 ────────────────────────────────────────
echo ""
info "[2/11] Checking PostgreSQL..."

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

# ── Step 3: Create database and user ─────────────────────────────────────
echo ""
info "[3/11] Setting up database..."

# Run postgres commands from a directory the postgres user can access
pushd /tmp > /dev/null

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

popd > /dev/null

# ── Step 4: Deploy backend ───────────────────────────────────────────────
echo ""
info "[4/11] Deploying backend to $INSTALL_DIR..."

# Disable health check cron to prevent auto-restart during upgrade
rm -f /etc/cron.d/aptm-gate-health 2>/dev/null || true

# Stop existing service and kill any lingering processes
if systemctl is-active --quiet "$SERVICE_NAME" 2>/dev/null; then
    info "Stopping existing $SERVICE_NAME service..."
    systemctl stop "$SERVICE_NAME" 2>/dev/null || true
fi
# Kill any remaining processes regardless of service state
pkill -9 -f "APTM.Gate.Api" 2>/dev/null || true
sleep 2

# Remove old binaries BEFORE copying new ones.
# On Linux, 'cp' over a running executable fails with "Text file busy"
# because the kernel locks the inode. 'rm' removes the dir entry but
# lets the kernel keep the old inode until fully released --so the new
# copy gets a fresh inode and succeeds.
if [ -f "$INSTALL_DIR/APTM.Gate.Api" ]; then
    info "Removing old binaries..."
    find "$INSTALL_DIR" -maxdepth 1 -type f \( -name "*.dll" -o -name "*.so" -o -name "*.json" -o -name "APTM.Gate.Api" -o -name "*.deps.json" -o -name "*.runtimeconfig.json" \) \
        ! -name "appsettings.Production.json" \
        -delete 2>/dev/null || true
    ok "Old binaries removed"
fi

mkdir -p "$INSTALL_DIR"

# Copy new backend binaries (fresh inodes, no "Text file busy")
cp -r "$SCRIPT_DIR/backend/"* "$INSTALL_DIR/"

# Deploy production config (preserve existing if present)
if [ -f "$INSTALL_DIR/appsettings.Production.json" ]; then
    warn "Existing appsettings.Production.json found --preserving (backup at .bak)"
    cp "$INSTALL_DIR/appsettings.Production.json" "$INSTALL_DIR/appsettings.Production.json.bak"
else
    cp "$SCRIPT_DIR/config/appsettings.Production.json" "$INSTALL_DIR/"
    ok "Production config deployed"
fi

# Deploy health check script
cp "$SCRIPT_DIR/healthcheck.sh" "$INSTALL_DIR/healthcheck.sh"
chmod +x "$INSTALL_DIR/healthcheck.sh"

# Set permissions
chmod +x "$INSTALL_DIR/APTM.Gate.Api" 2>/dev/null || true
chown -R root:root "$INSTALL_DIR"

ok "Backend deployed to $INSTALL_DIR"

# ── Step 5: Interactive configuration ────────────────────────────────────
echo ""
info "[5/11] Configuration..."

CONFIG_FILE="$INSTALL_DIR/appsettings.Production.json"

echo ""
echo -e "  Current settings (from appsettings.Production.json):"
echo -e "  ${CYAN}Gate DeviceCode${NC}: $(grep -o '"DeviceCode"[^,]*' "$CONFIG_FILE" | head -1 | cut -d'"' -f4)"
echo -e "  ${CYAN}Accepted Token${NC} : $(grep -o '"AcceptedToken"[^,]*' "$CONFIG_FILE" | head -1 | cut -d'"' -f4)"
echo -e "  ${CYAN}Reader Host${NC}    : $(grep -o '"Host"[^,]*' "$CONFIG_FILE" | head -1 | cut -d'"' -f4)"
echo -e "  ${CYAN}Reader Port${NC}    : $(grep -o '"Port"[^,]*' "$CONFIG_FILE" | head -1 | grep -o '[0-9]*')"
echo ""

GATE_DEVICE_CODE=$(grep -o '"DeviceCode"[^,]*' "$CONFIG_FILE" | head -1 | cut -d'"' -f4)

read -p "  Edit configuration now? [y/N]: " EDIT_CONFIG
if [[ "$EDIT_CONFIG" =~ ^[Yy]$ ]]; then
    read -p "  Gate DeviceCode [$GATE_DEVICE_CODE]: " NEW_CODE
    if [ -n "$NEW_CODE" ]; then
        sed -i "s/\"DeviceCode\": \"[^\"]*\"/\"DeviceCode\": \"$NEW_CODE\"/" "$CONFIG_FILE"
        GATE_DEVICE_CODE="$NEW_CODE"
    fi

    read -p "  Accepted Token: " NEW_TOKEN
    if [ -n "$NEW_TOKEN" ]; then
        sed -i "s/\"AcceptedToken\": \"[^\"]*\"/\"AcceptedToken\": \"$NEW_TOKEN\"/" "$CONFIG_FILE"
    fi

    read -p "  UHF Reader IP [192.168.0.250]: " NEW_READER
    if [ -n "$NEW_READER" ]; then
        sed -i "s/\"Host\": \"[^\"]*\"/\"Host\": \"$NEW_READER\"/" "$CONFIG_FILE"
    fi

    ok "Configuration updated"
fi

# ── Step 6: Disable sleep/suspend/hibernate ──────────────────────────────
echo ""
info "[6/11] Disabling sleep and power management..."

# Mask all sleep targets
systemctl mask sleep.target suspend.target hibernate.target hybrid-sleep.target 2>/dev/null || true

# Configure logind to ignore lid switch and idle
# NOTE: changes take effect on next reboot (we do NOT restart logind
# during setup because that kills the active desktop session)
mkdir -p /etc/systemd/logind.conf.d
cat > /etc/systemd/logind.conf.d/aptm-no-sleep.conf << 'EOF'
[Login]
HandleLidSwitch=ignore
HandleLidSwitchExternalPower=ignore
HandleLidSwitchDocked=ignore
IdleAction=ignore
IdleActionSec=0
EOF

# Disable GNOME screen blanking for the logged-in desktop user
DISPLAY_USER=$(logname 2>/dev/null || echo "")
if [ -z "$DISPLAY_USER" ] || [ "$DISPLAY_USER" = "root" ]; then
    DISPLAY_USER=$(awk -F: '$3 >= 1000 && $3 < 65534 { print $1; exit }' /etc/passwd)
fi

if [ -n "$DISPLAY_USER" ] && command -v gsettings &>/dev/null; then
    # gsettings must run as the desktop user, not root
    sudo -u "$DISPLAY_USER" DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/$(id -u "$DISPLAY_USER")/bus" \
        gsettings set org.gnome.desktop.session idle-delay 0 2>/dev/null || true
    sudo -u "$DISPLAY_USER" DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/$(id -u "$DISPLAY_USER")/bus" \
        gsettings set org.gnome.desktop.screensaver lock-enabled false 2>/dev/null || true
    sudo -u "$DISPLAY_USER" DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/$(id -u "$DISPLAY_USER")/bus" \
        gsettings set org.gnome.settings-daemon.plugins.power sleep-inactive-ac-type 'nothing' 2>/dev/null || true
    ok "GNOME screen blanking disabled for $DISPLAY_USER"
fi

# Disable console blanking at kernel level (takes effect after reboot)
if [ -f /etc/default/grub ]; then
    if ! grep -q "consoleblank=0" /etc/default/grub; then
        sed -i 's/GRUB_CMDLINE_LINUX_DEFAULT="[^"]*"/& consoleblank=0/' /etc/default/grub
        update-grub 2>/dev/null || true
    fi
fi

# Disable blanking for the current session immediately (no reboot needed)
setterm -blank 0 -powerdown 0 2>/dev/null || true
xset s off -dpms 2>/dev/null || true

ok "Sleep, suspend, hibernate disabled (logind changes apply after reboot)"

# ── Step 7: Configure Wi-Fi Access Point ─────────────────────────────────
echo ""
info "[7/11] Configuring Wi-Fi access point..."

# Detect wireless interface
WIFI_IFACE=$(iw dev 2>/dev/null | awk '$1=="Interface"{print $2}' | head -1)

if [ -z "$WIFI_IFACE" ]; then
    warn "No wireless interface detected --skipping Wi-Fi AP setup"
    warn "Connect Field/HHT devices via ethernet or configure Wi-Fi AP manually"
else
    info "Wireless interface: $WIFI_IFACE"

    # SSID = gate device code (so Field app can auto-detect)
    WIFI_SSID="${GATE_DEVICE_CODE:-aptm-gate}"

    read -p "  Wi-Fi SSID [$WIFI_SSID]: " NEW_SSID
    [ -n "$NEW_SSID" ] && WIFI_SSID="$NEW_SSID"

    read -p "  Wi-Fi Password [$WIFI_PASS]: " NEW_WIFI_PASS
    [ -n "$NEW_WIFI_PASS" ] && WIFI_PASS="$NEW_WIFI_PASS"

    # Install hostapd and dnsmasq
    if ! command -v hostapd &>/dev/null || ! command -v dnsmasq &>/dev/null; then
        OFFLINE_HOSTAPD=$(find "$SCRIPT_DIR/prerequisites" -name "hostapd*.deb" 2>/dev/null | head -1)
        if [ -n "$OFFLINE_HOSTAPD" ]; then
            dpkg -i "$SCRIPT_DIR/prerequisites"/hostapd*.deb "$SCRIPT_DIR/prerequisites"/dnsmasq*.deb 2>/dev/null || true
            apt-get -f install -y -qq 2>/dev/null || true
        else
            apt-get update -qq
            apt-get install -y -qq hostapd dnsmasq
        fi
    fi
    ok "hostapd and dnsmasq installed"

    # Stop NetworkManager from managing the Wi-Fi interface
    if command -v nmcli &>/dev/null; then
        nmcli device set "$WIFI_IFACE" managed no 2>/dev/null || true
    fi

    # Assign static IP to wireless interface
    cat > /etc/network/interfaces.d/aptm-wifi << EOF
auto $WIFI_IFACE
iface $WIFI_IFACE inet static
    address 192.168.1.1
    netmask 255.255.255.0
EOF

    # Configure hostapd
    cat > /etc/hostapd/hostapd.conf << EOF
interface=$WIFI_IFACE
driver=nl80211
ssid=$WIFI_SSID
hw_mode=g
channel=$WIFI_CHANNEL
wmm_enabled=0
macaddr_acl=0
auth_algs=1
ignore_broadcast_ssid=0
wpa=2
wpa_passphrase=$WIFI_PASS
wpa_key_mgmt=WPA-PSK
wpa_pairwise=TKIP
rsn_pairwise=CCMP
EOF

    # Tell hostapd to use our config
    if [ -f /etc/default/hostapd ]; then
        sed -i 's|^#*DAEMON_CONF=.*|DAEMON_CONF="/etc/hostapd/hostapd.conf"|' /etc/default/hostapd
    fi

    # Configure dnsmasq for DHCP
    cat > /etc/dnsmasq.d/aptm-gate.conf << EOF
interface=$WIFI_IFACE
dhcp-range=192.168.1.10,192.168.1.50,255.255.255.0,24h
bind-interfaces
server=8.8.8.8
domain-needed
bogus-priv
EOF

    # Bring up the interface
    ip addr flush dev "$WIFI_IFACE" 2>/dev/null || true
    ip addr add 192.168.1.1/24 dev "$WIFI_IFACE" 2>/dev/null || true
    ip link set "$WIFI_IFACE" up 2>/dev/null || true

    # Enable and start services
    systemctl unmask hostapd 2>/dev/null || true
    systemctl enable hostapd dnsmasq 2>/dev/null
    systemctl restart dnsmasq 2>/dev/null || true
    systemctl restart hostapd 2>/dev/null || true

    if systemctl is-active --quiet hostapd; then
        ok "Wi-Fi AP active --SSID: $WIFI_SSID, IP: 192.168.1.1"
    else
        warn "hostapd failed to start. Check: journalctl -u hostapd -e"
        warn "You may need to disable Wi-Fi power management: iwconfig $WIFI_IFACE power off"
    fi
fi

# ── Step 8: Create systemd service ───────────────────────────────────────
echo ""
info "[8/11] Creating systemd service..."

cat > "/etc/systemd/system/$SERVICE_NAME.service" << EOF
[Unit]
Description=APTM Gate Service
After=network.target postgresql.service hostapd.service
Wants=postgresql.service

[Service]
Type=simple
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
ok "Systemd service created and enabled (with watchdog)"

# ── Step 9: Health check cron + firewall ─────────────────────────────────
echo ""
info "[9/11] Setting up health check cron and firewall..."

# Health check cron disabled — was causing restart loops during startup.
# Uncomment after verifying the service starts reliably.
# cat > /etc/cron.d/aptm-gate-health << EOF
# */2 * * * * root $INSTALL_DIR/healthcheck.sh
# EOF
ok "Health check cron: disabled (enable manually after verification)"

# Firewall
if command -v ufw &>/dev/null; then
    ufw allow "$KESTREL_PORT/tcp" comment "APTM Gate API" 2>/dev/null || true
    ok "UFW rule added for port $KESTREL_PORT"
else
    warn "UFW not found --ensure port $KESTREL_PORT is accessible"
fi

# ── Step 10: Browser kiosk for Start/Finish gates ────────────────────────
echo ""
info "[10/11] Display browser setup..."

DISPLAY_ENABLED="n"
read -p "  Is this a Start or Finish gate with a screen? [y/N]: " DISPLAY_ENABLED

if [[ "$DISPLAY_ENABLED" =~ ^[Yy]$ ]]; then
    # Determine display page based on gate type
    DISPLAY_PAGE="start-display.html"
    read -p "  Gate type --(s)tart or (f)inish? [s/f]: " GATE_TYPE_CHOICE
    if [[ "$GATE_TYPE_CHOICE" =~ ^[Ff]$ ]]; then
        DISPLAY_PAGE="finish-display.html"
    fi

    DISPLAY_URL="http://localhost:$KESTREL_PORT/$DISPLAY_PAGE"

    # Install a browser if not present
    # On Ubuntu 22.04+, chromium-browser is a snap transitional package.
    # We try multiple approaches: snap, apt, flatpak, then fall back to firefox.
    CHROMIUM_BIN=""

    # Check if any supported browser is already installed
    for bin in chromium-browser chromium google-chrome-stable firefox; do
        if command -v "$bin" &>/dev/null; then
            CHROMIUM_BIN="$bin"
            break
        fi
    done

    if [ -z "$CHROMIUM_BIN" ]; then
        info "No browser found. Installing..."

        # Try 1: Offline .deb packages from prerequisites/
        OFFLINE_CHROMIUM=$(find "$SCRIPT_DIR/prerequisites" -name "chromium*.deb" 2>/dev/null | head -1)
        if [ -n "$OFFLINE_CHROMIUM" ]; then
            info "Installing from offline .deb packages..."
            dpkg -i "$SCRIPT_DIR/prerequisites"/chromium*.deb 2>/dev/null || true
            apt-get -f install -y -qq 2>/dev/null || true
        fi

        # Try 2: snap (Ubuntu 22.04+ default method for chromium)
        if [ -z "$CHROMIUM_BIN" ] && command -v snap &>/dev/null; then
            info "Trying snap install..."
            snap install chromium 2>/dev/null && CHROMIUM_BIN="chromium"
        fi

        # Try 3: apt-get (works on older Ubuntu or if snap not available)
        if [ -z "$CHROMIUM_BIN" ]; then
            info "Trying apt install..."
            apt-get update -qq 2>/dev/null
            apt-get install -y -qq chromium-browser 2>/dev/null || \
            apt-get install -y -qq chromium 2>/dev/null || \
            apt-get install -y -qq firefox 2>/dev/null || true
        fi

        # Re-detect after install
        for bin in chromium-browser chromium google-chrome-stable firefox; do
            if command -v "$bin" &>/dev/null; then
                CHROMIUM_BIN="$bin"
                break
            fi
        done
    fi

    if [ -z "$CHROMIUM_BIN" ]; then
        warn "Could not install a browser automatically."
        warn "Install manually: sudo snap install chromium"
        warn "Then re-run setup or open $DISPLAY_URL in a browser."
    else
        ok "Browser found: $CHROMIUM_BIN"

        # Detect the primary login user (non-root user who will see the screen)
        DISPLAY_USER=$(logname 2>/dev/null || echo "")
        if [ -z "$DISPLAY_USER" ] || [ "$DISPLAY_USER" = "root" ]; then
            # Fallback: pick the first non-root user with a home directory
            DISPLAY_USER=$(awk -F: '$3 >= 1000 && $3 < 65534 { print $1; exit }' /etc/passwd)
        fi
        if [ -z "$DISPLAY_USER" ]; then
            DISPLAY_USER="root"
        fi

        DISPLAY_HOME=$(eval echo "~$DISPLAY_USER")
        info "Display user: $DISPLAY_USER (home: $DISPLAY_HOME)"

        # Create a systemd user service for kiosk browser
        # This runs under the graphical session via the user's autostart
        AUTOSTART_DIR="$DISPLAY_HOME/.config/autostart"
        mkdir -p "$AUTOSTART_DIR"

        # Build kiosk launch command based on browser type
        if [[ "$CHROMIUM_BIN" == *"firefox"* ]]; then
            KIOSK_CMD="$CHROMIUM_BIN --kiosk $DISPLAY_URL"
        else
            KIOSK_CMD="$CHROMIUM_BIN --noerrdialogs --disable-infobars --disable-session-crashed-bubble --disable-translate --no-first-run --start-fullscreen --kiosk $DISPLAY_URL"
        fi

        cat > "$AUTOSTART_DIR/aptm-display.desktop" << EOF
[Desktop Entry]
Type=Application
Name=APTM Gate Display
Comment=Opens gate display in kiosk mode
Exec=bash -c 'sleep 15 && $KIOSK_CMD'
X-GNOME-Autostart-enabled=true
Hidden=false
NoDisplay=false
EOF

        chown -R "$DISPLAY_USER:$DISPLAY_USER" "$AUTOSTART_DIR"
        ok "Browser kiosk autostart created for $DISPLAY_USER"
        info "  Display URL: $DISPLAY_URL"
        info "  On next login/reboot, Chromium opens fullscreen automatically"

        # Also create a manual launch script for convenience
        cat > "$INSTALL_DIR/open-display.sh" << EOF
#!/bin/bash
# Manually open the gate display in kiosk mode
export DISPLAY=:0
$KIOSK_CMD &
EOF
        chmod +x "$INSTALL_DIR/open-display.sh"
        ok "Manual launcher: $INSTALL_DIR/open-display.sh"
    fi
else
    ok "No display --headless checkpoint gate (browser not configured)"
fi

# ── Step 11: Start service and verify ────────────────────────────────────
echo ""
info "[11/11] Starting service..."

systemctl start "$SERVICE_NAME"

# Wait for health check (use wget since curl may not be installed)
MAX_WAIT=30
HEALTH_CMD=""
if command -v curl &>/dev/null; then
    HEALTH_CMD="curl -sf"
elif command -v wget &>/dev/null; then
    HEALTH_CMD="wget -qO-"
fi

if [ -n "$HEALTH_CMD" ]; then
    for i in $(seq 1 $MAX_WAIT); do
        if $HEALTH_CMD "http://localhost:$KESTREL_PORT/gate/health" > /dev/null 2>&1; then
            ok "Service is healthy!"
            break
        fi
        if [ $i -eq $MAX_WAIT ]; then
            warn "Service not responding after ${MAX_WAIT}s. Check logs: journalctl -u $SERVICE_NAME -f"
        fi
        sleep 1
    done
    HEALTH=$($HEALTH_CMD "http://localhost:$KESTREL_PORT/gate/health" 2>/dev/null || echo '{"status":"unknown"}')
else
    warn "Neither curl nor wget found -- cannot verify health. Install with: sudo apt install curl"
    sleep 5
    HEALTH='{"status":"unknown"}'
fi

echo ""
echo -e "${GREEN}=======================================${NC}"
echo -e "${GREEN}  APTM Gate Service --Installed!${NC}"
echo -e "${GREEN}=======================================${NC}"
echo ""
echo -e "  Service    : $SERVICE_NAME"
echo -e "  Status     : $(echo "$HEALTH" | grep -o '"status":"[^"]*"' | cut -d'"' -f4)"
echo -e "  API        : http://192.168.1.1:$KESTREL_PORT"
echo -e "  Swagger    : http://192.168.1.1:$KESTREL_PORT/swagger"
echo -e "  Health     : http://192.168.1.1:$KESTREL_PORT/gate/health"
echo -e "  Display    : http://192.168.1.1:$KESTREL_PORT/start-display.html"
echo -e "  Install Dir: $INSTALL_DIR"
echo -e "  Database   : $DB_NAME (PostgreSQL)"
echo -e "  Wi-Fi AP   : ${WIFI_SSID:-N/A} (192.168.1.1)"
echo -e "  Logs       : journalctl -u $SERVICE_NAME -f"
echo ""
echo -e "  ${YELLOW}System hardening:${NC}"
echo -e "    Sleep/suspend/hibernate: ${GREEN}DISABLED${NC}"
echo -e "    Watchdog timeout       : 90s (systemd auto-restart)"
echo -e "    Health check cron      : every 2 min"
if [[ "$DISPLAY_ENABLED" =~ ^[Yy]$ ]]; then
echo -e "    Browser kiosk          : $DISPLAY_PAGE (auto-open on boot)"
else
echo -e "    Browser kiosk          : not configured (headless)"
fi
echo ""
echo -e "  ${YELLOW}Commands:${NC}"
echo -e "    sudo systemctl status $SERVICE_NAME"
echo -e "    sudo systemctl restart $SERVICE_NAME"
echo -e "    sudo journalctl -u $SERVICE_NAME -f"
echo -e "    nano $INSTALL_DIR/appsettings.Production.json"
echo ""
