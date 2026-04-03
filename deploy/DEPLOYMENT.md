# APTM Gate Service — Deployment Guide

## Overview

The APTM Gate Service runs on Ubuntu NUC devices at each physical gate (Start, Checkpoint, Finish). Each NUC connects to a UHF RFID reader via TCP, processes tag reads, and serves a real-time display.

## Architecture

```
[UHF Reader] --TCP--> [NUC: APTM Gate Service] --Wi-Fi--> [Field Tablet]
                              |                              [Display Browser]
                              v
                       [PostgreSQL 16]
```

## Prerequisites

| Component | Version | Notes |
|-----------|---------|-------|
| Ubuntu | 22.04 or 24.04 LTS | x64 |
| .NET | ASP.NET Core 10 Runtime | Framework-dependent by default |
| PostgreSQL | 16+ | Local instance |
| UHF Reader | Series V2.20 | TCP connection (default 192.168.0.250:27011) |

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

For fully offline deployment, also copy to `prerequisites/`:
- `aspnetcore-runtime-10.0.x-linux-x64.tar.gz` from [Microsoft Downloads](https://dotnet.microsoft.com/download/dotnet/10.0)
- PostgreSQL .deb packages (optional)

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
5. Deploy production config (preserves existing if upgrading)
6. Prompt for configuration edits (DeviceCode, Reader IP, etc.)
7. Create and enable systemd service `aptm-gate`
8. Open firewall port 5000
9. Start service and verify health

### Step 3: Verify

```bash
# Check health
curl http://localhost:5000/gate/health

# Check logs
journalctl -u aptm-gate -f

# Open display in browser
# http://<nuc-ip>:5000/start-display.html
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
    "AcceptedToken": "Tab-01"       // Token from Field tablet (device code)
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
| Reader not connecting | Check Reader.Host/Port in config, verify network to reader |
| 401 on API calls | Token mismatch — check `Gate.AcceptedToken` vs Field app device code |
| Tags not processing | Gate must be activated: `PUT /gate/status` with `{"status":"active"}` |
| Display shows "Reconnecting" | Restart service to reinit SSE/PostgreSQL LISTEN |
| Tags UNRESOLVED | Tag EPCs don't match config — check tag_assignments table |

## Multi-Gate Deployment

For multiple gates at a site, each NUC needs:
- Unique `Gate.DeviceCode` (e.g., `gate-start-01`, `gate-finish-01`)
- Same `Gate.AcceptedToken` (shared across all gates for the same Field tablet)
- Its own PostgreSQL instance (local to each NUC)
- Correct `Reader.Host` for its specific UHF reader
