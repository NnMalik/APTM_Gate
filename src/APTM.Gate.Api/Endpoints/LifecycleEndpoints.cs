using System.Diagnostics;
using APTM.Gate.Api.Services;
using APTM.Gate.Core.Interfaces;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace APTM.Gate.Api.Endpoints;

/// <summary>
/// Field-app-driven lifecycle control: prepare-shutdown (quiesce) and power-off.
///
/// The reader has no notion of "at the gate" vs "lingering nearby" — it reports every
/// in-range tag, so tags on people/vehicles parked near the gate keep landing in the
/// buffer. That makes the power-off guard's counters a moving target and a clean
/// (non-forced) shutdown unreachable. <c>prepare-shutdown</c> breaks the loop: it stops
/// the reader first, then drains what's already buffered, so the gate goes quiet and the
/// remaining state is stable and finite.
/// </summary>
public static class LifecycleEndpoints
{
    public static void MapLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/gate")
            .RequireAuthorization()
            .RequireProvisioned()
            .WithTags("Lifecycle");

        group.MapPost("/prepare-shutdown", async (
            IReaderStatusProvider readerStatus,
            IBufferProcessingService processor,
            GateDbContext db,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            // Stop new captures, then flush everything already buffered. After this the
            // reader is no longer feeding the buffer, so the counters below stop moving.
            var outcome = await QuiesceAsync(readerStatus, processor, ct);

            var (unpulledEvents, pendingRaw, ingestQueueDepth) = await ComputeStrandedAsync(db, readerStatus, ct);
            var readyForCleanShutdown = unpulledEvents == 0 && pendingRaw == 0 && ingestQueueDepth == 0;

            logger.LogInformation(
                "prepare-shutdown: reader stopped, drained {Processed} rows. " +
                "unpulled={Unpulled} pendingRaw={Pending} ingestQueue={Queue} ready={Ready}",
                outcome.ProcessedRows, unpulledEvents, pendingRaw, ingestQueueDepth, readyForCleanShutdown);

            return Results.Ok(new
            {
                readerStopped = true,
                processedRows = outcome.ProcessedRows,
                unpulledEvents,
                pendingRaw,
                ingestQueueDepth,
                readyForCleanShutdown,
                message = readyForCleanShutdown
                    ? "Reader stopped and buffer drained. Safe to power off cleanly."
                    : "Reader stopped and buffer drained, but reads remain that no device has pulled. " +
                      "Pull from each device, then power off (or use ?force=true)."
            });
        })
        .WithName("PrepareShutdown")
        .WithSummary("Stop the reader and drain the buffer so the gate can be shut down cleanly")
        .WithDescription(
            "Disconnects the UHF reader (halting new captures), waits for the in-memory ingest " +
            "queue to flush, then drains all PENDING raw reads through the processor. Use this " +
            "before /gate/power-off when tags lingering near the gate (e.g. on people in parked " +
            "cars) would otherwise keep the buffer non-empty and block a non-forced shutdown. " +
            "Reports whether the gate is now ready for a clean power-off. To resume reading " +
            "instead of powering off, call POST /gate/reader/connect.");

