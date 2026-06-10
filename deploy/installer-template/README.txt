APTM Gate Service — NUC Installer
===================================

Topology:
  A router is the Wi-Fi access point. This NUC and its UHF reader are BOTH wired
  to the router with static IPs. The Field tablet joins the router Wi-Fi and reaches
  the NUC at its static IP. The NUC is NOT an access point.

Quick Start:
  1. Wire the NUC and the UHF reader to the gate's router (Ethernet).
  2. Copy this folder to the NUC (USB drive)
  3. Open terminal on NUC
  4. cd /media/<usb>/APTM-Gate-Installer
  5. sudo bash setup.sh
  6. Follow the prompts. You will be asked for:
       - Role: Start / Checkpoint / Finish  (+ sequence 1-4 for checkpoints)
       - Gate name, device code, accepted token, reader IP/port
       - NUC static IP, router/gateway IP, subnet prefix, DNS
  7. Note the SSID / password / static IP — enter them once in the Field app's
     gate cards so the operator can one-tap connect.

Prerequisites (for offline install):
  Place these files in the prerequisites/ folder:
  - aspnetcore-runtime-10.0.x-linux-x64.tar.gz
    (from https://dotnet.microsoft.com/download/dotnet/10.0)
  - postgresql-16 .deb packages (optional, script can install online)

After Install:
  - API:     http://localhost:5000/gate/health
  - Display: http://localhost:5000/start-display.html
  - Swagger: http://localhost:5000/swagger
  - Config:  /opt/aptm-gate/appsettings.Production.json
  - Logs:    journalctl -u aptm-gate -f
  - Restart: sudo systemctl restart aptm-gate

Configuration:
  Edit /opt/aptm-gate/appsettings.Production.json to change:
  - Gate.DeviceCode       : This gate's identity (must match APTM Main config)
  - Gate.AcceptedToken    : Token accepted from Field tablet (device code)
  - Gate.Identity.Role    : Start | Checkpoint | Finish (seeded on first boot only)
  - Gate.Identity.CheckpointSequence : 1-4 for checkpoints, null otherwise
  - Gate.Identity.Name    : Operator-facing gate name
  - Reader.Host / Port    : UHF reader IP / TCP port (on the router LAN)
  - Reader.DefaultPower   : RF power level (0-30 dBm)
  (Restart the service after editing: sudo systemctl restart aptm-gate)

Static IP:
  - Desktop NUC (NetworkManager): connection name "aptm-gate-lan"
      nmcli con mod aptm-gate-lan ipv4.addresses <ip>/<prefix> ipv4.gateway <gw>
      nmcli con up aptm-gate-lan
  - Headless NUC (netplan): edit /etc/netplan/99-aptm-gate.yaml; sudo netplan apply
