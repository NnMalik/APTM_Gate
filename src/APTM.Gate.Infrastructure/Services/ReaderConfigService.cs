using APTM.Gate.Core.Interfaces;
using APTM.Gate.Core.Models;
using APTM.Gate.Infrastructure.Entities;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace APTM.Gate.Infrastructure.Services;

public sealed class ReaderConfigService : IReaderConfigService
{
    private readonly GateDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly IReaderConfigProvider _provider;

    public ReaderConfigService(GateDbContext db, IConfiguration configuration, IReaderConfigProvider provider)
    {
        _db = db;
        _configuration = configuration;
        _provider = provider;
    }

    public async Task<ReaderSettingsInfo> GetAsync(CancellationToken ct = default)
    {
        var row = await _db.ReaderConfigs.AsNoTracking().FirstOrDefaultAsync(ct);
        if (row is null)
        {
            // Fallback to appsettings — same defaults the old TcpReaderWorker constructor used.
            return new ReaderSettingsInfo
            {
                Host = _configuration["Reader:Host"] ?? "127.0.0.1",
                Port = int.TryParse(_configuration["Reader:Port"], out var port) ? port : 27011,
                DefaultPower = int.TryParse(_configuration["Reader:DefaultPower"], out var power) ? power : 20,
                EpcFilterBits = int.TryParse(_configuration["Reader:EpcFilterBits"], out var bits) ? bits : 0,
                ReconnectDelayMs = int.TryParse(_configuration["Reader:ReconnectDelayMs"], out var delay) ? delay : 5000,
                Source = "config",
                UpdatedAt = null,
                UpdatedBy = null
            };
        }

        return ToInfo(row);
    }

    public async Task<SetReaderSettingsResult> SetAsync(UpdateReaderSettingsRequest request, string updatedBy, CancellationToken ct = default)
    {
        // Validate
        if (string.IsNullOrWhiteSpace(request.Host))
            return SetReaderSettingsResult.Fail("Host is required.");
        if (request.Port <= 0 || request.Port > 65535)
            return SetReaderSettingsResult.Fail("Port must be 1–65535.");

        // Pull the existing row (or appsettings fallback) so optional fields are preserved when omitted.
        var existing = await _db.ReaderConfigs.FirstOrDefaultAsync(ct);
        var fallback = await GetAsync(ct);

        var defaultPower = request.DefaultPower ?? existing?.DefaultPower ?? fallback.DefaultPower;
        var epcFilterBits = request.EpcFilterBits ?? existing?.EpcFilterBits ?? fallback.EpcFilterBits;
        var reconnectDelayMs = request.ReconnectDelayMs ?? existing?.ReconnectDelayMs ?? fallback.ReconnectDelayMs;

        if (defaultPower < 0 || defaultPower > 30)
            return SetReaderSettingsResult.Fail("DefaultPower must be 0–30 dBm.");
        if (epcFilterBits < 0 || epcFilterBits > 128)
            return SetReaderSettingsResult.Fail("EpcFilterBits must be 0–128.");
        if (reconnectDelayMs < 500 || reconnectDelayMs > 60000)
            return SetReaderSettingsResult.Fail("ReconnectDelayMs must be 500–60000.");

        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            _db.ReaderConfigs.Add(new ReaderConfigEntity
            {
                Id = 1,
                Host = request.Host.Trim(),
                Port = request.Port,
                DefaultPower = defaultPower,
                EpcFilterBits = epcFilterBits,
                ReconnectDelayMs = reconnectDelayMs,
                UpdatedAt = now,
                UpdatedBy = updatedBy
            });
        }
        else
        {
            existing.Host = request.Host.Trim();
            existing.Port = request.Port;
            existing.DefaultPower = defaultPower;
            existing.EpcFilterBits = epcFilterBits;
            existing.ReconnectDelayMs = reconnectDelayMs;
            existing.UpdatedAt = now;
            existing.UpdatedBy = updatedBy;
        }

        await _db.SaveChangesAsync(ct);

        // Invalidate cache so the next reconnect picks up the new values.
        _provider.Invalidate();

        var info = await GetAsync(ct);
        return SetReaderSettingsResult.Ok(info);
    }

    private static ReaderSettingsInfo ToInfo(ReaderConfigEntity row) => new()
    {
        Host = row.Host,
        Port = row.Port,
        DefaultPower = row.DefaultPower,
        EpcFilterBits = row.EpcFilterBits,
        ReconnectDelayMs = row.ReconnectDelayMs,
        Source = "db",
        UpdatedAt = row.UpdatedAt,
        UpdatedBy = row.UpdatedBy
    };
}
