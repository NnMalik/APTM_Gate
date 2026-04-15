using APTM.Gate.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace APTM.Gate.Infrastructure.Persistence.Configurations;

public class RaceStartTimeConfiguration : IEntityTypeConfiguration<RaceStartTime>
{
    public void Configure(EntityTypeBuilder<RaceStartTime> builder)
    {
        builder.ToTable("race_start_times");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.HeatId).HasColumnName("heat_id").IsRequired();
        builder.HasIndex(x => x.HeatId).IsUnique();
        builder.Property(x => x.HeatNumber).HasColumnName("heat_number").IsRequired();
        builder.Property(x => x.GunStartTime).HasColumnName("gun_start_time").IsRequired();
        builder.Property(x => x.OriginalGunStartTime).HasColumnName("original_gun_start_time").IsRequired();
        builder.Property(x => x.SourceDeviceId).HasColumnName("source_device_id").IsRequired();
        builder.Property(x => x.CandidateIds).HasColumnName("candidate_ids").IsRequired();
        builder.Property(x => x.SourceClockOffsetMs).HasColumnName("source_clock_offset_ms").HasDefaultValue(0);
        builder.Property(x => x.ReceivedAt).HasColumnName("received_at").IsRequired();
    }
}
