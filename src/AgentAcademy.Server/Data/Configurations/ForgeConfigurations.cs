using AgentAcademy.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentAcademy.Server.Data.Configurations;

internal sealed class ForgeJobConfiguration : IEntityTypeConfiguration<ForgeJobEntity>
{
    public void Configure(EntityTypeBuilder<ForgeJobEntity> entity)
    {
        entity.ToTable("forge_jobs");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Status).IsRequired().HasDefaultValue("queued");
        entity.Property(e => e.TaskBriefJson).IsRequired().HasDefaultValue("{}");
        entity.Property(e => e.MethodologyJson).IsRequired().HasDefaultValue("{}");
        entity.Property(e => e.CreatedAt).IsRequired();

        entity.HasIndex(e => e.Status).HasDatabaseName("idx_forge_jobs_status");
        entity.HasIndex(e => e.CreatedAt).HasDatabaseName("idx_forge_jobs_created");
    }
}
