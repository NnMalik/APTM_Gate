# ============================================================================
#  APTM Gate Service - Drop & Recreate Database (Windows)
#  Run as Administrator on the target Windows machine.
#
#  Usage:
#    powershell -ExecutionPolicy Bypass -File reset-database.ps1
#    powershell -ExecutionPolicy Bypass -File reset-database.ps1 -Force
#    powershell -ExecutionPolicy Bypass -File reset-database.ps1 -Force -PgSuperPassword "yourPgPass"
#
#  What this does:
#    1. Stops the aptm-gate Windows service (so connections drop).
#    2. Terminates any remaining backend sessions on aptm_gate.
#    3. DROPs the aptm_gate database.
#    4. CREATEs a fresh aptm_gate database owned by 'aptm'.
#    5. Re-grants privileges on the public schema.
#    6. Restarts the aptm-gate service.
#       -> On startup, PostgresInitService auto-applies all EF Core migrations
#          and installs the NOTIFY triggers from init_triggers.sql, so the
#          schema comes back fully.
#    7. Hits /gate/health to confirm the service is up.
#
#  Safe to run on a cold machine: if the service doesn't exist yet or the
#  'aptm' role is missing, the script skips/creates as appropriate.
# ============================================================================

#Requires -RunAsAdministrator

param(
    [string]$ServiceName        = "aptm-gate",
    [string]$DbName             = "aptm_gate",
    [string]$DbUser             = "aptm",
    [string]$DbPassword         = "AptmGate@2024",
    [int]   $KestrelPort        = 5000,
    [string]$PgSuperUser        = "postgres",
    [string]$PgSuperPassword    = "",
    [switch]$Force,
    [switch]$SkipServiceRestart
)

$ErrorActionPreference = "Stop"

function Write-Step($step, $total, $message) {
    Write-Host "`n[$step/$total] $message" -ForegroundColor Yellow
}
function Write-Ok($msg)   { Write-Host "       $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "       $msg" -ForegroundColor DarkYellow }
function Write-Fail($msg) { Write-Host "       $msg" -ForegroundColor Red }
function Write-Info($msg) { Write-Host "       $msg" -ForegroundColor Gray }

$TOTAL_STEPS = 7

Write-Host "`n=====================================================" -ForegroundColor Cyan
Write-Host "  APTM Gate - Drop & Recreate Database (Windows)"     -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "  Service     : $ServiceName"
Write-Host "  Database    : $DbName"
Write-Host "  DB owner    : $DbUser"
Write-Host "  Kestrel port: $KestrelPort"

# ---------------------------------------------------------------------------
#  Confirmation (skipped when -Force)
# ---------------------------------------------------------------------------
if (-not $Force) {
    Write-Host "`n  !!! WARNING !!!" -ForegroundColor Red
    Write-Host "  This will PERMANENTLY DESTROY all data in '$DbName' including:" -ForegroundColor Red
    Write-Host "    - raw_tag_buffer (all captured UHF reads)"                    -ForegroundColor Red
    Write-Host "    - processed_events (all resolved candidate events)"           -ForegroundColor Red
    Write-Host "    - race_start_times, attendance, sync data"                    -ForegroundColor Red
    Write-Host "    - the applied config package"                                 -ForegroundColor Red
    Write-Host "  The service will be stopped, the DB dropped, recreated empty,"  -ForegroundColor Red
    Write-Host "  and the service restarted. Migrations + triggers rebuild auto." -ForegroundColor Red
    $answer = Read-Host "`n  Type 'YES' (exactly) to continue"
    if ($answer -ne "YES") {
        Write-Host "`n  Aborted." -ForegroundColor Yellow
        exit 1
    }
}

# ---------------------------------------------------------------------------
#  Step 1: Locate psql.exe
# ---------------------------------------------------------------------------
Write-Step 1 $TOTAL_STEPS "Locating psql.exe..."

$psqlPath = $null
$inPath = Get-Command psql -ErrorAction SilentlyContinue
if ($inPath) { $psqlPath = $inPath.Source }

if (-not $psqlPath) {
    $candidates = @(
        "C:\Program Files\PostgreSQL\17\bin\psql.exe",
        "C:\Program Files\PostgreSQL\16\bin\psql.exe",
        "C:\Program Files\PostgreSQL\15\bin\psql.exe",
        "C:\Program Files\PostgreSQL\14\bin\psql.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $psqlPath = $c; break }
    }
}

if (-not $psqlPath) {
    Write-Fail "psql.exe not found in PATH or C:\Program Files\PostgreSQL\{14..17}\bin\"
    Write-Fail "Install PostgreSQL or add psql to PATH, then re-run."
    exit 1
}
Write-Ok "Found: $psqlPath"

# ---------------------------------------------------------------------------
#  Step 2: Stop the service so connections drop
# ---------------------------------------------------------------------------
Write-Step 2 $TOTAL_STEPS "Stopping '$ServiceName' service..."

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -eq 'Running') {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        # Wait up to 15s for it to fully stop
        $waited = 0
        while ((Get-Service -Name $ServiceName).Status -ne 'Stopped' -and $waited -lt 15) {
            Start-Sleep -Seconds 1
            $waited++
        }
        Write-Ok "Service stopped"
    } else {
        Write-Ok "Service was not running ($($svc.Status))"
    }
} else {
    Write-Warn "Service '$ServiceName' is not installed - continuing"
}

