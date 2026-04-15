# ============================================================================
#  APTM Gate Service — Build Windows Installer Package
#  Builds a portable installer for Windows deployment.
#  Run from the deploy/windows/ directory on the developer's Windows machine.
# ============================================================================

param(
    [string]$GateDeviceCode = "gate-01",
    [string]$AcceptedToken  = "Tab-01",
    [string]$ReaderHost     = "192.168.0.250",
    [int]$ReaderPort        = 27011,
    [int]$DefaultPower      = 20,
    [int]$KestrelPort       = 5000,
    [string]$DbPassword     = "AptmGate@2024",
    [switch]$SelfContained,
    [switch]$SkipZip
)

$ErrorActionPreference = "Stop"
$scriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot   = Split-Path -Parent (Split-Path -Parent $scriptDir)   # deploy/windows -> deploy -> repo
$srcDir     = Join-Path $repoRoot "src"
$apiProject = Join-Path $srcDir "APTM.Gate.Api"
$outDir     = Join-Path $scriptDir "APTM-Gate-Windows-Installer"

Write-Host "`n=== APTM Gate Windows Installer Builder ===" -ForegroundColor Cyan
Write-Host "Output: $outDir"

# ── Step 1: Clean output ─────────────────────────────────────────────────
Write-Host "`n[1/5] Cleaning output directory..." -ForegroundColor Yellow
if (Test-Path (Join-Path $outDir "backend")) {
    Remove-Item (Join-Path $outDir "backend") -Recurse -Force
}
New-Item -ItemType Directory -Path (Join-Path $outDir "backend") -Force | Out-Null

# ── Step 2: Publish backend ──────────────────────────────────────────────
Write-Host "[2/5] Publishing APTM.Gate.Api for Windows (win-x64)..." -ForegroundColor Yellow

$publishArgs = @(
    "publish", $apiProject,
    "-c", "Release",
    "-o", (Join-Path $outDir "backend"),
    "-r", "win-x64"
)

if ($SelfContained) {
    $publishArgs += "--self-contained", "true"
    Write-Host "       Mode: Self-contained (includes .NET runtime)" -ForegroundColor Gray
} else {
    $publishArgs += "--self-contained", "false"
    Write-Host "       Mode: Framework-dependent (requires .NET 10 on target)" -ForegroundColor Gray
}

dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# ── Step 3: Generate production config ───────────────────────────────────
Write-Host "[3/5] Generating production config..." -ForegroundColor Yellow

$configDir = Join-Path $outDir "config"
if (!(Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir -Force | Out-Null }

$prodConfig = @{
    ConnectionStrings = @{
        GateDb = "Host=localhost;Port=5432;Database=aptm_gate;Username=aptm;Password=$DbPassword"
    }
    Reader = @{
        Host           = $ReaderHost
        Port           = $ReaderPort
        ReconnectDelayMs = 5000
        DefaultPower   = $DefaultPower
        EpcFilterBits  = 0
    }
    Gate = @{
        DeviceCode    = $GateDeviceCode
        AcceptedToken = $AcceptedToken
    }
    Kestrel = @{
        Endpoints = @{
            Http = @{
                Url = "http://0.0.0.0:$KestrelPort"
            }
        }
    }
    Logging = @{
        LogLevel = @{
            Default                 = "Information"
            "Microsoft.AspNetCore"  = "Warning"
            Npgsql                  = "Warning"
        }
    }
} | ConvertTo-Json -Depth 5

$prodConfig | Out-File -FilePath (Join-Path $configDir "appsettings.Production.json") -Encoding utf8
Write-Host "       Gate DeviceCode : $GateDeviceCode" -ForegroundColor Gray
Write-Host "       Accepted Token  : $AcceptedToken" -ForegroundColor Gray
Write-Host "       Reader          : $($ReaderHost):$ReaderPort" -ForegroundColor Gray
Write-Host "       Kestrel Port    : $KestrelPort" -ForegroundColor Gray

# ── Step 4: Copy setup scripts ───────────────────────────────────────────
Write-Host "[4/5] Copying installer scripts..." -ForegroundColor Yellow
$templateDir = Join-Path $scriptDir "installer-template"
Copy-Item (Join-Path $templateDir "setup.ps1")       (Join-Path $outDir "setup.ps1")       -Force
Copy-Item (Join-Path $templateDir "healthcheck.ps1")  (Join-Path $outDir "healthcheck.ps1")  -Force
Copy-Item (Join-Path $templateDir "README.txt")       (Join-Path $outDir "README.txt")       -Force -ErrorAction SilentlyContinue

# ── Step 5: Create ZIP ───────────────────────────────────────────────────
if (-not $SkipZip) {
    Write-Host "[5/5] Creating ZIP archive..." -ForegroundColor Yellow
    $zipPath = Join-Path $scriptDir "APTM-Gate-Windows-Installer.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path "$outDir\*" -DestinationPath $zipPath -CompressionLevel Optimal
    $zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Host "       Archive: $zipPath ($zipSize MB)" -ForegroundColor Gray
} else {
    Write-Host "[5/5] Skipping ZIP (--SkipZip)" -ForegroundColor Gray
}

Write-Host "`n=== Build Complete ===" -ForegroundColor Green
Write-Host @"

Next steps:
  1. Copy APTM-Gate-Windows-Installer/ (or .zip) to the Windows gate machine
  2. Ensure .NET 10 ASP.NET Core Runtime is installed (dotnet.microsoft.com/download)
  3. Ensure PostgreSQL 16 is installed (postgresql.org/download/windows/)
  4. Run setup.ps1 as Administrator:  powershell -ExecutionPolicy Bypass -File setup.ps1
  5. Verify: curl http://localhost:$KestrelPort/gate/health

"@
