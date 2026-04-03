using APTM.Gate.Core.Interfaces;
using APTM.Gate.Core.Models;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace APTM.Gate.Api.Endpoints;

public static class ConfigEndpoints
{
    public static void MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/gate")
            .RequireAuthorization()
            .WithTags("Config");

        group.MapPost("/config", async (ConfigPackageDto config, IGateConfigService configService, CancellationToken ct) =>
        {
            var result = await configService.ApplyConfigAsync(config, ct);

            if (!result.Success)
                return Results.BadRequest(new { error = result.Error });

            return Results.Ok(new
            {
                status = result.Status,
                gateRole = result.GateRole,
                candidateCount = result.CandidateCount
            });
        })
        .WithName("ApplyConfig")
        .WithSummary("Apply config package from APTM Main")
        .WithDescription("Receives a full ConfigPackageDto, truncates existing config tables, and inserts the new data. Computes clock offset and fires NOTIFY config_updated.");

        group.MapGet("/status", async (ISyncHubService syncHub, CancellationToken ct) =>
        {
            var status = await syncHub.GetStatusAsync(ct);
            return Results.Ok(status);
        })
        .WithName("GetGateStatus")
        .WithSummary("Get gate status")
        .WithDescription("Returns gate role, event counts, sync pull history, and last event timestamp.");

        group.MapPut("/status", (StatusRequest request, IGateStatusProvider statusProvider) =>
        {
            var previous = statusProvider.IsActive ? "active" : "idle";
            var newStatus = string.Equals(request.Status, "active", StringComparison.OrdinalIgnoreCase);
            statusProvider.SetActive(newStatus);

            return Results.Ok(new
            {
                previousStatus = previous,
                newStatus = newStatus ? "active" : "idle"
            });
        })
        .WithName("SetGateStatus")
        .WithSummary("Toggle gate active/idle status")
        .WithDescription("Sets the gate to active (processing tags) or idle. Signals the BufferProcessorWorker.");

        group.MapGet("/events", async (long? since, GateDbContext db, CancellationToken ct) =>
        {
            var sinceId = since ?? 0;

            var events = await db.ProcessedEvents
                .AsNoTracking()
                .Where(pe => pe.Id > sinceId)
                .OrderBy(pe => pe.Id)
                .Select(pe => new
                {
                    pe.Id,
                    pe.CandidateId,
                    tagEpc = pe.TagEPC,
                    pe.EventType,
                    pe.ReadTime,
                    pe.DurationSeconds,
                    pe.CheckpointSequence,
                    pe.IsFirstRead,
                    candidateName = pe.Candidate.Name,
                    jacketNumber = pe.Candidate.JacketNumber,
                    pe.ProcessedAt
                })
                .ToListAsync(ct);

            var highWaterMark = events.Count > 0 ? events.Max(e => e.Id) : sinceId;
            var totalEvents = await db.ProcessedEvents.CountAsync(ct);

            return Results.Ok(new { events, highWaterMark, totalEvents });
        })
        .WithName("GetProcessedEvents")
        .WithSummary("Get processed tag events")
        .WithDescription("Returns processed events since a given ID. Use the highWaterMark for subsequent polling.");
    }
}

public record StatusRequest(string Status);
