using APTM.Gate.Api.Services;
using APTM.Gate.Core.Interfaces;

namespace APTM.Gate.Api.Endpoints;

/// <summary>
/// Field-app-driven lifecycle controls. Used by Checkpoint NUCs (which have no screen)
/// to remotely stop processing or shut the service down at end-of-race; useful for
/// Finish/Start gates too if operators prefer remote control over a physical interface.
/// </summary>
public static class LifecycleEndpoints
{
    public static void MapLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/gate")
            .RequireAuthorization()
            .RequireProvisioned()
            .WithTags("Lifecycle");

        group.MapPost("/park", async (
            IGateStatusProvider statusProvider,
            IReaderStatusProvider readerStatus,
            CancellationToken ct) =>
        {
            // Soft stop: buffer processor goes IDLE on next status check, reader disconnects.
            // Service stays alive — /gate/health, /gate/sync/pull, /gate/identity keep working.
            // Reversible by PUT /gate/status active.
            statusProvider.SetActive(false);

            // Best-effort reader disconnect; ignore failures (Start gates have no real reader to drop).
            try { await readerStatus.DisconnectReaderAsync(ct); }
            catch { /* best-effort */ }

            return Results.Ok(new
            {
                status = "parked",
                message = "Workers stopped. Service is still responsive. PUT /gate/status with status=active to resume."
            });
        })
        .WithName("ParkGate")
        .WithSummary("Soft-stop the gate (workers idle, service alive)")
        .WithDescription("Stops tag processing and disconnects the reader without exiting the process. Reversible.");

        group.MapPost("/shutdown", async (
            IGateStatusProvider statusProvider,
            IReaderStatusProvider readerStatus,
            IHostApplicationLifetime lifetime,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            // Drain first so we don't lose in-flight buffer rows.
            statusProvider.SetActive(false);
            try { await readerStatus.DisconnectReaderAsync(ct); }
            catch { /* best-effort */ }

            logger.LogWarning("Shutdown requested via /gate/shutdown — exiting cleanly. Service will not restart automatically (systemd Restart=on-failure assumed).");

            // Schedule the host stop AFTER the response is flushed so the caller sees a 200.
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                lifetime.StopApplication();
            }, CancellationToken.None);

            return Results.Ok(new
            {
                status = "shutting_down",
                message = "Service is shutting down. systemd unit must be configured Restart=on-failure for this to be permanent until manual restart."
            });
        })
        .WithName("ShutdownGate")
        .WithSummary("Hard-stop the gate (graceful process exit)")
        .WithDescription(
            "Drains workers, then exits the host. Pair with a systemd unit configured " +
            "Restart=on-failure so the clean exit is permanent — operator must SSH or " +
            "power-cycle to restart.");
    }
}
