using APTM.Gate.Api.Services;
using APTM.Gate.Core.Enums;
using APTM.Gate.Core.Models;
using APTM.Gate.Infrastructure.Entities;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace APTM.Gate.Api.Endpoints;

/// <summary>
/// Heat lifecycle controls. Currently only force-close (DNF case).
/// Auto-completion happens server-side inside <c>BufferProcessingService</c> after each
/// finish event — no endpoint needed for that path.
/// </summary>
public static class HeatEndpoints
{
    public static void MapHeatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/gate/heat")
            .RequireAuthorization()
            .RequireRoles(GateRole.Finish)  // Force-close lives at the finish gate; replication to start follows via sync.
            .WithTags("Heat");

        // Read-only completions feed. A roaming HHT polls this from the FINISH gate to learn which
        // heats completed (auto or force-close), then relays each onward to the START gate via the
        // existing /gate/sync/push channel so the start LED can freeze its timer. durationSeconds is
        // the authoritative total heat time (completedAt − adjusted gun) computed here, so the start
        // LED freezes on the SAME value the finish LED shows — no cross-gate clock dependency.
        group.MapGet("/completions", async (
            GateDbContext db,
            long? sinceMs,
            CancellationToken ct) =>
        {
            var since = DateTimeOffset.FromUnixTimeMilliseconds(sinceMs ?? 0L);

            var rows = await db.HeatCompletions
                .AsNoTracking()
                .Where(hc => hc.ReceivedAt > since)
                .OrderBy(hc => hc.ReceivedAt)
                .ToListAsync(ct);

            // Adjusted gun time per heat (gate clock domain). Volumes are a few dozen heats per
            // test, so an in-memory join is cheaper than a correlated subquery.
            var heatIds = rows.Select(r => r.HeatId).ToList();
            var guns = await db.RaceStartTimes
                .AsNoTracking()
                .Where(r => heatIds.Contains(r.HeatId))
                .GroupBy(r => r.HeatId)
                .Select(g => new { HeatId = g.Key, Gun = g.Max(x => x.GunStartTime) })
                .ToDictionaryAsync(x => x.HeatId, x => x.Gun, ct);

            var completions = rows.Select(hc =>
            {
                // Prefer a stored duration when present (future-proof); else compute from the gun.
                double? duration = hc.DurationSeconds;
                if (duration is null && guns.TryGetValue(hc.HeatId, out var gun))
                    duration = Math.Max(0, (hc.CompletedAt - gun).TotalSeconds);

                return new HeatCompletionDto
                {
                    HeatId = hc.HeatId,
                    HeatNumber = hc.HeatNumber,
                    ExpectedCount = hc.ExpectedCount,
                    FinishedCount = hc.FinishedCount,
                    LastCandidateId = hc.LastCandidateId,
                    CompletedAt = hc.CompletedAt,
                    ClosureReason = hc.ClosureReason,
                    SourceDeviceCode = hc.SourceDeviceCode,
                    DurationSeconds = duration
                };
            }).ToList();

            var highWaterMs = rows.Count > 0
                ? rows.Max(r => r.ReceivedAt).ToUnixTimeMilliseconds()
                : (sinceMs ?? 0L);

            return Results.Ok(new HeatCompletionFeedResponse
            {
                Completions = completions,
                HighWaterMs = highWaterMs
            });
        })
        .WithName("GetHeatCompletions")
        .WithSummary("Completions feed for start-gate relay")
        .WithDescription(
            "Returns heat completions received after sinceMs (received_at watermark), each with the " +
            "authoritative durationSeconds. Polled by the HHT to replicate completions to the start gate.");

