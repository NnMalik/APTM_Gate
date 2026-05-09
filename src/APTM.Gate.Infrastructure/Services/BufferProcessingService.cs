using APTM.Gate.Core.Enums;
using APTM.Gate.Core.Interfaces;
using APTM.Gate.Infrastructure.Entities;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace APTM.Gate.Infrastructure.Services;

public sealed class BufferProcessingService : IBufferProcessingService
{
    private const int DefaultDedupWindowSeconds = 60;

    private readonly GateDbContext _db;
    private readonly IGateIdentityProvider _identityProvider;
    private readonly TimeSpan _dedupWindow;
    private readonly string _deviceCode;

    public BufferProcessingService(
        GateDbContext db,
        IGateIdentityProvider identityProvider,
        IConfiguration configuration)
    {
        _db = db;
        _identityProvider = identityProvider;
        var seconds = int.TryParse(configuration["Gate:DedupWindowSeconds"], out var v) && v > 0
            ? v
            : DefaultDedupWindowSeconds;
        _dedupWindow = TimeSpan.FromSeconds(seconds);
        _deviceCode = configuration["Gate:DeviceCode"] ?? "unknown";
    }

    public async Task<int> ProcessBatchAsync(int batchSize = 100, CancellationToken ct = default)
    {
        var identity = _identityProvider.Current;
        if (identity is null) return 0; // un-provisioned — caller (worker) shouldn't have called us anyway

        // Branch by role. Checkpoint takes a stripped-down path with no candidate / event /
        // tag_assignment dependencies. Finish (and Start_attendance) take the full pipeline.
        var role = Enum.TryParse<GateRole>(identity.Role, out var parsed) ? parsed : (GateRole?)null;
        return role switch
        {
            GateRole.Checkpoint => await ProcessCheckpointBatchAsync(identity, batchSize, ct),
            _ => await ProcessFullBatchAsync(batchSize, ct)
        };
    }

    /// <summary>
    /// Checkpoint path: no config, no candidate resolution, no event context.
    /// Dedups raw rows by (tag_epc, checkpoint_sequence) within the cooldown window
    /// and writes minimal ProcessedEvent rows. The field app / Main system enriches
    /// downstream using its own copy of the candidate registry and race timings.
    /// </summary>
    private async Task<int> ProcessCheckpointBatchAsync(
        Core.Models.GateIdentityInfo identity,
        int batchSize,
        CancellationToken ct)
    {
        var pendingRows = await _db.RawTagBuffers
            .Where(r => r.Status == "PENDING")
            .OrderBy(r => r.Id)
            .Take(batchSize)
            .ToListAsync(ct);

        if (pendingRows.Count == 0) return 0;

        var checkpointSequence = identity.CheckpointSequence;
        var distinctEpcs = pendingRows.Select(r => r.TagEPC).Distinct().ToList();

        // Preload most-recent kept read per EPC for cooldown comparison. Scoped to this
        // checkpoint's sequence so different checkpoints on the same route don't share state.
        // Note: in normal operation a single NUC only ever has its own sequence's rows here,
        // but the filter is defensive in case a NUC is repurposed without a buffer wipe.
        var dedupQuery = _db.ProcessedEvents
            .Where(pe => pe.EventType == "checkpoint" && !pe.Voided && distinctEpcs.Contains(pe.TagEPC));

        if (checkpointSequence.HasValue)
        {
            var seq = checkpointSequence.Value;
            dedupQuery = dedupQuery.Where(pe => pe.CheckpointSequence == seq);
        }
        else
        {
            dedupQuery = dedupQuery.Where(pe => pe.CheckpointSequence == null);
        }

        var lastReadAtByEpc = await dedupQuery
            .GroupBy(pe => pe.TagEPC)
            .Select(g => new { Epc = g.Key, LastReadAt = g.Max(pe => pe.ReadTime) })
            .ToDictionaryAsync(x => x.Epc, x => x.LastReadAt, ct);

        int processed = 0;

        foreach (var row in pendingRows)
        {
            DateTimeOffset? prev = lastReadAtByEpc.TryGetValue(row.TagEPC, out var lastReadAt)
                ? lastReadAt
                : null;

            if (prev.HasValue && (row.ReadTime - prev.Value) < _dedupWindow)
            {
                row.Status = "DUPLICATE";
                row.IsDuplicate = true;
                processed++;
                continue;
            }

            var processedEvent = new ProcessedEvent
            {
                CandidateId = null,                       // checkpoint never resolves a candidate
                TagEPC = row.TagEPC,
                EventType = "checkpoint",
                EventId = null,                           // no test/event awareness
                ReadTime = row.ReadTime,
                DurationSeconds = null,
                HeatNumber = null,
                CheckpointSequence = checkpointSequence,
                IsFirstRead = !prev.HasValue,             // true only for the very first crossing
                CandidateName = null,
                JacketNumber = null,
                RawBufferId = row.Id,
                ProcessedAt = DateTimeOffset.UtcNow
            };

            _db.ProcessedEvents.Add(processedEvent);
            // Update intra-batch state so subsequent rows in this batch dedup against this read.
            lastReadAtByEpc[row.TagEPC] = row.ReadTime;
            row.Status = "PROCESSED";
            processed++;
        }

        await _db.SaveChangesAsync(ct);
        return processed;
    }

