using APTM.Gate.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace APTM.Gate.Infrastructure.Persistence.Configurations;

public class ProcessedEventConfiguration : IEntityTypeConfiguration<ProcessedEvent>
{
    public void Configure(EntityTypeBuilder<ProcessedEvent> builder)
    {
        builder.ToTable("processed_events");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.CandidateId).HasColumnName("candidate_id").IsRequired();
        builder.Property(x => x.TagEPC).HasColumnName("tag_epc").HasMaxLength(64).IsRequired();
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(20).IsRequired();
        builder.Property(x => x.EventId).HasColumnName("event_id");
        builder.Property(x => x.ReadTime).HasColumnName("read_time").IsRequired();
        builder.Property(x => x.DurationSeconds).HasColumnName("duration_seconds").HasPrecision(10, 3);
        builder.Property(x => x.CheckpointSequence).HasColumnName("checkpoint_sequence");
        builder.Property(x => x.IsFirstRead).HasColumnName("is_first_read").HasDefaultValue(true).IsRequired();
        builder.Property(x => x.RawBufferId).HasColumnName("raw_buffer_id");
        builder.Property(x => x.ProcessedAt).HasColumnName("processed_at").IsRequired();
        builder.HasIndex(x => x.CandidateId).HasDatabaseName("idx_processed_events_candidate");
        builder.HasIndex(x => x.EventType).HasDatabaseName("idx_processed_events_type");
        builder.HasIndex(x => x.EventId).HasDatabaseName("idx_processed_events_event_id");
        builder.HasOne(x => x.Candidate).WithMany().HasForeignKey(x => x.CandidateId);
        builder.HasOne(x => x.RawTag).WithMany().HasForeignKey(x => x.RawBufferId);
    }
}
