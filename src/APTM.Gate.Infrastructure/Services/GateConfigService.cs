using APTM.Gate.Core.Interfaces;
using APTM.Gate.Core.Models;
using APTM.Gate.Infrastructure.Entities;
using APTM.Gate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APTM.Gate.Infrastructure.Services;

public sealed class GateConfigService : IGateConfigService
{
    private readonly GateDbContext _db;
    private readonly IGateIdentityProvider _identityProvider;
    private readonly string _deviceCode;
    private readonly string _connectionString;

    public GateConfigService(GateDbContext db, IGateIdentityProvider identityProvider, IConfiguration configuration)
    {
        _db = db;
        _identityProvider = identityProvider;
        _deviceCode = configuration["Gate:DeviceCode"] ?? throw new InvalidOperationException("Gate:DeviceCode not configured");
        _connectionString = configuration.GetConnectionString("GateDb") ?? throw new InvalidOperationException("GateDb connection string not configured");
    }

    public async Task<ConfigResult> ApplyConfigAsync(ConfigPackageDto config, CancellationToken ct = default)
    {
        // Identity is the source of truth for role + checkpoint sequence. The gate must be
        // provisioned via PUT /gate/identity before any config-package can be applied.
        var identity = _identityProvider.Current;
        if (identity is null)
        {
            return ConfigResult.Fail(
                "Gate is not provisioned. PUT /gate/identity to set role, restart the service, then re-push the config.");
        }

        // Find this gate in the config package
        var thisGate = config.Gates.FirstOrDefault(g =>
            string.Equals(g.DeviceCode, _deviceCode, StringComparison.OrdinalIgnoreCase));

        if (thisGate is null)
            return ConfigResult.Fail($"Gate '{_deviceCode}' not found in config package gates list.");

        // Validate the package's gateType against the provisioned identity. The package's role
        // is informational only — identity wins. Mismatch means the field app sent the wrong
        // package or someone re-imaged a NUC without updating Main's gate registry.
        if (!string.Equals(thisGate.GateType, identity.Role, StringComparison.OrdinalIgnoreCase))
        {
            return ConfigResult.Fail(
                $"Config role mismatch. Package says '{thisGate.GateType}', " +
                $"this gate is provisioned as '{identity.Role}'. " +
                $"Re-push a corrected config or use PUT /gate/identity?force=true to re-provision.");
        }
        if (identity.Role == "Checkpoint" && thisGate.CheckpointSequence != identity.CheckpointSequence)
        {
            return ConfigResult.Fail(
                $"Checkpoint sequence mismatch. Package says {thisGate.CheckpointSequence}, " +
                $"this gate is provisioned as sequence {identity.CheckpointSequence}.");
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        try
        {
            // If switching to a different test instance, clean up old data tables
            // (only data that has already been pulled by at least one device)
            var currentConfig = await _db.GateConfigs.Where(g => g.IsActive).FirstOrDefaultAsync(ct);
            if (currentConfig != null && currentConfig.TestInstanceId != config.TestInstanceId)
            {
                // Find the max event ID that has been pulled by ANY device
                var maxPulledEventId = await _db.SyncLogs
                    .Select(s => (long?)s.LastProcessedEventId)
                    .MaxAsync(ct) ?? 0;

                var totalEvents = await _db.ProcessedEvents.CountAsync(ct);
                var maxEventId = totalEvents > 0
                    ? await _db.ProcessedEvents.MaxAsync(pe => pe.Id, ct)
                    : 0;

                if (maxPulledEventId >= maxEventId && totalEvents > 0)
                {
                    // All data has been synced — safe to clear. heat_completions is included
                    // here because it's per-race state (one row per heat finish/cancel) and
                    // would otherwise leak across test instances.
                    await _db.Database.ExecuteSqlRawAsync(
                        "TRUNCATE TABLE processed_events, raw_tag_buffer, race_start_times, received_sync_data, sync_logs, heat_completions CASCADE", ct);
                }
                // If not fully synced, old data remains (will be collected on next pull)
            }

            // Truncate config tables. race_start_times is intentionally NOT truncated here:
            // gun starts are event-scoped now (race_start_times.event_id + test_instance_id),
            // so re-pushing / updating the same test instance preserves already-collected
            // starts, and the finish processor still filters out anything from a different
            // event. Race data is only wiped on an actual test-instance switch (the
            // conditional block above). operator_group cascades to
            // operator_group_candidate / operator_group_assignment via FK.
            await _db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE scoring_statuses, scoring_types, test_events, checkpoint_config, tag_assignments, candidates, operator_group CASCADE", ct);

            // Insert candidates
            foreach (var c in config.Candidates)
            {
                _db.Candidates.Add(new CandidateEntity
                {
                    CandidateId = c.CandidateId,
                    ServiceNumber = c.ServiceNumber,
                    Name = c.Name,
                    Gender = c.Gender,
                    CandidateTypeId = c.CandidateTypeId,
                    DateOfBirth = c.DateOfBirth,
                    JacketNumber = c.JacketNumber
                });
            }

            // Insert tag assignments
            foreach (var c in config.Candidates.Where(c => c.TagEPC is not null))
            {
                _db.TagAssignments.Add(new TagAssignmentEntity
                {
                    Id = Guid.NewGuid(),
                    CandidateId = c.CandidateId,
                    TagEPC = c.TagEPC!
                });
            }

            // Insert checkpoint config
            foreach (var route in config.CheckpointRoutes)
            {
                foreach (var item in route.Items)
                {
                    _db.CheckpointConfigs.Add(new CheckpointConfig
                    {
                        RouteName = route.RouteName,
                        Sequence = item.Sequence,
                        CheckpointName = item.Name
                    });
                }
            }

            // Insert scoring types
            foreach (var st in config.ScoringTypes)
            {
                _db.ScoringTypes.Add(new ScoringTypeEntity
                {
                    ScoringTypeId = st.ScoringTypeId,
                    Name = st.Name
                });
            }

            // Insert scoring statuses
            foreach (var st in config.ScoringTypes)
            {
                foreach (var ss in st.Statuses)
                {
                    _db.ScoringStatuses.Add(new ScoringStatusEntity
                    {
                        ScoringStatusId = ss.ScoringStatusId,
                        ScoringTypeId = st.ScoringTypeId,
                        StatusCode = ss.StatusCode,
                        StatusLabel = ss.StatusLabel,
                        Sequence = ss.Sequence,
                        IsPassingStatus = ss.IsPassingStatus
                    });
                }
            }

            // Insert test events
            foreach (var e in config.Events)
            {
                _db.TestEvents.Add(new TestEventEntity
                {
                    TestTypeEventId = e.TestTypeEventId,
                    EventId = e.EventId,
                    EventName = e.EventName,
                    EventType = e.EventType,
                    Sequence = e.Sequence,
                    ScoringTypeId = e.ScoringTypeId
                });
            }

            // ── Operator groups + assignments ────────────────────────────────────
            // Persisted on the gate so:
            //   1. The finish display can show per-group counters (Phase 6 wires the read-side).
            //   2. /gate/operator-groups exposes the active state to other HHTs picking
            //      their selection — supports decision #3 overlap warnings.
            // Truncated above as part of the config-table TRUNCATE (operator_group cascade).
            foreach (var group in config.OperatorGroups)
            {
                _db.OperatorGroups.Add(new OperatorGroupEntity
                {
                    GroupId = group.GroupId,
                    Name = group.Name,
                    // Denormalised array — fast-path for set-membership checks during
                    // finish processing without joining to operator_group_candidate.
                    CandidateIds = group.CandidateIds.ToArray()
                });

                foreach (var candidateId in group.CandidateIds)
                {
                    _db.OperatorGroupCandidates.Add(new OperatorGroupCandidateEntity
                    {
                        GroupId = group.GroupId,
                        CandidateId = candidateId
                    });
                }
            }

            foreach (var assignment in config.OperatorGroupAssignments)
            {
                // Skip orphaned assignments — defensive against stale Main payloads
                // where a group was deleted but its assignments still ship.
                if (config.OperatorGroups.All(g => g.GroupId != assignment.GroupId))
                    continue;

                _db.OperatorGroupAssignments.Add(new OperatorGroupAssignmentEntity
                {
                    GroupId = assignment.GroupId,
                    DeviceCode = assignment.DeviceCode
                });
            }

            await _db.SaveChangesAsync(ct);

            // Backfill denormalized candidate info on existing ProcessedEvents
            // (fixes null jacket_number / candidate_name from events processed before candidate data was available)
            await _db.Database.ExecuteSqlRawAsync(@"
                UPDATE processed_events
                SET candidate_name = c.name,
                    jacket_number  = c.jacket_number
                FROM candidates c
                WHERE processed_events.candidate_id = c.candidate_id
                  AND (processed_events.jacket_number IS NULL
                       OR processed_events.candidate_name IS NULL
                       OR processed_events.candidate_name = '')", ct);

            // Upsert gate_config — deactivate old, insert new
            await _db.GateConfigs
                .Where(g => g.IsActive)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.IsActive, false), ct);

            // Clock offset = server_time - local_time (ms).
            // Guard against missing/default ServerReferenceTime (0001-01-01) which
            // would overflow int and produce a garbage offset.
            var clockOffsetMs = config.ServerReferenceTime.Year > 2000
                ? (int)Math.Clamp((config.ServerReferenceTime - DateTime.UtcNow).TotalMilliseconds,
                    int.MinValue, int.MaxValue)
                : 0;

            _db.GateConfigs.Add(new GateConfig
            {
                TestInstanceId = config.TestInstanceId,
                TestInstanceName = config.TestInstanceName,
                DeviceId = thisGate.DeviceId,
                DeviceCode = _deviceCode,
                // GateRole + CheckpointSequence are sourced from gate_identity, NOT the config-package.
                // Kept on gate_config as a denormalized read cache for legacy consumers; identity is
                // the single source of truth.
                GateRole = identity.Role,
                CheckpointSequence = identity.CheckpointSequence,
                ActiveEventId = thisGate.EventId,
                ActiveEventName = thisGate.EventId.HasValue
                    ? config.Events.FirstOrDefault(e => e.EventId == thisGate.EventId.Value)?.EventName
                    : null,
                ScheduledDate = config.ScheduledDate,
                DataSnapshotVersion = config.DataSnapshotVersion,
                ClockOffsetMs = clockOffsetMs,
                IsActive = true,
                ReceivedAt = DateTimeOffset.UtcNow
            });

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            // Fire NOTIFY config_updated
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                $"NOTIFY config_updated, '{System.Text.Json.JsonSerializer.Serialize(new { gateRole = identity.Role, candidateCount = config.Candidates.Count })}'",
                conn);
            await cmd.ExecuteNonQueryAsync(ct);

            return ConfigResult.Ok(identity.Role, config.Candidates.Count);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);
            return ConfigResult.Fail($"Configuration failed: {ex.Message}");
        }
    }
}
