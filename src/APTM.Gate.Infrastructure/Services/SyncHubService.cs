using System.Text.Json;
using APTM.Gate.Core.Interfaces;
using APTM.Gate.Core.Models;
using APTM.Gate.Infrastructure.Entities;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace APTM.Gate.Infrastructure.Services;

public sealed class SyncHubService : ISyncHubService
{
    private readonly GateDbContext _db;

    public SyncHubService(GateDbContext db) => _db = db;

    public async Task<SyncPushResult> PushAsync(SyncPushPayload payload, CancellationToken ct = default)
    {
        // Dedup by clientRecordId
        var exists = await _db.ReceivedSyncData
            .AnyAsync(r => r.ClientRecordId == payload.ClientRecordId, ct);

        if (exists)
            return SyncPushResult.Duplicate(payload.ClientRecordId);

        // If race_start, also insert into race_start_times
        if (string.Equals(payload.DataType, "race_start", StringComparison.OrdinalIgnoreCase))
        {
            var racePayload = payload.Payload.Deserialize<RaceStartPayload>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (racePayload is not null)
            {
                var existsHeat = await _db.RaceStartTimes
                    .AnyAsync(r => r.HeatId == racePayload.HeatId, ct);

                if (!existsHeat)
                {
                    // Handle clock differences between HHT and gate:
                    // - If HHT clock is ahead: gun start is in the future for gate
                    //   → clamp to gate's current time so the timer starts immediately.
                    // - If HHT clock is behind: gun start is in the past for gate
                    //   → use HHT's timestamp (elapsed is slightly inflated but positive).
                    // - If clocks are synced: HHT's timestamp is accurate.
                    var now = DateTimeOffset.UtcNow;
                    var effectiveGunStart = racePayload.GunStartTime > now
                        ? now   // HHT clock is ahead — use gate's time to prevent negative elapsed
                        : racePayload.GunStartTime;

                    _db.RaceStartTimes.Add(new RaceStartTime
                    {
                        Id = Guid.NewGuid(),
                        HeatId = racePayload.HeatId,
                        HeatNumber = (int)racePayload.HeatNumber,
                        GunStartTime = effectiveGunStart,
                        SourceDeviceId = payload.DeviceId,
                        CandidateIds = racePayload.Candidates?
                            .Select(c => c.CandidateId).ToArray() ?? [],
                        SourceClockOffsetMs = (int)racePayload.SourceClockOffsetMs,
                        ReceivedAt = now
                    });
                }
            }
        }

        // Insert received sync data
        _db.ReceivedSyncData.Add(new ReceivedSyncData
        {
            Id = Guid.NewGuid(),
            ClientRecordId = payload.ClientRecordId,
            SourceDeviceId = payload.DeviceId,
            SourceDeviceCode = payload.DeviceCode,
            DataType = payload.DataType,
            Payload = JsonDocument.Parse(payload.Payload.GetRawText()),
            ReceivedAt = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(ct);
        return SyncPushResult.Ok(payload.ClientRecordId);
    }

    public async Task<SyncPullResponse> PullAsync(
        Guid pullerDeviceId, string pullerDeviceCode,
        long sinceEventId, long? sinceSyncMs = null, CancellationToken ct = default)
    {
        // Read candidate info directly from the denormalized columns on ProcessedEvent
        // (no JOIN to Candidates — survives candidate table truncation across config pushes)
        var events = await _db.ProcessedEvents
            .AsNoTracking()
            .Where(pe => pe.Id > sinceEventId)
            .OrderBy(pe => pe.Id)
            .Select(pe => new ProcessedEventDto
            {
                Id = pe.Id,
                CandidateId = pe.CandidateId,
                TagEpc = pe.TagEPC,
                EventType = pe.EventType,
                EventId = pe.EventId,
                ReadTime = pe.ReadTime,
                DurationSeconds = pe.DurationSeconds,
                CheckpointSequence = pe.CheckpointSequence,
                IsFirstRead = pe.IsFirstRead,
                CandidateName = pe.CandidateName ?? "",
                JacketNumber = pe.JacketNumber,
                ProcessedAt = pe.ProcessedAt
            })
            .ToListAsync(ct);

        // Incremental sync data: filter by receivedAt if a since-timestamp was provided
        var syncQuery = _db.ReceivedSyncData.AsNoTracking();
        if (sinceSyncMs.HasValue && sinceSyncMs.Value > 0)
        {
            var sinceTime = DateTimeOffset.FromUnixTimeMilliseconds(sinceSyncMs.Value);
            syncQuery = syncQuery.Where(r => r.ReceivedAt > sinceTime);
        }

        var syncData = await syncQuery
            .OrderBy(r => r.ReceivedAt)
            .Select(r => new ReceivedSyncDataDto
            {
                Id = r.Id,
                ClientRecordId = r.ClientRecordId,
                SourceDeviceCode = r.SourceDeviceCode,
                DataType = r.DataType,
                Payload = r.Payload,
                ReceivedAt = r.ReceivedAt
            })
            .ToListAsync(ct);

        var raceStarts = await _db.RaceStartTimes
            .AsNoTracking()
            .OrderBy(r => r.ReceivedAt)
            .Select(r => new RaceStartTimeDto
            {
                HeatId = r.HeatId,
                HeatNumber = r.HeatNumber,
                GunStartTime = r.GunStartTime,
                SourceDeviceId = r.SourceDeviceId
            })
            .ToListAsync(ct);

        var highWaterMark = events.Count > 0 ? events.Max(e => e.Id) : sinceEventId;

        // Log the pull
        _db.SyncLogs.Add(new SyncLogEntry
        {
            Id = Guid.NewGuid(),
            PullerDeviceId = pullerDeviceId,
            PullerDeviceCode = pullerDeviceCode,
            LastProcessedEventId = highWaterMark,
            LastReceivedSyncId = syncData.Count > 0 ? syncData.Last().Id : null,
            PulledAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);

        return new SyncPullResponse
        {
            ProcessedEvents = events,
            ReceivedSyncData = syncData,
            RaceStartTimes = raceStarts,
            HighWaterMark = highWaterMark,
            SyncDataHighWaterMs = syncData.Count > 0
                ? syncData.Max(s => s.ReceivedAt).ToUnixTimeMilliseconds()
                : sinceSyncMs
        };
    }

    public async Task<SyncStatusResponse> GetStatusAsync(CancellationToken ct = default)
    {
        var gateConfig = await _db.GateConfigs
            .Where(g => g.IsActive)
            .FirstOrDefaultAsync(ct);

        var eventCount = await _db.ProcessedEvents.CountAsync(ct);
        var syncDataCount = await _db.ReceivedSyncData.CountAsync(ct);
        var raceStartCount = await _db.RaceStartTimes.CountAsync(ct);
        var lastEvent = await _db.ProcessedEvents
            .OrderByDescending(pe => pe.ProcessedAt)
            .Select(pe => (DateTimeOffset?)pe.ProcessedAt)
            .FirstOrDefaultAsync(ct);

        var syncPulls = await _db.SyncLogs
            .GroupBy(s => s.PullerDeviceCode)
            .Select(g => new SyncPullInfo
            {
                DeviceCode = g.Key,
                LastPulledAt = g.Max(s => s.PulledAt),
                EventsPulled = g.Max(s => s.LastProcessedEventId)
            })
            .ToListAsync(ct);

        return new SyncStatusResponse
        {
            DeviceCode = gateConfig?.DeviceCode ?? "",
            GateRole = gateConfig?.GateRole ?? "unconfigured",
            ActiveEventId = gateConfig?.ActiveEventId,
            ActiveEventName = gateConfig?.ActiveEventName,
            TestInstanceId = gateConfig?.TestInstanceId ?? Guid.Empty,
            ProcessedEventCount = eventCount,
            ReceivedSyncDataCount = syncDataCount,
            RaceStartTimesCount = raceStartCount,
            LastEventAt = lastEvent,
            SyncPulls = syncPulls
        };
    }
}

// Internal DTO for deserializing race_start payload.
// Gson (HHT) serialises Map<String,Any> numbers as doubles (e.g. 1.0 instead of 1),
// so numeric fields use double here and are cast to int when stored.
file sealed class RaceStartPayload
{
    public Guid HeatId { get; set; }
    public double HeatNumber { get; set; }
    public DateTimeOffset GunStartTime { get; set; }
    public double SourceClockOffsetMs { get; set; }
    public List<RaceStartCandidate>? Candidates { get; set; }
}

file sealed class RaceStartCandidate
{
    public Guid CandidateId { get; set; }
    public string? AttendanceStatus { get; set; }
}
