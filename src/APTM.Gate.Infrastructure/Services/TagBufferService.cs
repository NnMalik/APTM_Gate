using APTM.Gate.Core.Interfaces;
using APTM.Gate.Core.Models;
using APTM.Gate.Infrastructure.Entities;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace APTM.Gate.Infrastructure.Services;

public sealed class TagBufferService : ITagBufferService
{
    private readonly GateDbContext _db;
    private readonly IEpcFilterProvider _epcFilter;
    private readonly ILogger<TagBufferService> _logger;

    public TagBufferService(
        GateDbContext db,
        IEpcFilterProvider epcFilter,
        ILogger<TagBufferService> logger)
    {
        _db = db;
        _epcFilter = epcFilter;
        _logger = logger;
    }

    public async Task InsertRawTagsAsync(IReadOnlyList<RawTagFrame> frames, CancellationToken ct = default)
    {
        // Stamp every read with the gate's currently-active event. This lets the buffer
        // processor attribute a read — and compute elapsed time against the correct
        // event's gun — even when the active event is switched before the read is
        // processed. NULL when no config is loaded yet: the read is still stored, it's
        // just processed against the active event at process time once config arrives.
        var activeEventId = await _db.GateConfigs
            .Where(g => g.IsActive)
            .Select(g => g.ActiveEventId)
            .FirstOrDefaultAsync(ct);

        // EPC range filter enforcement. This is the single ingestion choke point for
        // every reader role, so applying the filter here keeps raw_tag_buffer (and the
        // whole downstream pipeline) clean: reads whose EPC is outside the configured
        // range are dropped before they are ever persisted. When the filter is disabled
        // or unconfigured, IsAccepted returns true for everything.
        var entities = new List<RawTagBuffer>(frames.Count);
        var rejected = 0;

        foreach (var f in frames)
        {
            if (!_epcFilter.IsAccepted(f.TagEPC))
            {
                rejected++;
                continue;
            }

            entities.Add(new RawTagBuffer
            {
                TagEPC = f.TagEPC,
                ReadTime = f.ReadTime,
                AntennaPort = f.AntennaPort,
                RSSI = f.RSSI.HasValue ? (decimal)f.RSSI.Value : null,
                Status = "PENDING",
                IsDuplicate = false,
                InsertedAt = DateTimeOffset.UtcNow,
                EventId = activeEventId
            });
        }

        if (rejected > 0)
            _logger.LogDebug("EPC filter rejected {Rejected} of {Total} reads (out of range)", rejected, frames.Count);

        if (entities.Count == 0)
            return;

        _db.RawTagBuffers.AddRange(entities);
        await _db.SaveChangesAsync(ct);
    }
}
