using System.Security.Claims;
using APTM.Gate.Api.Services;
using APTM.Gate.Infrastructure.Entities;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace APTM.Gate.Api.Endpoints;

/// <summary>
/// "End-of-test" cleanup for race data on the gate. After a test is complete and the
/// processed events + sync data have been pulled into Main (via the field app's
/// existing pull flow), the operator can wipe the per-race tables here so the NUC
/// doesn't carry stale results forward into the next test.
///
/// Tables wiped: <c>processed_events</c>, <c>raw_tag_buffer</c>, <c>race_start_times</c>,
/// <c>received_sync_data</c>, <c>sync_logs</c>, <c>heat_completions</c>.
///
/// Tables preserved: <c>candidates</c>, <c>tag_assignments</c>, <c>checkpoint_config</c>,
/// <c>scoring_*</c>, <c>test_events</c>, <c>gate_config</c>, <c>gate_identity</c>,
/// <c>accepted_tokens</c>, <c>reader_config</c> — i.e. anything that's part of the
/// configuration / provisioning rather than per-race output.
/// </summary>
public static class RaceDataEndpoints
{
    public static void MapRaceDataEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/gate/race-data")
            .RequireAuthorization()
            .RequireReaderRole()  // Start gates have no race data of their own.
            .WithTags("RaceData");

        group.MapGet("/status", async (GateDbContext db, CancellationToken ct) =>
        {
            var status = await ComputeStatusAsync(db, ct);
            return Results.Ok(status);
        })
        .WithName("GetRaceDataStatus")
        .WithSummary("Counts + sync gap for the per-race tables")
        .WithDescription(
            "Returns row counts for processed_events / raw / race_starts / received_sync_data / heat_completions, " +
            "plus the max event id, the highest pulled event id (across all devices), and an 'allPulled' flag — " +
            "use this to decide whether a wipe is safe.");