# Belt-and-braces: kill any lingering API process holding a pooled connection
$procs = Get-Process -Name "APTM.Gate.Api" -ErrorAction SilentlyContinue
if ($procs) {
    Write-Info "Killing $($procs.Count) lingering APTM.Gate.Api process(es)..."
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

# Give Postgres a moment to recognise the dropped connections
Start-Sleep -Seconds 2

# ---------------------------------------------------------------------------
#  Step 3: Get postgres superuser password
# ---------------------------------------------------------------------------
Write-Step 3 $TOTAL_STEPS "Authenticating as PostgreSQL superuser '$PgSuperUser'..."

if ([string]::IsNullOrEmpty($PgSuperPassword)) {
    $securePw = Read-Host "       Enter PostgreSQL '$PgSuperUser' password" -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePw)
    try {
        $PgSuperPassword = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
    } finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

$env:PGPASSWORD = $PgSuperPassword

try {
    # Sanity check: can we actually connect?
    $check = & $psqlPath -U $PgSuperUser -h localhost -d postgres -tAc "SELECT 1;" 2>&1
    if ($LASTEXITCODE -ne 0 -or ($check -notmatch "1")) {
        Write-Fail "Cannot authenticate to PostgreSQL as '$PgSuperUser'."
        Write-Fail "psql output: $check"
        exit 1
    }
    Write-Ok "Superuser auth OK"

    # -----------------------------------------------------------------------
    #  Step 4: Terminate backends on the target DB + drop it
    # -----------------------------------------------------------------------
    Write-Step 4 $TOTAL_STEPS "Dropping database '$DbName'..."

    # IMPORTANT: must connect to a DB other than the one we're dropping.
    # We connect to 'postgres'. First revoke new connections so races can't
    # sneak in between the terminate and the drop, then terminate any existing
    # backends, then DROP.
    $dbExists = & $psqlPath -U $PgSuperUser -h localhost -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='$DbName';" 2>&1
    if ($dbExists -match "1") {
        Write-Info "Revoking new connections to '$DbName'..."
        & $psqlPath -U $PgSuperUser -h localhost -d postgres -c "REVOKE CONNECT ON DATABASE $DbName FROM PUBLIC;" 2>&1 | Out-Null

        Write-Info "Terminating active backends on '$DbName'..."
        $termSql = "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='$DbName' AND pid <> pg_backend_pid();"
        & $psqlPath -U $PgSuperUser -h localhost -d postgres -c $termSql 2>&1 | Out-Null

        # Brief pause - backends sometimes take a beat to actually exit
        Start-Sleep -Seconds 2

        Write-Info "DROP DATABASE $DbName..."
        $dropOut = & $psqlPath -U $PgSuperUser -h localhost -d postgres -c "DROP DATABASE IF EXISTS $DbName;" 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Fail "DROP DATABASE failed:"
            Write-Fail $dropOut
            Write-Fail "Likely another session is still connected. Stop everything using the DB and retry."
            exit 1
        }
        Write-Ok "Database '$DbName' dropped"
    } else {
        Write-Ok "Database '$DbName' did not exist - skipping drop"
    }

    # -----------------------------------------------------------------------
    #  Step 5: Ensure role + create DB + grant privileges
    # -----------------------------------------------------------------------
    Write-Step 5 $TOTAL_STEPS "Recreating database '$DbName' owned by '$DbUser'..."

    # Ensure the 'aptm' role exists (cold-machine safety).
    $roleExists = & $psqlPath -U $PgSuperUser -h localhost -d postgres -tAc "SELECT 1 FROM pg_roles WHERE rolname='$DbUser';" 2>&1
    if ($roleExists -match "1") {
        Write-Ok "Role '$DbUser' already exists"
        # Ensure password matches what's in appsettings.Production.json
        & $psqlPath -U $PgSuperUser -h localhost -d postgres -c "ALTER USER $DbUser WITH PASSWORD '$DbPassword';" 2>&1 | Out-Null
    } else {
        Write-Info "Creating role '$DbUser'..."
        & $psqlPath -U $PgSuperUser -h localhost -d postgres -c "CREATE USER $DbUser WITH PASSWORD '$DbPassword';" 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Fail "CREATE USER failed"
            exit 1
        }
        Write-Ok "Role '$DbUser' created"
    }

    # Create a fresh DB owned by aptm
    $createOut = & $psqlPath -U $PgSuperUser -h localhost -d postgres -c "CREATE DATABASE $DbName OWNER $DbUser;" 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "CREATE DATABASE failed:"
        Write-Fail $createOut
        exit 1
    }
    Write-Ok "Database '$DbName' created (owner: $DbUser)"

    # Grants (belt-and-braces; owner already has these, but PG15+ locks
    # down the public schema so this GRANT is important).
    & $psqlPath -U $PgSuperUser -h localhost -d postgres -c "GRANT ALL PRIVILEGES ON DATABASE $DbName TO $DbUser;" 2>&1 | Out-Null
    & $psqlPath -U $PgSuperUser -h localhost -d $DbName   -c "GRANT ALL ON SCHEMA public TO $DbUser;"               2>&1 | Out-Null
    Write-Ok "Privileges granted"

} finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
#  Step 6: Restart the service (unless -SkipServiceRestart)
# ---------------------------------------------------------------------------
Write-Step 6 $TOTAL_STEPS "Restarting '$ServiceName' service..."

