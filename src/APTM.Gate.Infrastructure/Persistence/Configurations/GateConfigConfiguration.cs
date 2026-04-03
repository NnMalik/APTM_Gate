using APTM.Gate.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace APTM.Gate.Infrastructure.Persistence.Configurations;

public class GateConfigConfiguration : IEntityTypeConfiguration<GateConfig>
{
    public void Configure(EntityTypeBuilder<GateConfig> builder)
    {
        builder.ToTable("gate_config");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.TestInstanceId).HasColumnName("test_instance_id").IsRequired();
        builder.Property(x => x.TestInstanceName).HasColumnName("test_instance_name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.DeviceId).HasColumnName("device_id").IsRequired();
        builder.Property(x => x.DeviceCode).HasColumnName("device_code").HasMaxLength(50).IsRequired();
        builder.Property(x => x.GateRole).HasColumnName("gate_role").HasMaxLength(20).IsRequired();
        builder.Property(x => x.CheckpointSequence).HasColumnName("checkpoint_sequence");
        builder.Property(x => x.ScheduledDate).HasColumnName("scheduled_date").IsRequired();
        builder.Property(x => x.DataSnapshotVersion).HasColumnName("data_snapshot_version").IsRequired();
        builder.Property(x => x.ClockOffsetMs).HasColumnName("clock_offset_ms").HasDefaultValue(0);
        builder.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true).IsRequired();
        builder.Property(x => x.ReceivedAt).HasColumnName("received_at").IsRequired();
    }
}
