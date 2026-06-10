using APTM.Gate.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace APTM.Gate.Infrastructure.Persistence.Configurations;

public class GateIdentityConfiguration : IEntityTypeConfiguration<GateIdentity>
{
    public void Configure(EntityTypeBuilder<GateIdentity> builder)
    {
        builder.ToTable("gate_identity", t =>
        {
            // Single-row table — enforce at the DB level so accidental inserts fail loudly.
            t.HasCheckConstraint("ck_gate_identity_singleton", "id = 1");
            // Checkpoint role must have a checkpoint_sequence; other roles must not.
            t.HasCheckConstraint(
                "ck_gate_identity_checkpoint_sequence",
                "(role = 'Checkpoint' AND checkpoint_sequence IS NOT NULL) OR (role <> 'Checkpoint' AND checkpoint_sequence IS NULL)");
            // Role must be one of the known values.
            t.HasCheckConstraint(
                "ck_gate_identity_role",
                "role IN ('Start', 'Checkpoint', 'Finish')");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.Role).HasColumnName("role").HasMaxLength(20).IsRequired();
        builder.Property(x => x.CheckpointSequence).HasColumnName("checkpoint_sequence");
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(100);
        builder.Property(x => x.DeviceCode).HasColumnName("device_code").HasMaxLength(50).IsRequired();
        builder.Property(x => x.SetAt).HasColumnName("set_at").IsRequired();
        builder.Property(x => x.SetBy).HasColumnName("set_by").HasMaxLength(256).IsRequired();
    }
}
