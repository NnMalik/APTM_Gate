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
    }
}
