# ============================================================================
#  APTM Gate Service — Windows Health Check
#  Pings the health endpoint and restarts the service on failure.
#
#  Schedule via Task Scheduler:
#    Action  : powershell -ExecutionPolicy Bypass -File C:\aptm-gate\healthcheck.ps1
#    Trigger : Every 5 minutes
#    Run as  : SYSTEM (or Administrator account)
# ============================================================================

param(
    [string]$ServiceName = "aptm-gate",
    [int]$Port           = 5000,
    [int]$TimeoutSec     = 10
)

$healthUrl = "http://localhost:$Port/gate/health"
$logPrefix = "APTM-HealthCheck"

try {
    $response = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec $TimeoutSec -ErrorAction Stop
    if ($response.StatusCode -eq 200) {
        # Healthy — nothing to do
        exit 0
    }
} catch {
    # Health check failed — attempt restart
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $errorMsg = $_.Exception.Message

    Write-EventLog -LogName Application -Source "Application" -EventId 9001 -EntryType Warning `
        -Message "$logPrefix [$timestamp] Health check failed: $errorMsg. Restarting service '$ServiceName'." `
        -ErrorAction SilentlyContinue

    try {
        Restart-Service -Name $ServiceName -Force -ErrorAction Stop
        Start-Sleep -Seconds 5

        $svc = Get-Service -Name $ServiceName
        if ($svc.Status -eq 'Running') {
            Write-EventLog -LogName Application -Source "Application" -EventId 9002 -EntryType Information `
                -Message "$logPrefix [$timestamp] Service '$ServiceName' restarted successfully." `
                -ErrorAction SilentlyContinue
        } else {
            Write-EventLog -LogName Application -Source "Application" -EventId 9003 -EntryType Error `
                -Message "$logPrefix [$timestamp] Service '$ServiceName' restart attempted but status is: $($svc.Status)" `
                -ErrorAction SilentlyContinue
        }
    } catch {
        Write-EventLog -LogName Application -Source "Application" -EventId 9004 -EntryType Error `
            -Message "$logPrefix [$timestamp] Failed to restart service: $($_.Exception.Message)" `
            -ErrorAction SilentlyContinue
    }

    exit 1
}
