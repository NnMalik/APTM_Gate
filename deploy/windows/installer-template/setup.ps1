# ============================================================================
#  APTM Gate Service — Windows Setup Script
#  Run as Administrator on the target Windows machine.
#
#  Usage:  powershell -ExecutionPolicy Bypass -File setup.ps1
#
#  Steps:
#   1. Check .NET 10 ASP.NET Core Runtime
#   2. Check PostgreSQL 16
#   3. Create database and user
#   4. Deploy application binaries
#   5. Copy production configuration
#   6. Register Windows Service
#   7. Configure Windows Firewall
#   8. Disable sleep / hibernate
#   9. Start the service
#  10. (Optional) Create browser kiosk shortcut
# ============================================================================

#Requires -RunAsAdministrator

param(
    [string]$InstallDir   = "C:\aptm-gate",
    [string]$ServiceName  = "aptm-gate",
    [int]$KestrelPort     = 5000,
    [string]$DbPassword   = "AptmGate@2024",
    [string]$DbUser       = "aptm",
    [string]$DbName       = "aptm_gate",
    [switch]$SkipDbSetup,
    [switch]$SkipFirewall,
    [switch]$UpgradeOnly
)

$ErrorActionPreference = "Stop"
$SCRIPT_DIR = Split-Path -Parent $MyInvocation.MyCommand.Path

function Write-Step($step, $total, $message) {
    Write-Host "`n[$step/$total] $message" -ForegroundColor Yellow
}

function Write-Ok($message) {
    Write-Host "       $message" -ForegroundColor Green
}

function Write-Warn($message) {
    Write-Host "       $message" -ForegroundColor DarkYellow
}

function Write-Fail($message) {
    Write-Host "       $message" -ForegroundColor Red
}

$TOTAL_STEPS = 10

Write-Host "`n============================================" -ForegroundColor Cyan
Write-Host "  APTM Gate Service — Windows Installer" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Install Dir  : $InstallDir"
Write-Host "  Service Name : $ServiceName"
Write-Host "  Kestrel Port : $KestrelPort"
Write-Host "  Database     : $DbName"

# ═══════════════════════════════════════════════════════════════════════════
#  Step 1: Check .NET 10 ASP.NET Core Runtime
# ═══════════════════════════════════════════════════════════════════════════
Write-Step 1 $TOTAL_STEPS "Checking .NET ASP.NET Core Runtime..."

$dotnetOk = $false
try {
    $runtimes = & dotnet --list-runtimes 2>&1
    if ($runtimes -match "Microsoft\.AspNetCore\.App 10\.") {
        $matched = ($runtimes | Select-String "Microsoft\.AspNetCore\.App 10\.").Line
        Write-Ok "Found: $matched"
        $dotnetOk = $true
    }
} catch {}

if (-not $dotnetOk) {
    Write-Fail ".NET 10 ASP.NET Core Runtime NOT found."
    Write-Host @"

       Please install it from:
       https://dotnet.microsoft.com/en-us/download/dotnet/10.0

       Download: ASP.NET Core Runtime 10.x (Hosting Bundle recommended for Windows)
       After installing, re-run this script.

"@ -ForegroundColor Red
    exit 1
}

# ═══════════════════════════════════════════════════════════════════════════
#  Step 2: Check PostgreSQL
# ═══════════════════════════════════════════════════════════════════════════
Write-Step 2 $TOTAL_STEPS "Checking PostgreSQL..."

$psqlPath = $null
$pgOk = $false

# Check PATH first
$psqlInPath = Get-Command psql -ErrorAction SilentlyContinue
if ($psqlInPath) {
    $psqlPath = $psqlInPath.Source
}

# Check common installation directories
if (-not $psqlPath) {
    $pgDirs = @(
        "C:\Program Files\PostgreSQL\16\bin\psql.exe",
        "C:\Program Files\PostgreSQL\17\bin\psql.exe",
        "C:\Program Files\PostgreSQL\15\bin\psql.exe"
    )
    foreach ($p in $pgDirs) {
        if (Test-Path $p) { $psqlPath = $p; break }
    }
}

