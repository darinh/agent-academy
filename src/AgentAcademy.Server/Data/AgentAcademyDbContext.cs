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

            entity.HasOne(e => e.Room)
                .WithMany(r => r.Tasks)
                .HasForeignKey(e => e.RoomId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.RoomId).HasDatabaseName("idx_tasks_room");
            entity.HasIndex(e => e.AssignedAgentId).HasDatabaseName("idx_tasks_agent");
            entity.HasIndex(e => e.Status).HasDatabaseName("idx_tasks_status");
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
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();

            entity.HasOne(e => e.ParentRoom)
                .WithMany(r => r.BreakoutRooms)
                .HasForeignKey(e => e.ParentRoomId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.ParentRoomId).HasDatabaseName("idx_breakout_rooms_parent");
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

            entity.HasOne(e => e.Room)
                .WithOne(r => r.Plan)
                .HasForeignKey<PlanEntity>(e => e.RoomId)
                .OnDelete(DeleteBehavior.Cascade);
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
            entity.Property(e => e.Command).IsRequired();
            entity.Property(e => e.ArgsJson).IsRequired().HasDefaultValue("{}");
            entity.Property(e => e.Status).IsRequired().HasDefaultValue("Success");
            entity.Property(e => e.Timestamp).IsRequired();

            entity.HasIndex(e => e.AgentId).HasDatabaseName("idx_cmd_audits_agent");
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
        });
    }
}
