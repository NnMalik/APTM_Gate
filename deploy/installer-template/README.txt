APTM Gate Service — NUC Installer
===================================

Quick Start:
  1. Copy this folder to the NUC (USB drive)
  2. Open terminal on NUC
  3. cd /media/<usb>/APTM-Gate-Installer
  4. sudo bash setup.sh
  5. Follow the prompts

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
  - Gate.DeviceCode    : This gate's identity (must match APTM Main config)
  - Gate.AcceptedToken : Token accepted from Field tablet (device code)
  - Reader.Host        : UHF reader IP address
  - Reader.Port        : UHF reader TCP port
  - Reader.DefaultPower: RF power level (0-30 dBm)
