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
        // Nullable: checkpoint events never resolve a candidate (no tag_assignments).
        builder.Property(x => x.CandidateId).HasColumnName("candidate_id");
        builder.Property(x => x.TagEPC).HasColumnName("tag_epc").HasMaxLength(64).IsRequired();
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(20).IsRequired();
        builder.Property(x => x.EventId).HasColumnName("event_id");
        builder.Property(x => x.ReadTime).HasColumnName("read_time").IsRequired();
        builder.Property(x => x.DurationSeconds).HasColumnName("duration_seconds").HasPrecision(10, 3);
        builder.Property(x => x.CheckpointSequence).HasColumnName("checkpoint_sequence");
        builder.Property(x => x.HeatNumber).HasColumnName("heat_number");
        builder.Property(x => x.IsFirstRead).HasColumnName("is_first_read").HasDefaultValue(true).IsRequired();
        builder.Property(x => x.CandidateName).HasColumnName("candidate_name").HasMaxLength(200);
        builder.Property(x => x.JacketNumber).HasColumnName("jacket_number");
        builder.Property(x => x.RawBufferId).HasColumnName("raw_buffer_id");
        builder.Property(x => x.ProcessedAt).HasColumnName("processed_at").IsRequired();
        builder.Property(x => x.Voided).HasColumnName("voided").HasDefaultValue(false).IsRequired();
        builder.HasIndex(x => x.CandidateId).HasDatabaseName("idx_processed_events_candidate");
        builder.HasIndex(x => x.EventType).HasDatabaseName("idx_processed_events_type");
        builder.HasIndex(x => x.EventId).HasDatabaseName("idx_processed_events_event_id");
        // Partial index — most queries care only about non-voided rows. Keeps
        // dedup and display-feed scans cheap even after lots of cancellations.
        builder.HasIndex(x => x.Voided)
            .HasDatabaseName("idx_processed_events_active")
            .HasFilter("voided = false");
        // Optional FK — checkpoint rows have CandidateId = null. SetNull on candidate
        // delete (defensive; candidates aren't deleted in normal ops).
        builder.HasOne(x => x.Candidate)
            .WithMany()
            .HasForeignKey(x => x.CandidateId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);
        builder.HasOne(x => x.RawTag)
            .WithMany()
            .HasForeignKey(x => x.RawBufferId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
