using AgentAcademy.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentAcademy.Server.Data.Configurations;

internal sealed class TaskConfiguration : IEntityTypeConfiguration<TaskEntity>
{
    public void Configure(EntityTypeBuilder<TaskEntity> entity)
    {
        entity.ToTable("tasks");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Title).IsRequired();
        entity.Property(e => e.Description).IsRequired().HasDefaultValue("");
        entity.Property(e => e.SuccessCriteria).IsRequired().HasDefaultValue("");
        entity.Property(e => e.Status).IsRequired().HasDefaultValue("Active");
        entity.Property(e => e.Type).IsRequired().HasDefaultValue("Feature");
        entity.Property(e => e.CurrentPhase).IsRequired().HasDefaultValue("Planning");
        entity.Property(e => e.CurrentPlan).IsRequired().HasDefaultValue("");
        entity.Property(e => e.ValidationStatus).IsRequired().HasDefaultValue("NotStarted");
        entity.Property(e => e.ValidationSummary).IsRequired().HasDefaultValue("");
        entity.Property(e => e.ImplementationStatus).IsRequired().HasDefaultValue("NotStarted");
        entity.Property(e => e.ImplementationSummary).IsRequired().HasDefaultValue("");
        entity.Property(e => e.PreferredRoles).IsRequired().HasDefaultValue("[]");
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.Property(e => e.UpdatedAt).IsRequired();

        entity.Property(e => e.UsedFleet).HasDefaultValue(false);
        entity.Property(e => e.FleetModels).IsRequired().HasDefaultValue("[]");
        entity.Property(e => e.ReviewRounds).HasDefaultValue(0);
        entity.Property(e => e.TestsCreated).IsRequired().HasDefaultValue("[]");
        entity.Property(e => e.CommitCount).HasDefaultValue(0);
        entity.Property(e => e.MergeCommitSha).IsRequired(false);

        entity.HasOne(e => e.Room)
            .WithMany(r => r.Tasks)
            .HasForeignKey(e => e.RoomId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasOne(e => e.Sprint)
            .WithMany()
            .HasForeignKey(e => e.SprintId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasIndex(e => e.RoomId).HasDatabaseName("idx_tasks_room");
        entity.HasIndex(e => e.AssignedAgentId).HasDatabaseName("idx_tasks_agent");
        entity.HasIndex(e => e.Status).HasDatabaseName("idx_tasks_status");
        entity.HasIndex(e => e.SprintId).HasDatabaseName("idx_tasks_sprint");
        entity.Property(e => e.WorkspacePath).IsRequired(false);
        entity.HasIndex(e => e.WorkspacePath).HasDatabaseName("idx_tasks_workspace");
        entity.HasIndex(e => e.CreatedAt).HasDatabaseName("idx_tasks_created");
        entity.HasIndex(e => e.CompletedAt).HasDatabaseName("idx_tasks_completed");
    }
}

internal sealed class TaskDependencyConfiguration : IEntityTypeConfiguration<TaskDependencyEntity>
{
    public void Configure(EntityTypeBuilder<TaskDependencyEntity> entity)
    {
        entity.ToTable("task_dependencies");
        entity.HasKey(e => new { e.TaskId, e.DependsOnTaskId });
        entity.Property(e => e.CreatedAt).IsRequired();

        entity.HasOne(e => e.Task)
            .WithMany(t => t.Dependencies)
            .HasForeignKey(e => e.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.DependsOn)
            .WithMany(t => t.Dependents)
            .HasForeignKey(e => e.DependsOnTaskId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => e.DependsOnTaskId).HasDatabaseName("idx_task_deps_depends_on");
    }
}

internal sealed class TaskItemConfiguration : IEntityTypeConfiguration<TaskItemEntity>
{
    public void Configure(EntityTypeBuilder<TaskItemEntity> entity)
    {
        entity.ToTable("task_items");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Title).IsRequired();
        entity.Property(e => e.Description).IsRequired().HasDefaultValue("");
        entity.Property(e => e.Status).IsRequired().HasDefaultValue("Pending");
        entity.Property(e => e.AssignedTo).IsRequired();
        entity.Property(e => e.RoomId).IsRequired();
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.Property(e => e.UpdatedAt).IsRequired();

        entity.HasIndex(e => e.AssignedTo).HasDatabaseName("idx_task_items_agent");
        entity.HasIndex(e => e.RoomId).HasDatabaseName("idx_task_items_room");
    }
}

internal sealed class TaskCommentConfiguration : IEntityTypeConfiguration<TaskCommentEntity>
{
    public void Configure(EntityTypeBuilder<TaskCommentEntity> entity)
    {
        entity.ToTable("task_comments");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.TaskId).IsRequired();
        entity.Property(e => e.AgentId).IsRequired();
        entity.Property(e => e.AgentName).IsRequired();
        entity.Property(e => e.CommentType).IsRequired().HasDefaultValue("Comment");
        entity.Property(e => e.Content).IsRequired();
        entity.Property(e => e.CreatedAt).IsRequired();

        entity.HasOne(e => e.Task)
            .WithMany()
            .HasForeignKey(e => e.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => e.TaskId).HasDatabaseName("idx_task_comments_task");
        entity.HasIndex(e => e.AgentId).HasDatabaseName("idx_task_comments_agent");
    }
}

internal sealed class TaskEvidenceConfiguration : IEntityTypeConfiguration<TaskEvidenceEntity>
{
    public void Configure(EntityTypeBuilder<TaskEvidenceEntity> entity)
    {
        entity.ToTable("task_evidence");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.TaskId).IsRequired();
        entity.Property(e => e.Phase).IsRequired().HasDefaultValue("After");
        entity.Property(e => e.CheckName).IsRequired();
        entity.Property(e => e.Tool).IsRequired();
        entity.Property(e => e.Passed).IsRequired();
        entity.Property(e => e.AgentId).IsRequired();
        entity.Property(e => e.AgentName).IsRequired();
        entity.Property(e => e.CreatedAt).IsRequired();

        entity.HasOne(e => e.Task)
            .WithMany()
            .HasForeignKey(e => e.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => e.TaskId).HasDatabaseName("idx_task_evidence_task");
        entity.HasIndex(e => new { e.TaskId, e.Phase }).HasDatabaseName("idx_task_evidence_task_phase");
    }
}

internal sealed class SpecTaskLinkConfiguration : IEntityTypeConfiguration<SpecTaskLinkEntity>
{
    public void Configure(EntityTypeBuilder<SpecTaskLinkEntity> entity)
    {
        entity.ToTable("spec_task_links");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.TaskId).IsRequired();
        entity.Property(e => e.SpecSectionId).IsRequired();
        entity.Property(e => e.LinkType).IsRequired().HasDefaultValue("Implements");
        entity.Property(e => e.LinkedByAgentId).IsRequired();
        entity.Property(e => e.LinkedByAgentName).IsRequired();
        entity.Property(e => e.Note).IsRequired(false);
        entity.Property(e => e.CreatedAt).IsRequired();

        entity.HasOne(e => e.Task)
            .WithMany()
            .HasForeignKey(e => e.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => e.TaskId).HasDatabaseName("idx_spec_task_links_task");
        entity.HasIndex(e => e.SpecSectionId).HasDatabaseName("idx_spec_task_links_spec");
        entity.HasIndex(e => new { e.TaskId, e.SpecSectionId })
            .IsUnique()
            .HasDatabaseName("idx_spec_task_links_unique");
    }
}
