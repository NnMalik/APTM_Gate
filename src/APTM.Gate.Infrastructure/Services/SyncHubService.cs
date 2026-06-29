using System.Text.Json;
using APTM.Gate.Core.Interfaces;
using APTM.Gate.Core.Models;
using APTM.Gate.Infrastructure.Entities;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace APTM.Gate.Infrastructure.Services;

public sealed class SyncHubService : ISyncHubService
{
    private readonly GateDbContext _db;
    private readonly IGateIdentityProvider _identityProvider;
    private readonly IReaderStatusProvider _readerStatus;
    private readonly ILogger<SyncHubService> _logger;

    public SyncHubService(
        GateDbContext db,
        IGateIdentityProvider identityProvider,
        IReaderStatusProvider readerStatus,
        ILogger<SyncHubService> logger)
    {
        _db = db;
        _identityProvider = identityProvider;
        _readerStatus = readerStatus;
        _logger = logger;
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

        // An HHT race_start that names a different RACE event than the gate's current active one
        // triggers a one-event-at-a-time auto-switch — evaluated after persistence (end of method),
        // and only when the current event's heats are all complete.
        int? autoSwitchToEventId = null;

        // If heat_completion, also upsert into heat_completions so the local display can
        // freeze its timer at the same moment the finish gate did. Idempotent — duplicate
        // pushes for the same heat_id are silently dropped (unique index on heat_id).
        if (string.Equals(payload.DataType, "heat_completion", StringComparison.OrdinalIgnoreCase))
        {
            // Deserialize into the double-typed HeatCompletionPushPayload, NOT the int-typed
            // HeatCompletionDto: the HHT round-trips the relay payload through a Map<String,Any>, so
            // Gson emits the counts as JSON doubles (3.0, not 3). System.Text.Json refuses 3.0 → int and
            // throws — which 500s the push and (because heat_completion is a CRITICAL retry-forever sync
            // type) leaves the start LED's timer running forever. Same accommodation as RaceStartPayload;
            // we cast to int when storing.
            var heatPayload = payload.Payload.Deserialize<HeatCompletionPushPayload>(
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
                        HeatNumber = (int)heatPayload.HeatNumber,
                        ExpectedCount = (int)heatPayload.ExpectedCount,
                        FinishedCount = (int)heatPayload.FinishedCount,
                        LastCandidateId = heatPayload.LastCandidateId,
                        CompletedAt = heatPayload.CompletedAt,
                        ClosureReason = string.IsNullOrWhiteSpace(heatPayload.ClosureReason) ? "auto" : heatPayload.ClosureReason,
                        SourceDeviceCode = string.IsNullOrWhiteSpace(heatPayload.SourceDeviceCode) ? payload.DeviceCode : heatPayload.SourceDeviceCode,
                        // Authoritative finish-gate heat time carried in the relay. The start display
                        // shows this verbatim so both LEDs freeze on an identical value.
                        DurationSeconds = heatPayload.DurationSeconds,
                        ReceivedAt = DateTimeOffset.UtcNow
                    });
                }
            }
        }

        // If race_cancel, mark a HeatCompletion(closure_reason='cancelled') and
        // void any finish events that landed against this heat. Idempotent —
        // a second race_cancel for the same heat re-stamps the same reason.
        //
        // We don't remove the matching RaceStartTime — keeping it lets the
        // gate retain the audit trail and lets BufferProcessingService skip
        // it via the cancel-aware roster query without losing data. A re-fired
        // heat (restart) arrives as a new RaceStartTime row with a fresh HeatId
        // and wins the heat-candidate matching.
        if (string.Equals(payload.DataType, "race_cancel", StringComparison.OrdinalIgnoreCase))
        {
            var cancelPayload = payload.Payload.Deserialize<RaceCancelPayload>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (cancelPayload is not null)
            {
                var raceStart = await _db.RaceStartTimes
                    .FirstOrDefaultAsync(r => r.HeatId == cancelPayload.HeatId, ct);

                // Upsert the completion as 'cancelled'. A heat may already carry a
                // completion row — most importantly an AUTO-completion written when
                // the roster finished before the operator aborted. In that case we
                // MUST flip closure_reason to 'cancelled': BufferProcessingService
                // only excludes closure_reason='cancelled' from gun-start matching,
                // so leaving it 'auto' would keep the heat's gun live even though we
                // void its finishes just below — voided events but a live gun.
                var existingCompletion = await _db.HeatCompletions
                    .FirstOrDefaultAsync(hc => hc.HeatId == cancelPayload.HeatId, ct);

                if (existingCompletion is null)
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
                else if (!string.Equals(existingCompletion.ClosureReason, "cancelled",
                             StringComparison.OrdinalIgnoreCase))
                {
                    // Heat had auto-completed (or closed some other way) before the
                    // abort. Re-stamp it as cancelled so downstream matching and the
                    // field-app exclusion treat it consistently.
                    existingCompletion.ClosureReason = "cancelled";
                    existingCompletion.CompletedAt = cancelPayload.CancelledAt == default
                        ? DateTimeOffset.UtcNow
                        : cancelPayload.CancelledAt;
                    existingCompletion.FinishedCount = 0;       // not meaningful on cancel
                    existingCompletion.LastCandidateId = null;
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

                    // Convert the gun start from the HHT's clock to this gate's clock.
                    //
                    // Preferred: the HHT measured its offset to THIS gate (NTP-style via
                    // GET /gate/time) shortly before pushing and sent it along. Then
                    //   adjustedGunStart = hhtGunTime + measuredOffset
                    // is exact regardless of how late the push arrives — queue polling,
                    // retries, or minutes of Wi-Fi outage no longer shorten elapsed times.
                    //
                    // Fallback (older HHTs, no offset in payload): assume the push arrived
                    // instantly — offset = gateReceiveTime − hhtGunTime. Any delivery delay
                    // makes the whole heat appear faster by exactly that delay.
                    long appliedOffsetMs;
                    string offsetMethod;
                    if (payload.GateClockOffsetMs is long measuredOffsetMs)
                    {
                        appliedOffsetMs = measuredOffsetMs;
                        offsetMethod = "measured";
                    }
                    else
                    {
                        appliedOffsetMs = (long)(now - racePayload.GunStartTime).TotalMilliseconds;
                        offsetMethod = "receipt";
                    }

                    var adjustedGunStart = racePayload.GunStartTime.AddMilliseconds(appliedOffsetMs);

                    // Safety: a gun start can't be in the future. Allow a small margin for
                    // the offset sample's own uncertainty before clamping — and log, because
                    // a clamp on a "measured" offset means the sample was bad.
                    var rttMarginMs = Math.Max(payload.GateClockOffsetRttMs ?? 0, 250);
                    if (adjustedGunStart > now.AddMilliseconds(rttMarginMs))
                    {
                        _logger.LogWarning(
                            "Adjusted gun start for heat {HeatId} lands {Ms}ms in the future (method: {Method}) — clamping to now",
                            racePayload.HeatId,
                            (long)(adjustedGunStart - now).TotalMilliseconds,
                            offsetMethod);
                        adjustedGunStart = now;
                    }

                    // Event scope: prefer the eventId the HHT put in the payload; fall
                    // back to the gate's own active event when an older HHT omits it.
                    // test_instance_id is always stamped from the active config so a
                    // race-start lookup never leaks across test instances.
                    var raceGateConfig = await _db.GateConfigs
                        .Where(g => g.IsActive)
                        .Select(g => new { g.ActiveEventId, g.TestInstanceId })
                        .FirstOrDefaultAsync(ct);

                    _db.RaceStartTimes.Add(new RaceStartTime
                    {
                        Id = Guid.NewGuid(),
                        HeatId = racePayload.HeatId,
                        HeatNumber = (int)racePayload.HeatNumber,
                        GunStartTime = adjustedGunStart,
                        OriginalGunStartTime = racePayload.GunStartTime,
                        SourceDeviceId = payload.DeviceId,
                        SourceDeviceCode = payload.DeviceCode,
                        CandidateIds = racePayload.Candidates?
                            .Select(c => c.CandidateId).ToArray() ?? [],
                        SourceClockOffsetMs = (int)racePayload.SourceClockOffsetMs,
                        AppliedOffsetMs = appliedOffsetMs,
                        OffsetMethod = offsetMethod,
                        ReceivedAt = now,
                        GroupId = racePayload.GroupId,
                        EventId = (int?)racePayload.EventId ?? raceGateConfig?.ActiveEventId,
                        TestInstanceId = raceGateConfig?.TestInstanceId
                    });

                    // The HHT explicitly named an event that differs from the gate's current one:
                    // remember it so we can follow the operator to the new event after this heat is
                    // persisted (only switches if the current event is already finished — below).
                    var incomingEventId = (int?)racePayload.EventId;
                    if (incomingEventId is int ev && ev != raceGateConfig?.ActiveEventId)
                    {
                        autoSwitchToEventId = ev;
                    }
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

        if (autoSwitchToEventId is int switchTo)
        {
            await TryAutoSwitchActiveEventAsync(switchTo, ct);
        }

        return SyncPushResult.Ok(payload.ClientRecordId);
    }

    /// <summary>
    /// One-event-at-a-time auto-switch. When an HHT fires a heat for a different RACE event than the
    /// gate's current active one, follow it — but ONLY once the current event's heats are all
    /// complete, so a still-running event is never yanked off the display mid-measurement. Otherwise
    /// the new heat stays recorded but the active event is left unchanged (the operator finishes the
    /// current event first; the HHT shows a warning at fire time when it can reach the gate). Mirrors
    /// the validation of POST /gate/active-event and fires the same config_updated NOTIFY.
    /// </summary>
    private async Task TryAutoSwitchActiveEventAsync(int incomingEventId, CancellationToken ct)
    {
        var gateConfig = await _db.GateConfigs.FirstOrDefaultAsync(g => g.IsActive, ct);
        if (gateConfig is null || gateConfig.ActiveEventId == incomingEventId)
        {
            return;
        }

        var target = await _db.TestEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.EventId == incomingEventId, ct);
        if (target is null || !string.Equals(target.EventType, "RACE", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Auto-switch skipped: event {EventId} is unknown or not a RACE event.", incomingEventId);
            return;
        }

        // One event at a time: don't switch away from an event that still has running heats
        // (a race start with no completion). The HHT is warned at fire time; this is the backstop.
        if (gateConfig.ActiveEventId is int current)
        {
            var currentEventStillRunning = await _db.RaceStartTimes
                .AnyAsync(rs => rs.EventId == current
                             && !_db.HeatCompletions.Any(hc => hc.HeatId == rs.HeatId), ct);
            if (currentEventStillRunning)
            {
                _logger.LogInformation(
                    "Auto-switch to event {EventId} deferred: current event {Current} still has running heats.",
                    incomingEventId, current);
                return;
            }
        }

        gateConfig.ActiveEventId = target.EventId;
        gateConfig.ActiveEventName = target.EventName;
        await _db.SaveChangesAsync(ct);

        // Refresh the display's title bar without polling (same channel the manual switch uses).
        var notifyPayload = JsonSerializer.Serialize(new { activeEventId = target.EventId, activeEventName = target.EventName });
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_notify('config_updated', {notifyPayload})", ct);

        _logger.LogInformation("Auto-switched active event → {EventId} ({Name}) on an HHT race_start.", target.EventId, target.EventName);
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
                ProcessedAt = pe.ProcessedAt,
                HeatId = pe.HeatId,
                Voided = pe.Voided,
                Status = pe.Status
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
                SourceDeviceId = r.SourceDeviceId,
                EventId = r.EventId,
                CandidateIds = r.CandidateIds
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

        // Current max id on the gate — lets the puller detect a wiped gate (its cached
        // mark exceeding this value) and reset. 0 when the table is empty.
        var maxEventId = await _db.ProcessedEvents.MaxAsync(pe => (long?)pe.Id, ct) ?? 0L;

        // Log the pull. Cap the logged mark at the gate's actual max id: after a wipe,
        // a stale client pulls with a mark from before the reset — logging that value
        // verbatim would make race-data/status report "all pulled" for events the
        // device never saw.
        _db.SyncLogs.Add(new SyncLogEntry
        {
            Id = Guid.NewGuid(),
            PullerDeviceId = pullerDeviceId,
            PullerDeviceCode = pullerDeviceCode,
            LastProcessedEventId = Math.Min(highWaterMark, maxEventId),
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
            MaxEventId = maxEventId,
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

        // "Anything left on this gate?" numbers — what the operator checks before
        // erase/power-off. WIPE:/ERASE: audit markers are excluded so a previous
        // teardown can't masquerade as a real device having pulled.
        var maxEventId = await _db.ProcessedEvents.MaxAsync(pe => (long?)pe.Id, ct) ?? 0L;
        var maxPulledEventId = await _db.SyncLogs
            .Where(s => !s.PullerDeviceCode.StartsWith("WIPE:") && !s.PullerDeviceCode.StartsWith("ERASE:"))
            .Select(s => (long?)s.LastProcessedEventId)
            .MaxAsync(ct) ?? 0L;
        var pendingRawCount = await _db.RawTagBuffers.CountAsync(r => r.Status == "PENDING", ct);

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
            SyncPulls = syncPulls,
            MaxEventId = maxEventId,
            UnpulledEventCount = Math.Max(0, maxEventId - maxPulledEventId),
            PendingRawCount = pendingRawCount,
            IngestQueueDepth = _readerStatus.IngestQueueDepth
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

    /// <summary>
    /// The event (TestEvent.EventId) this heat belongs to. Sent by HHTs running the
    /// event-scoped code; nullable for back-compat with older HHTs — the gate then
    /// falls back to its own active event at receipt. Stored on
    /// <c>race_start_times.event_id</c> so the finish processor matches a finish read
    /// to the gun start of the same event. <c>double?</c> for the same Gson numeric
    /// quirk as the other numeric fields.
    /// </summary>
    public double? EventId { get; set; }
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

// Internal DTO for deserializing the heat_completion relay push. Counts are double (not int) for the
// same Gson Map<String,Any> reason as RaceStartPayload — the HHT serialises 3 as 3.0, and
// System.Text.Json throws on 3.0 → int. Cast to int when storing the HeatCompletion entity.
file sealed class HeatCompletionPushPayload
{
    public Guid HeatId { get; set; }
    public double HeatNumber { get; set; }
    public double ExpectedCount { get; set; }
    public double FinishedCount { get; set; }
    public Guid? LastCandidateId { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public string? ClosureReason { get; set; }
    public string? SourceDeviceCode { get; set; }
    public double? DurationSeconds { get; set; }
}