if ($psqlPath) {
    $pgVersion = & $psqlPath --version 2>&1
    Write-Ok "Found: $pgVersion"
    Write-Ok "Path:  $psqlPath"
    $pgOk = $true
} else {
    Write-Fail "PostgreSQL NOT found in PATH or standard locations."
    Write-Host @"

       Please install PostgreSQL 16 from:
       https://www.postgresql.org/download/windows/

       Use the interactive installer from EDB (EnterpriseDB).
       During install, note the superuser password you set.
       After installing, re-run this script.

"@ -ForegroundColor Red
    exit 1
}

# ═══════════════════════════════════════════════════════════════════════════
#  Step 3: Create Database and User
# ═══════════════════════════════════════════════════════════════════════════
Write-Step 3 $TOTAL_STEPS "Setting up database..."

if ($SkipDbSetup) {
    Write-Warn "Skipping database setup (-SkipDbSetup)"
} else {
    # Prompt for postgres superuser password
    Write-Host "       Enter the PostgreSQL superuser (postgres) password:" -ForegroundColor Gray -NoNewline
    $pgSuperPass = Read-Host " "

    $env:PGPASSWORD = $pgSuperPass

    # Check if database exists
    $dbExists = & $psqlPath -U postgres -h localhost -tAc "SELECT 1 FROM pg_database WHERE datname='$DbName'" 2>&1
    if ($dbExists -match "1") {
        Write-Ok "Database '$DbName' already exists"
    } else {
        Write-Host "       Creating database '$DbName'..." -ForegroundColor Gray
        & $psqlPath -U postgres -h localhost -c "CREATE DATABASE $DbName;" 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Fail "Failed to create database. Check your PostgreSQL superuser password."
            Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
            exit 1
        }
        Write-Ok "Database '$DbName' created"
    }

    # Check if user exists
    $userExists = & $psqlPath -U postgres -h localhost -tAc "SELECT 1 FROM pg_roles WHERE rolname='$DbUser'" 2>&1
    if ($userExists -match "1") {
        Write-Ok "User '$DbUser' already exists"
        # Update password in case it changed
        & $psqlPath -U postgres -h localhost -c "ALTER USER $DbUser WITH PASSWORD '$DbPassword';" 2>&1 | Out-Null
    } else {
        Write-Host "       Creating user '$DbUser'..." -ForegroundColor Gray
        & $psqlPath -U postgres -h localhost -c "CREATE USER $DbUser WITH PASSWORD '$DbPassword';" 2>&1 | Out-Null
        Write-Ok "User '$DbUser' created"
    }

    # Grant privileges
    & $psqlPath -U postgres -h localhost -c "GRANT ALL PRIVILEGES ON DATABASE $DbName TO $DbUser;" 2>&1 | Out-Null
    & $psqlPath -U postgres -h localhost -d $DbName -c "GRANT ALL ON SCHEMA public TO $DbUser;" 2>&1 | Out-Null
    Write-Ok "Privileges granted to '$DbUser' on '$DbName'"

    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}

# ═══════════════════════════════════════════════════════════════════════════
#  Step 4: Deploy Application Binaries
# ═══════════════════════════════════════════════════════════════════════════
Write-Step 4 $TOTAL_STEPS "Deploying application to $InstallDir..."

$backendSrc = Join-Path $SCRIPT_DIR "backend"
if (-not (Test-Path $backendSrc)) {
    Write-Fail "backend/ directory not found. Run build-windows-installer.ps1 first."
    exit 1
}

