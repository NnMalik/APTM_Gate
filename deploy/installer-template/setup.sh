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

    # Offline packages: prefer a dedicated prerequisites/postgresql/ folder holding the
    # COMPLETE dependency set (postgresql-16 + postgresql-common + libpq5 + ssl-cert + ...).
    # See prerequisites/postgresql/README.txt for how to gather it (Docker one-liner).
    PG_DIR=""
    if ls "$SCRIPT_DIR/prerequisites/postgresql"/*.deb >/dev/null 2>&1; then
        PG_DIR="$SCRIPT_DIR/prerequisites/postgresql"
    elif ls "$SCRIPT_DIR/prerequisites"/postgresql*.deb >/dev/null 2>&1; then
        # Legacy/flat layout: deps must also be present alongside as *.deb.
        PG_DIR="$SCRIPT_DIR/prerequisites"
    fi

    if [ -n "$PG_DIR" ]; then
        info "Installing PostgreSQL from offline packages in $PG_DIR ..."
        # Install the whole set at once so inter-package dependencies resolve locally
        # (dpkg orders them itself). No network needed if the set is complete.
        dpkg -i "$PG_DIR"/*.deb 2>/dev/null || true
        # -f fixes any leftover ordering; reaches the network only if one is available
        # and the offline set was incomplete.
        apt-get -f install -y -qq 2>/dev/null || true

        if ! command -v psql &>/dev/null; then
            fail "PostgreSQL install incomplete. The offline set in $PG_DIR is missing dependencies. \
It must include ALL .debs apt would pull (libpq5, ssl-cert, postgresql-common, \
postgresql-client-common, postgresql-client-16, postgresql-16). See \
prerequisites/postgresql/README.txt."
        fi
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

# ── Step 5: Per-NUC configuration (identity + reader + connection) ────────
echo ""
info "[5/11] Configuration..."

CONFIG_FILE="$INSTALL_DIR/appsettings.Production.json"

# Best-effort readers for prefilling defaults from any existing config.
cfg_get()     { grep -o "\"$1\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" "$CONFIG_FILE" 2>/dev/null | head -1 | sed -E 's/.*:[[:space:]]*"([^"]*)".*/\1/' || true; }
cfg_get_num() { grep -o "\"$1\"[[:space:]]*:[[:space:]]*[0-9]\+" "$CONFIG_FILE" 2>/dev/null | head -1 | grep -o '[0-9]\+$' || true; }

GATE_DEVICE_CODE="$(cfg_get DeviceCode)"; GATE_DEVICE_CODE="${GATE_DEVICE_CODE:-gate-01}"
ACCEPTED_TOKEN="$(cfg_get AcceptedToken)"; ACCEPTED_TOKEN="${ACCEPTED_TOKEN:-Tab-01}"
READER_HOST="$(cfg_get Host)"; READER_HOST="${READER_HOST:-192.168.0.250}"
READER_PORT="$(cfg_get_num Port)"; READER_PORT="${READER_PORT:-27011}"
DEFAULT_POWER="$(cfg_get_num DefaultPower)"; DEFAULT_POWER="${DEFAULT_POWER:-20}"
ROLE="$(cfg_get Role)"
GATE_NAME="$(cfg_get Name)"
CHECKPOINT_SEQUENCE="$(cfg_get_num CheckpointSequence)"

echo ""
echo -e "  Enter this gate's settings (press Enter to keep the shown default)."
echo ""

# Role drives pre-flash identity, the display served, and the processing pipeline.
while :; do
    read -p "  Gate role --(s)tart, (c)heckpoint, (f)inish [${ROLE:-c}]: " R
    R="${R:-${ROLE:-c}}"
    case "$R" in
        s|S|start|Start)            ROLE="Start"; break ;;
        c|C|checkpoint|Checkpoint)  ROLE="Checkpoint"; break ;;
        f|F|finish|Finish)          ROLE="Finish"; break ;;
        *) warn "  Enter s, c, or f." ;;
    esac
