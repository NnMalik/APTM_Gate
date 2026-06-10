using APTM.Gate.Api.Services;
using APTM.Gate.Core.Interfaces;

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

        group.MapPost("/power-off", (
            IGateStatusProvider statusProvider,
            IReaderStatusProvider readerStatus,
            ISystemControlService systemControl,
            IConfiguration configuration,
            ILogger<Program> logger) =>
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
            "command (Gate:PowerOffCommand, default 'systemctl poweroff'). Irreversible " +
            "remotely — the machine must be physically powered back on. Can be disabled " +
            "per gate via Gate:AllowRemotePowerOff.");
    }
}