# Stop service if upgrading
$svcExists = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svcExists) {
    Write-Host "       Stopping existing service..." -ForegroundColor Gray
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

# Create install directory
if (!(Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Write-Ok "Created $InstallDir"
}

# Copy binaries
Write-Host "       Copying application files..." -ForegroundColor Gray
Copy-Item -Path "$backendSrc\*" -Destination $InstallDir -Recurse -Force
Write-Ok "Application deployed to $InstallDir"

# ═══════════════════════════════════════════════════════════════════════════
#  Step 5: Copy Production Configuration
# ═══════════════════════════════════════════════════════════════════════════
Write-Step 5 $TOTAL_STEPS "Configuring production settings..."

$configSrc  = Join-Path $SCRIPT_DIR "config\appsettings.Production.json"
$configDest = Join-Path $InstallDir "appsettings.Production.json"

if (Test-Path $configDest) {
    # Backup existing config on upgrade
    $bakFile = "$configDest.bak"
    Copy-Item $configDest $bakFile -Force
    Write-Warn "Existing config backed up to appsettings.Production.json.bak"

    if ($UpgradeOnly) {
        Write-Ok "Keeping existing production config (-UpgradeOnly)"
    } else {
        Copy-Item $configSrc $configDest -Force
        Write-Ok "Production config updated (previous version backed up)"
    }
} else {
    Copy-Item $configSrc $configDest -Force
    Write-Ok "Production config installed"
}

# ═══════════════════════════════════════════════════════════════════════════
#  Step 6: Register Windows Service
# ═══════════════════════════════════════════════════════════════════════════
Write-Step 6 $TOTAL_STEPS "Registering Windows Service..."

$exePath = Join-Path $InstallDir "APTM.Gate.Api.exe"
if (-not (Test-Path $exePath)) {
    Write-Fail "APTM.Gate.Api.exe not found in $InstallDir"
    exit 1
}

$svcExists = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svcExists) {
    Write-Warn "Service '$ServiceName' already registered — updating..."
    # Update the binary path in case install dir changed
    sc.exe config $ServiceName binPath= "`"$exePath`"" start= auto | Out-Null
    # Set environment variable for Production
    $regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    $currentEnv = (Get-ItemProperty -Path $regPath -Name Environment -ErrorAction SilentlyContinue).Environment
    $envVars = @("ASPNETCORE_ENVIRONMENT=Production", "DOTNET_CONTENTROOT=$InstallDir")
    Set-ItemProperty -Path $regPath -Name Environment -Value $envVars -Type MultiString
    Write-Ok "Service configuration updated"
} else {
    # Create the service
    sc.exe create $ServiceName binPath= "`"$exePath`"" start= auto DisplayName= "APTM Gate Service" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "Failed to create Windows Service"
        exit 1
    }
    sc.exe description $ServiceName "APTM Gate Service - UHF tag capture, processing, display, and sync hub" | Out-Null

    # Set environment variables via registry
    $regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    $envVars = @("ASPNETCORE_ENVIRONMENT=Production", "DOTNET_CONTENTROOT=$InstallDir")
    Set-ItemProperty -Path $regPath -Name Environment -Value $envVars -Type MultiString

    # Configure recovery: restart on failure
    sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null

    Write-Ok "Service '$ServiceName' created with auto-start and failure recovery"
}

# ═══════════════════════════════════════════════════════════════════════════
#  Step 7: Configure Windows Firewall
# ═══════════════════════════════════════════════════════════════════════════
Write-Step 7 $TOTAL_STEPS "Configuring Windows Firewall..."

if ($SkipFirewall) {
    Write-Warn "Skipping firewall configuration (-SkipFirewall)"
} else {
    $ruleName = "APTM Gate Service (Port $KestrelPort)"

    # Remove existing rule if present
    netsh advfirewall firewall delete rule name="$ruleName" 2>&1 | Out-Null

    # Add inbound rule
    netsh advfirewall firewall add rule `
        name="$ruleName" `
        dir=in `
        action=allow `
        protocol=tcp `
        localport=$KestrelPort `
        profile=any | Out-Null

    if ($LASTEXITCODE -eq 0) {
        Write-Ok "Firewall rule added: allow TCP port $KestrelPort inbound"
    } else {
        Write-Warn "Could not add firewall rule. You may need to manually allow port $KestrelPort."
    }
}

# ═══════════════════════════════════════════════════════════════════════════
#  Step 8: Disable Sleep / Hibernate
# ═══════════════════════════════════════════════════════════════════════════
Write-Step 8 $TOTAL_STEPS "Disabling sleep and hibernate..."

# Disable sleep on AC power
powercfg /change standby-timeout-ac 0
powercfg /change hibernate-timeout-ac 0
powercfg /change monitor-timeout-ac 0

# Disable hibernate entirely
powercfg /hibernate off 2>&1 | Out-Null

Write-Ok "Sleep, hibernate, and monitor timeout disabled (AC power)"

