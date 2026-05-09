using APTM.Gate.Api.Services;
using APTM.Gate.Core.Enums;
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
    }
}