        group.MapPost("/power-off", async (
            bool? force,
            bool? quiesce,
            IGateStatusProvider statusProvider,
            IReaderStatusProvider readerStatus,
            IBufferProcessingService processor,
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

            // Optional quiesce: stop the reader and drain the buffer BEFORE the guard so it
            // evaluates a stable state instead of a moving target. Without this, stray reads
            // from tags near the gate keep pendingRaw / ingestQueue non-zero and a non-forced
            // power-off can never succeed. Equivalent to calling /gate/prepare-shutdown first.
            if (quiesce == true)
                await QuiesceAsync(readerStatus, processor, ct);

            // Stranded-data guard (same philosophy as checkpoint/clear and race-data/clear):
            // refuse to power off while reads exist that no device has pulled. The data
            // would survive in Postgres, but a powered-off NUC may not be booted again
            // before redeployment — pull first, or pass ?force=true.
            if (force != true)
            {
                var (unpulledEvents, pendingRaw, ingestQueueDepth) = await ComputeStrandedAsync(db, readerStatus, ct);

                if (unpulledEvents > 0 || pendingRaw > 0 || ingestQueueDepth > 0)
                {
                    return Results.Conflict(new
                    {
                        error = "Gate still holds reads no device has pulled. " +
                                "Call /gate/prepare-shutdown (or pass ?quiesce=true) and pull first, " +
                                "or pass ?force=true to power off anyway.",
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
                // Best-effort reader disconnect (no-op if already quiesced), a short grace
                // period for the HTTP 200 to flush, then the OS power-off.
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
            "command (Gate:PowerOffCommand, default 'systemctl poweroff'). Pass ?quiesce=true " +
            "to stop the reader and drain the buffer first so the guard below isn't fighting " +
            "stray reads. Returns 409 if unpulled reads remain (processed, pending raw, or " +
            "in-memory) unless ?force=true. Irreversible remotely — the machine must be " +
            "physically powered back on. Can be disabled per gate via Gate:AllowRemotePowerOff.");

        // Service restart lives OUTSIDE RequireProvisioned: it's the recovery step run right AFTER
        // first-time provisioning so the readers/workers (which read their role only at startup)
        // come online — at which point the gate IS provisioned, but we don't want the guard to be
        // the thing standing between the operator and a working gate. Auth still required.
        var restartGroup = app.MapGroup("/gate")
            .RequireAuthorization()
            .WithTags("Lifecycle");

        restartGroup.MapPost("/restart", (
            IGateStatusProvider statusProvider,
            ISystemControlService systemControl,
            ILogger<Program> logger) =>
        {
            // Unlike power-off, a restart comes straight back, so there's no stranded-data guard:
            // the buffer survives in Postgres and the workers resume on boot. Stop claiming new
            // batches, ack, then restart after the 200 has flushed.
            statusProvider.SetActive(false);
            logger.LogWarning("Service restart requested via /gate/restart — the gate process will bounce.");

            _ = Task.Run(async () =>
            {
                await Task.Delay(1000); // let the HTTP 200 reach the tablet before we go down
                await systemControl.RestartServiceAsync(CancellationToken.None);
            }, CancellationToken.None);

            return Results.Ok(new
            {
                status = "restarting",
                message = "The gate service is restarting. It will be back in a few seconds — reconnect then."
            });
        })
        .WithName("RestartGateService")
        .WithSummary("Restart the gate service process")
        .WithDescription(
            "Restarts the gate service at the OS level via the configured command " +
            "(Gate:RestartCommand, default schedules 'systemctl restart aptm-gate' via systemd-run). " +
            "Used after first-time provisioning so the readers/workers register their newly-set role. " +
            "The service comes back automatically in a few seconds; data in Postgres is preserved.");

        // Full OS reboot — the NUC restarts and comes back on its own (unlike power-off, which stays
        // down). No stranded-data guard: data survives in Postgres and the workers resume on boot
        // (same rationale as /gate/restart). We DO quiesce by default so reads sitting in the
        // in-memory ingest queue are flushed before the OS goes down; ?force=true skips that.
        group.MapPost("/reboot", async (
            bool? force,
            IGateStatusProvider statusProvider,
            IReaderStatusProvider readerStatus,
            IBufferProcessingService processor,
            ISystemControlService systemControl,
            IConfiguration configuration,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            // Per-gate kill switch. Missing or unparseable key = allowed (default on).
            var allowed = !bool.TryParse(configuration["Gate:AllowRemoteReboot"], out var enabled) || enabled;
            if (!allowed)
            {
                return Results.Json(new
                {
                    status = "reboot_disabled",
                    message = "Remote reboot is disabled on this gate (Gate:AllowRemoteReboot = false)."
                }, statusCode: 403);
            }

            // Flush the reader's in-memory queue + drain buffered reads before going down, unless forced.
            if (force != true)
                await QuiesceAsync(readerStatus, processor, ct);

            statusProvider.SetActive(false);
            logger.LogWarning(
                "Reboot requested via /gate/reboot — the NUC will restart. Its Wi-Fi AP drops for ~1 minute, " +
                "then the gate comes back automatically.");

            _ = Task.Run(async () =>
            {
                try { await readerStatus.DisconnectReaderAsync(CancellationToken.None); }
                catch { /* best-effort */ }

                await Task.Delay(1000); // let the HTTP 200 reach the tablet before we go down
                await systemControl.RebootAsync(CancellationToken.None);
            }, CancellationToken.None);

            return Results.Ok(new
            {
                status = "rebooting",
                message = "The NUC is rebooting. Its Wi-Fi will drop for about a minute and then reconnect — " +
                          "no need to power it on manually."
            });
        })
        .WithName("RebootGate")
        .WithSummary("Reboot the NUC (full OS restart)")
        .WithDescription(
            "Reboots the NUC at the OS level via the configured command (Gate:RebootCommand, default " +
            "schedules 'systemctl reboot' via systemd-run). Unlike power-off, the machine comes back on " +
            "its own. Quiesces the reader/buffer first unless ?force=true. No stranded-data guard — data " +
            "survives in Postgres and workers resume on boot. Can be disabled via Gate:AllowRemoteReboot.");
    }

    /// <summary>
    /// Stops the UHF reader and drains the buffer so the gate goes quiet for teardown.
    /// Disconnects the reader (halting new captures), waits for the in-memory ingest queue
    /// to flush to raw_tag_buffer, then runs the processor until no PENDING rows remain.
    /// Mirrors /gate/buffer/process-now's drain, with the reader-stop added so the drain
    /// actually converges instead of chasing fresh stray reads.
    /// </summary>
    private static async Task<QuiesceOutcome> QuiesceAsync(
        IReaderStatusProvider readerStatus,
        IBufferProcessingService processor,
        CancellationToken ct)
    {
        // 1. Stop new captures so the buffer stops growing. Manual disconnect is honoured
        //    by the reader's auto-reconnect loop until /gate/reader/connect is called.
        try { await readerStatus.DisconnectReaderAsync(ct); }
        catch { /* best-effort — drain anyway */ }

        // 2. Wait briefly for reads already parsed but not yet persisted to land in the DB.
        const int drainWaitMs = 5000;
        var drainSw = Stopwatch.StartNew();
        while (readerStatus.IngestQueueDepth > 0 && drainSw.ElapsedMilliseconds < drainWaitMs)
        {
            if (ct.IsCancellationRequested) break;
            await Task.Delay(100, ct);
        }

        // 3. Drain PENDING raw → processed_events. Capped so a runaway buffer can't tie up
        //    the request indefinitely. ProcessBatchAsync uses FOR UPDATE SKIP LOCKED, so
        //    running it here concurrently with the background worker is safe.
        const int maxBatches = 1000;
        const int batchSize = 100;
        var totalProcessed = 0;
        for (int i = 0; i < maxBatches; i++)
        {
            if (ct.IsCancellationRequested) break;
            var processed = await processor.ProcessBatchAsync(batchSize, ct);
            totalProcessed += processed;
            if (processed == 0) break;
        }

        return new QuiesceOutcome(totalProcessed, readerStatus.IngestQueueDepth);
    }

    /// <summary>
    /// Computes the "stranded data" counters used to decide whether a clean shutdown is safe:
    /// processed events not yet pulled by any device, PENDING raw rows, and reads still sitting
    /// in the reader's in-memory ingest queue. WIPE/ERASE marker rows are excluded from the
    /// max-pulled calculation so they don't make the gate look drained when it isn't.
    /// </summary>
    private static async Task<(long UnpulledEvents, int PendingRaw, int IngestQueueDepth)> ComputeStrandedAsync(
        GateDbContext db,
        IReaderStatusProvider readerStatus,
        CancellationToken ct)
    {
        var maxEventId = await db.ProcessedEvents.MaxAsync(pe => (long?)pe.Id, ct) ?? 0L;
        var maxPulledEventId = await db.SyncLogs
            .Where(s => !s.PullerDeviceCode.StartsWith("WIPE:") && !s.PullerDeviceCode.StartsWith("ERASE:"))
            .Select(s => (long?)s.LastProcessedEventId)
            .MaxAsync(ct) ?? 0L;
        var unpulledEvents = Math.Max(0, maxEventId - maxPulledEventId);
        var pendingRaw = await db.RawTagBuffers.CountAsync(r => r.Status == "PENDING", ct);
        var ingestQueueDepth = readerStatus.IngestQueueDepth;

        return (unpulledEvents, pendingRaw, ingestQueueDepth);
    }

    private sealed record QuiesceOutcome(int ProcessedRows, int IngestQueueDepth);
}
