using APTM.Gate.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace APTM.Gate.Infrastructure.Persistence.Configurations;

public class HeatCompletionConfiguration : IEntityTypeConfiguration<HeatCompletion>
{
    public void Configure(EntityTypeBuilder<HeatCompletion> builder)
    {
        builder.ToTable("heat_completions");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");

        builder.Property(x => x.HeatId).HasColumnName("heat_id").IsRequired();
        // One completion per heat — unique constraint also serves as race-condition guard
        // when concurrent finish-event batches attempt to insert.
        builder.HasIndex(x => x.HeatId)
            .IsUnique()
            .HasDatabaseName("idx_heat_completions_heat_id");

        builder.Property(x => x.HeatNumber).HasColumnName("heat_number").IsRequired();
        builder.Property(x => x.ExpectedCount).HasColumnName("expected_count").IsRequired();
        builder.Property(x => x.FinishedCount).HasColumnName("finished_count").IsRequired();
        builder.Property(x => x.LastCandidateId).HasColumnName("last_candidate_id");
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at").IsRequired();
        builder.Property(x => x.ClosureReason)
            .HasColumnName("closure_reason")
            .HasMaxLength(20)
            .HasDefaultValue("auto")
            .IsRequired();
        builder.Property(x => x.SourceDeviceCode)
            .HasColumnName("source_device_code")
            .HasMaxLength(50)
            .IsRequired();
        builder.Property(x => x.ReceivedAt).HasColumnName("received_at").IsRequired();
    }
}
