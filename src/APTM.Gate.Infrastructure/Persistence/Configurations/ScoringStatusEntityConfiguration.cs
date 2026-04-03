using APTM.Gate.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace APTM.Gate.Infrastructure.Persistence.Configurations;

public class ScoringStatusEntityConfiguration : IEntityTypeConfiguration<ScoringStatusEntity>
{
    public void Configure(EntityTypeBuilder<ScoringStatusEntity> builder)
    {
        builder.ToTable("scoring_statuses");
        builder.HasKey(x => x.ScoringStatusId);
        builder.Property(x => x.ScoringStatusId).HasColumnName("scoring_status_id").ValueGeneratedNever();
        builder.Property(x => x.ScoringTypeId).HasColumnName("scoring_type_id").IsRequired();
        builder.Property(x => x.StatusCode).HasColumnName("status_code").HasMaxLength(30).IsRequired();
        builder.Property(x => x.StatusLabel).HasColumnName("status_label").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Sequence).HasColumnName("sequence").IsRequired();
        builder.Property(x => x.IsPassingStatus).HasColumnName("is_passing_status").IsRequired();
        builder.HasOne(x => x.ScoringType).WithMany(s => s.ScoringStatuses).HasForeignKey(x => x.ScoringTypeId);
    }
}
