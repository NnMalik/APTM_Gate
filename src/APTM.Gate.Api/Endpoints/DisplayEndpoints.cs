using APTM.Gate.Api.Services;
using APTM.Gate.Core.Interfaces;
using APTM.Gate.Core.Models;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace APTM.Gate.Api.Endpoints;

public static class DisplayEndpoints
{
    public static void MapDisplayEndpoints(this IEndpointRouteBuilder app)
    {
        // No auth — local Wi-Fi only
        app.MapGet("/gate/display-stream", async (HttpContext context, SseNotificationService sseService) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.Append("Cache-Control", "no-cache");
            context.Response.Headers.Append("Connection", "keep-alive");

            await sseService.StreamEvents(context.Response, context.RequestAborted);
        })
        .WithTags("Display")
        .WithName("DisplayStream")
        .WithSummary("SSE event stream")
        .WithDescription("Server-Sent Events stream for real-time display updates. Channels: tag_event, race_start, sync_data, config_updated.")
        .ExcludeFromDescription();

        app.MapGet("/gate/display-data", async (
            GateDbContext db,
            IReaderStatusProvider readerStatus,
            IGateStatusProvider gateStatus,
            IGateIdentityProvider identityProvider,
            CancellationToken ct) =>
        {
            // Identity drives the role label; config drives the rich event/heat payload.
            var identity = identityProvider.Current;
            if (identity is null)
                return Results.Ok(new { gateRole = "unprovisioned" });

            // Checkpoint has no display surface (root endpoint returns 204). Return a
            // coherent stub so anything that does hit /gate/display-data on a checkpoint
            // gets a sensible shape rather than an empty/broken full payload.
            if (string.Equals(identity.Role, "Checkpoint", StringComparison.OrdinalIgnoreCase))
                return Results.Ok(new { gateRole = identity.Role, note = "headless gate" });

            var gateConfig = await db.GateConfigs
                .Where(g => g.IsActive)
                .FirstOrDefaultAsync(ct);

            if (gateConfig is null)
                return Results.Ok(new { gateRole = identity.Role, note = "awaiting config" });

            var activeEventId = gateConfig.ActiveEventId;
            var totalCandidates = await db.Candidates.CountAsync(ct);

            // Live heats = race starts not cancelled, scoped to the active event. A PARALLEL
            // event (e.g. cross-country) can have several heats live at once, each started by a
            // different HHT; a SPRINT event (e.g. 100m) has one at a time. Cancelled heats are
            // kept in race_start_times for audit; a HeatCompletion with closure_reason='cancelled'
            // is the canonical "aborted" marker.
            var cancelledHeatIds = await db.HeatCompletions
                .Where(hc => hc.ClosureReason == "cancelled")
                .Select(hc => hc.HeatId)
                .ToListAsync(ct);

            var liveStarts = await db.RaceStartTimes
                .Where(r => !cancelledHeatIds.Contains(r.HeatId)
                         && (r.EventId == activeEventId || r.EventId == null))
                .OrderByDescending(r => r.ReceivedAt)
                .ToListAsync(ct);

            // Total groups/batches = distinct live heats for the active event. Keyed by
            // HeatId (one row per heat), so two HHTs that each number their heats from 1
            // don't collapse together, cancelled heats are excluded, and the count agrees
            // with the per-group rows and the finish display.
            var totalGroups = liveStarts.Count;

            // Group-name lookup for per-group row labels (falls back to "Group N").
            var groupNames = await db.OperatorGroups
                .AsNoTracking()
                .ToDictionaryAsync(g => g.GroupId, g => g.Name, ct);

            // Completions for these heats — a completed heat's timer freezes on the display.
            var liveHeatIds = liveStarts.Select(s => s.HeatId).ToList();
            var completions = await db.HeatCompletions
                .AsNoTracking()
                .Where(hc => liveHeatIds.Contains(hc.HeatId))
                .ToDictionaryAsync(hc => hc.HeatId, ct);

            var activeHeats = new List<ActiveHeatData>(liveStarts.Count);
            foreach (var rs in liveStarts)
            {
                var candidateIds = rs.CandidateIds ?? [];
                var heatCandidates = await db.Candidates
                    .Where(c => candidateIds.Contains(c.CandidateId))
                    .Select(c => new HeatCandidateData
                    {
                        CandidateId = c.CandidateId,
                        Name = c.Name,
                        JacketNumber = c.JacketNumber
                    })
                    .ToListAsync(ct);

                completions.TryGetValue(rs.HeatId, out var completion);

                var finishedCount = candidateIds.Length == 0
                    ? 0
                    : await db.ProcessedEvents
                        .Where(pe => pe.EventType == "finish"
                                  && pe.IsFirstRead
                                  && !pe.Voided
                                  && pe.HeatId == rs.HeatId
                                  && pe.CandidateId != null
                                  && candidateIds.Contains(pe.CandidateId.Value))
                        .Select(pe => pe.CandidateId)
                        .Distinct()
                        .CountAsync(ct);

                var label = rs.GroupId.HasValue
                            && groupNames.TryGetValue(rs.GroupId.Value, out var gname)
                            && !string.IsNullOrWhiteSpace(gname)
                    ? gname
                    : $"Group {rs.HeatNumber}";

                activeHeats.Add(new ActiveHeatData
                {
                    HeatId = rs.HeatId,
                    HeatNumber = rs.HeatNumber,
                    GroupId = rs.GroupId,
                    GroupLabel = label,
                    Abbrev = Abbreviate(
                        rs.GroupId.HasValue ? groupNames.GetValueOrDefault(rs.GroupId.Value) : null,
                        rs.SourceDeviceCode, rs.HeatNumber),
                    SourceDeviceCode = rs.SourceDeviceCode,
                    HasStartTime = true,
                    GunStartTime = rs.GunStartTime,
                    OriginalGunStartTime = rs.OriginalGunStartTime,
                    Candidates = heatCandidates,
                    ExpectedCount = candidateIds.Length,
                    FinishedCount = completion?.FinishedCount ?? finishedCount,
                    CompletedAt = completion?.CompletedAt,
                    ClosureReason = completion?.ClosureReason,
                    // Authoritative finish-gate heat time, when known (relayed to the start gate).
                    // The start LED freezes on this so both displays show the same total time.
                    CompletedDurationSeconds = completion?.DurationSeconds
                });
            }

            // SPRINT mode / back-compat consumers use the most recent live heat.
            var activeHeat = activeHeats.FirstOrDefault();

            // Display mode for the active event. Main resolves the explicit per-event flag (and its
            // type default) into test_events.DisplayMode, so we use that. Fall back to deriving from
            // the event type only for legacy gate data pushed before the flag existed (DisplayMode null).
            var activeEventRow = activeEventId == null ? null : await db.TestEvents
                .AsNoTracking()
                .Where(e => e.EventId == activeEventId)
                .Select(e => new { e.DisplayMode, e.EventType })
                .FirstOrDefaultAsync(ct);
            var displayMode = !string.IsNullOrWhiteSpace(activeEventRow?.DisplayMode)
                ? activeEventRow!.DisplayMode!.ToUpperInvariant()
                : (string.Equals(activeEventRow?.EventType, "CROSS_COUNTRY", StringComparison.OrdinalIgnoreCase)
                    ? "PARALLEL"
                    : "SPRINT");

            // Get finish reads for current event only (reads from denormalized columns — no JOIN).
            // Filter out null CandidateId (only checkpoint events have null; finish always resolves).
            // Voided rows are excluded — those are reads that landed against a heat that
            // was later cancelled, or for a candidate that was pulled out of the heat.
            var finishReads = await db.ProcessedEvents
                .Where(pe => pe.EventType == "finish" && pe.IsFirstRead && !pe.Voided && pe.CandidateId != null)
                .Where(pe => activeEventId == null || pe.EventId == activeEventId)
                .OrderBy(pe => pe.ReadTime)
                .Select(pe => new FinishReadData
                {
                    Position = 0,
                    CandidateId = pe.CandidateId!.Value,
                    Name = pe.CandidateName ?? "",
                    JacketNumber = pe.JacketNumber,
                    TagEPC = pe.TagEPC,
                    ReadTime = pe.ReadTime,
                    ElapsedSeconds = pe.DurationSeconds,
                    HeatNumber = pe.HeatNumber
                })
                .ToListAsync(ct);

            // Number the positions
            for (int i = 0; i < finishReads.Count; i++)
                finishReads[i].Position = i + 1;

            // Start/attendance reads for current event (reads from denormalized columns — no JOIN).
            // Forward-compat: today this list is always empty because Start gates have no reader
            // so no rows of event_type='start_attendance' ever land in processed_events. Live
            // attendance for the start display is sourced from received_sync_data below
            // (HHT pushes attendance via /gate/sync/push). This query reactivates if a Start
            // NUC ever gains a reader for tag-based in-person check-in.
            var startReads = await db.ProcessedEvents
                .Where(pe => pe.EventType == "start_attendance" && pe.IsFirstRead && pe.CandidateId != null)
                .Where(pe => activeEventId == null || pe.EventId == activeEventId)
                .OrderByDescending(pe => pe.ReadTime)
                .Select(pe => new StartReadData
                {
                    CandidateId = pe.CandidateId!.Value,
                    Name = pe.CandidateName ?? "",
                    JacketNumber = pe.JacketNumber,
                    TagEPC = pe.TagEPC,
                    ReadTime = pe.ReadTime
                })
                .ToListAsync(ct);

            // Attendance counts: gate's own processed reads (always PRESENT) plus
            // synced attendance from HHTs split by the `status` field on each
            // received_sync_data row's payload.
            //
            // Older HHT clients (pre-mark-absent rollout) don't send a status
            // field — those payloads are treated as PRESENT, matching legacy
            // behaviour.
            //
            // Volume note: a few hundred attendance rows per test, scanned in a
            // tight in-memory loop. If volumes ever grow, switch to a JSONB GIN
            // index on `payload->>'status'` and aggregate in SQL.
            var gateAttendanceCount = startReads.Count;
            var attendancePayloads = await db.ReceivedSyncData
                .AsNoTracking()
                .Where(r => r.DataType == "attendance")
                .Select(r => r.Payload)
                .ToListAsync(ct);

            int presentFromHht = 0, absentFromHht = 0;
            foreach (var doc in attendancePayloads)
            {
                var status = "PRESENT";
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("status", out var statusEl) &&
                    statusEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    status = statusEl.GetString() ?? "PRESENT";
                }
                if (string.Equals(status, "ABSENT", StringComparison.OrdinalIgnoreCase))
                    absentFromHht++;
                else
                    presentFromHht++;
            }

            var totalPresent = gateAttendanceCount + presentFromHht;
            var totalAbsent = absentFromHht;

            var data = new DisplayData
            {
                // Source of truth is identity — keeps the role consistent even if
                // gate_config is stale or out-of-sync after a force-flip.
                GateRole = identity.Role,
                ReaderConnected = readerStatus.IsConnected,
                IsProcessingActive = gateStatus.IsActive,
                ActiveEventId = gateConfig.ActiveEventId,
                ActiveEventName = gateConfig.ActiveEventName,
                TestInstanceName = gateConfig.TestInstanceName,
                ScheduledDate = gateConfig.ScheduledDate.ToString("yyyy-MM-dd"),
                TotalCandidates = totalCandidates,
                TotalGroups = totalGroups,
                DisplayMode = displayMode,
                ActiveHeat = activeHeat,
                ActiveHeats = activeHeats,
                FinishReads = finishReads,
                StartReads = startReads,
                Attendance = new AttendanceData
                {
                    TotalPresent = totalPresent,
                    TotalAbsent = totalAbsent,
                    TotalNotScanned = Math.Max(0, totalCandidates - totalPresent - totalAbsent)
                }
            };

            return Results.Ok(data);
        })
        .WithTags("Display")
        .WithName("GetDisplayData")
        .WithSummary("Get full display state")
        .WithDescription("Returns gate config, active heat, finish reads, and attendance data for display initialization.");
    }

    private static readonly HashSet<string> AbbrevFiller =
        new(StringComparer.OrdinalIgnoreCase) { "group", "batch", "heat", "grp", "the", "of", "team" };

    /// <summary>
    /// Derive a short, uppercased group code for the per-group display label ("H{n} · {code}").
    /// Multi-word names become initials ("Red Bravo" → "RB"); a single significant word becomes its
    /// first three letters ("Group Alpha" → "ALP", after dropping the filler word "Group"). When no
    /// group name resolves, falls back to the source HHT code (e.g. "HHT-02") so two HHTs that each
    /// number their heats from 1 stay distinguishable; failing that, "G{heatNumber}".
    /// </summary>
    private static string Abbreviate(string? groupName, string? deviceCode, int heatNumber)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            return !string.IsNullOrWhiteSpace(deviceCode)
                ? deviceCode.Trim().ToUpperInvariant()
                : "G" + heatNumber;

        var words = groupName
            .Split([' ', '-', '_', '/', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Any(char.IsLetter) && !AbbrevFiller.Contains(w))
            .ToList();

        // Name was only filler/numbers (e.g. "Group 2") — fall back to the first letters of the raw name.
        if (words.Count == 0)
        {
            var letters = new string([.. groupName.Where(char.IsLetterOrDigit)]);
            return (letters.Length == 0 ? "G" + heatNumber : letters[..Math.Min(3, letters.Length)]).ToUpperInvariant();
        }

        string abbr = words.Count >= 2
            ? new string([.. words.Take(4).Select(w => w[0])])      // initials: "Red Bravo" → "RB"
            : words[0][..Math.Min(3, words[0].Length)];             // first 3:  "Alpha"     → "ALP"

        return abbr.ToUpperInvariant();
    }
}
