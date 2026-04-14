using AgentAcademy.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentAcademy.Server.Data.Configurations;

internal sealed class RoomConfiguration : IEntityTypeConfiguration<RoomEntity>
{
    public void Configure(EntityTypeBuilder<RoomEntity> entity)
    {
        entity.ToTable("rooms");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Name).IsRequired();
        entity.Property(e => e.Status).IsRequired().HasDefaultValue("Idle");
        entity.Property(e => e.CurrentPhase).IsRequired().HasDefaultValue("Intake");
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.Property(e => e.UpdatedAt).IsRequired();
        entity.Property(e => e.WorkspacePath).IsRequired(false);
        entity.HasIndex(e => e.WorkspacePath).HasDatabaseName("idx_rooms_workspace");
    }
}

internal sealed class PlanConfiguration : IEntityTypeConfiguration<PlanEntity>
{
    public void Configure(EntityTypeBuilder<PlanEntity> entity)
    {
        entity.ToTable("plans");
        entity.HasKey(e => e.RoomId);
        entity.Property(e => e.Content).IsRequired();
        entity.Property(e => e.UpdatedAt).IsRequired();

        entity.HasOne(e => e.Sprint)
            .WithMany()
            .HasForeignKey(e => e.SprintId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
