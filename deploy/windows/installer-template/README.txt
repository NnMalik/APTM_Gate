============================================================================
  APTM Gate Service — Windows Deployment Guide
============================================================================

PREREQUISITES
─────────────
1. Windows 10/11 (64-bit)
2. .NET 10 ASP.NET Core Runtime (Hosting Bundle recommended)
   Download: https://dotnet.microsoft.com/en-us/download/dotnet/10.0
3. PostgreSQL 16 for Windows
   Download: https://www.postgresql.org/download/windows/
   (Use the interactive installer from EDB. Note your superuser password.)

QUICK INSTALL
─────────────
1. Open PowerShell as Administrator
2. Navigate to this folder:
     cd path\to\APTM-Gate-Windows-Installer
3. Run the setup script:
     powershell -ExecutionPolicy Bypass -File setup.ps1
4. Follow the prompts (PostgreSQL password, display kiosk choice)
5. Verify:
     curl http://localhost:5000/gate/health

UPGRADE
───────
To upgrade an existing installation, run setup with -UpgradeOnly:
     powershell -ExecutionPolicy Bypass -File setup.ps1 -UpgradeOnly

This preserves your existing appsettings.Production.json (backs up to .bak).

SETUP PARAMETERS
────────────────
  -InstallDir   Installation directory (default: C:\aptm-gate)
  -ServiceName  Windows Service name (default: aptm-gate)
  -KestrelPort  API listen port (default: 5000)
  -DbPassword   Database password (default: AptmGate@2024)
  -DbUser       Database user (default: aptm)
  -DbName       Database name (default: aptm_gate)
  -SkipDbSetup  Skip database creation (if DB already exists)
  -SkipFirewall Skip firewall rule creation
  -UpgradeOnly  Preserve existing production config

SERVICE MANAGEMENT
──────────────────
  Start   : sc.exe start aptm-gate
  Stop    : sc.exe stop  aptm-gate
  Status  : sc.exe query aptm-gate
  Remove  : sc.exe delete aptm-gate  (stop first!)
  Logs    : Get-EventLog -LogName Application -Source 'aptm-gate' -Newest 50

HEALTH CHECK
────────────
Schedule healthcheck.ps1 via Task Scheduler for automatic recovery:
  1. Open Task Scheduler
  2. Create Basic Task: "APTM Gate Health Check"
  3. Trigger: Every 5 minutes
  4. Action: Start a program
     Program: powershell
     Arguments: -ExecutionPolicy Bypass -File C:\aptm-gate\healthcheck.ps1
  5. Run whether user is logged on or not, with highest privileges

URLS
────
  API     : http://localhost:5000
  Swagger : http://localhost:5000/swagger
  Health  : http://localhost:5000/gate/health
  Start   : http://localhost:5000/start-display.html
  Finish  : http://localhost:5000/finish-display.html

CONFIG
──────
  Main config: C:\aptm-gate\appsettings.Production.json
  Edit this file to change:
    - Reader IP/port (Reader.Host, Reader.Port)
    - Gate device code (Gate.DeviceCode)
    - Auth token (Gate.AcceptedToken)
    - Database connection (ConnectionStrings.GateDb)
    - Kestrel port (Kestrel.Endpoints.Http.Url)

  After editing, restart the service:
    sc.exe stop aptm-gate && sc.exe start aptm-gate

DIRECTORY STRUCTURE
───────────────────
  APTM-Gate-Windows-Installer\
  ├── backend\              Published application binaries
  ├── config\               appsettings.Production.json
  ├── setup.ps1             Installation script (run as Admin)
  ├── healthcheck.ps1       Health check script (for Task Scheduler)
  └── README.txt            This file
