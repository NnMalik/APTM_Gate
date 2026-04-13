using System.Text.Json;
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
                    pe.EventId,
                    pe.ReadTime,
                    pe.DurationSeconds,
                    pe.CheckpointSequence,
                    pe.IsFirstRead,
                    candidateName = pe.CandidateName,
                    jacketNumber = pe.JacketNumber,
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
        // Device code management (no auth -- used during initial Field app setup)
        app.MapGet("/gate/device-code", (IConfiguration config) =>
        {
            return Results.Ok(new
            {
                deviceCode = config["Gate:DeviceCode"] ?? "gate-01"
            });
        })
        .WithTags("Config")
        .WithName("GetDeviceCode")
        .WithSummary("Get current gate device code");

        app.MapPut("/gate/device-code", (DeviceCodeRequest request, IConfiguration config, IWebHostEnvironment env) =>
        {
            if (string.IsNullOrWhiteSpace(request.DeviceCode))
                return Results.BadRequest(new { error = "DeviceCode cannot be empty" });

            // Update the production config file on disk
            var configPath = Path.Combine(env.ContentRootPath, "appsettings.Production.json");

            try
            {
                JsonDocument doc;
                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    doc = JsonDocument.Parse(json);
                }
                else
                {
                    doc = JsonDocument.Parse("{}");
                }

                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    var wrote = false;

                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Name == "Gate")
                        {
                            writer.WriteStartObject("Gate");
                            foreach (var gateProp in prop.Value.EnumerateObject())
                            {
                                if (gateProp.Name == "DeviceCode")
                                    writer.WriteString("DeviceCode", request.DeviceCode);
                                else
                                    gateProp.WriteTo(writer);
                            }
                            // If DeviceCode didn't exist in Gate section
                            if (!prop.Value.TryGetProperty("DeviceCode", out _))
                                writer.WriteString("DeviceCode", request.DeviceCode);
                            writer.WriteEndObject();
                            wrote = true;
                        }
                        else
                        {
                            prop.WriteTo(writer);
                        }
                    }

                    if (!wrote)
                    {
                        writer.WriteStartObject("Gate");
                        writer.WriteString("DeviceCode", request.DeviceCode);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndObject();
                }

                File.WriteAllBytes(configPath, ms.ToArray());
                doc.Dispose();

                return Results.Ok(new
                {
                    deviceCode = request.DeviceCode,
                    message = "DeviceCode updated. Restart the service for changes to take full effect.",
                    restartRequired = true
                });
            }
            catch
            {
                return Results.StatusCode(500);
            }
        })
        .WithTags("Config")
        .WithName("SetDeviceCode")
        .WithSummary("Update gate device code")
        .WithDescription("Updates the DeviceCode in appsettings.Production.json. A service restart is required for the auth handler to pick up the new value.");
    }
}

public record StatusRequest(string Status);
public record DeviceCodeRequest(string DeviceCode);
