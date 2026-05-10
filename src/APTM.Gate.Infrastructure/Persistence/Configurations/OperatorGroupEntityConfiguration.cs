using APTM.Gate.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace APTM.Gate.Infrastructure.Persistence.Configurations;

public class OperatorGroupEntityConfiguration : IEntityTypeConfiguration<OperatorGroupEntity>
{
    public void Configure(EntityTypeBuilder<OperatorGroupEntity> builder)
    {
        builder.ToTable("operator_group");
        builder.HasKey(x => x.GroupId);
        builder.Property(x => x.GroupId).HasColumnName("group_id");
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();

        // PostgreSQL native uuid[] — Npgsql converts Guid[] transparently.
        // Stored denormalized for set-membership queries during finish processing.
        builder.Property(x => x.CandidateIds)
            .HasColumnName("candidate_ids")
            .HasColumnType("uuid[]")
            .IsRequired();
    }
}

public class OperatorGroupCandidateEntityConfiguration : IEntityTypeConfiguration<OperatorGroupCandidateEntity>
{
    public void Configure(EntityTypeBuilder<OperatorGroupCandidateEntity> builder)
    {
        builder.ToTable("operator_group_candidate");
        builder.HasKey(x => new { x.GroupId, x.CandidateId });
        builder.Property(x => x.GroupId).HasColumnName("group_id");
        builder.Property(x => x.CandidateId).HasColumnName("candidate_id");

        builder.HasIndex(x => x.CandidateId)
            .HasDatabaseName("idx_operator_group_candidate_candidate");
    }
}

public class OperatorGroupAssignmentEntityConfiguration : IEntityTypeConfiguration<OperatorGroupAssignmentEntity>
{
    public void Configure(EntityTypeBuilder<OperatorGroupAssignmentEntity> builder)
    {
        builder.ToTable("operator_group_assignment");
        builder.HasKey(x => new { x.GroupId, x.DeviceCode });
        builder.Property(x => x.GroupId).HasColumnName("group_id");
        builder.Property(x => x.DeviceCode)
            .HasColumnName("device_code")
            .HasMaxLength(50)
            .IsRequired();

        builder.HasIndex(x => x.DeviceCode)
            .HasDatabaseName("idx_operator_group_assignment_device");
    }
}