if ($SkipServiceRestart) {
    Write-Warn "Skipping service restart (-SkipServiceRestart)"
    Write-Warn "Start it manually: Start-Service $ServiceName"
} else {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-Warn "Service '$ServiceName' is not installed - skipping start"
        Write-Warn "Run setup.ps1 first to install the service."
    } else {
        Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 3
        $svc = Get-Service -Name $ServiceName
        if ($svc.Status -eq 'Running') {
            Write-Ok "Service is running"
        } else {
            Write-Warn "Service status: $($svc.Status)"
        }
    }
}

# ---------------------------------------------------------------------------
#  Step 7: Health check + wait for migrations to complete
# ---------------------------------------------------------------------------
Write-Step 7 $TOTAL_STEPS "Waiting for PostgresInitService to apply migrations..."

if ($SkipServiceRestart) {
    Write-Warn "Skipped (service not started)"
} else {
    # PostgresInitService retries up to 5 times with 5s delay, so give it
    # plenty of room. In practice a fresh recreate takes ~5-15s.
    $healthy = $false
    $maxWait = 45
    $waited  = 0
    while ($waited -lt $maxWait) {
        try {
            $resp = Invoke-WebRequest -Uri "http://localhost:$KestrelPort/gate/health" `
                                      -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop
            if ($resp.StatusCode -eq 200) {
                $healthy = $true
                break
            }
        } catch {
            # still starting
        }
        Start-Sleep -Seconds 2
        $waited += 2
    }

    if ($healthy) {
        Write-Ok "/gate/health returned 200 OK after ${waited}s"
    } else {
        Write-Warn "/gate/health did not return 200 within ${maxWait}s"
        Write-Warn "Check logs: Get-EventLog -LogName Application -Source '$ServiceName' -Newest 30"
    }

    # Verify the schema actually came back by counting tables in the new DB.
    # This uses the aptm role (not superuser) to also prove the creds work.
    $env:PGPASSWORD = $DbPassword
    try {
        $tableCount = & $psqlPath -U $DbUser -h localhost -d $DbName -tAc `
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='public';" 2>&1
        if ($LASTEXITCODE -eq 0 -and $tableCount -match '^\s*\d+\s*$') {
            $n = [int]$tableCount.Trim()
            if ($n -ge 10) {
                Write-Ok "Schema rebuilt - $n tables in public schema"
            } else {
                Write-Warn "Only $n tables found in public schema - migrations may still be running"
            }
        } else {
            Write-Warn "Could not verify schema rebuild (psql said: $tableCount)"
        }
    } finally {
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------
#  Done
# ---------------------------------------------------------------------------
Write-Host "`n=====================================================" -ForegroundColor Green
Write-Host "  Database reset complete"                               -ForegroundColor Green
Write-Host "=====================================================" -ForegroundColor Green
Write-Host @"

  Database : $DbName (fresh, owner = $DbUser)
  Service  : $ServiceName
  Health   : http://localhost:$KestrelPort/gate/health
  Displays : http://localhost:$KestrelPort/start-display.html
             http://localhost:$KestrelPort/finish-display.html

  Tail service logs:
    Get-EventLog -LogName Application -Source '$ServiceName' -Newest 30

"@ -ForegroundColor Cyan
