using APTM.Gate.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace APTM.Gate.Infrastructure.Persistence.Configurations;

public class ScoringTypeEntityConfiguration : IEntityTypeConfiguration<ScoringTypeEntity>
{
    public void Configure(EntityTypeBuilder<ScoringTypeEntity> builder)
    {
        builder.ToTable("scoring_types");
        builder.HasKey(x => x.ScoringTypeId);
        builder.Property(x => x.ScoringTypeId).HasColumnName("scoring_type_id").ValueGeneratedNever();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
    }
}
