using APTM.Gate.Core.Interfaces;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace APTM.Gate.Api.Endpoints;

public static class HealthEndpoints
{
    private static readonly DateTimeOffset _startTime = DateTimeOffset.UtcNow;

    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // No auth
        app.MapGet("/gate/health", async (GateDbContext db, IReaderStatusProvider readerStatus, IConfiguration config, CancellationToken ct) =>
        {
            var deviceCode = config["Gate:DeviceCode"] ?? "";

            var dbConnected = false;
            try
            {
                dbConnected = await db.Database.CanConnectAsync(ct);
            }
            catch { }

            var gateConfigured = await db.GateConfigs.AnyAsync(g => g.IsActive, ct);
            var uptime = DateTimeOffset.UtcNow - _startTime;

            return Results.Ok(new
            {
                status = dbConnected ? "healthy" : "unhealthy",
                database = dbConnected ? "connected" : "disconnected",
                readerConnected = readerStatus.IsConnected,
                gateConfigured,
                deviceCode,
                uptime = uptime.ToString(@"hh\:mm\:ss")
            });
        })
        .WithTags("Health")
        .WithName("GetHealth")
        .WithSummary("Health check")
        .WithDescription("Returns DB connectivity, reader status, gate config status, and uptime. No auth required.");
    }
}
