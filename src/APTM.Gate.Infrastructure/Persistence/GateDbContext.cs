using APTM.Gate.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace APTM.Gate.Infrastructure.Persistence;

public class GateDbContext : DbContext
{
    public GateDbContext(DbContextOptions<GateDbContext> options) : base(options) { }

    public DbSet<GateConfig> GateConfigs => Set<GateConfig>();
    public DbSet<CandidateEntity> Candidates => Set<CandidateEntity>();
    public DbSet<TagAssignmentEntity> TagAssignments => Set<TagAssignmentEntity>();
    public DbSet<CheckpointConfig> CheckpointConfigs => Set<CheckpointConfig>();
    public DbSet<RawTagBuffer> RawTagBuffers => Set<RawTagBuffer>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<ReceivedSyncData> ReceivedSyncData => Set<ReceivedSyncData>();
    public DbSet<RaceStartTime> RaceStartTimes => Set<RaceStartTime>();
    public DbSet<SyncLogEntry> SyncLogs => Set<SyncLogEntry>();
    public DbSet<ScoringTypeEntity> ScoringTypes => Set<ScoringTypeEntity>();
    public DbSet<ScoringStatusEntity> ScoringStatuses => Set<ScoringStatusEntity>();
    public DbSet<TestEventEntity> TestEvents => Set<TestEventEntity>();
    public DbSet<AcceptedTokenEntity> AcceptedTokens => Set<AcceptedTokenEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(GateDbContext).Assembly);
    }
}
