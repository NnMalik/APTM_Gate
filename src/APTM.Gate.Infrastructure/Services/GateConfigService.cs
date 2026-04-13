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
    private readonly string _deviceCode;
    private readonly string _connectionString;

    public GateConfigService(GateDbContext db, IConfiguration configuration)
    {
        _db = db;
        _deviceCode = configuration["Gate:DeviceCode"] ?? throw new InvalidOperationException("Gate:DeviceCode not configured");
        _connectionString = configuration.GetConnectionString("GateDb") ?? throw new InvalidOperationException("GateDb connection string not configured");
    }

    public async Task<ConfigResult> ApplyConfigAsync(ConfigPackageDto config, CancellationToken ct = default)
    {
        // Find this gate in the config package
        var thisGate = config.Gates.FirstOrDefault(g =>
            string.Equals(g.DeviceCode, _deviceCode, StringComparison.OrdinalIgnoreCase));

        if (thisGate is null)
            return ConfigResult.Fail($"Gate '{_deviceCode}' not found in config package gates list.");

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
                    // All data has been synced — safe to clear
                    await _db.Database.ExecuteSqlRawAsync(
                        "TRUNCATE TABLE processed_events, raw_tag_buffer, race_start_times, received_sync_data, sync_logs CASCADE", ct);
                }
                // If not fully synced, old data remains (will be collected on next pull)
            }

            // Truncate config tables + race start times (stale starts from previous events must not affect new elapsed calculations)
            await _db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE scoring_statuses, scoring_types, test_events, checkpoint_config, tag_assignments, candidates, race_start_times CASCADE", ct);

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

            await _db.SaveChangesAsync(ct);

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
                GateRole = thisGate.GateType,
                CheckpointSequence = thisGate.CheckpointSequence,
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
                $"NOTIFY config_updated, '{System.Text.Json.JsonSerializer.Serialize(new { gateRole = thisGate.GateType, candidateCount = config.Candidates.Count })}'",
                conn);
            await cmd.ExecuteNonQueryAsync(ct);

            return ConfigResult.Ok(thisGate.GateType, config.Candidates.Count);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);
            return ConfigResult.Fail($"Configuration failed: {ex.Message}");
        }
    }
}
