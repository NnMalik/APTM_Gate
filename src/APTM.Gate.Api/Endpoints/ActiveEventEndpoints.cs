using APTM.Gate.Api.Services;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace APTM.Gate.Api.Endpoints;

/// <summary>
/// Lightweight "switch active event" endpoints. Once a config package is on the gate,
/// candidates / scoring / checkpoint routes don't change between events of the same
/// test instance — only the <c>gate_config.ActiveEventId</c> does. These endpoints let
/// the field app flip that single field without re-pushing the whole package.
///
/// For "switch test instance" or "first config push", use <c>POST /gate/config</c> as
/// before — that's still the heavy path that truncates and re-inserts everything.
/// </summary>
public static class ActiveEventEndpoints
{
    public static void MapActiveEventEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/gate/active-event")
            .RequireAuthorization()
            .RequireProvisioned()  // All gate roles can have an active event.
            .WithTags("ActiveEvent");

        group.MapGet("/", async (GateDbContext db, CancellationToken ct) =>
        {
            var gateConfig = await db.GateConfigs
                .AsNoTracking()
                .Where(g => g.IsActive)
                .FirstOrDefaultAsync(ct);

            if (gateConfig is null)
            {
                return Results.NotFound(new
                {
                    error = "No active config on this gate. Push a config package first via POST /gate/config.",
                });
            }

            // Return all RACE events from the loaded config so the field app can render
            // a picker. Non-RACE event types (e.g. ground activities) wouldn't be valid
            // here — they don't run through gate readers.
            var availableEvents = await db.TestEvents
                .AsNoTracking()
                .Where(e => e.EventType.ToLower() == "race")
                .OrderBy(e => e.Sequence)
                .Select(e => new
                {
                    eventId = e.EventId,
                    eventName = e.EventName,
                    sequence = e.Sequence
                })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                testInstanceId = gateConfig.TestInstanceId,
                testInstanceName = gateConfig.TestInstanceName,
                activeEventId = gateConfig.ActiveEventId,
                activeEventName = gateConfig.ActiveEventName,
                availableEvents
            });
        })
        .WithName("GetActiveEvent")
        .WithSummary("Get the currently active event + the list of selectable RACE events")
        .WithDescription("Returns 404 when no config has been pushed yet. The 'availableEvents' list comes from the test_events table populated by the last config push.");

        group.MapPost("/", async (
            SetActiveEventRequest request,
            GateDbContext db,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var gateConfig = await db.GateConfigs.Where(g => g.IsActive).FirstOrDefaultAsync(ct);
            if (gateConfig is null)
            {
                return Results.NotFound(new
                {
                    error = "No active config on this gate. Push a config package first."
                });
            }

            // Validate the requested event exists in this gate's loaded test_events. Catches
            // the "operator typed in an event id from a different test" case.
            var testEvent = await db.TestEvents
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.EventId == request.EventId, ct);

            if (testEvent is null)
            {
                return Results.BadRequest(new
                {
                    error = $"Event {request.EventId} is not in this gate's loaded test_events. " +
                            "Re-push the config package or pick an event from GET /gate/active-event."
                });
            }

            if (!string.Equals(testEvent.EventType, "RACE", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new
                {
                    error = $"Event {request.EventId} has type '{testEvent.EventType}', not RACE — " +
                            "non-race events don't run through gate readers."
                });
            }

            var previousEventId = gateConfig.ActiveEventId;
            var previousEventName = gateConfig.ActiveEventName;

            // Idempotent same-event write — return without touching the DB.
            if (previousEventId == testEvent.EventId)
            {
                return Results.Ok(new
                {
                    activeEventId = gateConfig.ActiveEventId,
                    activeEventName = gateConfig.ActiveEventName,
                    changed = false,
                    message = "Already on this event."
                });
            }

            gateConfig.ActiveEventId = testEvent.EventId;
            gateConfig.ActiveEventName = testEvent.EventName;
            await db.SaveChangesAsync(ct);

            // NOTIFY config_updated so the local display refreshes its title bar (which
            // shows the current event) without polling.
            try
            {
                var connStr = config.GetConnectionString("GateDb");
                await using var conn = new NpgsqlConnection(connStr);
                await conn.OpenAsync(ct);
                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    activeEventId = testEvent.EventId,
                    activeEventName = testEvent.EventName
                });
                await using var cmd = new NpgsqlCommand($"NOTIFY config_updated, '{payload}'", conn);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch { /* best-effort — UI will pick up the change on next poll */ }

            return Results.Ok(new
            {
                activeEventId = testEvent.EventId,
                activeEventName = testEvent.EventName,
                previousEventId,
                previousEventName,
                changed = true
            });
        })
        .WithName("SetActiveEvent")
        .WithSummary("Switch the active event on this gate (no full config re-push)")
        .WithDescription(
            "Updates gate_config.ActiveEventId in place. Validates that the event exists in the " +
            "currently loaded test_events table and that its type is RACE. Per-race state " +
            "(processed_events, race_start_times, etc.) is NOT touched — use POST /gate/race-data/clear " +
            "first if you want a clean slate before the next event.");
    }
}

public sealed record SetActiveEventRequest(int EventId);
