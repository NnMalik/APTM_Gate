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

        // For finish gates, load the latest race start time for duration calculation
        RaceStartTime? latestRaceStart = null;
        if (eventType == "finish")
        {
            latestRaceStart = await _db.RaceStartTimes
                .OrderByDescending(r => r.ReceivedAt)
                .FirstOrDefaultAsync(ct);
        }

        // Preload existing first reads for dedup (First Read Rule)
        var resolvedCandidateIds = tagMap.Values.Distinct().ToList();
        var existingReads = await _db.ProcessedEvents
            .Where(pe => resolvedCandidateIds.Contains(pe.CandidateId) && pe.IsFirstRead)
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

            // Compute duration for finish gates
            decimal? durationSeconds = null;
            if (eventType == "finish" && latestRaceStart is not null)
            {
                var elapsed = row.ReadTime - latestRaceStart.GunStartTime;
                durationSeconds = (decimal)elapsed.TotalSeconds;
            }

            var processedEvent = new ProcessedEvent
            {
                CandidateId = candidateId,
                TagEPC = row.TagEPC,
                EventType = eventType,
                ReadTime = row.ReadTime,
                DurationSeconds = durationSeconds,
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
