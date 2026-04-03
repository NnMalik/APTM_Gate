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
            // Truncate config tables
            await _db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE scoring_statuses, scoring_types, test_events, checkpoint_config, tag_assignments, candidates CASCADE", ct);

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

            var clockOffsetMs = (int)(config.ServerReferenceTime - DateTime.UtcNow).TotalMilliseconds;

            _db.GateConfigs.Add(new GateConfig
            {
                TestInstanceId = config.TestInstanceId,
                TestInstanceName = config.TestInstanceName,
                DeviceId = thisGate.DeviceId,
                DeviceCode = _deviceCode,
                GateRole = thisGate.GateType,
                CheckpointSequence = thisGate.CheckpointSequence,
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
