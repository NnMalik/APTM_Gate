using System.Diagnostics;
using APTM.Gate.Api.Services;
using APTM.Gate.Core.Interfaces;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace APTM.Gate.Api.Endpoints;

/// <summary>
/// Buffer-management endpoints used primarily by the field app's "Pull &amp; Clear" flow
/// on Checkpoint NUCs. process-now is a synchronous flush — drains all PENDING raw rows
/// through the dedup pipeline. raw/clear deletes the bulk raw rows once the field app
/// has safely cached the resulting processed events upstream.
/// </summary>
public static class BufferEndpoints
{
    public static void MapBufferEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/gate/buffer")
            .RequireAuthorization()
            .RequireReaderRole()  // Start gates have no buffer.
            .WithTags("Buffer");

        group.MapPost("/process-now", async (
            IBufferProcessingService processor,
            IReaderStatusProvider readerStatus,
            CancellationToken ct) =>
        {
            // Reads can sit in the worker's in-memory ingest queue for a moment before
            // they reach raw_tag_buffer (longer if the DB is retrying). Wait briefly for
            // it to drain so a flush-then-pull can't miss reads that are already parsed
            // but not yet persisted.
            const int drainWaitMs = 3000;
            var drainSw = Stopwatch.StartNew();
            while (readerStatus.IngestQueueDepth > 0 && drainSw.ElapsedMilliseconds < drainWaitMs)
            {
                if (ct.IsCancellationRequested) break;
                await Task.Delay(100, ct);
            }

            // Drain the buffer: keep calling ProcessBatchAsync(100) until it returns 0.
            // Safety cap so a runaway buffer can't tie up the request indefinitely.
            const int maxBatches = 1000;
            const int batchSize = 100;

            var sw = Stopwatch.StartNew();
            var totalProcessed = 0;
            for (int i = 0; i < maxBatches; i++)
            {
                if (ct.IsCancellationRequested) break;
                var processed = await processor.ProcessBatchAsync(batchSize, ct);
                totalProcessed += processed;
                if (processed == 0) break;
            }
            sw.Stop();

            return Results.Ok(new
            {
                processedRows = totalProcessed,
                durationMs = sw.ElapsedMilliseconds,
                hitBatchCap = totalProcessed >= maxBatches * batchSize,
                // Non-zero = reads still stuck in memory (DB retrying) — the caller's
                // subsequent pull may be missing them; retry the flush.
                ingestQueueDepth = readerStatus.IngestQueueDepth
            });
        })
        .WithName("ProcessBufferNow")
        .WithSummary("Synchronously flush all PENDING raw reads through the processor")
        .WithDescription(
            "Drains the raw_tag_buffer until ProcessBatchAsync returns 0. Used by the " +
            "field app immediately before pulling so any reads from the last 0–500 ms " +
            "aren't left behind. Capped at 100 000 rows per call as a safety net.");

        var rawGroup = app.MapGroup("/gate/raw")
            .RequireAuthorization()
            .RequireReaderRole()
            .WithTags("Buffer");

        rawGroup.MapPost("/clear", async (
            ClearRawRequest request,
            GateDbContext db,
            CancellationToken ct) =>
        {
            if (request.UpToId <= 0)
                return Results.BadRequest(new { error = "upToId must be > 0." });

            // Step 1: null out raw_buffer_id on processed_events that point at rows we're
            // about to delete, so the FK doesn't block the delete.
            var nulledRefs = await db.ProcessedEvents
                .Where(pe => pe.RawBufferId != null && pe.RawBufferId <= request.UpToId)
                .ExecuteUpdateAsync(s => s.SetProperty(pe => pe.RawBufferId, (long?)null), ct);

            // Step 2: delete raw rows. By default refuses to touch PENDING — those have
            // not been turned into processed_events yet and the operator probably wants
            // them. With force=true, deletes everything regardless of status (used only
            // when wiping a NUC for redeployment).
            int deleted;
            if (request.Force)
            {
                deleted = await db.RawTagBuffers
                    .Where(r => r.Id <= request.UpToId)
                    .ExecuteDeleteAsync(ct);
            }
            else
            {
                deleted = await db.RawTagBuffers
                    .Where(r => r.Id <= request.UpToId && r.Status != "PENDING")
                    .ExecuteDeleteAsync(ct);
            }

            var remainingTotal = await db.RawTagBuffers.CountAsync(ct);
            var remainingPending = await db.RawTagBuffers.CountAsync(r => r.Status == "PENDING", ct);

            return Results.Ok(new
            {
                deletedRows = deleted,
                processedEventsUnlinked = nulledRefs,
                remainingTotal,
                remainingPending
            });
        })
        .WithName("ClearRawBuffer")
        .WithSummary("Delete raw_tag_buffer rows up to the given id")
        .WithDescription(
            "Defensive default: only deletes rows where Status != 'PENDING' so unprocessed " +
            "data is never lost. ProcessedEvent.RawBufferId references are nulled out first " +
            "to keep the FK clean. With force=true, deletes everything up to id including " +
            "PENDING (use only for full NUC wipe).");
    }
}

public sealed record ClearRawRequest(long UpToId, bool Force = false);
