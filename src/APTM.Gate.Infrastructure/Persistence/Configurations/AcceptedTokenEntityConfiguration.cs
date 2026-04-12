using APTM.Gate.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace APTM.Gate.Infrastructure.Persistence.Configurations;

public class AcceptedTokenEntityConfiguration : IEntityTypeConfiguration<AcceptedTokenEntity>
{
    public void Configure(EntityTypeBuilder<AcceptedTokenEntity> builder)
    {
        builder.ToTable("accepted_tokens");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Token).HasColumnName("token").HasMaxLength(128).IsRequired();
        builder.Property(x => x.Label).HasColumnName("label").HasMaxLength(256).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(x => x.Token)
            .IsUnique()
            .HasDatabaseName("idx_accepted_tokens_token");
    }
}
