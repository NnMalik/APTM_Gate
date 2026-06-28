using APTM.Gate.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace APTM.Gate.Infrastructure.Persistence.Configurations;

public class RaceStartTimeConfiguration : IEntityTypeConfiguration<RaceStartTime>
{
    public void Configure(EntityTypeBuilder<RaceStartTime> builder)
    {
        builder.ToTable("race_start_times");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.HeatId).HasColumnName("heat_id").IsRequired();
        builder.HasIndex(x => x.HeatId).IsUnique();
        builder.Property(x => x.HeatNumber).HasColumnName("heat_number").IsRequired();
        builder.Property(x => x.GunStartTime).HasColumnName("gun_start_time").IsRequired();
        builder.Property(x => x.OriginalGunStartTime).HasColumnName("original_gun_start_time").IsRequired();
        builder.Property(x => x.SourceDeviceId).HasColumnName("source_device_id").IsRequired();
        builder.Property(x => x.SourceDeviceCode).HasColumnName("source_device_code").HasMaxLength(64);
        builder.Property(x => x.CandidateIds).HasColumnName("candidate_ids").IsRequired();
        builder.Property(x => x.SourceClockOffsetMs).HasColumnName("source_clock_offset_ms").HasDefaultValue(0);
        builder.Property(x => x.ReceivedAt).HasColumnName("received_at").IsRequired();
        builder.Property(x => x.AppliedOffsetMs).HasColumnName("applied_offset_ms");
        builder.Property(x => x.OffsetMethod).HasColumnName("offset_method").HasMaxLength(16);
        builder.Property(x => x.GroupId).HasColumnName("group_id");
        builder.Property(x => x.EventId).HasColumnName("event_id");
        builder.Property(x => x.TestInstanceId).HasColumnName("test_instance_id");

        // Index on group_id for the per-group display counter query
        // ("how many heats has Group A fired?"). Non-unique — one group can fire many heats.
        builder.HasIndex(x => x.GroupId)
            .HasDatabaseName("idx_race_start_times_group_id");

        // Composite index for the finish processor's per-event race-start lookup
        // (scoped by test instance + event). Non-unique — one event has many heats.
        builder.HasIndex(x => new { x.TestInstanceId, x.EventId })
            .HasDatabaseName("idx_race_start_times_test_event");
    }
}
