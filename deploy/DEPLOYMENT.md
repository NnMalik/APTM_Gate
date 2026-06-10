# APTM Gate Service — Deployment Guide

## Overview

The APTM Gate Service runs on Ubuntu NUC devices at each physical gate (Start, Checkpoint, Finish). Each NUC connects to a UHF RFID reader via TCP, processes tag reads, and serves a real-time display.

## Architecture

Each gate (Start, Finish, and 4 Checkpoints) is a **router + NUC + UHF reader** on one
LAN. A dedicated **router** is the Wi-Fi access point; the **NUC and the reader are both
wired to it with static IPs**. The Field tablet joins the router's Wi-Fi and reaches the
NUC at its static IP. The NUC is **not** an access point.

```
        ┌─────────────── router LAN (e.g. 192.168.1.0/24) ───────────────┐
        │                                                                │
 [UHF Reader .250] ──┐                                          [Field Tablet]
                     ├── [Router / Wi-Fi AP .1] ── Wi-Fi ───────────┘
 [NUC: Gate Svc .11]─┘          │
        │                  (gateway/DNS)
        v
 [PostgreSQL 16 @ localhost]
```

Gates may share a router (e.g. Start + Finish at the main venue) with **distinct static
IPs**. The same SSID/password/static-IP are entered once into the Field app per gate.

## Prerequisites

| Component | Version | Notes |
|-----------|---------|-------|
| Ubuntu | 22.04 or 24.04 LTS | x64 |
| .NET | ASP.NET Core 10 Runtime | Framework-dependent by default |
| PostgreSQL | 16+ | Local instance |
| UHF Reader | Series V2.20 | TCP, wired to the router with a static IP (default 192.168.0.250:27011) |
| Router | Any Wi-Fi router | Acts as the AP; NUC + reader wired to it; assigns/reserves static IPs |

## Build Installer (Developer Machine — Windows)

```powershell
cd deploy

# Default build (framework-dependent, uses default settings)
.\build-installer.ps1

# Custom build with specific gate settings
.\build-installer.ps1 `
    -GateDeviceCode "gate-start-01" `
    -AcceptedToken "Tab-01" `
    -ReaderHost "192.168.0.250" `
    -ReaderPort 27011 `
    -DefaultPower 20 `
    -DbPassword "MySecurePass123"

# Self-contained build (includes .NET runtime — larger, no runtime install needed)
.\build-installer.ps1 -SelfContained

# Build without ZIP
.\build-installer.ps1 -SkipZip
```

### Output Structure

```
APTM-Gate-Installer/
├── setup.sh              # Main installer (run on NUC)
├── README.txt            # Quick reference
├── backend/              # Published .NET binaries
│   ├── APTM.Gate.Api     # Main executable
│   ├── appsettings.json  # Base config
│   └── ...
├── config/
│   └── appsettings.Production.json  # Production config (editable)
└── prerequisites/        # Offline install packages
    └── PLACE_FILES_HERE.txt
```

## Deploy to NUC (USB)

### Step 1: Prepare USB Drive

Copy `APTM-Gate-Installer/` folder (or extract `APTM-Gate-Installer.zip`) to USB drive.

