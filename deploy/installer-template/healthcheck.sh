#!/usr/bin/env bash
# APTM Gate health check -- called by cron every 2 minutes.
# Restarts the service if the health endpoint is unreachable.

SERVICE="aptm-gate"
HEALTH_URL="http://localhost:5000/gate/health"

# Use curl or wget (whichever is available)
if command -v curl &>/dev/null; then
    RESULT=$(curl -sf --max-time 5 "$HEALTH_URL" 2>/dev/null)
elif command -v wget &>/dev/null; then
    RESULT=$(wget -qO- --timeout=5 "$HEALTH_URL" 2>/dev/null)
else
    exit 0  # No HTTP client available, skip check
fi

if [ -z "$RESULT" ]; then
    logger -t "$SERVICE" "Health check FAILED -- restarting service"
    systemctl restart "$SERVICE"
fi