# ═══════════════════════════════════════════════════════════════════════════
#  Step 9: Start Service
# ═══════════════════════════════════════════════════════════════════════════
Write-Step 9 $TOTAL_STEPS "Starting APTM Gate Service..."

Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

$svc = Get-Service -Name $ServiceName
if ($svc.Status -eq 'Running') {
    Write-Ok "Service is running"

    # Quick health check
    Start-Sleep -Seconds 2
    try {
        $health = Invoke-WebRequest -Uri "http://localhost:$KestrelPort/gate/health" -UseBasicParsing -TimeoutSec 10
        if ($health.StatusCode -eq 200) {
            Write-Ok "Health check passed (HTTP 200)"
        } else {
            Write-Warn "Health check returned HTTP $($health.StatusCode)"
        }
    } catch {
        Write-Warn "Health check not responding yet — service may still be initializing"
    }
} else {
    Write-Warn "Service status: $($svc.Status)"
    Write-Host "       Check logs: Get-EventLog -LogName Application -Source '$ServiceName' -Newest 20" -ForegroundColor Gray
}

# ═══════════════════════════════════════════════════════════════════════════
#  Step 10: Optional — Browser Kiosk Shortcut
# ═══════════════════════════════════════════════════════════════════════════
Write-Step 10 $TOTAL_STEPS "Browser kiosk setup (optional)..."

$displayChoice = Read-Host "       Create display kiosk shortcut on Desktop? (start/finish/skip) [skip]"
if ($displayChoice -and $displayChoice -ne "skip") {
    $displayPage = switch ($displayChoice.ToLower()) {
        "start"  { "start-display.html" }
        "finish" { "finish-display.html" }
        default  { $null }
    }

    if ($displayPage) {
        $displayUrl = "http://localhost:$KestrelPort/$displayPage"
        $desktopPath = [Environment]::GetFolderPath("Desktop")
        $shortcutPath = Join-Path $desktopPath "APTM Gate Display.lnk"

        # Find Chrome or Edge
        $browserExe = $null
        $browserPaths = @(
            "${env:ProgramFiles}\Google\Chrome\Application\chrome.exe",
            "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
            "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
            "${env:ProgramFiles}\Microsoft\Edge\Application\msedge.exe"
        )
        foreach ($bp in $browserPaths) {
            if (Test-Path $bp) { $browserExe = $bp; break }
        }

        if ($browserExe) {
            $WshShell = New-Object -ComObject WScript.Shell
            $shortcut = $WshShell.CreateShortcut($shortcutPath)
            $shortcut.TargetPath = $browserExe
            $shortcut.Arguments = "--kiosk `"$displayUrl`" --no-first-run --disable-translate"
            $shortcut.Description = "APTM Gate $displayChoice Display (Kiosk Mode)"
            $shortcut.Save()
            Write-Ok "Desktop shortcut created: $shortcutPath"
            Write-Ok "Browser: $browserExe"
            Write-Ok "URL: $displayUrl"
        } else {
            Write-Warn "Chrome/Edge not found. Manually open: $displayUrl"
        }
    }
} else {
    Write-Ok "Skipped — open displays manually at http://localhost:$KestrelPort/"
}

# ═══════════════════════════════════════════════════════════════════════════
#  Done
# ═══════════════════════════════════════════════════════════════════════════
Write-Host "`n============================================" -ForegroundColor Green
Write-Host "  Installation Complete!" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host @"

  Service    : $ServiceName (Windows Service)
  Install Dir: $InstallDir
  API URL    : http://localhost:$KestrelPort
  Swagger    : http://localhost:$KestrelPort/swagger
  Health     : http://localhost:$KestrelPort/gate/health
  Start      : http://localhost:$KestrelPort/start-display.html
  Finish     : http://localhost:$KestrelPort/finish-display.html

  Manage service:
    sc.exe query $ServiceName
    sc.exe stop  $ServiceName
    sc.exe start $ServiceName
    Get-Service  $ServiceName

  View logs:
    Get-EventLog -LogName Application -Source '$ServiceName' -Newest 50

  Config file:
    $InstallDir\appsettings.Production.json

"@