For fully offline deployment, also stage these:
- `prerequisites/aspnetcore-runtime-10.0.x-linux-x64.tar.gz` — from [Microsoft Downloads](https://dotnet.microsoft.com/download/dotnet/10.0)
- `prerequisites/postgresql/*.deb` — the **complete** PostgreSQL 16 dependency set (see below)

#### Offline PostgreSQL 16 packages

Docker is **not** required. Easiest options first:

- **Got internet at the gate?** Skip offline entirely — give the NUC internet for ~2 min
  during `setup.sh` and it installs PostgreSQL online automatically.
- **Air-gapped?** Harvest from **one identical NUC** (same Ubuntu image), no Docker:
  ```bash
  sudo apt-get clean && sudo apt-get update
  sudo apt-get install --download-only postgresql postgresql-contrib
  sudo cp /var/cache/apt/archives/*.deb <usb>/APTM-Gate-Installer/prerequisites/postgresql/
  ```
  The cached `.deb`s are exactly what a stock NUC is missing; copy to the other gates.
- **Building on a mismatched machine?** Use the Docker one-liner in
  `prerequisites/postgresql/README.txt` (a clean container guarantees a complete set).

Note: plain `apt-get download postgresql` is **not** enough — it omits dependencies.
`setup.sh` installs every `.deb` in `prerequisites/postgresql/` together (`dpkg -i *.deb`),
so PostgreSQL comes up with no network. (Ubuntu 22.04 needs the PGDG repo for PG16 — see
the README; 24.04 has it built in.)

### Step 2: Run Installer on NUC

```bash
# Mount USB and navigate
cd /media/<username>/<usb>/APTM-Gate-Installer

# Run installer
sudo bash setup.sh
```

The installer will:
1. Install .NET 10 runtime (offline tarball or online)
2. Install PostgreSQL 16 (offline .deb or online)
3. Create database `aptm_gate` with user `aptm`
4. Deploy binaries to `/opt/aptm-gate/`
5. **Prompt for this gate's identity + settings** — role (Start/Checkpoint/Finish),
   checkpoint sequence, gate name, device code, accepted token, reader IP/port — and
   write `appsettings.Production.json` (incl. the `Gate:Identity` pre-flash block)
6. **Configure the static IP on the router LAN** — detected wired interface, NUC static
   IP, gateway, prefix, DNS (via NetworkManager on desktop NUCs, netplan otherwise). Any
   legacy NUC-as-AP config (`hostapd`/`dnsmasq`) is removed.
7. Create and enable systemd service `aptm-gate` (waits for `network-online.target`)
8. Configure the kiosk display automatically for Start/Finish; headless for Checkpoint
9. Open firewall port 5000, start service, and verify health

On first boot the service migrates the DB and **pre-flash-seeds the role** from
`Gate:Identity` — so a fresh NUC comes up already provisioned, no Field-app step needed.

### Step 3: Verify

```bash
# Check health (locally on the NUC)
curl http://localhost:5000/gate/health

# Confirm the seeded identity (role/name/sequence)
curl http://localhost:5000/gate/identity   # or check from the Field tablet

# Check logs
journalctl -u aptm-gate -f

# From the Field tablet (joined to the router Wi-Fi):
# http://<nuc-static-ip>:5000/gate/health
```

## Configuration

Edit `/opt/aptm-gate/appsettings.Production.json`:

```json
{
  "ConnectionStrings": {
    "GateDb": "Host=localhost;Port=5432;Database=aptm_gate;Username=aptm;Password=..."
  },
  "Reader": {
    "Host": "192.168.0.250",     // UHF reader IP
    "Port": 27011,                // UHF reader TCP port
    "ReconnectDelayMs": 5000,     // Retry delay on disconnect
    "DefaultPower": 20,           // RF power (0-30 dBm)
    "EpcFilterBits": 0            // Hardware EPC filter (0 = disabled)
  },
  "Gate": {
    "DeviceCode": "uhf-reader-01",  // This gate's identity (must match APTM Main)
    "AcceptedToken": "Tab-01",      // Token from Field tablet (device code)
    "Identity": {                   // Pre-flash provisioning (seeded on first boot only)
      "Role": "Checkpoint",         // "Start" | "Checkpoint" | "Finish"
      "CheckpointSequence": 1,      // number for Checkpoint, null otherwise
      "Name": "River Bend"          // operator-facing gate name (optional)
    }
  },
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://0.0.0.0:5000" }
    }
  }
}
```

After editing, restart the service:
```bash
sudo systemctl restart aptm-gate
```

> **Note on `Gate:Identity`:** it only seeds the role when the gate has **no** identity yet
> (fresh DB). To change a role on an already-provisioned NUC, re-run `setup.sh` (it offers to
> reset the stored identity) or use the Field app's provisioning screen.

### Static IP

The installer writes the static IP for you. To change it later:
- **Desktop NUC (NetworkManager):** `nmcli con mod aptm-gate-lan ipv4.addresses <ip>/<prefix> ipv4.gateway <gw>` then `nmcli con up aptm-gate-lan`
- **Headless NUC (netplan):** edit `/etc/netplan/99-aptm-gate.yaml`, then `sudo netplan apply`

## Service Management

```bash
# Status
sudo systemctl status aptm-gate

# Start / Stop / Restart
sudo systemctl start aptm-gate
sudo systemctl stop aptm-gate
sudo systemctl restart aptm-gate

# Logs (live)
journalctl -u aptm-gate -f

# Logs (last 100 lines)
journalctl -u aptm-gate -n 100

# Disable auto-start
sudo systemctl disable aptm-gate
```

## Upgrading

1. Build new installer: `.\build-installer.ps1`
2. Copy to USB, run `sudo bash setup.sh` on NUC
3. The script preserves existing `appsettings.Production.json` (backs up to `.bak`)
4. Service restarts automatically with new binaries

## Endpoints

| Endpoint | Auth | Description |
|----------|------|-------------|
| GET /gate/health | No | Health check |
| GET /gate/display-data | No | Display state (JSON) |
| GET /gate/display-stream | No | SSE real-time stream |
| GET /start-display.html | No | Start/Checkpoint display |
| GET /finish-display.html | No | Finish display |
| POST /gate/config | Yes | Push config package |
| PUT /gate/status | Yes | Start/stop gate (active/idle) |
| GET /gate/status | Yes | Gate status |
| GET /gate/diagnostics | Yes | Reader/buffer/DB diagnostics |
| GET /gate/events | Yes | Processed events |
| GET /swagger | No | API documentation |

Auth = `X-Device-Token` header matching `Gate.AcceptedToken`.

## Troubleshooting

| Issue | Solution |
|-------|----------|
| Service won't start | Check logs: `journalctl -u aptm-gate -f` |
| DB connection failed | Verify PostgreSQL running: `systemctl status postgresql` |
| Reader not connecting | Reader must be wired to the router with its static IP reachable from the NUC; verify `ping <reader-ip>` and Reader.Host/Port |
| Field tablet can't reach NUC | Tablet must be on the router Wi-Fi; ping the NUC static IP; confirm `nmcli con show aptm-gate-lan` / `netplan get` shows the static IP |
| Wrong role after install | Pre-flash only seeds an empty identity; re-run `setup.sh` and accept the identity reset, or re-provision via the Field app |
| 401 on API calls | Token mismatch — check `Gate.AcceptedToken` vs Field app device code |
| Tags not processing | Gate must be activated: `PUT /gate/status` with `{"status":"active"}` |
| Display shows "Reconnecting" | Restart service to reinit SSE/PostgreSQL LISTEN |
| Tags UNRESOLVED | Tag EPCs don't match config — check tag_assignments table |

## Multi-Gate Deployment (6 gates: 1 Start + 1 Finish + 4 Checkpoints)

Run `setup.sh` on each NUC and answer the prompts. Each NUC needs:
- Unique `Gate.Identity.Role` (+ `CheckpointSequence` 1–4 for checkpoints) and `Name`
- Unique `Gate.DeviceCode` (e.g. `gate-start-01`, `gate-finish-01`, `cp-01`…`cp-04`)
- A **distinct static IP** on its router's subnet (e.g. `.11` Start, `.12` Finish, `.21`–`.24` CPs)
- Same `Gate.AcceptedToken` (shared across gates for the same Field tablet)
- Its own local PostgreSQL instance
- Correct `Reader.Host` for its UHF reader (wired to the same router)

Record each gate's **SSID / password / static IP** — enter them once into the Field app's
gate cards (Manage Gates) so the operator can one-tap connect to each gate. Gates sharing a
router use the same SSID/password with different static IPs.

### Suggested per-gate values

| Gate | Role | Seq | Static IP | DeviceCode |
|------|------|-----|-----------|------------|
| Start | Start | – | 192.168.1.11 | gate-start-01 |
| Finish | Finish | – | 192.168.1.12 | gate-finish-01 |
| Checkpoint 1 | Checkpoint | 1 | 192.168.1.21 | cp-01 |
| Checkpoint 2 | Checkpoint | 2 | 192.168.1.22 | cp-02 |
| Checkpoint 3 | Checkpoint | 3 | 192.168.1.23 | cp-03 |
| Checkpoint 4 | Checkpoint | 4 | 192.168.1.24 | cp-04 |
