using APTM.Gate.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace APTM.Gate.Infrastructure.Persistence.Configurations;

public class CheckpointConfigConfiguration : IEntityTypeConfiguration<CheckpointConfig>
{
    public void Configure(EntityTypeBuilder<CheckpointConfig> builder)
    {
        builder.ToTable("checkpoint_config");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        builder.Property(x => x.RouteName).HasColumnName("route_name").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Sequence).HasColumnName("sequence").IsRequired();
        builder.Property(x => x.CheckpointName).HasColumnName("checkpoint_name").HasMaxLength(100).IsRequired();
    }
}
