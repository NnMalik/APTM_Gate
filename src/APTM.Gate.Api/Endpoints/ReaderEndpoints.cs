using APTM.Gate.Core.Interfaces;

namespace APTM.Gate.Api.Endpoints;

public static class ReaderEndpoints
{
    public static void MapReaderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/gate/reader")
            .RequireAuthorization()
            .WithTags("Reader");

        group.MapGet("/status", (IReaderStatusProvider reader) =>
        {
            return Results.Ok(new
            {
                reader.IsConnected,
                reader.ReaderId,
                reader.ReaderModel,
                reader.FirmwareVersion,
                LastReadAt = reader.LastReadAt?.ToString("o")
            });
        });

        group.MapPost("/power/{value:int}", async (int value, IReaderStatusProvider reader, CancellationToken ct) =>
        {
            if (value < 0 || value > 30)
                return Results.BadRequest("Power must be between 0 and 30");

            bool success = await reader.SetPowerAsync((byte)value, ct);
            return success
                ? Results.Ok(new { Message = $"Power set to {value}" })
                : Results.StatusCode(502);
        });

        group.MapPost("/reset", async (IReaderStatusProvider reader, CancellationToken ct) =>
        {
            bool success = await reader.ResetReaderAsync(ct);
            return success
                ? Results.Ok(new { Message = "Reset command sent. Reader is rebooting." })
                : Results.StatusCode(502);
        });

        group.MapGet("/antenna-check/{port:int}", async (int port, IReaderStatusProvider reader, CancellationToken ct) =>
        {
            if (port < 0 || port > 3)
                return Results.BadRequest("Port must be between 0 and 3");

            string result = await reader.CheckAntennaHealthAsync((byte)port, ct);
            return Results.Ok(new { Result = result });
        });
    }
}
