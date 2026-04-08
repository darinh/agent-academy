using AgentAcademy.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Data;

/// <summary>
/// EF Core database context for Agent Academy.
/// Manages persistence of rooms, messages, tasks, agents, and activity events.
/// </summary>
public class AgentAcademyDbContext : DbContext
{
    public AgentAcademyDbContext(DbContextOptions<AgentAcademyDbContext> options)
        : base(options)
    {
    }

    public DbSet<RoomEntity> Rooms => Set<RoomEntity>();
    public DbSet<MessageEntity> Messages => Set<MessageEntity>();
    public DbSet<TaskEntity> Tasks => Set<TaskEntity>();
    public DbSet<TaskItemEntity> TaskItems => Set<TaskItemEntity>();
    public DbSet<AgentLocationEntity> AgentLocations => Set<AgentLocationEntity>();
    public DbSet<BreakoutRoomEntity> BreakoutRooms => Set<BreakoutRoomEntity>();
    public DbSet<BreakoutMessageEntity> BreakoutMessages => Set<BreakoutMessageEntity>();
    public DbSet<PlanEntity> Plans => Set<PlanEntity>();
    public DbSet<ActivityEventEntity> ActivityEvents => Set<ActivityEventEntity>();
    public DbSet<WorkspaceEntity> Workspaces => Set<WorkspaceEntity>();
    public DbSet<CommandAuditEntity> CommandAudits => Set<CommandAuditEntity>();
    public DbSet<AgentMemoryEntity> AgentMemories => Set<AgentMemoryEntity>();
    public DbSet<NotificationConfigEntity> NotificationConfigs => Set<NotificationConfigEntity>();
    public DbSet<AgentConfigEntity> AgentConfigs => Set<AgentConfigEntity>();
    public DbSet<InstructionTemplateEntity> InstructionTemplates => Set<InstructionTemplateEntity>();
    public DbSet<TaskCommentEntity> TaskComments => Set<TaskCommentEntity>();
    public DbSet<ServerInstanceEntity> ServerInstances => Set<ServerInstanceEntity>();
    public DbSet<ConversationSessionEntity> ConversationSessions => Set<ConversationSessionEntity>();
    public DbSet<SystemSettingEntity> SystemSettings => Set<SystemSettingEntity>();
    public DbSet<NotificationDeliveryEntity> NotificationDeliveries => Set<NotificationDeliveryEntity>();
    public DbSet<LlmUsageEntity> LlmUsage => Set<LlmUsageEntity>();
    public DbSet<AgentErrorEntity> AgentErrors => Set<AgentErrorEntity>();
    public DbSet<SpecTaskLinkEntity> SpecTaskLinks => Set<SpecTaskLinkEntity>();
    public DbSet<TaskEvidenceEntity> TaskEvidence => Set<TaskEvidenceEntity>();
    public DbSet<SprintEntity> Sprints => Set<SprintEntity>();
    public DbSet<SprintArtifactEntity> SprintArtifacts => Set<SprintArtifactEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Rooms ──────────────────────────────────────────────
        modelBuilder.Entity<RoomEntity>(entity =>
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
        });

        // ── Messages ──────────────────────────────────────────
        modelBuilder.Entity<MessageEntity>(entity =>
        {
            entity.ToTable("messages");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RoomId).IsRequired();
            entity.Property(e => e.SenderId).IsRequired();
            entity.Property(e => e.SenderName).IsRequired();
            entity.Property(e => e.SenderKind).IsRequired();
            entity.Property(e => e.Kind).IsRequired();
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.SentAt).IsRequired();

            entity.HasOne(e => e.Room)
                .WithMany(r => r.Messages)
                .HasForeignKey(e => e.RoomId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.RoomId).HasDatabaseName("idx_messages_room");
            entity.HasIndex(e => e.SentAt).HasDatabaseName("idx_messages_sentAt");
            entity.HasIndex(e => new { e.RecipientId, e.SentAt }).HasDatabaseName("idx_messages_recipient_sentAt");
        });

        // ── Tasks ─────────────────────────────────────────────
        modelBuilder.Entity<TaskEntity>(entity =>
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

            // Extended task metadata
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

            entity.HasIndex(e => e.RoomId).HasDatabaseName("idx_tasks_room");
            entity.HasIndex(e => e.AssignedAgentId).HasDatabaseName("idx_tasks_agent");
            entity.HasIndex(e => e.Status).HasDatabaseName("idx_tasks_status");
            entity.Property(e => e.WorkspacePath).IsRequired(false);
            entity.HasIndex(e => e.WorkspacePath).HasDatabaseName("idx_tasks_workspace");
        });

        // ── Task Items ────────────────────────────────────────
        modelBuilder.Entity<TaskItemEntity>(entity =>
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
        });

        // ── Task Comments ─────────────────────────────────────
        modelBuilder.Entity<TaskCommentEntity>(entity =>
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
        });

        // ── Task Evidence ─────────────────────────────────────
        modelBuilder.Entity<TaskEvidenceEntity>(entity =>
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
        });

        // ── Agent Locations ───────────────────────────────────
        modelBuilder.Entity<AgentLocationEntity>(entity =>
        {
            entity.ToTable("agent_locations");
            entity.HasKey(e => e.AgentId);
            entity.Property(e => e.RoomId).IsRequired();
            entity.Property(e => e.State).IsRequired().HasDefaultValue("Idle");
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasIndex(e => e.RoomId);
        });

        // ── Breakout Rooms ────────────────────────────────────
        modelBuilder.Entity<BreakoutRoomEntity>(entity =>
        {
            entity.ToTable("breakout_rooms");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.ParentRoomId).IsRequired();
            entity.Property(e => e.AssignedAgentId).IsRequired();
            entity.Property(e => e.Status).IsRequired().HasDefaultValue("Active");
            entity.Property(e => e.CloseReason).IsRequired(false);
            entity.Property(e => e.TaskId).IsRequired(false);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();

            entity.HasOne(e => e.ParentRoom)
                .WithMany(r => r.BreakoutRooms)
                .HasForeignKey(e => e.ParentRoomId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.ParentRoomId).HasDatabaseName("idx_breakout_rooms_parent");
            entity.HasIndex(e => e.TaskId).HasDatabaseName("idx_breakout_rooms_task");
        });

        // ── Breakout Messages ─────────────────────────────────
        modelBuilder.Entity<BreakoutMessageEntity>(entity =>
        {
            entity.ToTable("breakout_messages");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.BreakoutRoomId).IsRequired();
            entity.Property(e => e.SenderId).IsRequired();
            entity.Property(e => e.SenderName).IsRequired();
            entity.Property(e => e.SenderKind).IsRequired();
            entity.Property(e => e.Kind).IsRequired();
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.SentAt).IsRequired();

            entity.HasOne(e => e.BreakoutRoom)
                .WithMany(br => br.Messages)
                .HasForeignKey(e => e.BreakoutRoomId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Plans ─────────────────────────────────────────────
        modelBuilder.Entity<PlanEntity>(entity =>
        {
            entity.ToTable("plans");
            entity.HasKey(e => e.RoomId);
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
        });

        // ── Activity Events ───────────────────────────────────
        modelBuilder.Entity<ActivityEventEntity>(entity =>
        {
            entity.ToTable("activity_events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Type).IsRequired();
            entity.Property(e => e.Severity).IsRequired().HasDefaultValue("Info");
            entity.Property(e => e.Message).IsRequired();
            entity.Property(e => e.OccurredAt).IsRequired();

            entity.HasOne(e => e.Room)
                .WithMany(r => r.ActivityEvents)
                .HasForeignKey(e => e.RoomId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.RoomId).HasDatabaseName("idx_activity_room");
            entity.HasIndex(e => e.OccurredAt).HasDatabaseName("idx_activity_time");
        });

        // ── Workspaces ───────────────────────────────────────
        modelBuilder.Entity<WorkspaceEntity>(entity =>
        {
            entity.ToTable("workspaces");
            entity.HasKey(e => e.Path);
            entity.Property(e => e.CreatedAt).IsRequired();
        });

        // ── Command Audits ───────────────────────────────────
        modelBuilder.Entity<CommandAuditEntity>(entity =>
        {
            entity.ToTable("command_audits");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CorrelationId).IsRequired();
            entity.Property(e => e.AgentId).IsRequired();
            entity.Property(e => e.Source);
            entity.Property(e => e.Command).IsRequired();
            entity.Property(e => e.ArgsJson).IsRequired().HasDefaultValue("{}");
            entity.Property(e => e.Status).IsRequired().HasDefaultValue("Success");
            entity.Property(e => e.ErrorCode);
            entity.Property(e => e.Timestamp).IsRequired();

            entity.HasIndex(e => e.AgentId).HasDatabaseName("idx_cmd_audits_agent");
            entity.HasIndex(e => e.Source).HasDatabaseName("idx_cmd_audits_source");
            entity.HasIndex(e => e.Timestamp).HasDatabaseName("idx_cmd_audits_time");
            entity.HasIndex(e => e.CorrelationId).HasDatabaseName("idx_cmd_audits_correlation");
        });

        // ── Agent Memories ───────────────────────────────────
        modelBuilder.Entity<AgentMemoryEntity>(entity =>
        {
            entity.ToTable("agent_memories");
            entity.HasKey(e => new { e.AgentId, e.Key });
            entity.Property(e => e.Category).IsRequired();
            entity.Property(e => e.Value).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();

            entity.HasIndex(e => e.AgentId).HasDatabaseName("idx_agent_memories_agent");
            entity.HasIndex(e => e.Category).HasDatabaseName("idx_agent_memories_category");
            entity.HasIndex(e => e.ExpiresAt).HasDatabaseName("idx_agent_memories_expires");
        });

        // ── Notification Configs ────────────────────────────
        modelBuilder.Entity<NotificationConfigEntity>(entity =>
        {
            entity.ToTable("notification_configs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ProviderId).IsRequired();
            entity.Property(e => e.Key).IsRequired();
            entity.Property(e => e.Value).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();

            entity.HasIndex(e => new { e.ProviderId, e.Key })
                .IsUnique()
                .HasDatabaseName("idx_notification_configs_provider_key");
        });

        // ── Agent Configs ───────────────────────────────────
        modelBuilder.Entity<AgentConfigEntity>(entity =>
        {
            entity.ToTable("agent_configs");
            entity.HasKey(e => e.AgentId);
            entity.Property(e => e.UpdatedAt).IsRequired();

            entity.HasOne(e => e.InstructionTemplate)
                .WithMany()
                .HasForeignKey(e => e.InstructionTemplateId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ── Instruction Templates ───────────────────────────
        modelBuilder.Entity<InstructionTemplateEntity>(entity =>
        {
            entity.ToTable("instruction_templates");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();

            entity.HasIndex(e => e.Name)
                .IsUnique()
                .HasDatabaseName("idx_instruction_templates_name");
        });

        // ── Server Instances ───────────────────────────────────
        modelBuilder.Entity<ServerInstanceEntity>(entity =>
        {
            entity.ToTable("server_instances");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.StartedAt).IsRequired();
            entity.Property(e => e.Version).IsRequired();
        });

        // ── Conversation Sessions ───────────────────────────────
        modelBuilder.Entity<ConversationSessionEntity>(entity =>
        {
            entity.ToTable("conversation_sessions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RoomId).IsRequired();
            entity.Property(e => e.RoomType).IsRequired().HasDefaultValue("Main");
            entity.Property(e => e.SequenceNumber).IsRequired().HasDefaultValue(1);
            entity.Property(e => e.Status).IsRequired().HasDefaultValue("Active");
            entity.Property(e => e.MessageCount).IsRequired().HasDefaultValue(0);
            entity.Property(e => e.CreatedAt).IsRequired();

            entity.HasIndex(e => new { e.RoomId, e.Status })
                .HasDatabaseName("idx_conversation_sessions_room_status");
            entity.Property(e => e.WorkspacePath).IsRequired(false);
            entity.HasIndex(e => e.WorkspacePath)
                .HasDatabaseName("idx_conversation_sessions_workspace");
        });

        // ── System Settings ─────────────────────────────────────
        modelBuilder.Entity<SystemSettingEntity>(entity =>
        {
            entity.ToTable("system_settings");
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Value).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
        });

        // ── Notification Deliveries ────────────────────────────
        modelBuilder.Entity<NotificationDeliveryEntity>(entity =>
        {
            entity.ToTable("notification_deliveries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Channel).IsRequired();
            entity.Property(e => e.ProviderId).IsRequired();
            entity.Property(e => e.Status).IsRequired().HasDefaultValue("Delivered");
            entity.Property(e => e.AttemptedAt).IsRequired();

            entity.HasIndex(e => e.AttemptedAt).HasDatabaseName("idx_notification_deliveries_time");
            entity.HasIndex(e => e.ProviderId).HasDatabaseName("idx_notification_deliveries_provider");
            entity.HasIndex(e => e.Channel).HasDatabaseName("idx_notification_deliveries_channel");
            entity.HasIndex(e => e.RoomId).HasDatabaseName("idx_notification_deliveries_room");
        });

        // ── LLM Usage ─────────────────────────────────────────
        modelBuilder.Entity<LlmUsageEntity>(entity =>
        {
            entity.ToTable("llm_usage");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AgentId).IsRequired();
            entity.Property(e => e.RecordedAt).IsRequired();

            entity.HasIndex(e => e.AgentId).HasDatabaseName("idx_llm_usage_agent");
            entity.HasIndex(e => e.RoomId).HasDatabaseName("idx_llm_usage_room");
            entity.HasIndex(e => e.RecordedAt).HasDatabaseName("idx_llm_usage_time");
        });

        modelBuilder.Entity<AgentErrorEntity>(entity =>
        {
            entity.ToTable("agent_errors");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AgentId).IsRequired();
            entity.Property(e => e.ErrorType).IsRequired();
            entity.Property(e => e.Message).IsRequired();
            entity.Property(e => e.OccurredAt).IsRequired();

            entity.HasIndex(e => e.AgentId).HasDatabaseName("idx_agent_errors_agent");
            entity.HasIndex(e => e.RoomId).HasDatabaseName("idx_agent_errors_room");
            entity.HasIndex(e => e.OccurredAt).HasDatabaseName("idx_agent_errors_time");
            entity.HasIndex(e => e.ErrorType).HasDatabaseName("idx_agent_errors_type");
        });

        // ── Spec–Task Links ──────────────────────────────────
        modelBuilder.Entity<SpecTaskLinkEntity>(entity =>
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
        });

        // ── Sprints ────────────────────────────────────────────
        modelBuilder.Entity<SprintEntity>(entity =>
        {
            entity.ToTable("sprints");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Number).IsRequired();
            entity.Property(e => e.WorkspacePath).IsRequired();
            entity.Property(e => e.Status).IsRequired().HasDefaultValue("Active");
            entity.Property(e => e.CurrentStage).IsRequired().HasDefaultValue("Intake");
            entity.Property(e => e.CreatedAt).IsRequired();

            entity.HasIndex(e => e.WorkspacePath).HasDatabaseName("idx_sprints_workspace");
            entity.HasIndex(e => new { e.WorkspacePath, e.Status })
                .HasDatabaseName("idx_sprints_workspace_status");
            entity.HasIndex(e => new { e.WorkspacePath, e.Number })
                .IsUnique()
                .HasDatabaseName("idx_sprints_workspace_number_unique");
        });

        // ── Sprint Artifacts ───────────────────────────────────
        modelBuilder.Entity<SprintArtifactEntity>(entity =>
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
        });
    }
}
