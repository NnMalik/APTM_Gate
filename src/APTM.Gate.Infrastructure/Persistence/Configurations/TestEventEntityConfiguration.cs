using APTM.Gate.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace APTM.Gate.Infrastructure.Persistence.Configurations;

public class TestEventEntityConfiguration : IEntityTypeConfiguration<TestEventEntity>
{
    public void Configure(EntityTypeBuilder<TestEventEntity> builder)
    {
        builder.ToTable("test_events");
        builder.HasKey(x => x.TestTypeEventId);
        builder.Property(x => x.TestTypeEventId).HasColumnName("test_type_event_id").ValueGeneratedNever();
        builder.Property(x => x.EventId).HasColumnName("event_id").IsRequired();
        builder.Property(x => x.EventName).HasColumnName("event_name").HasMaxLength(100).IsRequired();
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Sequence).HasColumnName("sequence").IsRequired();
        builder.Property(x => x.ScoringTypeId).HasColumnName("scoring_type_id");
        builder.HasOne(x => x.ScoringType).WithMany().HasForeignKey(x => x.ScoringTypeId);
    }
}