        group.MapPost("/clear", async (
            ClearRaceDataRequest request,
            HttpContext httpContext,
            GateDbContext db,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var status = await ComputeStatusAsync(db, ct);

            // Optional optimistic-concurrency guard: caller pins the max event id they
            // saw in a preceding /status call, and we refuse if more events have arrived
            // since (would otherwise wipe data the caller didn't know about).
            if (request.ExpectedMaxEventId.HasValue
                && status.MaxEventId > request.ExpectedMaxEventId.Value)
            {
                return Results.Conflict(new
                {
                    error = "More events arrived since the status check. Re-check status before clearing.",
                    expectedMaxEventId = request.ExpectedMaxEventId.Value,
                    actualMaxEventId = status.MaxEventId,
                    status
                });
            }

            // Default safety: refuse if anything hasn't been pulled. force=true bypasses.
            if (!request.Force && !status.AllPulled)
            {
                return Results.Conflict(new
                {
                    error = "Race data has not been fully pulled. Pull from each device first or pass force=true.",
                    status
                });
            }

            // Single SQL statement — TRUNCATE is faster than per-table DELETE and resets
            // the BIGSERIAL identity on processed_events and raw_tag_buffer (which is
            // important: callers track high-water-marks by id, so we want them back at 0).
            await db.Database.ExecuteSqlRawAsync(
                "TRUNCATE TABLE processed_events, raw_tag_buffer, race_start_times, " +
                "received_sync_data, sync_logs, heat_completions " +
                "RESTART IDENTITY CASCADE", ct);

            // Audit trail: write a marker row to sync_logs so a forensic trace exists.
            // PullerDeviceCode = "WIPE:<token>" — easy to filter out of the regular pull
            // history view and tells you who triggered the wipe.
            var triggeredBy = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
            db.SyncLogs.Add(new SyncLogEntry
            {
                Id = Guid.NewGuid(),
                PullerDeviceId = Guid.Empty,
                PullerDeviceCode = $"WIPE:{triggeredBy}{(request.Force ? ":FORCE" : "")}",
                LastProcessedEventId = status.MaxEventId,
                LastReceivedSyncId = null,
                PulledAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);

            // Best-effort NOTIFY so the local display refreshes (clears any stale finish
            // results on screen).
            try
            {
                var connStr = config.GetConnectionString("GateDb");
                await using var conn = new NpgsqlConnection(connStr);
                await conn.OpenAsync(ct);
                var payload = System.Text.Json.JsonSerializer.Serialize(new { wiped = true });
                await using var cmd = new NpgsqlCommand($"NOTIFY config_updated, '{payload}'", conn);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch { /* display refresh is best-effort */ }

            return Results.Ok(new
            {
                wiped = true,
                forced = request.Force,
                triggeredBy,
                cleared = new
                {
                    processedEvents = status.ProcessedEventCount,
                    rawRows = status.RawRowCount,
                    raceStarts = status.RaceStartCount,
                    receivedSyncData = status.ReceivedSyncDataCount,
                    heatCompletions = status.HeatCompletionCount
                }
            });
        })
        .WithName("ClearRaceData")
        .WithSummary("Wipe per-race tables (processed events, raw, sync data, race starts, heat completions)")
        .WithDescription(
            "Defensive default: returns 409 if any device hasn't pulled the latest events. " +
            "Pass force=true to override (intended for end-of-event teardown when data has " +
            "been confirmed in Main out-of-band). Configuration tables (candidates, " +
            "tag_assignments, scoring_*, test_events, gate_config, gate_identity, " +
            "accepted_tokens, reader_config) are NOT touched.");
    }

    private static async Task<RaceDataStatusResponse> ComputeStatusAsync(GateDbContext db, CancellationToken ct)
    {
        var processedEventCount = await db.ProcessedEvents.CountAsync(ct);
        var rawRowCount = await db.RawTagBuffers.CountAsync(ct);
        var rawPendingCount = await db.RawTagBuffers.CountAsync(r => r.Status == "PENDING", ct);
        var raceStartCount = await db.RaceStartTimes.CountAsync(ct);
        var receivedSyncDataCount = await db.ReceivedSyncData.CountAsync(ct);
        var heatCompletionCount = await db.HeatCompletions.CountAsync(ct);

        var maxEventId = processedEventCount > 0
            ? await db.ProcessedEvents.MaxAsync(pe => pe.Id, ct)
            : 0L;

        // Filter out our own WIPE-marker rows when computing max-pulled — otherwise a
        // previous force-wipe would always look "fully pulled" even though no real
        // device actually drained the new events.
        var maxPulledEventId = await db.SyncLogs
            .Where(s => !s.PullerDeviceCode.StartsWith("WIPE:"))
            .Select(s => (long?)s.LastProcessedEventId)
            .MaxAsync(ct) ?? 0L;

        // Per-device pull state — handy for the UI to call out the laggard.
        var perDevicePullState = await db.SyncLogs
            .Where(s => !s.PullerDeviceCode.StartsWith("WIPE:"))
            .GroupBy(s => s.PullerDeviceCode)
            .Select(g => new RaceDataDeviceSync
            {
                DeviceCode = g.Key,
                LastPulledEventId = g.Max(s => s.LastProcessedEventId),
                LastPulledAt = g.Max(s => s.PulledAt)
            })
            .ToListAsync(ct);

        var allPulled = processedEventCount == 0 || maxPulledEventId >= maxEventId;

        return new RaceDataStatusResponse
        {
            ProcessedEventCount = processedEventCount,
            RawRowCount = rawRowCount,
            RawPendingCount = rawPendingCount,
            RaceStartCount = raceStartCount,
            ReceivedSyncDataCount = receivedSyncDataCount,
            HeatCompletionCount = heatCompletionCount,
            MaxEventId = maxEventId,
            MaxPulledEventId = maxPulledEventId,
            AllPulled = allPulled,
            UnpulledCount = Math.Max(0, maxEventId - maxPulledEventId),
            PerDevice = perDevicePullState
        };
    }
}

public sealed record ClearRaceDataRequest(bool Force = false, long? ExpectedMaxEventId = null);

public sealed class RaceDataStatusResponse
{
    public int ProcessedEventCount { get; set; }
    public int RawRowCount { get; set; }
    public int RawPendingCount { get; set; }
    public int RaceStartCount { get; set; }
    public int ReceivedSyncDataCount { get; set; }
    public int HeatCompletionCount { get; set; }
    public long MaxEventId { get; set; }
    public long MaxPulledEventId { get; set; }
    public bool AllPulled { get; set; }
    public long UnpulledCount { get; set; }
    public List<RaceDataDeviceSync> PerDevice { get; set; } = new();
}

public sealed class RaceDataDeviceSync
{
    public string DeviceCode { get; set; } = default!;
    public long LastPulledEventId { get; set; }
    public DateTimeOffset LastPulledAt { get; set; }
}
