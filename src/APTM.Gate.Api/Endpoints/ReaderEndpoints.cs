using APTM.Gate.Core.Interfaces;

namespace APTM.Gate.Api.Endpoints;

public static class ReaderEndpoints
{
    public static void MapReaderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/gate/reader")
            .RequireAuthorization()
            .WithTags("Reader");

        // --- Existing endpoints ---

        group.MapGet("/status", (IReaderStatusProvider reader) =>
        {
            return Results.Ok(new
            {
                reader.IsConnected,
                reader.ReaderId,
                reader.ReaderModel,
                reader.FirmwareVersion,
                reader.AntennaCount,
                LastReadAt = reader.LastReadAt?.ToString("o")
            });
        })
        .WithName("ReaderStatus")
        .WithSummary("Get reader connection status and metadata");

        group.MapPost("/power/{value:int}", async (int value, IReaderStatusProvider reader, CancellationToken ct) =>
        {
            if (value < 0 || value > 30)
                return Results.BadRequest("Power must be between 0 and 30");

            bool success = await reader.SetPowerAsync((byte)value, ct);
            return success
                ? Results.Ok(new { Message = $"Power set to {value}" })
                : Results.StatusCode(502);
        })
        .WithName("SetPower")
        .WithSummary("Set RF power (0-30 dBm)");

        group.MapPost("/reset", async (IReaderStatusProvider reader, CancellationToken ct) =>
        {
            bool success = await reader.ResetReaderAsync(ct);
            return success
                ? Results.Ok(new { Message = "Reset command sent. Reader is rebooting." })
                : Results.StatusCode(502);
        })
        .WithName("ResetReader")
        .WithSummary("Reboot the reader hardware");

        group.MapGet("/antenna-check/{port:int}", async (int port, IReaderStatusProvider reader, CancellationToken ct) =>
        {
            if (port < 0 || port > 7)
                return Results.BadRequest("Port must be between 0 and 7");

            string result = await reader.CheckAntennaHealthAsync((byte)port, ct);
            return Results.Ok(new { Result = result });
        })
        .WithName("CheckAntenna")
        .WithSummary("Check antenna health by port (0-7)");

        // --- New endpoints (ported from old UHFReaderService) ---

        group.MapGet("/info", async (IReaderStatusProvider reader, CancellationToken ct) =>
        {
            var info = await reader.GetReaderInfoAsync(ct);
            if (info is null) return Results.StatusCode(502);

            return Results.Ok(new
            {
                Version = info.Value.Version,
                Type = info.Value.Type,
                Power = info.Value.Power
            });
        })
        .WithName("ReaderInfo")
        .WithSummary("Get reader firmware version, type, and current power");

        group.MapGet("/mode", async (IReaderStatusProvider reader, CancellationToken ct) =>
        {
            var mode = await reader.GetReaderModeAsync(ct);
            return Results.Ok(new { Mode = mode });
        })
        .WithName("GetMode")
        .WithSummary("Get current working mode (Answering, Real-Time, Trigger)");

        group.MapPost("/mode/{mode}", async (string mode, IReaderStatusProvider reader, CancellationToken ct) =>
        {
            byte modeValue = mode.ToLower() switch
            {
                "answer" or "answering" => 0x00,
                "realtime" or "real-time" => 0x01,
                "trigger" => 0x02,
                _ => 0xFF
            };

            if (modeValue == 0xFF)
                return Results.BadRequest("Mode must be 'realtime', 'answer', or 'trigger'");

            bool success = await reader.SetModeAsync(modeValue, ct);
            return success
                ? Results.Ok(new { Message = $"Mode set to {mode}" })
                : Results.StatusCode(502);
        })
        .WithName("SetMode")
        .WithSummary("Switch working mode: realtime, answer, or trigger");

        group.MapGet("/antenna-powers", async (IReaderStatusProvider reader, CancellationToken ct) =>
        {
            var powers = await reader.GetAntennaPowersAsync(ct);
            if (powers is null) return Results.StatusCode(502);

            var result = powers.Select((p, i) => new { Port = i, Power = (int)p }).ToList();
            return Results.Ok(new { Powers = result });
        })
        .WithName("GetAntennaPowers")
        .WithSummary("Get power level per antenna port");

