using AgentAcademy.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentAcademy.Server.Data.Configurations;

internal sealed class LearningDigestConfiguration : IEntityTypeConfiguration<LearningDigestEntity>
{
    public void Configure(EntityTypeBuilder<LearningDigestEntity> entity)
    {
        entity.ToTable("learning_digests");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.Property(e => e.Summary).IsRequired();
        entity.HasMany(e => e.Sources)
            .WithOne(s => s.Digest)
            .HasForeignKey(s => s.DigestId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class LearningDigestSourceConfiguration : IEntityTypeConfiguration<LearningDigestSourceEntity>
{
    public void Configure(EntityTypeBuilder<LearningDigestSourceEntity> entity)
    {
        entity.ToTable("learning_digest_sources");
        entity.HasKey(e => new { e.DigestId, e.RetrospectiveCommentId });

        entity.HasOne(e => e.RetrospectiveComment)
            .WithMany()
            .HasForeignKey(e => e.RetrospectiveCommentId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => e.RetrospectiveCommentId)
            .IsUnique()
            .HasDatabaseName("idx_digest_sources_retro_unique");
    }
}
