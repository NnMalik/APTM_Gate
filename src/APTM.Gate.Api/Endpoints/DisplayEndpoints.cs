using APTM.Gate.Api.Services;
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

        app.MapGet("/gate/display-data", async (GateDbContext db, CancellationToken ct) =>
        {
            var gateConfig = await db.GateConfigs
                .Where(g => g.IsActive)
                .FirstOrDefaultAsync(ct);

            if (gateConfig is null)
                return Results.Ok(new { gateRole = "unconfigured" });

            var activeEventId = gateConfig.ActiveEventId;
            var totalCandidates = await db.Candidates.CountAsync(ct);

            // Get latest race start for active heat info
            var latestRaceStart = await db.RaceStartTimes
                .OrderByDescending(r => r.ReceivedAt)
                .FirstOrDefaultAsync(ct);

            ActiveHeatData? activeHeat = null;
            if (latestRaceStart is not null)
            {
                var heatCandidates = await db.Candidates
                    .Where(c => latestRaceStart.CandidateIds.Contains(c.CandidateId))
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
                    Candidates = heatCandidates
                };
            }

            // Get finish reads for current event only
            var finishReads = await db.ProcessedEvents
                .Where(pe => pe.EventType == "finish" && pe.IsFirstRead)
                .Where(pe => activeEventId == null || pe.EventId == activeEventId)
                .OrderBy(pe => pe.ReadTime)
                .Join(db.Candidates,
                    pe => pe.CandidateId,
                    c => c.CandidateId,
                    (pe, c) => new { pe, c })
                .Select(x => new FinishReadData
                {
                    Position = 0,
                    CandidateId = x.pe.CandidateId,
                    Name = x.c.Name,
                    JacketNumber = x.c.JacketNumber,
                    ReadTime = x.pe.ReadTime,
                    ElapsedSeconds = x.pe.DurationSeconds
                })
                .ToListAsync(ct);

            // Number the positions
            for (int i = 0; i < finishReads.Count; i++)
                finishReads[i].Position = i + 1;

            // Start/attendance reads for current event
            var startReads = await db.ProcessedEvents
                .Where(pe => pe.EventType == "start_attendance" && pe.IsFirstRead)
                .Where(pe => activeEventId == null || pe.EventId == activeEventId)
                .OrderByDescending(pe => pe.ReadTime)
                .Join(db.Candidates,
                    pe => pe.CandidateId,
                    c => c.CandidateId,
                    (pe, c) => new StartReadData
                    {
                        CandidateId = pe.CandidateId,
                        Name = c.Name,
                        JacketNumber = c.JacketNumber,
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
                ActiveEventId = gateConfig.ActiveEventId,
                ActiveEventName = gateConfig.ActiveEventName,
                TestInstanceName = gateConfig.TestInstanceName,
                ScheduledDate = gateConfig.ScheduledDate.ToString("yyyy-MM-dd"),
                TotalCandidates = totalCandidates,
                ActiveHeat = activeHeat,
                FinishReads = finishReads,
                StartReads = startReads,
                Attendance = new AttendanceData
                {
                    TotalPresent = totalPresent,
                    TotalAbsent = totalCandidates - totalPresent,
                    TotalNotScanned = 0
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
