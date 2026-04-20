using AgentAcademy.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentAcademy.Server.Data.Configurations;

internal sealed class GoalCardConfiguration : IEntityTypeConfiguration<GoalCardEntity>
{
    public void Configure(EntityTypeBuilder<GoalCardEntity> entity)
    {
        entity.ToTable("goal_cards");
        entity.HasKey(e => e.Id);

        entity.Property(e => e.AgentId).IsRequired();
        entity.Property(e => e.AgentName).IsRequired();
        entity.Property(e => e.RoomId).IsRequired();
        entity.Property(e => e.TaskDescription).IsRequired();
        entity.Property(e => e.Intent).IsRequired();
        entity.Property(e => e.Divergence).IsRequired();
        entity.Property(e => e.Steelman).IsRequired();
        entity.Property(e => e.Strawman).IsRequired();
        entity.Property(e => e.Verdict).IsRequired().HasDefaultValue("Proceed");
        entity.Property(e => e.FreshEyes1).IsRequired();
        entity.Property(e => e.FreshEyes2).IsRequired();
        entity.Property(e => e.FreshEyes3).IsRequired();
        entity.Property(e => e.PromptVersion).IsRequired().HasDefaultValue(1);
        entity.Property(e => e.Status).IsRequired().HasDefaultValue("Active");
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.Property(e => e.UpdatedAt).IsRequired();

        entity.HasOne(e => e.Room)
            .WithMany()
            .HasForeignKey(e => e.RoomId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.Task)
            .WithMany()
            .HasForeignKey(e => e.TaskId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasIndex(e => e.RoomId).HasDatabaseName("idx_goal_cards_room");
        entity.HasIndex(e => e.AgentId).HasDatabaseName("idx_goal_cards_agent");
        entity.HasIndex(e => e.TaskId).HasDatabaseName("idx_goal_cards_task");
        entity.HasIndex(e => e.Status).HasDatabaseName("idx_goal_cards_status");
        entity.HasIndex(e => e.Verdict).HasDatabaseName("idx_goal_cards_verdict");
        entity.HasIndex(e => e.CreatedAt).HasDatabaseName("idx_goal_cards_created");
    }
}
