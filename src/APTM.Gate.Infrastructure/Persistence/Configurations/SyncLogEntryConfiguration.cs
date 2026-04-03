using APTM.Gate.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace APTM.Gate.Infrastructure.Persistence.Configurations;

public class SyncLogEntryConfiguration : IEntityTypeConfiguration<SyncLogEntry>
{
    public void Configure(EntityTypeBuilder<SyncLogEntry> builder)
    {
        builder.ToTable("sync_log");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.PullerDeviceId).HasColumnName("puller_device_id").IsRequired();
        builder.Property(x => x.PullerDeviceCode).HasColumnName("puller_device_code").HasMaxLength(50).IsRequired();
        builder.Property(x => x.LastProcessedEventId).HasColumnName("last_processed_event_id").HasDefaultValue(0L).IsRequired();
        builder.Property(x => x.LastReceivedSyncId).HasColumnName("last_received_sync_id");
        builder.Property(x => x.PulledAt).HasColumnName("pulled_at").IsRequired();
    }
}
