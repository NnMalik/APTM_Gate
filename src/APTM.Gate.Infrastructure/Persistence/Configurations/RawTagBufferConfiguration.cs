using APTM.Gate.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace APTM.Gate.Infrastructure.Persistence.Configurations;

public class RawTagBufferConfiguration : IEntityTypeConfiguration<RawTagBuffer>
{
    public void Configure(EntityTypeBuilder<RawTagBuffer> builder)
    {
        builder.ToTable("raw_tag_buffer");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.TagEPC).HasColumnName("tag_epc").HasMaxLength(64).IsRequired();
        builder.Property(x => x.ReadTime).HasColumnName("read_time").IsRequired();
        builder.Property(x => x.AntennaPort).HasColumnName("antenna_port");
        builder.Property(x => x.RSSI).HasColumnName("rssi").HasPrecision(6, 2);
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).HasDefaultValue("PENDING").IsRequired();
        builder.Property(x => x.IsDuplicate).HasColumnName("is_duplicate").HasDefaultValue(false).IsRequired();
        builder.Property(x => x.InsertedAt).HasColumnName("inserted_at").IsRequired();
        builder.HasIndex(x => x.Status).HasDatabaseName("idx_raw_tag_buffer_status").HasFilter("status = 'PENDING'");
    }
}