done

if [ "$ROLE" = "Checkpoint" ]; then
    read -p "  Checkpoint sequence (1,2,3...) [${CHECKPOINT_SEQUENCE:-1}]: " S
    CHECKPOINT_SEQUENCE="${S:-${CHECKPOINT_SEQUENCE:-1}}"
else
    CHECKPOINT_SEQUENCE=""
fi

read -p "  Gate name (e.g. River Bend) [${GATE_NAME}]: " N; [ -n "$N" ] && GATE_NAME="$N"
read -p "  Gate DeviceCode [$GATE_DEVICE_CODE]: " V; [ -n "$V" ] && GATE_DEVICE_CODE="$V"
read -p "  Accepted device token [$ACCEPTED_TOKEN]: " V; [ -n "$V" ] && ACCEPTED_TOKEN="$V"
read -p "  UHF Reader IP [$READER_HOST]: " V; [ -n "$V" ] && READER_HOST="$V"
read -p "  UHF Reader port [$READER_PORT]: " V; [ -n "$V" ] && READER_PORT="$V"

# CheckpointSequence is a JSON number for checkpoints, null otherwise.
if [ "$ROLE" = "Checkpoint" ] && [ -n "$CHECKPOINT_SEQUENCE" ]; then
    SEQ_JSON="$CHECKPOINT_SEQUENCE"
else
    SEQ_JSON="null"
fi

# Regenerate the production config from the collected values so the Gate:Identity
# block is always present — PostgresInitService pre-flash-seeds the role on first
# boot (no field provisioning needed for a fresh NUC).
[ -f "$CONFIG_FILE" ] && cp "$CONFIG_FILE" "$CONFIG_FILE.bak"
cat > "$CONFIG_FILE" << EOF
{
  "ConnectionStrings": {
    "GateDb": "Host=localhost;Port=5432;Database=$DB_NAME;Username=$DB_USER;Password=$DB_PASS"
  },
  "Reader": {
    "Host": "$READER_HOST",
    "Port": $READER_PORT,
    "ReconnectDelayMs": 5000,
    "DefaultPower": $DEFAULT_POWER,
    "EpcFilterBits": 0
  },
  "Gate": {
    "DeviceCode": "$GATE_DEVICE_CODE",
    "AcceptedToken": "$ACCEPTED_TOKEN",
    "AllowRemotePowerOff": true,
    "PowerOffCommand": "systemctl poweroff",
    "Identity": {
      "Role": "$ROLE",
      "CheckpointSequence": $SEQ_JSON,
      "Name": "$GATE_NAME"
    }
  },
  "Kestrel": {
    "Endpoints": { "Http": { "Url": "http://0.0.0.0:$KESTREL_PORT" } }
  },
  "Logging": {
    "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning", "Npgsql": "Warning" }
  }
}
EOF
chown root:root "$CONFIG_FILE"
ok "Config written: role=$ROLE${CHECKPOINT_SEQUENCE:+ seq=$CHECKPOINT_SEQUENCE} name='${GATE_NAME}' device=$GATE_DEVICE_CODE"

# Pre-flash only seeds an EMPTY identity. If this NUC was already provisioned with a
# different role (re-running setup on a live gate), offer to clear it so the new role
# takes effect. On a fresh NUC the table doesn't exist yet and this is a no-op.
OLD_ROLE=$(sudo -u postgres psql -tAqc "SELECT role FROM gate_identity LIMIT 1;" "$DB_NAME" 2>/dev/null | tr -d '[:space:]')
if [ -n "$OLD_ROLE" ] && [ "$OLD_ROLE" != "$ROLE" ]; then
    warn "Gate is already provisioned as '$OLD_ROLE' in the database; pre-flash will NOT override it."
    read -p "  Reset stored identity to '$ROLE'? This clears race data. [y/N]: " RESET
    if [[ "$RESET" =~ ^[Yy]$ ]]; then
        sudo -u postgres psql -d "$DB_NAME" -c "TRUNCATE TABLE gate_identity, raw_tag_buffer, processed_events RESTART IDENTITY CASCADE;" 2>/dev/null || true
        ok "Stored identity cleared; role=$ROLE will seed on next service start."
    fi
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

