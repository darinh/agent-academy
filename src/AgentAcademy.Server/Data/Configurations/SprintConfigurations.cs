using AgentAcademy.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentAcademy.Server.Data.Configurations;

internal sealed class SprintConfiguration : IEntityTypeConfiguration<SprintEntity>
{
    public void Configure(EntityTypeBuilder<SprintEntity> entity)
    {
        entity.ToTable("sprints");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Number).IsRequired();
        entity.Property(e => e.WorkspacePath).IsRequired();
        entity.Property(e => e.Status).IsRequired().HasDefaultValue("Active");
        entity.Property(e => e.CurrentStage).IsRequired().HasDefaultValue("Intake");
        entity.Property(e => e.AwaitingSignOff).HasDefaultValue(false);
        entity.Property(e => e.CreatedAt).IsRequired();

        // Self-drive counters (P1.2). Defaults make existing rows safe — they
        // backfill to "no rounds yet, no continuations yet" which is the
        // correct semantic for sprints created before P1.2 shipped.
        entity.Property(e => e.RoundsThisSprint).HasDefaultValue(0);
        entity.Property(e => e.RoundsThisStage).HasDefaultValue(0);
        entity.Property(e => e.SelfDriveContinuations).HasDefaultValue(0);

        entity.HasIndex(e => new { e.WorkspacePath, e.Status })
            .HasDatabaseName("idx_sprints_workspace_status");
        entity.HasIndex(e => e.WorkspacePath)
            .IsUnique()
            .HasFilter("\"Status\" = 'Active'")
            .HasDatabaseName("idx_sprints_one_active_per_workspace");
        entity.HasIndex(e => new { e.WorkspacePath, e.Number })
            .IsUnique()
            .HasDatabaseName("idx_sprints_workspace_number_unique");

        entity.HasOne(e => e.OverflowFromSprint)
            .WithMany()
            .HasForeignKey(e => e.OverflowFromSprintId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class SprintArtifactConfiguration : IEntityTypeConfiguration<SprintArtifactEntity>
{
    public void Configure(EntityTypeBuilder<SprintArtifactEntity> entity)
    {
        entity.ToTable("sprint_artifacts");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).ValueGeneratedOnAdd();
        entity.Property(e => e.SprintId).IsRequired();
        entity.Property(e => e.Stage).IsRequired();
        entity.Property(e => e.Type).IsRequired();
        entity.Property(e => e.Content).IsRequired();
        entity.Property(e => e.CreatedAt).IsRequired();

        entity.HasOne(e => e.Sprint)
            .WithMany()
            .HasForeignKey(e => e.SprintId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => e.SprintId).HasDatabaseName("idx_sprint_artifacts_sprint");
        entity.HasIndex(e => new { e.SprintId, e.Stage })
            .HasDatabaseName("idx_sprint_artifacts_sprint_stage");
        entity.HasIndex(e => new { e.SprintId, e.Type })
            .HasDatabaseName("idx_sprint_artifacts_sprint_type");
        entity.HasIndex(e => new { e.SprintId, e.Stage, e.Type })
            .IsUnique()
            .HasDatabaseName("idx_sprint_artifacts_sprint_stage_type_unique");
    }
}

internal sealed class SprintScheduleConfiguration : IEntityTypeConfiguration<SprintScheduleEntity>
{
    public void Configure(EntityTypeBuilder<SprintScheduleEntity> entity)
    {
        entity.ToTable("sprint_schedules");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.WorkspacePath).IsRequired();
        entity.Property(e => e.CronExpression).IsRequired();
        entity.Property(e => e.TimeZoneId).IsRequired().HasDefaultValue("UTC");
        entity.Property(e => e.Enabled).HasDefaultValue(false);
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.Property(e => e.UpdatedAt).IsRequired();

        entity.HasIndex(e => e.WorkspacePath)
            .IsUnique()
            .HasDatabaseName("idx_sprint_schedules_workspace_unique");
        entity.HasIndex(e => new { e.Enabled, e.NextRunAtUtc })
            .HasDatabaseName("idx_sprint_schedules_enabled_next_run");
    }
}
