using System.Security.Claims;
using System.Text.Json;
using APTM.Gate.Core.Interfaces;
using APTM.Gate.Core.Models;
using APTM.Gate.Infrastructure.Entities;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

        // Remove the current test from THIS gate (any role — unlike /gate/race-data/clear
        // which is reader-only, so a Start gate can clear too). Wipes both the per-race
        // output AND the test/config identity (test name, candidates, events…) so the
        // display returns to an idle "no test loaded" state. Provisioning is preserved:
        // gate_identity (role), accepted_tokens (auth), reader_config and epc_filters are
        // NOT touched — the gate stays ready for the next test's config push.
        group.MapPost("/test/clear", async (
            ClearTestRequest request,
            HttpContext httpContext,
            GateDbContext db,
            IConfiguration config,
            CancellationToken ct) =>
        {
            // Safety: refuse if race output hasn't been pulled to Main yet, unless forced.
            // On a Start gate there is no local race output, so AllPulled is trivially true.
            var status = await RaceDataEndpoints.ComputeStatusAsync(db, ct);

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

            if (!request.Force && !status.AllPulled)
            {
                return Results.Conflict(new
                {
                    error = "Race data has not been fully pulled. Pull from each device first or pass force=true.",
                    status
                });
            }

            // One TRUNCATE for the whole test footprint:
            //   per-race output : processed_events, raw_tag_buffer, race_start_times,
            //                     received_sync_data, sync_log, heat_completions
            //   test identity   : gate_config (test name), candidates, tag_assignments,
            //                     test_events, checkpoint_config, scoring_statuses,
            //                     scoring_types, operator_group (+ FK cascade to its children)
            // RESTART IDENTITY resets the BIGSERIAL high-water marks back to 0.
            await db.Database.ExecuteSqlRawAsync(
                "TRUNCATE TABLE processed_events, raw_tag_buffer, race_start_times, " +
                "received_sync_data, sync_log, heat_completions, " +
                "gate_config, candidates, tag_assignments, test_events, checkpoint_config, " +
                "scoring_statuses, scoring_types, operator_group " +
                "RESTART IDENTITY CASCADE", ct);

            // Audit marker (PullerDeviceCode prefix keeps it out of the regular pull history).
            var triggeredBy = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
            db.SyncLogs.Add(new SyncLogEntry
            {
                Id = Guid.NewGuid(),
                PullerDeviceId = Guid.Empty,
                PullerDeviceCode = $"TESTCLEAR:{triggeredBy}{(request.Force ? ":FORCE" : "")}",
                LastProcessedEventId = status.MaxEventId,
                LastReceivedSyncId = null,
                PulledAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);

            // Best-effort NOTIFY so the local display drops to its idle state immediately.
            try
            {
                var connStr = config.GetConnectionString("GateDb");
                await using var conn = new NpgsqlConnection(connStr);
                await conn.OpenAsync(ct);
                var payload = JsonSerializer.Serialize(new { testCleared = true });
                await using var cmd = new NpgsqlCommand($"NOTIFY config_updated, '{payload}'", conn);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch { /* display refresh is best-effort */ }

            return Results.Ok(new
            {
                cleared = true,
                forced = request.Force,
                triggeredBy
            });
        })
        .WithName("ClearTest")
        .WithSummary("Remove the current test from this gate (test name, attendance, race data)")
        .WithDescription(
            "Wipes the per-race output AND the test/config identity so the display returns to idle. " +
            "Defensive default: returns 409 if race data hasn't been fully pulled — pass force=true to override. " +
            "Provisioning (gate_identity, accepted_tokens, reader_config, epc_filters) is preserved. " +
            "Allowed for any role, so Start gates can be cleared too.");

        group.MapGet("/status", async (ISyncHubService syncHub, CancellationToken ct) =>
        {
            var status = await syncHub.GetStatusAsync(ct);
            return Results.Ok(status);
        })
        .WithName("GetGateStatus")
        .WithSummary("Get gate status")
        .WithDescription("Returns gate role, event counts, sync pull history, and last event timestamp.");

        group.MapPut("/status", async (StatusRequest request, IGateStatusProvider statusProvider,
            IConfiguration config, CancellationToken ct) =>
        {
            var previous = statusProvider.IsActive ? "active" : "idle";
            var newStatus = string.Equals(request.Status, "active", StringComparison.OrdinalIgnoreCase);
            statusProvider.SetActive(newStatus);

            // Notify display to refresh so Processing status updates immediately
            try
            {
                var connStr = config.GetConnectionString("GateDb");
                await using var conn = new NpgsqlConnection(connStr);
                await conn.OpenAsync(ct);
                var payload = System.Text.Json.JsonSerializer.Serialize(new { processingStatus = newStatus ? "active" : "idle" });
                await using var cmd = new NpgsqlCommand($"NOTIFY config_updated, '{payload}'", conn);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch { /* display refresh is best-effort */ }

            return Results.Ok(new
            {
                previousStatus = previous,
                newStatus = newStatus ? "active" : "idle"
            });
        })
        .WithName("SetGateStatus")
        .WithSummary("Toggle gate active/idle status")
        .WithDescription("Sets the gate to active (processing tags) or idle. Signals the BufferProcessorWorker and notifies display.");

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
public sealed record ClearTestRequest(bool Force = false, long? ExpectedMaxEventId = null);
