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

    /// <summary>
    /// Claims a batch of PENDING raw_tag_buffer rows under a row-level lock that other
    /// concurrent processors will skip. Defends against the case where the background
    /// worker and an operator-triggered <c>POST /gate/buffer/process-now</c> race for
    /// the same rows — without this, both could SELECT the same PENDING ids, both
    /// would insert duplicate processed_events, and the resulting unique-index breakage
    /// would 500 the loser.
    ///
    /// PostgreSQL <c>FOR UPDATE SKIP LOCKED</c> only operates inside an active
    /// transaction. The caller is responsible for committing the returned transaction
    /// after the rows have been updated to PROCESSED (or rolling back on error). Rows
    /// are returned tracked so the caller can mutate <c>Status</c> and SaveChangesAsync
    /// will issue the right UPDATE.
    /// </summary>
    private async Task<(List<RawTagBuffer> Rows, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? Tx)>
        ClaimPendingAsync(int batchSize, bool scopeToOldestEvent, CancellationToken ct)
    {
        // Begin a transaction with the default isolation so the row locks released only
        // when we commit. Other workers calling SELECT … FOR UPDATE SKIP LOCKED will pass
        // these rows over and pick the next available ones.
        var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // FromSqlRaw + tracked entities so the caller can mutate Status. The
            // {0} parameter is the LIMIT — Npgsql parameterises it correctly.
            //
            // When scopeToOldestEvent is set (the finish pipeline), the batch is
            // restricted to a single event — the one owning the oldest PENDING row.
            // event_id is stamped at ingestion, so this lets the processor compute
            // elapsed time against the right gun even across an event switch, and
            // drains each event's backlog in turn over successive worker cycles.
            // IS NOT DISTINCT FROM keeps the match null-safe: reads captured before
            // any config was loaded carry event_id = NULL and are still drained.
            var sql = scopeToOldestEvent
                ? @"SELECT * FROM raw_tag_buffer
                    WHERE status = 'PENDING'
                      AND event_id IS NOT DISTINCT FROM (
                          SELECT event_id FROM raw_tag_buffer
                          WHERE status = 'PENDING' ORDER BY id LIMIT 1)
                    ORDER BY id
                    LIMIT {0}
                    FOR UPDATE SKIP LOCKED"
                : @"SELECT * FROM raw_tag_buffer
                    WHERE status = 'PENDING'
                    ORDER BY id
                    LIMIT {0}
                    FOR UPDATE SKIP LOCKED";

            var rows = await _db.RawTagBuffers
                .FromSqlRaw(sql, batchSize)
                .ToListAsync(ct);
            return (rows, tx);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            tx.Dispose();
            throw;
        }
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
        // Claim rows under a row-level lock so concurrent invocations (worker +
        // /buffer/process-now) don't double-process. Tx is committed at the end
        // alongside the SaveChangesAsync that flips Status to PROCESSED.
        // Checkpoint has no event context, so it claims across all events.
        var (pendingRows, tx) = await ClaimPendingAsync(batchSize, scopeToOldestEvent: false, ct);
        if (tx is null) return 0;
        try
        {
            if (pendingRows.Count == 0)
            {
                await tx.CommitAsync(ct);
                return 0;
            }

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
                // Pass through the active event stamped at ingestion (null when no config
                // was loaded — the normal midpoint case). The field app prefers this over
                // its own gun-window inference when present.
                EventId = row.EventId,
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
            await tx.CommitAsync(ct);
            return processed;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            await tx.DisposeAsync();
        }
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

        // Phase 7: claim under FOR UPDATE SKIP LOCKED so concurrent processors
        // (worker + /buffer/process-now) don't pick the same rows. The finish
        // pipeline claims one event at a time (oldest pending event first) so
        // elapsed time is always computed against that event's gun start.
        var (pendingRows, tx) = await ClaimPendingAsync(batchSize, scopeToOldestEvent: true, ct);
        if (tx is null) return 0;
        try
        {
            if (pendingRows.Count == 0)
            {
                await tx.CommitAsync(ct);
                return 0;
            }

            var gateConfig = await _db.GateConfigs
                .Where(g => g.IsActive)
                .FirstOrDefaultAsync(ct);

            if (gateConfig is null)
            {
                // No active config yet — leave reads PENDING so they can be processed once
                // config arrives. Roll back so the row locks release without UPDATEs.
                await tx.RollbackAsync(ct);
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
        // The batch was claimed for a single event (see ClaimPendingAsync) — the event
        // that owns these reads, stamped at ingestion. Reads captured before any config
        // was loaded carry event_id = NULL and fall back to the current active event.
        var activeEventId = pendingRows[0].EventId ?? gateConfig.ActiveEventId;

        List<RaceStartTime> raceStarts = [];
        if (eventType == "finish")
        {
            var cancelledHeatIds = await _db.HeatCompletions
                .Where(hc => hc.ClosureReason == "cancelled")
                .Select(hc => hc.HeatId)
                .ToListAsync(ct);

            // Event-scoped lookup: only this event's gun starts are eligible, so a
            // candidate who runs multiple events is matched to the correct gun. Scoped
            // to this test instance too. Legacy rows with no event_id fall back to the
            // old "received since the current config was applied" window.
            raceStarts = await _db.RaceStartTimes
                .Where(r => !cancelledHeatIds.Contains(r.HeatId)
                         && (r.TestInstanceId == null || r.TestInstanceId == gateConfig.TestInstanceId)
                         && (r.EventId == activeEventId
                              || (r.EventId == null && r.ReceivedAt >= gateConfig.ReceivedAt)))
                .OrderByDescending(r => r.ReceivedAt)
                .ToListAsync(ct);
        }

        // Universal cooldown: same tag is processed again only after the configured window.
        // Scoped to (candidate, active event, checkpoint sequence).
        // Voided rows are excluded — they're stale reads from a cancelled heat or
        // a removed candidate and shouldn't block the next valid finish.
        // UNRESOLVED rows are excluded too: a false-starter who crosses the finish
        // during an aborted run lands an UNRESOLVED event (no heat, no duration).
        // Counting it in the cooldown would suppress the candidate's REAL finish in
        // the re-run if it fell inside the dedup window — leaving them with no time.
        var resolvedCandidateIds = tagMap.Values.Distinct().ToList();

        var dedupQuery = _db.ProcessedEvents
            .Where(pe => pe.CandidateId != null
                      && !pe.Voided
                      && pe.Status != "UNRESOLVED"
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
            string? processedStatus = null;          // null = matched / not applicable
            if (eventType == "finish")
            {
                if (raceStarts.Count == 0)
                {
                    // No gun fired yet — leave as PENDING so the worker retries
                    // once a race_start arrives. Don't consume the cooldown slot.
                    continue;
                }

                // Match candidate to their specific heat by roster membership. With
                // multiple parallel heats (one per operator group), each candidate is
                // in at most one active heat, so this is unambiguous when groups are
                // disjoint. We deliberately do NOT fall back to raceStarts[0] for
                // unrostered finishes — guessing that a stranger ran in the most
                // recent heat caused the cross-group elapsed-time corruption that
                // motivated this rewrite (DESIGN_OPERATOR_GROUPS.md §6.3, Phase 7).
                var raceStart = raceStarts
                    .FirstOrDefault(r => r.CandidateIds is not null && r.CandidateIds.Contains(candidateId));

                if (raceStart is null)
                {
                    // Stray read — candidate was scanned at the finish line but isn't
                    // in any active heat's roster. Common causes: a candidate from a
                    // not-yet-started heat lingering nearby, a finished candidate
                    // re-passing the gate, a wrong-tag mapping. Persist as UNRESOLVED
                    // (no heat, no group, no duration) so the row exists for admin
                    // review without being charged to the wrong race.
                    candidateMap.TryGetValue(candidateId, out var unresolvedCandidate);
                    if (unresolvedCandidate == null)
                    {
                        unresolvedCandidate = await _db.Candidates.FindAsync(new object[] { candidateId }, ct);
                        if (unresolvedCandidate != null) candidateMap[candidateId] = unresolvedCandidate;
                    }

                    _db.ProcessedEvents.Add(new ProcessedEvent
                    {
                        CandidateId = candidateId,
                        TagEPC = row.TagEPC,
                        EventType = eventType,
                        EventId = activeEventId,
                        ReadTime = row.ReadTime,
                        DurationSeconds = null,
                        HeatNumber = null,
                        HeatId = null,
                        GroupId = null,
                        Status = "UNRESOLVED",
                        CheckpointSequence = gateConfig.CheckpointSequence,
                        IsFirstRead = !prev.HasValue,
                        CandidateName = unresolvedCandidate?.Name,
                        JacketNumber = unresolvedCandidate?.JacketNumber,
                        RawBufferId = row.Id,
                        ProcessedAt = DateTimeOffset.UtcNow
                    });
                    lastReadAtByCandidate[candidateId] = row.ReadTime;
                    row.Status = "PROCESSED";
                    processed++;
                    continue;
                }

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
                ProcessedAt = DateTimeOffset.UtcNow,
                // Denormalised from the matched heat (finish events only). Lets per-group
                // display counters run as a single indexed query instead of joining.
                // NULL for checkpoint events and legacy heats started without group context.
                GroupId = matchedRaceStart?.GroupId,
                // HeatId is the load-bearing identifier going forward — voids and
                // completions key on it instead of HeatNumber, which prevents
                // cross-group cross-contamination on number collisions.
                HeatId = matchedRaceStart?.HeatId,
                Status = processedStatus
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

            // Heat-completion detection happens AFTER the finish events are flushed so
            // the count query sees the just-inserted rows. Runs inside the same
            // transaction so a completion row + the finishing event commit atomically.
            if (heatsTouched.Count > 0)
            {
                await DetectHeatCompletionsAsync(heatsTouched, raceStarts, activeEventId, ct);
            }

            await tx.CommitAsync(ct);
            return processed;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            await tx.DisposeAsync();
        }
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

            // Strict roster filter, keyed on heat_id (Phase 7). Previously used
            // heat_number which collided across operator groups — Trainer-1's heat 3
            // and Trainer-2's heat 3 share a number but are different races. heat_id
            // is unique on race_start_times so this is unambiguous.
            var rosterFinishesQuery = _db.ProcessedEvents
                .Where(pe => pe.EventType == "finish"
                          && pe.IsFirstRead
                          && pe.HeatId == heatId
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
