using APTM.Gate.Core.Interfaces;
using APTM.Gate.Core.Models;
using APTM.Gate.Infrastructure.Entities;
using APTM.Gate.Infrastructure.Persistence;

namespace APTM.Gate.Infrastructure.Services;

public sealed class TagBufferService : ITagBufferService
{
    private readonly GateDbContext _db;

    public TagBufferService(GateDbContext db) => _db = db;

    public async Task InsertRawTagsAsync(IReadOnlyList<RawTagFrame> frames, CancellationToken ct = default)
    {
        var entities = frames.Select(f => new RawTagBuffer
        {
            TagEPC = f.TagEPC,
            ReadTime = f.ReadTime,
            AntennaPort = f.AntennaPort,
            RSSI = f.RSSI.HasValue ? (decimal)f.RSSI.Value : null,
            Status = "PENDING",
            IsDuplicate = false,
            InsertedAt = DateTimeOffset.UtcNow
        });

        _db.RawTagBuffers.AddRange(entities);
        await _db.SaveChangesAsync(ct);
    }
}
