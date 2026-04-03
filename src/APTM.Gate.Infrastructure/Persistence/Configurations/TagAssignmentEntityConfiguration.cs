using APTM.Gate.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace APTM.Gate.Infrastructure.Persistence.Configurations;

public class TagAssignmentEntityConfiguration : IEntityTypeConfiguration<TagAssignmentEntity>
{
    public void Configure(EntityTypeBuilder<TagAssignmentEntity> builder)
    {
        builder.ToTable("tag_assignments");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.CandidateId).HasColumnName("candidate_id").IsRequired();
        builder.Property(x => x.TagEPC).HasColumnName("tag_epc").HasMaxLength(64).IsRequired();
        builder.HasIndex(x => x.TagEPC).IsUnique().HasDatabaseName("idx_tag_assignments_epc");
        builder.HasOne(x => x.Candidate).WithMany().HasForeignKey(x => x.CandidateId);
    }
}