        group.MapPost("/{heatNumber:int}/close", async (
            int heatNumber,
            GateDbContext db,
            IConfiguration config,
            CancellationToken ct) =>
        {
            // Locate the most recent race_start for this heat number. We use latest because
            // the same heat number could be re-fired (rare) — most recent wins.
            var raceStart = await db.RaceStartTimes
                .Where(r => r.HeatNumber == heatNumber)
                .OrderByDescending(r => r.ReceivedAt)
                .FirstOrDefaultAsync(ct);

            if (raceStart is null)
                return Results.NotFound(new { error = $"No race_start found for heat {heatNumber}." });

            // Already complete? Idempotent return.
            var existing = await db.HeatCompletions
                .FirstOrDefaultAsync(hc => hc.HeatId == raceStart.HeatId, ct);
            if (existing is not null)
            {
                return Results.Ok(new
                {
                    status = "already_completed",
                    heatId = existing.HeatId,
                    heatNumber = existing.HeatNumber,
                    completedAt = existing.CompletedAt,
                    closureReason = existing.ClosureReason
                });
            }

            // Count actual finishers from the roster — informational only on a force-close,
            // operator already knows N didn't finish.
            var roster = raceStart.CandidateIds ?? [];
            var finishedCount = roster.Length == 0
                ? 0
                : await db.ProcessedEvents
                    .Where(pe => pe.EventType == "finish"
                              && pe.IsFirstRead
                              && pe.HeatNumber == heatNumber
                              && pe.CandidateId != null
                              && roster.Contains(pe.CandidateId.Value))
                    .Select(pe => pe.CandidateId)
                    .Distinct()
                    .CountAsync(ct);

            var deviceCode = config["Gate:DeviceCode"] ?? "unknown";
            var now = DateTimeOffset.UtcNow;

            var completion = new HeatCompletion
            {
                HeatId = raceStart.HeatId,
                HeatNumber = heatNumber,
                ExpectedCount = roster.Length,
                FinishedCount = finishedCount,
                LastCandidateId = null,            // force-close has no canonical "last finisher"
                CompletedAt = now,
                ClosureReason = "force_close",
                SourceDeviceCode = deviceCode,
                ReceivedAt = now
            };

            db.HeatCompletions.Add(completion);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Race with auto-completion. The other won; that's fine.
                var winner = await db.HeatCompletions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(hc => hc.HeatId == raceStart.HeatId, ct);
                return Results.Ok(new
                {
                    status = "raced_with_auto",
                    heatId = winner?.HeatId,
                    heatNumber = winner?.HeatNumber,
                    completedAt = winner?.CompletedAt,
                    closureReason = winner?.ClosureReason
                });
            }

            return Results.Ok(new
            {
                status = "force_closed",
                heatId = completion.HeatId,
                heatNumber = completion.HeatNumber,
                expectedCount = completion.ExpectedCount,
                finishedCount = completion.FinishedCount,
                completedAt = completion.CompletedAt,
                closureReason = completion.ClosureReason
            });
        })
        .WithName("ForceCloseHeat")
        .WithSummary("Manually close a heat (DNF case)")
        .WithDescription(
            "Marks the heat as complete with closure_reason='force_close'. Used when one " +
            "or more candidates didn't finish. Idempotent: a second call after a real " +
            "completion returns the already-completed row. Replicated to the start gate " +
            "via the existing /gate/sync/push channel.");

        // Close by heat_id (unambiguous). The by-number route above can't address the right heat
        // when two HHTs both ran "heat 1" — it always resolves to the most recent, so the second
        // heat reports "already completed". The field app uses this route, passing the heat's id.
        group.MapPost("/by-id/{heatId:guid}/close", async (
            Guid heatId,
            GateDbContext db,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var raceStart = await db.RaceStartTimes
                .Where(r => r.HeatId == heatId)
                .OrderByDescending(r => r.ReceivedAt)
                .FirstOrDefaultAsync(ct);

            if (raceStart is null)
                return Results.NotFound(new { error = $"No race_start found for heat {heatId}." });

            var existing = await db.HeatCompletions.FirstOrDefaultAsync(hc => hc.HeatId == heatId, ct);
            if (existing is not null)
                return Results.Ok(new
                {
                    status = "already_completed",
                    heatId = existing.HeatId,
                    heatNumber = existing.HeatNumber,
                    completedAt = existing.CompletedAt,
                    closureReason = existing.ClosureReason
                });

            var roster = raceStart.CandidateIds ?? [];
            var finishedCount = roster.Length == 0
                ? 0
                : await db.ProcessedEvents
                    .Where(pe => pe.EventType == "finish"
                              && pe.IsFirstRead
                              && pe.HeatId == heatId
                              && pe.CandidateId != null
                              && roster.Contains(pe.CandidateId.Value))
                    .Select(pe => pe.CandidateId)
                    .Distinct()
                    .CountAsync(ct);

            var deviceCode = config["Gate:DeviceCode"] ?? "unknown";
            var now = DateTimeOffset.UtcNow;

            var completion = new HeatCompletion
            {
                HeatId = heatId,
                HeatNumber = raceStart.HeatNumber,
                ExpectedCount = roster.Length,
                FinishedCount = finishedCount,
                LastCandidateId = null,
                CompletedAt = now,
                ClosureReason = "force_close",
                SourceDeviceCode = deviceCode,
                ReceivedAt = now
            };

            db.HeatCompletions.Add(completion);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                var winner = await db.HeatCompletions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(hc => hc.HeatId == heatId, ct);
                return Results.Ok(new
                {
                    status = "raced_with_auto",
                    heatId = winner?.HeatId,
                    heatNumber = winner?.HeatNumber,
                    completedAt = winner?.CompletedAt,
                    closureReason = winner?.ClosureReason
                });
            }

            return Results.Ok(new
            {
                status = "force_closed",
                heatId = completion.HeatId,
                heatNumber = completion.HeatNumber,
                expectedCount = completion.ExpectedCount,
                finishedCount = completion.FinishedCount,
                completedAt = completion.CompletedAt,
                closureReason = completion.ClosureReason
            });
        })
        .WithName("ForceCloseHeatById")
        .WithSummary("Manually close a specific heat by id (DNF case)")
        .WithDescription(
            "Closes the heat identified by heat_id — unambiguous when two HHTs share a heat " +
            "number. Same semantics and response shape as the by-number route, which is kept " +
            "for back-compat.");
    }
}
