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

internal sealed class AgentWorkspaceConfiguration : IEntityTypeConfiguration<AgentWorkspaceEntity>
{
    public void Configure(EntityTypeBuilder<AgentWorkspaceEntity> entity)
    {
        entity.ToTable("agent_workspaces");
        entity.HasKey(e => new { e.WorkspacePath, e.AgentId });
        entity.Property(e => e.CreatedAt).IsRequired();

        entity.HasOne(e => e.Workspace)
            .WithMany(w => w.AgentWorktrees)
            .HasForeignKey(e => e.WorkspacePath)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => e.AgentId).HasDatabaseName("idx_agent_workspaces_agent");
    }
}
