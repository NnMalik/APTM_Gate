using System.Security.Claims;
using APTM.Gate.Api.Services;
using APTM.Gate.Infrastructure.Entities;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace APTM.Gate.Api.Endpoints;

/// <summary>
/// Checkpoint review/erase flow. A remote checkpoint NUC records reads continuously; the
/// field operator pulls them, reviews the data on the tablet, and then explicitly confirms
/// whether to keep the data on the gate or erase it.
///
/// This is a SCOPED erase — it deletes only the processed checkpoint events up to the
/// high-water mark the operator actually reviewed (<c>upToEventId</c>), so reads that arrived
/// after the pull are preserved. It is deliberately separate from <c>/gate/race-data/clear</c>
/// (a full TRUNCATE teardown) and from <c>/gate/raw/clear</c> (raw-buffer housekeeping).
/// </summary>
public static class CheckpointEndpoints
{
    public static void MapCheckpointEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/gate/checkpoint")
            .RequireAuthorization()
            .RequireReaderRole()  // Start gates have no reads of their own.
            .WithTags("Checkpoint");

        group.MapPost("/clear", async (
            ClearCheckpointRequest request,
            HttpContext httpContext,
            GateDbContext db,
            IConfiguration config,
            CancellationToken ct) =>
        {
            if (request.UpToEventId <= 0)
                return Results.BadRequest(new { error = "upToEventId must be > 0 (pass the high-water mark you pulled)." });

            // The highest event id any real device has pulled (excludes our own WIPE/ERASE markers).
            var maxPulledEventId = await db.SyncLogs
                .Where(s => !s.PullerDeviceCode.StartsWith("WIPE:") && !s.PullerDeviceCode.StartsWith("ERASE:"))
                .Select(s => (long?)s.LastProcessedEventId)
                .MaxAsync(ct) ?? 0L;

            // Safety: never erase events that haven't been pulled yet. The operator reviews
            // pulled data and confirms erase against the marker they received; anything newer
            // than what's been pulled must survive. force=true bypasses (full teardown only).
            if (!request.Force && request.UpToEventId > maxPulledEventId)
            {
                return Results.Conflict(new
                {
                    error = "Cannot erase past the last pulled event. Pull again or pass force=true.",
                    upToEventId = request.UpToEventId,
                    maxPulledEventId
                });
            }

            // Scoped delete: only checkpoint events, only up to the reviewed marker. Deleting
            // processed_events is FK-safe (it's the child of raw_tag_buffer). Raw-buffer
            // housekeeping is handled separately by POST /gate/raw/clear.
            var deletedEvents = await db.ProcessedEvents
                .Where(pe => pe.EventType == "checkpoint" && pe.Id <= request.UpToEventId)
                .ExecuteDeleteAsync(ct);

            // Audit trail: ERASE marker mirrors the WIPE marker convention in race-data/clear.
            var triggeredBy = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
            db.SyncLogs.Add(new SyncLogEntry
            {
                Id = Guid.NewGuid(),
                PullerDeviceId = Guid.Empty,
                PullerDeviceCode = $"ERASE:{triggeredBy}{(request.Force ? ":FORCE" : "")}",
                LastProcessedEventId = request.UpToEventId,
                LastReceivedSyncId = null,
                PulledAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);

            // Best-effort NOTIFY (harmless on a headless checkpoint; keeps any debug viewer fresh).
            try
            {
                var connStr = config.GetConnectionString("GateDb");
                await using var conn = new NpgsqlConnection(connStr);
                await conn.OpenAsync(ct);
                var payload = System.Text.Json.JsonSerializer.Serialize(new { erasedUpToEventId = request.UpToEventId });
                await using var cmd = new NpgsqlCommand($"NOTIFY config_updated, '{payload}'", conn);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch { /* best-effort */ }

            return Results.Ok(new
            {
                erased = true,
                forced = request.Force,
                triggeredBy,
                deletedEvents,
                upToEventId = request.UpToEventId
            });
        })
        .WithName("ClearCheckpointData")
        .WithSummary("Erase reviewed checkpoint events up to a high-water mark")
        .WithDescription(
            "Operator-confirmed, scoped erase of processed checkpoint events with id <= upToEventId. " +
            "Reads that arrived after the pull (id > upToEventId) are preserved. Returns 409 if " +
            "upToEventId is past the last pulled event unless force=true. Does not touch raw_tag_buffer " +
            "(use POST /gate/raw/clear) or configuration tables.");
    }
}

public sealed record ClearCheckpointRequest(long UpToEventId, bool Force = false);
