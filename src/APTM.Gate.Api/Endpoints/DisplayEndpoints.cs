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

        app.MapGet("/gate/display-data", async (GateDbContext db, IReaderStatusProvider readerStatus, IGateStatusProvider gateStatus, CancellationToken ct) =>
        {
            var gateConfig = await db.GateConfigs
                .Where(g => g.IsActive)
                .FirstOrDefaultAsync(ct);

            if (gateConfig is null)
                return Results.Ok(new { gateRole = "unconfigured" });

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

                activeHeat = new ActiveHeatData
                {
                    HeatNumber = latestRaceStart.HeatNumber,
                    HasStartTime = true,
                    GunStartTime = latestRaceStart.GunStartTime,
                    OriginalGunStartTime = latestRaceStart.OriginalGunStartTime,
                    Candidates = heatCandidates
                };
            }

            // Get finish reads for current event only (reads from denormalized columns — no JOIN)
            var finishReads = await db.ProcessedEvents
                .Where(pe => pe.EventType == "finish" && pe.IsFirstRead)
                .Where(pe => activeEventId == null || pe.EventId == activeEventId)
                .OrderBy(pe => pe.ReadTime)
                .Select(pe => new FinishReadData
                {
                    Position = 0,
                    CandidateId = pe.CandidateId,
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

            // Start/attendance reads for current event (reads from denormalized columns — no JOIN)
            var startReads = await db.ProcessedEvents
                .Where(pe => pe.EventType == "start_attendance" && pe.IsFirstRead)
                .Where(pe => activeEventId == null || pe.EventId == activeEventId)
                .OrderByDescending(pe => pe.ReadTime)
                .Select(pe => new StartReadData
                {
                    CandidateId = pe.CandidateId,
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
                GateRole = gateConfig.GateRole,
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
