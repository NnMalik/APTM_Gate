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

            // Total groups/batches that have started (distinct heats received via race_start push)
            var totalGroups = await db.RaceStartTimes
                .Select(r => r.HeatNumber)
                .Distinct()
                .CountAsync(ct);

            // Get latest race start for active heat info
            var latestRaceStart = await db.RaceStartTimes
                .OrderByDescending(r => r.ReceivedAt)
                .FirstOrDefaultAsync(ct);

            ActiveHeatData? activeHeat = null;
            if (latestRaceStart is not null)
            {
                var candidateIds = latestRaceStart.CandidateIds ?? [];
                var heatCandidates = await db.Candidates
                    .Where(c => candidateIds.Contains(c.CandidateId))
                    .Select(c => new HeatCandidateData
                    {
                        CandidateId = c.CandidateId,
                        Name = c.Name,
                        JacketNumber = c.JacketNumber
                    })
                    .ToListAsync(ct);

                // Has this heat already been marked complete? (auto by finish gate, or
                // force-closed by field app for DNF). If yes, the display should freeze.
                var completion = await db.HeatCompletions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(hc => hc.HeatId == latestRaceStart.HeatId, ct);

                // Live progress count (only meaningful while the heat is in progress; harmless when complete).
                var roster = candidateIds;
                var finishedCount = roster.Length == 0
                    ? 0
                    : await db.ProcessedEvents
                        .Where(pe => pe.EventType == "finish"
                                  && pe.IsFirstRead
                                  && pe.HeatNumber == latestRaceStart.HeatNumber
                                  && pe.CandidateId != null
                                  && roster.Contains(pe.CandidateId.Value))
                        .Select(pe => pe.CandidateId)
                        .Distinct()
                        .CountAsync(ct);

                activeHeat = new ActiveHeatData
                {
                    HeatId = latestRaceStart.HeatId,
                    HeatNumber = latestRaceStart.HeatNumber,
                    HasStartTime = true,
                    GunStartTime = latestRaceStart.GunStartTime,
                    OriginalGunStartTime = latestRaceStart.OriginalGunStartTime,
                    Candidates = heatCandidates,
                    ExpectedCount = roster.Length,
                    FinishedCount = completion?.FinishedCount ?? finishedCount,
                    CompletedAt = completion?.CompletedAt,
                    ClosureReason = completion?.ClosureReason
                };
            }

            // Get finish reads for current event only (reads from denormalized columns — no JOIN).
            // Filter out null CandidateId (only checkpoint events have null; finish always resolves).
            var finishReads = await db.ProcessedEvents
                .Where(pe => pe.EventType == "finish" && pe.IsFirstRead && pe.CandidateId != null)
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

            // Attendance count: gate's own processed reads + synced attendance from HHTs
            var gateAttendanceCount = startReads.Count;
            var syncedAttendanceCount = await db.ReceivedSyncData
                .CountAsync(r => r.DataType == "attendance", ct);
            var totalPresent = gateAttendanceCount + syncedAttendanceCount;

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
                ActiveHeat = activeHeat,
                FinishReads = finishReads,
                StartReads = startReads,
                Attendance = new AttendanceData
                {
                    TotalPresent = totalPresent,
                    TotalAbsent = 0,  // Only set when candidates are explicitly marked absent
                    TotalNotScanned = totalCandidates - totalPresent
                }
            };

            return Results.Ok(data);
        })
        .WithTags("Display")
        .WithName("GetDisplayData")
        .WithSummary("Get full display state")
        .WithDescription("Returns gate config, active heat, finish reads, and attendance data for display initialization.");
    }
}
