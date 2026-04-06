# ============================================================================
#  APTM Gate Service — Build Installer Package
#  Builds a portable, offline-capable installer for Ubuntu NUC deployment.
#  Run from the deploy/ directory on the developer's Windows machine.
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
$repoRoot   = Split-Path -Parent $scriptDir
$srcDir     = Join-Path $repoRoot "src"
$apiProject = Join-Path $srcDir "APTM.Gate.Api"
$outDir     = Join-Path $scriptDir "APTM-Gate-Installer"

Write-Host "`n=== APTM Gate Installer Builder ===" -ForegroundColor Cyan
Write-Host "Output: $outDir"

# ── Step 1: Clean output ─────────────────────────────────────────────────
Write-Host "`n[1/5] Cleaning output directory..." -ForegroundColor Yellow
if (Test-Path (Join-Path $outDir "backend")) {
    Remove-Item (Join-Path $outDir "backend") -Recurse -Force
}
New-Item -ItemType Directory -Path (Join-Path $outDir "backend") -Force | Out-Null

# ── Step 2: Publish backend ──────────────────────────────────────────────
Write-Host "[2/5] Publishing APTM.Gate.Api..." -ForegroundColor Yellow

$publishArgs = @(
    "publish", $apiProject,
    "-c", "Release",
    "-o", (Join-Path $outDir "backend"),
    "-r", "linux-x64"
)

if ($SelfContained) {
    $publishArgs += "--self-contained", "true"
    Write-Host "       Mode: Self-contained (includes .NET runtime)" -ForegroundColor Gray
} else {
    $publishArgs += "--self-contained", "false"
    Write-Host "       Mode: Framework-dependent (requires .NET 10 on NUC)" -ForegroundColor Gray
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

# ── Step 4: Copy setup script ────────────────────────────────────────────
Write-Host "[4/5] Copying installer scripts..." -ForegroundColor Yellow
Copy-Item (Join-Path $scriptDir "installer-template\setup.sh") (Join-Path $outDir "setup.sh") -Force
Copy-Item (Join-Path $scriptDir "installer-template\healthcheck.sh") (Join-Path $outDir "healthcheck.sh") -Force
Copy-Item (Join-Path $scriptDir "installer-template\README.txt") (Join-Path $outDir "README.txt") -Force -ErrorAction SilentlyContinue

# Ensure shell scripts have unix line endings (LF, not CRLF)
foreach ($shFile in @("setup.sh", "healthcheck.sh")) {
    $shPath = Join-Path $outDir $shFile
    if (Test-Path $shPath) {
        $content = Get-Content $shPath -Raw
        $content = $content -replace "`r`n", "`n"
        [System.IO.File]::WriteAllText($shPath, $content, [System.Text.UTF8Encoding]::new($false))
    }
}

# ── Step 5: Create ZIP ───────────────────────────────────────────────────
if (-not $SkipZip) {
    Write-Host "[5/5] Creating ZIP archive..." -ForegroundColor Yellow
    $zipPath = Join-Path $scriptDir "APTM-Gate-Installer.zip"
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
  1. Copy APTM-Gate-Installer/ (or .zip) to USB drive
  2. For offline install, also copy to prerequisites/:
     - dotnet-runtime-10.0.*-linux-x64.tar.gz (from https://dotnet.microsoft.com/download)
     - postgresql-16 .deb packages (from apt cache or download)
  3. On the NUC: sudo bash setup.sh
  4. Edit /opt/aptm-gate/appsettings.Production.json if needed (Reader IP, DeviceCode, etc.)
  5. Verify: curl http://localhost:$KestrelPort/gate/health

"@
