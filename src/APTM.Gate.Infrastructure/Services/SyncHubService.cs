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
    private readonly IGateIdentityProvider _identityProvider;

    public SyncHubService(GateDbContext db, IGateIdentityProvider identityProvider)
    {
        _db = db;
        _identityProvider = identityProvider;
    }

    public async Task<SyncPushResult> PushAsync(SyncPushPayload payload, CancellationToken ct = default)
    {
        // Dedup by clientRecordId. The AnyAsync check + the unique index on
        // received_sync_data.client_record_id together protect against double-insert,
        // but they're not atomic — under concurrent pushes (which the HHTs do) two
        // requests can both pass AnyAsync and the loser hits the unique index.
        // Phase 7 wraps the body in try/catch DbUpdateException to convert the loser's
        // exception into the same Duplicate response the AnyAsync path returns,
        // instead of bubbling a 500 that the HHT's sync queue would interpret as a
        // transient failure and retry forever.
        var exists = await _db.ReceivedSyncData
            .AnyAsync(r => r.ClientRecordId == payload.ClientRecordId, ct);

        if (exists)
            return SyncPushResult.Duplicate(payload.ClientRecordId);

        try
        {
            return await PushInternalAsync(payload, ct);
        }
        catch (DbUpdateException ex) when (IsClientRecordIdUniqueViolation(ex))
        {
            // Concurrent duplicate push — AnyAsync raced with another request that
            // got there first. Treat as the same as the AnyAsync-detected duplicate:
            // the row exists in the database, our job is done.
            return SyncPushResult.Duplicate(payload.ClientRecordId);
        }
    }

    /// <summary>
    /// Inspects a DbUpdateException to determine whether it was caused by the
    /// <c>received_sync_data.client_record_id</c> unique index. Postgres reports
    /// these via SQLSTATE 23505 (unique_violation); we additionally pattern-match the
    /// constraint/column name so we don't misclassify other unique-index breakages
    /// (e.g. heat_completions.heat_id) as duplicates of this push.
    /// </summary>
    private static bool IsClientRecordIdUniqueViolation(DbUpdateException ex)
    {
        var message = (ex.InnerException?.Message ?? ex.Message) ?? string.Empty;
        return message.Contains("23505")
            && (message.Contains("client_record_id", StringComparison.OrdinalIgnoreCase)
             || message.Contains("received_sync_data", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<SyncPushResult> PushInternalAsync(SyncPushPayload payload, CancellationToken ct = default)
    {

        // If heat_completion, also upsert into heat_completions so the local display can
        // freeze its timer at the same moment the finish gate did. Idempotent — duplicate
        // pushes for the same heat_id are silently dropped (unique index on heat_id).
        if (string.Equals(payload.DataType, "heat_completion", StringComparison.OrdinalIgnoreCase))
        {
            var heatPayload = payload.Payload.Deserialize<HeatCompletionDto>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (heatPayload is not null)
            {
                var existsHeatCompletion = await _db.HeatCompletions
                    .AnyAsync(hc => hc.HeatId == heatPayload.HeatId, ct);

                if (!existsHeatCompletion)
                {
                    _db.HeatCompletions.Add(new HeatCompletion
                    {
                        HeatId = heatPayload.HeatId,
                        HeatNumber = heatPayload.HeatNumber,
                        ExpectedCount = heatPayload.ExpectedCount,
                        FinishedCount = heatPayload.FinishedCount,
                        LastCandidateId = heatPayload.LastCandidateId,
                        CompletedAt = heatPayload.CompletedAt,
                        ClosureReason = string.IsNullOrWhiteSpace(heatPayload.ClosureReason) ? "auto" : heatPayload.ClosureReason,
                        SourceDeviceCode = heatPayload.SourceDeviceCode,
                        ReceivedAt = DateTimeOffset.UtcNow
                    });
                }
            }
        }

        // If race_cancel, mark a HeatCompletion(closure_reason='cancelled') and
        // void any finish events that landed against this heat. Idempotent —
        // a second race_cancel for the same heat is a no-op (HeatId is unique
        // on heat_completions).
        //
        // We don't remove the matching RaceStartTime — keeping it lets the
        // gate retain the audit trail and lets BufferProcessingService skip
        // it via the cancel-aware roster query without losing data. A re-fired
        // heat with the same heat number arrives as a new RaceStartTime row
        // (new HeatId) and wins the OrderByDescending lookup.
        if (string.Equals(payload.DataType, "race_cancel", StringComparison.OrdinalIgnoreCase))
        {
            var cancelPayload = payload.Payload.Deserialize<RaceCancelPayload>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (cancelPayload is not null)
            {
                var raceStart = await _db.RaceStartTimes
                    .FirstOrDefaultAsync(r => r.HeatId == cancelPayload.HeatId, ct);

                var existsCompletion = await _db.HeatCompletions
                    .AnyAsync(hc => hc.HeatId == cancelPayload.HeatId, ct);

                if (!existsCompletion)
                {
                    _db.HeatCompletions.Add(new HeatCompletion
                    {
                        HeatId = cancelPayload.HeatId,
                        HeatNumber = (int)cancelPayload.HeatNumber,
                        ExpectedCount = raceStart?.CandidateIds?.Length ?? 0,
                        FinishedCount = 0,                      // not meaningful on cancel
                        LastCandidateId = null,
                        CompletedAt = cancelPayload.CancelledAt == default
                            ? DateTimeOffset.UtcNow
                            : cancelPayload.CancelledAt,
                        ClosureReason = "cancelled",
                        SourceDeviceCode = payload.DeviceCode,
                        ReceivedAt = DateTimeOffset.UtcNow
                    });
                }

                // Void any processed_events that landed against THIS specific heat
                // (Phase 7). Switched from heat_number to heat_id keying so cancelling
                // one operator group's heat 3 no longer cross-voids another group's
                // heat 3 finishes. The gun-start lower bound is no longer needed —
                // heat_id is unique on race_start_times so no temporal disambiguation
                // is required, and processed_events created before Phase 7 have
                // heat_id = NULL anyway and are safely excluded.
                if (raceStart is not null)
                {
                    await _db.ProcessedEvents
                        .Where(pe => pe.EventType == "finish"
                                  && pe.HeatId == raceStart.HeatId
                                  && !pe.Voided)
                        .ExecuteUpdateAsync(s => s.SetProperty(pe => pe.Voided, true), ct);
                }
            }
        }

        // If heat_candidate_remove, strip the candidate from the heat's roster
        // and void any of their finish events for this heat. Used when the
        // operator pulls a candidate out of a RUNNING heat (false-start case).
        if (string.Equals(payload.DataType, "heat_candidate_remove", StringComparison.OrdinalIgnoreCase))
        {
            var removePayload = payload.Payload.Deserialize<HeatCandidateRemovePayload>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (removePayload is not null)
            {
                var raceStart = await _db.RaceStartTimes
                    .FirstOrDefaultAsync(r => r.HeatId == removePayload.HeatId, ct);

                if (raceStart is not null && raceStart.CandidateIds is not null)
                {
                    // EF in-memory array filter — race_start_times rows are few
                    // (one per heat) so this is cheap.
                    raceStart.CandidateIds = raceStart.CandidateIds
                        .Where(id => id != removePayload.CandidateId)
                        .ToArray();

                    // Phase 7: switched from heat_number to heat_id so removing a
                    // candidate from Group A's heat 3 doesn't void their finish in
                    // Group B's heat 3 (a different race entirely).
                    await _db.ProcessedEvents
                        .Where(pe => pe.CandidateId == removePayload.CandidateId
                                  && pe.HeatId == raceStart.HeatId
                                  && pe.EventType == "finish"
                                  && !pe.Voided)
                        .ExecuteUpdateAsync(s => s.SetProperty(pe => pe.Voided, true), ct);
                }
            }
        }

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
                    var now = DateTimeOffset.UtcNow;

                    // Compute HHT-to-Gate clock offset directly at receipt:
                    //   offset = gateReceiveTime - hhtGunTime ≈ clockDrift + networkLatency
                    //   Network latency on local Wi-Fi is ~10-50ms (negligible).
                    // This converts the gun start from HHT's clock to Gate's clock so
                    // elapsed = tagReadTime(Gate) - adjustedGunStart(Gate) is correct.
                    var hhtToGateOffsetMs = (long)(now - racePayload.GunStartTime).TotalMilliseconds;
                    var adjustedGunStart = racePayload.GunStartTime.AddMilliseconds(hhtToGateOffsetMs);

                    // Safety: if adjusted somehow lands in the future, clamp to now
                    if (adjustedGunStart > now)
                        adjustedGunStart = now;

                    _db.RaceStartTimes.Add(new RaceStartTime
                    {
                        Id = Guid.NewGuid(),
                        HeatId = racePayload.HeatId,
                        HeatNumber = (int)racePayload.HeatNumber,
                        GunStartTime = adjustedGunStart,
                        OriginalGunStartTime = racePayload.GunStartTime,
                        SourceDeviceId = payload.DeviceId,
                        CandidateIds = racePayload.Candidates?
                            .Select(c => c.CandidateId).ToArray() ?? [],
                        SourceClockOffsetMs = (int)racePayload.SourceClockOffsetMs,
                        ReceivedAt = now,
                        GroupId = racePayload.GroupId
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
                HeatNumber = pe.HeatNumber,
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

        var heatCompletions = await _db.HeatCompletions
            .AsNoTracking()
            .OrderBy(hc => hc.ReceivedAt)
            .Select(hc => new HeatCompletionDto
            {
                HeatId = hc.HeatId,
                HeatNumber = hc.HeatNumber,
                ExpectedCount = hc.ExpectedCount,
                FinishedCount = hc.FinishedCount,
                LastCandidateId = hc.LastCandidateId,
                CompletedAt = hc.CompletedAt,
                ClosureReason = hc.ClosureReason,
                SourceDeviceCode = hc.SourceDeviceCode
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
            HeatCompletions = heatCompletions,
            HighWaterMark = highWaterMark,
            SyncDataHighWaterMs = syncData.Count > 0
                ? syncData.Max(s => s.ReceivedAt).ToUnixTimeMilliseconds()
                : sinceSyncMs,
            // Stamped at response build time so the Field app can compute (and surface)
            // the per-gate clock drift before any time-window attribution downstream.
            ServerTime = DateTimeOffset.UtcNow
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

        // Identity is the source of truth for the role — config is the source of truth
        // for event metadata. Splitting them avoids "unconfigured" leaking out for
        // Checkpoint NUCs (which never receive config) or for Start/Finish that are
        // provisioned but pre-config.
        var identity = _identityProvider.Current;

        return new SyncStatusResponse
        {
            DeviceCode = identity?.DeviceCode ?? gateConfig?.DeviceCode ?? "",
            GateRole = identity?.Role ?? "unprovisioned",
            // Surfaced separately so the field app can detect a real desync (identity
            // force-flipped after a config push). Will normally equal GateRole.
            ConfiguredRole = gateConfig?.GateRole,
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

    /// <summary>
    /// Operator group that started this heat. Sent by HHTs running the new
    /// group-aware code; nullable for back-compat with older HHTs and tests
    /// running in legacy "no groups" mode (decision #1). Stored on
    /// <c>race_start_times.group_id</c> for per-group display counters and
    /// future audit logic. The matching algorithm at the finish gate doesn't
    /// rely on this — it still keys on <c>candidate_ids</c> roster membership.
    /// </summary>
    public Guid? GroupId { get; set; }
}

file sealed class RaceStartCandidate
{
    public Guid CandidateId { get; set; }
    public string? AttendanceStatus { get; set; }
}

file sealed class RaceCancelPayload
{
    public Guid HeatId { get; set; }
    public double HeatNumber { get; set; }      // double for the same Gson-quirk reason as race_start
    public DateTimeOffset CancelledAt { get; set; }
    public string? Reason { get; set; }
}

file sealed class HeatCandidateRemovePayload
{
    public Guid HeatId { get; set; }
    public double HeatNumber { get; set; }
    public Guid CandidateId { get; set; }
    public DateTimeOffset RemovedAt { get; set; }
    public string? Reason { get; set; }
}
