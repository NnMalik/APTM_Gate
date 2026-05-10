using APTM.Gate.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace APTM.Gate.Infrastructure.Persistence.Configurations;

public class ReaderConfigEntityConfiguration : IEntityTypeConfiguration<ReaderConfigEntity>
{
    public void Configure(EntityTypeBuilder<ReaderConfigEntity> builder)
    {
        builder.ToTable("reader_config");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.Host).HasColumnName("host").HasMaxLength(128).IsRequired();
        builder.Property(x => x.Port).HasColumnName("port").IsRequired();
        builder.Property(x => x.DefaultPower).HasColumnName("default_power").IsRequired();
        builder.Property(x => x.EpcFilterBits).HasColumnName("epc_filter_bits").IsRequired();
        builder.Property(x => x.ReconnectDelayMs).HasColumnName("reconnect_delay_ms").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.UpdatedBy).HasColumnName("updated_by").HasMaxLength(128).IsRequired();
    }
}
