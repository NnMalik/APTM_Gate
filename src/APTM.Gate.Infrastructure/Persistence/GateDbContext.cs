using APTM.Gate.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace APTM.Gate.Infrastructure.Persistence;

public class GateDbContext : DbContext
{
    public GateDbContext(DbContextOptions<GateDbContext> options) : base(options) { }

    public DbSet<GateConfig> GateConfigs => Set<GateConfig>();
    public DbSet<GateIdentity> GateIdentities => Set<GateIdentity>();
    public DbSet<CandidateEntity> Candidates => Set<CandidateEntity>();
    public DbSet<TagAssignmentEntity> TagAssignments => Set<TagAssignmentEntity>();
    public DbSet<CheckpointConfig> CheckpointConfigs => Set<CheckpointConfig>();
    public DbSet<RawTagBuffer> RawTagBuffers => Set<RawTagBuffer>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<ReceivedSyncData> ReceivedSyncData => Set<ReceivedSyncData>();
    public DbSet<RaceStartTime> RaceStartTimes => Set<RaceStartTime>();
    public DbSet<HeatCompletion> HeatCompletions => Set<HeatCompletion>();
    public DbSet<SyncLogEntry> SyncLogs => Set<SyncLogEntry>();
    public DbSet<ScoringTypeEntity> ScoringTypes => Set<ScoringTypeEntity>();
    public DbSet<ScoringStatusEntity> ScoringStatuses => Set<ScoringStatusEntity>();
    public DbSet<TestEventEntity> TestEvents => Set<TestEventEntity>();
    public DbSet<AcceptedTokenEntity> AcceptedTokens => Set<AcceptedTokenEntity>();
    public DbSet<ReaderConfigEntity> ReaderConfigs => Set<ReaderConfigEntity>();
    public DbSet<EpcFilterEntity> EpcFilters => Set<EpcFilterEntity>();
    public DbSet<OperatorGroupEntity> OperatorGroups => Set<OperatorGroupEntity>();
    public DbSet<OperatorGroupCandidateEntity> OperatorGroupCandidates => Set<OperatorGroupCandidateEntity>();
    public DbSet<OperatorGroupAssignmentEntity> OperatorGroupAssignments => Set<OperatorGroupAssignmentEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(GateDbContext).Assembly);
    }
}
