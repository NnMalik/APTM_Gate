using APTM.Gate.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace APTM.Gate.Infrastructure.Persistence.Configurations;

public class EpcFilterEntityConfiguration : IEntityTypeConfiguration<EpcFilterEntity>
{
    public void Configure(EntityTypeBuilder<EpcFilterEntity> builder)
    {
        builder.ToTable("epc_filter");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.Enabled).HasColumnName("enabled").IsRequired();
        builder.Property(x => x.RangeStart).HasColumnName("range_start").HasMaxLength(64);
        builder.Property(x => x.RangeEnd).HasColumnName("range_end").HasMaxLength(64);
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.UpdatedBy).HasColumnName("updated_by").HasMaxLength(128).IsRequired();
    }
}
