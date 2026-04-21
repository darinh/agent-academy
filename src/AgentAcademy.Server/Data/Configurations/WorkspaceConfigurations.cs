using AgentAcademy.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentAcademy.Server.Data.Configurations;

internal sealed class WorkspaceConfiguration : IEntityTypeConfiguration<WorkspaceEntity>
{
    public void Configure(EntityTypeBuilder<WorkspaceEntity> entity)
    {
        entity.ToTable("workspaces");
        entity.HasKey(e => e.Path);
        entity.Property(e => e.CreatedAt).IsRequired();
    }
}
