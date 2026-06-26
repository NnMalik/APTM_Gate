using APTM.Gate.Api.Services;
using APTM.Gate.Core.Enums;
using APTM.Gate.Core.Interfaces;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace APTM.Gate.Api.Endpoints;

/// <summary>
/// Lightweight clock-sampling endpoint. The field app hits this to measure the per-gate clock
/// offset (NUC vs tablet) WITHOUT pulling data — important for remote checkpoint NUCs whose
/// reads carry only tag + read_time. Pairing the request/response timestamps with serverTimeUtc
/// lets the tablet estimate offset and round-trip latency before reconciling reads into events.
/// </summary>
public static class TimeEndpoints
{
    public static void MapTimeEndpoints(this IEndpointRouteBuilder app)
    {
        // No auth — exposes only the wall-clock, and the field app needs it before provisioning.
        app.MapGet("/gate/time", (IConfiguration config) =>
        {
            var now = DateTimeOffset.UtcNow;
            return Results.Ok(new
            {
                serverTimeUtc = now,
                serverTimeUnixMs = now.ToUnixTimeMilliseconds(),
                deviceCode = config["Gate:DeviceCode"] ?? ""
            });
        })
        .WithTags("Time")
        .WithName("GetGateTime")
        .WithSummary("Current NUC wall-clock for clock-offset measurement")
        .WithDescription(
            "Returns the gate NUC's current UTC time. The field app records its own clock at send " +
            "and receive, then uses serverTimeUtc to compute the per-gate clock offset and round-trip " +
            "latency. No auth required.");

        // Set the NUC clock from the tablet. Authenticated + provisioned (a mutation).
        //
        // Race-safety: setting the clock mid-measurement would corrupt elapsed times
        // (finish − gun spans the jump). Start/Finish gates KNOW their heats, so we hard-block
        // when one is active (a race start with no matching completion). Checkpoint gates have
        // no heat awareness — the Field app gates that path with an operator confirmation
        // instead, so we don't block here.
        app.MapPost("/gate/time", async (
            SetGateTimeRequest request,
            IGateIdentityProvider identityProvider,
            ISystemControlService systemControl,
            GateDbContext db,
            CancellationToken ct) =>
        {
            var identity = identityProvider.Current;
            if (identity is null)
                return Results.Problem(statusCode: 503, title: "Gate not provisioned");

            Enum.TryParse<GateRole>(identity.Role, out var role);
            var isStartOrFinish = role == GateRole.Start || role == GateRole.Finish;

            if (isStartOrFinish && !request.Force)
            {
                // Active heat = a pushed race start with no completion (not finished, not cancelled).
                var heatActive = await db.RaceStartTimes
                    .AnyAsync(rs => !db.HeatCompletions.Any(hc => hc.HeatId == rs.HeatId), ct);

                if (heatActive)
                    return Results.Conflict(new
                    {
                        error = "A heat is currently running. Set the clock between tests, or pass force=true."
                    });
            }

            var utc = DateTimeOffset.FromUnixTimeMilliseconds(request.ServerTimeUnixMs);
            var ok = await systemControl.SetSystemTimeAsync(utc, ct);
            if (!ok)
                return Results.Problem(statusCode: 500, title: "Failed to set system clock",
                    detail: "timedatectl returned a non-zero exit. Check the gate logs.");

            var now = DateTimeOffset.UtcNow;
            return Results.Ok(new
            {
                applied = true,
                serverTimeUtc = now,
                serverTimeUnixMs = now.ToUnixTimeMilliseconds()
            });
        })
        .RequireAuthorization()
        .RequireProvisioned()
        .WithTags("Time")
        .WithName("SetGateTime")
        .WithSummary("Set the NUC wall-clock from the tablet")
        .WithDescription(
            "Sets the gate NUC's system clock to the supplied absolute instant (epoch ms). Disables " +
            "NTP first. Start/Finish gates refuse while a heat is running (pass force=true to override); " +
            "checkpoint gates rely on a Field-side confirmation instead.");
    }
}

public sealed record SetGateTimeRequest(long ServerTimeUnixMs, bool Force = false);