        group.MapGet("/antenna-config", async (IReaderStatusProvider reader, CancellationToken ct) =>
        {
            var config = await reader.GetAntennaConfigAsync(ct);
            if (config is null) return Results.StatusCode(502);

            return Results.Ok(new
            {
                ConnectedCount = config.Value.ConnectedCount,
                AntennaBitmask = config.Value.AntennaBitmask
            });
        })
        .WithName("GetAntennaConfig")
        .WithSummary("Get connected antenna count and bitmask");

        group.MapPost("/buzzer", async (BuzzerRequest request, IReaderStatusProvider reader, CancellationToken ct) =>
        {
            bool success = await reader.ControlBuzzerAsync(
                request.ActiveDuration, request.SilentDuration, request.Times, ct);
            return success
                ? Results.Ok(new { Message = $"Buzzer activated ({request.Times} times)" })
                : Results.StatusCode(502);
        })
        .WithName("ControlBuzzer")
        .WithSummary("Trigger reader buzzer/LED feedback");

        group.MapPost("/filter/epc/{bits:int}", async (int bits, IReaderStatusProvider reader, CancellationToken ct) =>
        {
            if (bits < 0 || bits > 128)
                return Results.BadRequest("EPC filter bits must be between 0 and 128");

            bool success = await reader.SetEpcFilterAsync((byte)bits, ct);
            return success
                ? Results.Ok(new { Message = $"EPC filter set to {bits} bits" })
                : Results.StatusCode(502);
        })
        .WithName("SetEpcFilter")
        .WithSummary("Set hardware EPC filter by bit length (e.g. 16 for 4-digit tags)");

        group.MapPost("/filter/disable", async (IReaderStatusProvider reader, CancellationToken ct) =>
        {
            bool success = await reader.DisableFilterAsync(ct);
            return success
                ? Results.Ok(new { Message = "Hardware filter disabled" })
                : Results.StatusCode(502);
        })
        .WithName("DisableFilter")
        .WithSummary("Remove hardware EPC filter");

        group.MapPost("/duplicate-filter/{value:int}", async (int value, IReaderStatusProvider reader, CancellationToken ct) =>
        {
            if (value < 0 || value > 255)
                return Results.BadRequest("Value must be between 0 and 255 (in 100ms units)");

            bool success = await reader.SetDuplicateFilterTimeAsync((byte)value, ct);
            return success
                ? Results.Ok(new { Message = $"Duplicate filter set to {value * 100}ms" })
                : Results.StatusCode(502);
        })
        .WithName("SetDuplicateFilter")
        .WithSummary("Set duplicate filtering time (value in 100ms units, e.g. 20 = 2 seconds)");

        group.MapGet("/duplicate-filter", async (IReaderStatusProvider reader, CancellationToken ct) =>
        {
            var value = await reader.GetDuplicateFilterTimeAsync(ct);
            if (value is null) return Results.StatusCode(502);

            return Results.Ok(new
            {
                Value = (int)value.Value,
                TimeMs = value.Value * 100
            });
        })
        .WithName("GetDuplicateFilter")
        .WithSummary("Get current duplicate filtering time");
        group.MapPost("/disconnect", async (IReaderStatusProvider reader, CancellationToken ct) =>
        {
            bool success = await reader.DisconnectReaderAsync(ct);
            return success
                ? Results.Ok(new { Message = "Reader disconnected" })
                : Results.StatusCode(502);
        })
        .WithName("DisconnectReader")
        .WithSummary("Manually disconnect from the UHF reader");

        group.MapPost("/connect", async (IReaderStatusProvider reader, CancellationToken ct) =>
        {
            if (reader.IsConnected)
                return Results.Ok(new { Message = "Reader already connected", Connected = true });

            bool connected = await reader.ReconnectReaderAsync(ct);
            return connected
                ? Results.Ok(new { Message = "Reader connected successfully", Connected = true })
                : Results.Ok(new { Message = "Reader did not connect. Check reader power and network.", Connected = false });
        })
        .WithName("ConnectReader")
        .WithSummary("Connect to the UHF reader (waits up to 10s for connection)");
    }
}

public record BuzzerRequest(byte ActiveDuration = 5, byte SilentDuration = 5, byte Times = 3);
