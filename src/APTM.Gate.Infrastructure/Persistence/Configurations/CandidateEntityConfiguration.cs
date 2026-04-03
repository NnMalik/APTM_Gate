using APTM.Gate.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace APTM.Gate.Infrastructure.Persistence.Configurations;

public class CandidateEntityConfiguration : IEntityTypeConfiguration<CandidateEntity>
{
    public void Configure(EntityTypeBuilder<CandidateEntity> builder)
    {
        builder.ToTable("candidates");
        builder.HasKey(x => x.CandidateId);
        builder.Property(x => x.CandidateId).HasColumnName("candidate_id");
        builder.Property(x => x.ServiceNumber).HasColumnName("service_number").HasMaxLength(50).IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.Gender).HasColumnName("gender").HasMaxLength(10).IsRequired();
        builder.Property(x => x.CandidateTypeId).HasColumnName("candidate_type_id").IsRequired();
        builder.Property(x => x.DateOfBirth).HasColumnName("date_of_birth").IsRequired();
        builder.Property(x => x.JacketNumber).HasColumnName("jacket_number");
    }
}