    /// <summary>
    /// Full pipeline used by Finish (and historically by Start_attendance): config-aware,
    /// resolves EPCs to candidates via tag_assignments, computes elapsed time against the
    /// gun start, scopes dedup by (candidate, event, checkpoint_sequence).
    /// </summary>
    private async Task<int> ProcessFullBatchAsync(int batchSize, CancellationToken ct)
    {
        // Re-read identity here so the eventType decision below doesn't depend on
        // gate_config.GateRole. Identity is the source of truth; gate_config.GateRole
        // remains as a denormalized read cache for external observers.
        var identity = _identityProvider.Current;
        if (identity is null) return 0;

        var pendingRows = await _db.RawTagBuffers
            .Where(r => r.Status == "PENDING")
            .OrderBy(r => r.Id)
            .Take(batchSize)
            .ToListAsync(ct);

        if (pendingRows.Count == 0) return 0;

        var gateConfig = await _db.GateConfigs
            .Where(g => g.IsActive)
            .FirstOrDefaultAsync(ct);

        if (gateConfig is null)
        {
            // No active config yet — leave reads PENDING so they can be processed once config
            // arrives. Previously these were marked UNRESOLVED, which silently dropped data
            // recorded before config-push.
            return 0;
        }

        // Preload tag assignments for EPC resolution
        var epcs = pendingRows.Select(r => r.TagEPC).Distinct().ToList();
        var tagMap = await _db.TagAssignments
            .Where(ta => epcs.Contains(ta.TagEPC))
            .ToDictionaryAsync(ta => ta.TagEPC, ta => ta.CandidateId, ct);

        // Preload candidate details for denormalization into ProcessedEvent
        var candidateIds = tagMap.Values.Distinct().ToList();
        var candidateMap = await _db.Candidates
            .Where(c => candidateIds.Contains(c.CandidateId))
            .ToDictionaryAsync(c => c.CandidateId, ct);

        // Determine event type from the provisioned role.
        //
        // NOTE: "start_attendance" is a forward-compat placeholder. Today Start gates have
        // no UHF reader so this branch is inert — attendance counts on the start display
        // come from received_sync_data (HHT pushes), not from processed_events. The branch
        // reactivates if a Start NUC ever gains a reader for tag-based in-person check-in.
        //
        // "checkpoint" is also unreachable here: checkpoint role is dispatched to
        // ProcessCheckpointBatchAsync above and never enters this method.
        var eventType = identity.Role.ToLower() switch
        {
            "start"      => "start_attendance",
            "finish"     => "finish",
            "checkpoint" => "checkpoint",      // unreachable — defensive
            _            => "unknown"
        };

        // For finish gates, load all race start times for heat-candidate matching.
        // Scoped to starts received after current config was applied (prevents using
        // a gun start from a previous event when events are switched).
        //
        // Cancelled heats (HeatCompletion.ClosureReason='cancelled') are excluded so
        // a re-fired heat with the same heat number — which arrives as a new
        // RaceStartTime with a fresh HeatId — wins the heat-candidate matching.
        // Without this filter the cancelled gun-start would still be picked up by
        // the FirstOrDefault and the new finish times would be computed against the
        // wrong gun (or worse, rejected by the elapsed≤0 guard).
        List<RaceStartTime> raceStarts = [];
        if (eventType == "finish")
        {
            var cancelledHeatIds = await _db.HeatCompletions
                .Where(hc => hc.ClosureReason == "cancelled")
                .Select(hc => hc.HeatId)
                .ToListAsync(ct);

            raceStarts = await _db.RaceStartTimes
                .Where(r => r.ReceivedAt >= gateConfig.ReceivedAt
                         && !cancelledHeatIds.Contains(r.HeatId))
                .OrderByDescending(r => r.ReceivedAt)
                .ToListAsync(ct);
        }

        // Universal cooldown: same tag is processed again only after the configured window.
        // Scoped to (candidate, active event, checkpoint sequence).
        // Voided rows are excluded — they're stale reads from a cancelled heat or
        // a removed candidate and shouldn't block the next valid finish.
        var activeEventId = gateConfig.ActiveEventId;
        var resolvedCandidateIds = tagMap.Values.Distinct().ToList();

        var dedupQuery = _db.ProcessedEvents
            .Where(pe => pe.CandidateId != null
                      && !pe.Voided
                      && resolvedCandidateIds.Contains(pe.CandidateId.Value));

        dedupQuery = activeEventId.HasValue
            ? dedupQuery.Where(pe => pe.EventId == activeEventId.Value)
            : dedupQuery.Where(pe => pe.EventId == null);

        if (gateConfig.CheckpointSequence.HasValue)
        {
            var seq = gateConfig.CheckpointSequence.Value;
            dedupQuery = dedupQuery.Where(pe => pe.CheckpointSequence == seq);
        }
        else
        {
            dedupQuery = dedupQuery.Where(pe => pe.CheckpointSequence == null);
        }

        var lastReadAtByCandidate = await dedupQuery
            .GroupBy(pe => pe.CandidateId!.Value)
            .Select(g => new { CandidateId = g.Key, LastReadAt = g.Max(pe => pe.ReadTime) })
            .ToDictionaryAsync(x => x.CandidateId, x => x.LastReadAt, ct);

        int processed = 0;
        // Track heats that gained a new finish in this batch so we can check completion
        // after SaveChangesAsync makes the new rows queryable.
        var heatsTouched = new HashSet<Guid>();

        foreach (var row in pendingRows)
        {
            if (!tagMap.TryGetValue(row.TagEPC, out var candidateId))
            {
                // EPC not in tag_assignments — leave PENDING so a future config-push that
                // adds the assignment can pick it up. This avoids silently dropping reads
                // when tags are added late.
                continue;
            }

            DateTimeOffset? prev = lastReadAtByCandidate.TryGetValue(candidateId, out var lastReadAt)
                ? lastReadAt
                : null;

            if (prev.HasValue && (row.ReadTime - prev.Value) < _dedupWindow)
            {
                row.Status = "DUPLICATE";
                row.IsDuplicate = true;
                processed++;
                continue;
            }

            // Compute duration for finish gates with heat-candidate matching
            decimal? durationSeconds = null;
            int? heatNumber = null;
            RaceStartTime? matchedRaceStart = null;
            if (eventType == "finish")
            {
                if (raceStarts.Count == 0)
                {
                    // No gun fired yet — leave as PENDING so the worker retries
                    // once a race_start arrives. Don't consume the cooldown slot.
                    continue;
                }

                // Match candidate to their specific heat, fall back to latest start
                var raceStart = raceStarts
                    .FirstOrDefault(r => r.CandidateIds is not null && r.CandidateIds.Contains(candidateId))
                    ?? raceStarts[0];
                matchedRaceStart = raceStart;

                // GunStartTime is already in gate-local time (adjusted at receipt in SyncHubService).
                // Both ReadTime and GunStartTime are in the same clock reference — simple subtraction.
                var elapsed = row.ReadTime - raceStart.GunStartTime;

                if (elapsed.TotalSeconds <= 0)
                {
                    // Read happened before gun fired — discard so the real crossing
                    // after gun fire can claim the slot.
                    row.Status = "DUPLICATE";
                    row.IsDuplicate = true;
                    processed++;
                    continue;
                }

                durationSeconds = (decimal)elapsed.TotalSeconds;
                heatNumber = raceStart.HeatNumber;
            }

            candidateMap.TryGetValue(candidateId, out var candidate);

            // Fallback: if candidate not in preloaded batch, load directly
            if (candidate == null)
            {
                candidate = await _db.Candidates.FindAsync(new object[] { candidateId }, ct);
                if (candidate != null) candidateMap[candidateId] = candidate;
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
                IsFirstRead = !prev.HasValue,
                CandidateName = candidate?.Name,
                JacketNumber = candidate?.JacketNumber,
                RawBufferId = row.Id,
                ProcessedAt = DateTimeOffset.UtcNow
            };

            _db.ProcessedEvents.Add(processedEvent);
            lastReadAtByCandidate[candidateId] = row.ReadTime;
            row.Status = "PROCESSED";
            processed++;

            // Remember this heat so we can re-evaluate completion after SaveChanges. We
            // only check completion for heats whose roster *contains* this candidate —
            // strangers fall back to the latest heat for elapsed-time math but mustn't
            // count toward that heat's roster.
            if (eventType == "finish"
                && matchedRaceStart is not null
                && matchedRaceStart.CandidateIds is { Length: > 0 }
                && matchedRaceStart.CandidateIds.Contains(candidateId))
            {
                heatsTouched.Add(matchedRaceStart.HeatId);
            }
        }

        await _db.SaveChangesAsync(ct);

        // Heat-completion detection happens AFTER the finish events are flushed so the
        // count query sees the just-inserted rows.
        if (heatsTouched.Count > 0)
        {
            await DetectHeatCompletionsAsync(heatsTouched, raceStarts, activeEventId, ct);
        }

        return processed;
    }

