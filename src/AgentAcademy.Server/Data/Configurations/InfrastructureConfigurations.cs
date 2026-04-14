using AgentAcademy.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentAcademy.Server.Data.Configurations;

internal sealed class ActivityEventConfiguration : IEntityTypeConfiguration<ActivityEventEntity>
{
    public void Configure(EntityTypeBuilder<ActivityEventEntity> entity)
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
    }
}

internal sealed class CommandAuditConfiguration : IEntityTypeConfiguration<CommandAuditEntity>
{
    public void Configure(EntityTypeBuilder<CommandAuditEntity> entity)
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
    }
}

internal sealed class ServerInstanceConfiguration : IEntityTypeConfiguration<ServerInstanceEntity>
{
    public void Configure(EntityTypeBuilder<ServerInstanceEntity> entity)
    {
        entity.ToTable("server_instances");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.StartedAt).IsRequired();
        entity.Property(e => e.Version).IsRequired();
    }
}

internal sealed class SystemSettingConfiguration : IEntityTypeConfiguration<SystemSettingEntity>
{
    public void Configure(EntityTypeBuilder<SystemSettingEntity> entity)
    {
        entity.ToTable("system_settings");
        entity.HasKey(e => e.Key);
        entity.Property(e => e.Value).IsRequired();
        entity.Property(e => e.UpdatedAt).IsRequired();
    }
}

internal sealed class LlmUsageConfiguration : IEntityTypeConfiguration<LlmUsageEntity>
{
    public void Configure(EntityTypeBuilder<LlmUsageEntity> entity)
    {
        entity.ToTable("llm_usage");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.AgentId).IsRequired();
        entity.Property(e => e.RecordedAt).IsRequired();

        entity.HasIndex(e => e.AgentId).HasDatabaseName("idx_llm_usage_agent");
        entity.HasIndex(e => e.RoomId).HasDatabaseName("idx_llm_usage_room");
        entity.HasIndex(e => e.RecordedAt).HasDatabaseName("idx_llm_usage_time");
        entity.HasIndex(e => new { e.AgentId, e.RecordedAt })
            .HasDatabaseName("idx_llm_usage_agent_time");
    }
}

internal sealed class ConversationSessionConfiguration : IEntityTypeConfiguration<ConversationSessionEntity>
{
    public void Configure(EntityTypeBuilder<ConversationSessionEntity> entity)
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

        entity.HasOne(e => e.Sprint)
            .WithMany()
            .HasForeignKey(e => e.SprintId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasIndex(e => e.SprintId)
            .HasDatabaseName("idx_conversation_sessions_sprint");
    }
}