# ── Step 7: Static IP on the router LAN ──────────────────────────────────
# Topology: a dedicated router is the access point. This NUC and its UHF reader
# are both wired to that router on one subnet, each with a static IP. The Field
# tablet joins the router's Wi-Fi and reaches this NUC at $STATIC_IP. The NUC is
# NOT an access point (any legacy hostapd/dnsmasq AP config is removed below).
echo ""
info "[7/11] Configuring static IP on the router LAN..."

# Remove legacy AP config from older installs (NUC is no longer an access point).
rm -f /etc/network/interfaces.d/aptm-wifi /etc/dnsmasq.d/aptm-gate.conf /etc/hostapd/hostapd.conf 2>/dev/null || true
systemctl disable --now hostapd dnsmasq 2>/dev/null || true

# Setting the static IP is OPTIONAL here. If the NUC has a screen, the easy path is to
# set it on the desktop: Settings > Network > Wired (gear) > IPv4 > Manual (persists, and
# you can visually pick the right NIC on dual-LAN boxes). Use this script only if you'd
# rather not touch the GUI (e.g. a headless gate over SSH).
echo "  A static IP lets the tablet reach this NUC reliably."
echo "  Easiest (NUC with a screen): set it later via Settings > Network > Wired > IPv4 > Manual."
read -p "  Configure the static IP now via this script instead? [y/N]: " DO_STATIC

if [[ "$DO_STATIC" =~ ^[Yy]$ ]]; then
    # Detect the wired interface (prefer en*/eth*; fall back to the default-route dev).
    ETH_IFACE=$(ip -o link show 2>/dev/null | awk -F': ' '{print $2}' | grep -E '^(en|eth)' | head -1)
    [ -z "$ETH_IFACE" ] && ETH_IFACE=$(ip route show default 2>/dev/null | awk '/default/ {print $5; exit}')

    echo "  Detected wired interface: ${ETH_IFACE:-none}"
    read -p "  Wired interface to use [$ETH_IFACE]: " V; [ -n "$V" ] && ETH_IFACE="$V"

    read -p "  NUC static IP [192.168.1.11]: " STATIC_IP;  STATIC_IP="${STATIC_IP:-192.168.1.11}"
    read -p "  Router / gateway IP [192.168.1.1]: " GATEWAY_IP; GATEWAY_IP="${GATEWAY_IP:-192.168.1.1}"
    read -p "  Subnet prefix [24]: " PREFIX; PREFIX="${PREFIX:-24}"
    read -p "  DNS server [$GATEWAY_IP]: " DNS_IP; DNS_IP="${DNS_IP:-$GATEWAY_IP}"

    if [ -z "$ETH_IFACE" ]; then
        warn "No wired interface detected --set the static IP manually after install."
    elif command -v nmcli &>/dev/null && systemctl is-active --quiet NetworkManager; then
        # Desktop NUCs (Start/Finish with a screen) are managed by NetworkManager.
        CON_NAME="aptm-gate-lan"
        nmcli con delete "$CON_NAME" 2>/dev/null || true
        if nmcli con add type ethernet con-name "$CON_NAME" ifname "$ETH_IFACE" \
            ipv4.method manual ipv4.addresses "$STATIC_IP/$PREFIX" \
            ipv4.gateway "$GATEWAY_IP" ipv4.dns "$DNS_IP" connection.autoconnect yes 2>/dev/null; then
            nmcli con up "$CON_NAME" 2>/dev/null || true
            ok "Static IP via NetworkManager: $STATIC_IP/$PREFIX (gw $GATEWAY_IP) on $ETH_IFACE"
        else
            warn "nmcli failed --set the static IP manually (nmcli or /etc/netplan)."
        fi
    else
        # Headless/server NUCs use netplan + systemd-networkd.
        NETPLAN_FILE="/etc/netplan/99-aptm-gate.yaml"
        cat > "$NETPLAN_FILE" << EOF
