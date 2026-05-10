using APTM.Gate.Api.Services;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace APTM.Gate.Api.Endpoints;

/// <summary>
/// Read-only operator-group state from the gate. Used by HHTs to detect overlap
/// when picking their selection (decision #3 in DESIGN_OPERATOR_GROUPS.md) and by
/// future tooling that wants to inspect what the gate currently knows.
///
/// The gate is a *replica* of Main's operator-group state — the source of truth
/// is Main, propagated via <c>POST /gate/config</c>. This endpoint deliberately
/// has no write surface; group definitions are not editable from the gate.
///
/// Read-only, so we keep auth on the same DeviceToken scheme as the rest of the
/// gate API but don't gate by role — Start gates also need this for their HHT
/// overlap-warning UI.
/// </summary>
public static class OperatorGroupEndpoints
{
    public static void MapOperatorGroupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/gate/operator-groups")
            .RequireAuthorization()
            .RequireProvisioned()
            .WithTags("OperatorGroups");

        group.MapGet("/", async (GateDbContext db, CancellationToken ct) =>
        {
            // Pull groups + assignments + per-group finish counter in one round-trip
            // each. Three queries beats a single LINQ subquery here because Npgsql's
            // COUNT-via-subquery codegen is awkward and we want the explicit indexed
            // path for `processed_events.group_id`.
            var groups = await db.OperatorGroups
                .AsNoTracking()
                .OrderBy(g => g.Name)
                .Select(g => new OperatorGroupListItem
                {
                    GroupId = g.GroupId,
                    Name = g.Name,
                    CandidateCount = g.CandidateIds.Length
                })
                .ToListAsync(ct);

            var assignments = await db.OperatorGroupAssignments
                .AsNoTracking()
                .Select(a => new { a.GroupId, a.DeviceCode })
                .ToListAsync(ct);
            var assignmentsByGroup = assignments
                .GroupBy(a => a.GroupId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.DeviceCode).ToList());

            // Per-group finish counts — only race events have group_id populated, so
            // the WHERE doubles as an event-type filter. Voided rows excluded for the
            // same reason the display feed excludes them: they're audit, not state.
            var finishCounts = await db.ProcessedEvents
                .AsNoTracking()
                .Where(pe => pe.GroupId != null && !pe.Voided)
                .GroupBy(pe => pe.GroupId!.Value)
                .Select(g => new { GroupId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.GroupId, g => g.Count, ct);

            var startedHeatCount = await db.RaceStartTimes
                .AsNoTracking()
                .Where(r => r.GroupId != null)
                .GroupBy(r => r.GroupId!.Value)
                .Select(g => new { GroupId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.GroupId, g => g.Count, ct);

            // Fold the per-group derived state into the response items.
            foreach (var item in groups)
            {
                item.AssignedDeviceCodes = assignmentsByGroup.TryGetValue(item.GroupId, out var codes)
                    ? codes : new List<string>();
                item.FinishedCount = finishCounts.TryGetValue(item.GroupId, out var fc) ? fc : 0;
                item.HeatsStarted = startedHeatCount.TryGetValue(item.GroupId, out var hs) ? hs : 0;
            }

            return Results.Ok(new OperatorGroupListResponse
            {
                Groups = groups,
                TotalGroups = groups.Count
            });
        })
        .WithName("ListOperatorGroups")
        .WithSummary("List operator groups defined for the active test on this gate")
        .WithDescription(
            "Returns each group's name, member count, the device-codes it is assigned to, " +
            "and live race-state counters (heats started, finishes processed) — used by " +
            "the HHT to detect overlap when an operator picks their selection.");
    }
}

public sealed class OperatorGroupListResponse
{
    public List<OperatorGroupListItem> Groups { get; set; } = [];
    public int TotalGroups { get; set; }
}

public sealed class OperatorGroupListItem
{
    public Guid GroupId { get; set; }
    public string Name { get; set; } = default!;
    public int CandidateCount { get; set; }
    public List<string> AssignedDeviceCodes { get; set; } = [];
    public int HeatsStarted { get; set; }
    public int FinishedCount { get; set; }
}
