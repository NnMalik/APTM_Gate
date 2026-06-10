using APTM.Gate.Core.Interfaces;
using APTM.Gate.Core.Models;
using APTM.Gate.Infrastructure.Entities;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace APTM.Gate.Infrastructure.Services;

public sealed class EpcFilterService : IEpcFilterService
{
    private readonly GateDbContext _db;
    private readonly IEpcFilterProvider _provider;

    public EpcFilterService(GateDbContext db, IEpcFilterProvider provider)
    {
        _db = db;
        _provider = provider;
    }

    public async Task<EpcFilterInfo> GetAsync(CancellationToken ct = default)
    {
        var row = await _db.EpcFilters.AsNoTracking().FirstOrDefaultAsync(ct);
        if (row is null)
        {
            // No row yet — the filter has never been configured. Report as disabled.
            return new EpcFilterInfo
            {
                Enabled = false,
                RangeStart = null,
                RangeEnd = null,
                Source = "default",
                UpdatedAt = null,
                UpdatedBy = null
            };
        }

        return ToInfo(row);
    }

    public async Task<SetEpcFilterResult> SetAsync(UpdateEpcFilterRequest request, string updatedBy, CancellationToken ct = default)
    {
        var existing = await _db.EpcFilters.FirstOrDefaultAsync(ct);

        string? rangeStart = string.IsNullOrWhiteSpace(request.RangeStart) ? null : request.RangeStart.Trim();
        string? rangeEnd = string.IsNullOrWhiteSpace(request.RangeEnd) ? null : request.RangeEnd.Trim();

        if (request.Enabled)
        {
            if (rangeStart is null || rangeEnd is null)
                return SetEpcFilterResult.Fail("RangeStart and RangeEnd are required when the filter is enabled.");

            if (!EpcFilterProvider.TryParseEpc(rangeStart, out var lower))
                return SetEpcFilterResult.Fail("RangeStart is not a valid hexadecimal EPC.");
            if (!EpcFilterProvider.TryParseEpc(rangeEnd, out var upper))
                return SetEpcFilterResult.Fail("RangeEnd is not a valid hexadecimal EPC.");
            if (lower > upper)
                return SetEpcFilterResult.Fail("RangeStart must be less than or equal to RangeEnd.");

            rangeStart = EpcFilterProvider.NormalizeEpc(rangeStart);
            rangeEnd = EpcFilterProvider.NormalizeEpc(rangeEnd);
        }
        else
        {
            // Disabled: preserve previously saved bounds when the caller omits them, so
            // toggling the filter back on later restores the last configured range.
            rangeStart ??= existing?.RangeStart;
            rangeEnd ??= existing?.RangeEnd;
            if (rangeStart is not null && EpcFilterProvider.TryParseEpc(rangeStart, out _))
                rangeStart = EpcFilterProvider.NormalizeEpc(rangeStart);
            if (rangeEnd is not null && EpcFilterProvider.TryParseEpc(rangeEnd, out _))
                rangeEnd = EpcFilterProvider.NormalizeEpc(rangeEnd);
        }

        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            _db.EpcFilters.Add(new EpcFilterEntity
            {
                Id = 1,
                Enabled = request.Enabled,
                RangeStart = rangeStart,
                RangeEnd = rangeEnd,
                UpdatedAt = now,
                UpdatedBy = updatedBy
            });
        }
        else
        {
            existing.Enabled = request.Enabled;
            existing.RangeStart = rangeStart;
            existing.RangeEnd = rangeEnd;
            existing.UpdatedAt = now;
            existing.UpdatedBy = updatedBy;
        }

        await _db.SaveChangesAsync(ct);

        // Invalidate the cache so the next ingest batch picks up the new filter.
        _provider.Invalidate();

        var info = await GetAsync(ct);
        return SetEpcFilterResult.Ok(info);
    }

    private static EpcFilterInfo ToInfo(EpcFilterEntity row) => new()
    {
        Enabled = row.Enabled,
        RangeStart = row.RangeStart,
        RangeEnd = row.RangeEnd,
        Source = "db",
        UpdatedAt = row.UpdatedAt,
        UpdatedBy = row.UpdatedBy
    };
}
