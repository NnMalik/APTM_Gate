using APTM.Gate.Api.Services;
using APTM.Gate.Core.Interfaces;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace APTM.Gate.Api.Endpoints;

/// <summary>
/// Field-app-driven lifecycle control. Only power-off is exposed: the reader must stay
/// active for the whole race, so there is no "park" (which would disconnect the reader)
/// or "shutdown". At end-of-race the operator powers the NUC off.
/// </summary>
public static class LifecycleEndpoints
{
    public static void MapLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/gate")
            .RequireAuthorization()
            .RequireProvisioned()
            .WithTags("Lifecycle");

        group.MapPost("/power-off", async (
            bool? force,
            IGateStatusProvider statusProvider,
            IReaderStatusProvider readerStatus,
            ISystemControlService systemControl,
            IConfiguration configuration,
            GateDbContext db,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            // Per-gate kill switch. Missing or unparseable key = allowed (default on).
            var allowed = !bool.TryParse(configuration["Gate:AllowRemotePowerOff"], out var enabled)
                          || enabled;
            if (!allowed)
            {
                return Results.Json(new
                {
                    status = "power_off_disabled",
                    message = "Remote power-off is disabled on this gate (Gate:AllowRemotePowerOff = false)."
                }, statusCode: 403);
            }

            // Stranded-data guard (same philosophy as checkpoint/clear and race-data/clear):
            // refuse to power off while reads exist that no device has pulled. The data
            // would survive in Postgres, but a powered-off NUC may not be booted again
            // before redeployment — pull first, or pass ?force=true.
            if (force != true)
            {
                var maxEventId = await db.ProcessedEvents.MaxAsync(pe => (long?)pe.Id, ct) ?? 0L;
                var maxPulledEventId = await db.SyncLogs
                    .Where(s => !s.PullerDeviceCode.StartsWith("WIPE:") && !s.PullerDeviceCode.StartsWith("ERASE:"))
                    .Select(s => (long?)s.LastProcessedEventId)
                    .MaxAsync(ct) ?? 0L;
                var unpulledEvents = Math.Max(0, maxEventId - maxPulledEventId);
                var pendingRaw = await db.RawTagBuffers.CountAsync(r => r.Status == "PENDING", ct);
                var ingestQueueDepth = readerStatus.IngestQueueDepth;

                if (unpulledEvents > 0 || pendingRaw > 0 || ingestQueueDepth > 0)
                {
                    return Results.Conflict(new
                    {
                        error = "Gate still holds reads no device has pulled. Pull first, or pass ?force=true to power off anyway.",
                        unpulledEvents,
                        pendingRaw,
                        ingestQueueDepth
                    });
                }
            }

            // Stop claiming new buffer batches, then power off AFTER the response is
            // flushed: the gate's own Wi-Fi AP dies with the NUC, so the caller must
            // receive the 200 before the machine goes down.
            statusProvider.SetActive(false);

            logger.LogWarning(
                "Power-off requested via /gate/power-off — the NUC will shut down. " +
                "Physical access is required to power it back on.");

            _ = Task.Run(async () =>
            {
                // Best-effort reader disconnect, a short grace period for the HTTP 200
                // to flush, then the OS power-off.
                try { await readerStatus.DisconnectReaderAsync(CancellationToken.None); }
                catch { /* best-effort */ }

                await Task.Delay(1000);
                await systemControl.PowerOffAsync(CancellationToken.None);
            }, CancellationToken.None);

            return Results.Ok(new
            {
                status = "powering_off",
                message = "The NUC is powering off. It will not come back automatically — " +
                          "someone must physically press the power button."
            });
        })
        .WithName("PowerOffGate")
        .WithSummary("Power off the NUC (full OS shutdown)")
        .WithDescription(
            "Drains workers, then powers the NUC off at the OS level via the configured " +
            "command (Gate:PowerOffCommand, default 'systemctl poweroff'). Returns 409 if " +
            "unpulled reads remain (processed, pending raw, or in-memory) unless ?force=true. " +
            "Irreversible remotely — the machine must be physically powered back on. Can be " +
            "disabled per gate via Gate:AllowRemotePowerOff.");
    }
}