network:
  version: 2
  ethernets:
    $ETH_IFACE:
      dhcp4: false
      addresses: [$STATIC_IP/$PREFIX]
      routes:
        - to: default
          via: $GATEWAY_IP
      nameservers:
        addresses: [$DNS_IP]
EOF
        chmod 600 "$NETPLAN_FILE"
        netplan apply 2>/dev/null || warn "netplan apply failed --check $NETPLAN_FILE"
        ok "Static IP via netplan: $STATIC_IP/$PREFIX (gw $GATEWAY_IP) on $ETH_IFACE"
    fi
else
    ok "Skipping static-IP config. Set it on the NUC: Settings > Network > Wired > IPv4 > Manual"
    info "  (or netplan/nmcli over SSH for a headless gate). The tablet must reach this NUC's IP."
fi

# Best-effort current IP for the summary URLs below (whether or not we set it here).
DISPLAY_IP="${STATIC_IP:-$(hostname -I 2>/dev/null | awk '{print $1}')}"
[ -z "$DISPLAY_IP" ] && DISPLAY_IP="localhost"

info "  Reader expected at $READER_HOST:$READER_PORT on this same LAN."

# ── Step 8: Create systemd service ───────────────────────────────────────
echo ""
info "[8/11] Creating systemd service..."

cat > "/etc/systemd/system/$SERVICE_NAME.service" << EOF
[Unit]
Description=APTM Gate Service
After=network-online.target postgresql.service
Wants=network-online.target postgresql.service

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

# Display is derived from the role chosen in Step 5: Start/Finish get a kiosk,
# Checkpoint is headless.
DISPLAY_ENABLED="n"
DISPLAY_PAGE=""
case "$ROLE" in
    Start)  DISPLAY_PAGE="start-display.html" ;;
    Finish) DISPLAY_PAGE="finish-display.html" ;;
esac

if [ -n "$DISPLAY_PAGE" ]; then
    DISPLAY_ENABLED="y"
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
echo -e "  Role       : $ROLE${CHECKPOINT_SEQUENCE:+ (seq $CHECKPOINT_SEQUENCE)}${GATE_NAME:+ -- $GATE_NAME}"
echo -e "  API        : http://$DISPLAY_IP:$KESTREL_PORT"
echo -e "  Swagger    : http://$DISPLAY_IP:$KESTREL_PORT/swagger"
echo -e "  Health     : http://$DISPLAY_IP:$KESTREL_PORT/gate/health"
if [ -n "$DISPLAY_PAGE" ]; then
echo -e "  Display    : http://$DISPLAY_IP:$KESTREL_PORT/$DISPLAY_PAGE"
fi
echo -e "  Install Dir: $INSTALL_DIR"
echo -e "  Database   : $DB_NAME (PostgreSQL)"
if [ -n "$STATIC_IP" ]; then
echo -e "  Network    : $STATIC_IP/$PREFIX on ${ETH_IFACE:-?} (gw $GATEWAY_IP)"
else
echo -e "  Network    : set on the NUC (Settings > Network > Wired > IPv4); current: $DISPLAY_IP"
fi
echo -e "  Reader     : $READER_HOST:$READER_PORT"
echo -e "  Logs       : journalctl -u $SERVICE_NAME -f"
echo ""
echo -e "  ${YELLOW}System hardening:${NC}"
echo -e "    Sleep/suspend/hibernate: ${GREEN}DISABLED${NC}"
echo -e "    Health check cron      : disabled (enable after verifying)"
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