    /// <summary>
    /// For each heat that just gained a finish event, check whether the entire roster
    /// has now finished. If so, write a <see cref="HeatCompletion"/> row (idempotent via
    /// unique index on heat_id) and let the NOTIFY trigger broadcast on the heat_complete
    /// SSE channel.
    /// </summary>
    private async Task DetectHeatCompletionsAsync(
        HashSet<Guid> heatIds,
        List<RaceStartTime> raceStarts,
        int? activeEventId,
        CancellationToken ct)
    {
        // Skip heats already marked complete to avoid noise + wasted queries.
        var alreadyCompleted = await _db.HeatCompletions
            .Where(hc => heatIds.Contains(hc.HeatId))
            .Select(hc => hc.HeatId)
            .ToListAsync(ct);
        var alreadyCompletedSet = alreadyCompleted.ToHashSet();

        var anyInserted = false;
        foreach (var heatId in heatIds)
        {
            if (alreadyCompletedSet.Contains(heatId)) continue;

            var raceStart = raceStarts.FirstOrDefault(r => r.HeatId == heatId);
            if (raceStart is null || raceStart.CandidateIds is null || raceStart.CandidateIds.Length == 0)
                continue;

            var roster = raceStart.CandidateIds;
            var heatNumber = raceStart.HeatNumber;

            // Strict roster filter: only count finishes by candidates actually in this heat.
            // Without this, a stranger that fell back to this heat's number would inflate the
            // count past expected.
            var rosterFinishesQuery = _db.ProcessedEvents
                .Where(pe => pe.EventType == "finish"
                          && pe.IsFirstRead
                          && pe.HeatNumber == heatNumber
                          && pe.CandidateId != null
                          && roster.Contains(pe.CandidateId.Value));

            if (activeEventId.HasValue)
                rosterFinishesQuery = rosterFinishesQuery.Where(pe => pe.EventId == activeEventId.Value);

            var rosterFinishes = await rosterFinishesQuery
                .GroupBy(pe => pe.CandidateId!.Value)
                .Select(g => new { CandidateId = g.Key, LastReadAt = g.Max(pe => pe.ReadTime) })
                .ToListAsync(ct);

            if (rosterFinishes.Count < roster.Length) continue;

            // Heat just completed. Last finisher = candidate with max read_time.
            var last = rosterFinishes.OrderByDescending(x => x.LastReadAt).First();

            _db.HeatCompletions.Add(new HeatCompletion
            {
                HeatId = heatId,
                HeatNumber = heatNumber,
                ExpectedCount = roster.Length,
                FinishedCount = rosterFinishes.Count,
                LastCandidateId = last.CandidateId,
                CompletedAt = last.LastReadAt,
                ClosureReason = "auto",
                SourceDeviceCode = _deviceCode,
                ReceivedAt = DateTimeOffset.UtcNow
            });
            anyInserted = true;
        }

        if (anyInserted)
        {
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Unique constraint on heat_id raced — another concurrent batch beat us
                // to the insert. The row exists, that's all we needed. Swallow.
            }
        }
    }
}
