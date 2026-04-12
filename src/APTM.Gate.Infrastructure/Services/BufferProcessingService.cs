using APTM.Gate.Core.Interfaces;
using APTM.Gate.Infrastructure.Entities;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace APTM.Gate.Infrastructure.Services;

public sealed class BufferProcessingService : IBufferProcessingService
{
    private readonly GateDbContext _db;

    public BufferProcessingService(GateDbContext db) => _db = db;

    public async Task<int> ProcessBatchAsync(int batchSize = 100, CancellationToken ct = default)
    {
        // Load pending buffer rows
        var pendingRows = await _db.RawTagBuffers
            .Where(r => r.Status == "PENDING")
            .OrderBy(r => r.Id)
            .Take(batchSize)
            .ToListAsync(ct);

        if (pendingRows.Count == 0) return 0;

        // Load current gate config
        var gateConfig = await _db.GateConfigs
            .Where(g => g.IsActive)
            .FirstOrDefaultAsync(ct);

        if (gateConfig is null)
        {
            // No active config — mark all as UNRESOLVED
            foreach (var row in pendingRows)
                row.Status = "UNRESOLVED";
            await _db.SaveChangesAsync(ct);
            return pendingRows.Count;
        }

        // Preload tag assignments for EPC resolution
        var epcs = pendingRows.Select(r => r.TagEPC).Distinct().ToList();
        var tagMap = await _db.TagAssignments
            .Where(ta => epcs.Contains(ta.TagEPC))
            .ToDictionaryAsync(ta => ta.TagEPC, ta => ta.CandidateId, ct);

        // Determine event type from gate role
        var eventType = gateConfig.GateRole.ToLower() switch
        {
            "start" => "start_attendance",
            "checkpoint" => "checkpoint",
            "finish" => "finish",
            _ => "unknown"
        };

        // For finish gates, load all race start times for heat-candidate matching.
        // Scoped to starts received after current config was applied (prevents using
        // a gun start from a previous event when events are switched).
        List<RaceStartTime> raceStarts = [];
        if (eventType == "finish")
        {
            raceStarts = await _db.RaceStartTimes
                .Where(r => r.ReceivedAt >= gateConfig.ReceivedAt)
                .OrderByDescending(r => r.ReceivedAt)
                .ToListAsync(ct);
        }

        // Preload existing first reads for dedup (First Read Rule — per candidate per event)
        var activeEventId = gateConfig.ActiveEventId;
        var resolvedCandidateIds = tagMap.Values.Distinct().ToList();

        // Build dedup query — handle NULL activeEventId explicitly because
        // EF parameterises the value and SQL "column = NULL" is always false.
        var dedupQuery = _db.ProcessedEvents
            .Where(pe => resolvedCandidateIds.Contains(pe.CandidateId) && pe.IsFirstRead);

        dedupQuery = activeEventId.HasValue
            ? dedupQuery.Where(pe => pe.EventId == activeEventId.Value)
            : dedupQuery.Where(pe => pe.EventId == null);

        var existingReads = await dedupQuery
            .Select(pe => pe.CandidateId)
            .Distinct()
            .ToListAsync(ct);
        var existingReadSet = existingReads.ToHashSet();

        int processed = 0;

        foreach (var row in pendingRows)
        {
            if (!tagMap.TryGetValue(row.TagEPC, out var candidateId))
            {
                row.Status = "UNRESOLVED";
                processed++;
                continue;
            }

            // First Read Rule: check if this candidate already has a first read
            var isFirstRead = !existingReadSet.Contains(candidateId);

            if (!isFirstRead)
            {
                row.Status = "DUPLICATE";
                row.IsDuplicate = true;
                processed++;
                continue;
            }

            // Compute duration for finish gates with heat-candidate matching
            decimal? durationSeconds = null;
            int? heatNumber = null;
            if (eventType == "finish" && raceStarts.Count > 0)
            {
                // Match candidate to their specific heat, fall back to latest start
                var raceStart = raceStarts
                    .FirstOrDefault(r => r.CandidateIds is not null && r.CandidateIds.Contains(candidateId))
                    ?? raceStarts[0];

                // Adjust for clock drift between HHT and gate:
                // Both offsets are "server_time - local_time" in ms.
                // Convert HHT-local gun start to gate-local time frame.
                // Guard: skip adjustment if offset is unreasonable (> 24h = device never synced).
                var offsetDiffMs = (long)gateConfig.ClockOffsetMs - raceStart.SourceClockOffsetMs;
                var adjustedGunStart = Math.Abs(offsetDiffMs) <= 86_400_000
                    ? raceStart.GunStartTime.AddMilliseconds(offsetDiffMs)
                    : raceStart.GunStartTime;
                var elapsed = row.ReadTime - adjustedGunStart;
                durationSeconds = elapsed.TotalSeconds > 0 ? (decimal)elapsed.TotalSeconds : 0m;
                heatNumber = raceStart.HeatNumber;
            }

            var processedEvent = new ProcessedEvent
            {
                CandidateId = candidateId,
                TagEPC = row.TagEPC,
                EventType = eventType,
                EventId = activeEventId,
                ReadTime = row.ReadTime,
                DurationSeconds = durationSeconds,
                HeatNumber = heatNumber,
                CheckpointSequence = gateConfig.CheckpointSequence,
                IsFirstRead = true,
                RawBufferId = row.Id,
                ProcessedAt = DateTimeOffset.UtcNow
            };

            _db.ProcessedEvents.Add(processedEvent);
            existingReadSet.Add(candidateId);
            row.Status = "PROCESSED";
            processed++;
        }

        await _db.SaveChangesAsync(ct);
        return processed;
    }
}
