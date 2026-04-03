using APTM.Gate.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace APTM.Gate.Infrastructure.Persistence.Configurations;

public class ReceivedSyncDataConfiguration : IEntityTypeConfiguration<ReceivedSyncData>
{
    public void Configure(EntityTypeBuilder<ReceivedSyncData> builder)
    {
        builder.ToTable("received_sync_data");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.ClientRecordId).HasColumnName("client_record_id").HasMaxLength(64).IsRequired();
        builder.HasIndex(x => x.ClientRecordId).IsUnique().HasDatabaseName("idx_received_sync_client_record");
        builder.Property(x => x.SourceDeviceId).HasColumnName("source_device_id").IsRequired();
        builder.Property(x => x.SourceDeviceCode).HasColumnName("source_device_code").HasMaxLength(50).IsRequired();
        builder.Property(x => x.DataType).HasColumnName("data_type").HasMaxLength(30).IsRequired();
        builder.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.ReceivedAt).HasColumnName("received_at").IsRequired();
    }
}
