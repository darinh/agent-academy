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
    public DbSet<SprintScheduleEntity> SprintSchedules => Set<SprintScheduleEntity>();
    public DbSet<TaskDependencyEntity> TaskDependencies => Set<TaskDependencyEntity>();
    public DbSet<LearningDigestEntity> LearningDigests => Set<LearningDigestEntity>();
    public DbSet<LearningDigestSourceEntity> LearningDigestSources => Set<LearningDigestSourceEntity>();
    public DbSet<RoomArtifactEntity> RoomArtifacts => Set<RoomArtifactEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AgentAcademyDbContext).Assembly);
    }
}
