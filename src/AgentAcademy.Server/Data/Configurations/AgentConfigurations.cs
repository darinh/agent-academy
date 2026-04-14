using AgentAcademy.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentAcademy.Server.Data.Configurations;

internal sealed class AgentLocationConfiguration : IEntityTypeConfiguration<AgentLocationEntity>
{
    public void Configure(EntityTypeBuilder<AgentLocationEntity> entity)
    {
        entity.ToTable("agent_locations");
        entity.HasKey(e => e.AgentId);
        entity.Property(e => e.RoomId).IsRequired();
        entity.Property(e => e.State).IsRequired().HasDefaultValue("Idle");
        entity.Property(e => e.UpdatedAt).IsRequired();
        entity.HasIndex(e => e.RoomId);
    }
}

internal sealed class AgentConfigConfiguration : IEntityTypeConfiguration<AgentConfigEntity>
{
    public void Configure(EntityTypeBuilder<AgentConfigEntity> entity)
    {
        entity.ToTable("agent_configs");
        entity.HasKey(e => e.AgentId);
        entity.Property(e => e.UpdatedAt).IsRequired();
        entity.Property(e => e.MaxCostPerHour).HasColumnType("TEXT"); // SQLite decimal

        entity.HasOne(e => e.InstructionTemplate)
            .WithMany()
            .HasForeignKey(e => e.InstructionTemplateId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class AgentMemoryConfiguration : IEntityTypeConfiguration<AgentMemoryEntity>
{
    public void Configure(EntityTypeBuilder<AgentMemoryEntity> entity)
    {
        entity.ToTable("agent_memories");
        entity.HasKey(e => new { e.AgentId, e.Key });
        entity.Property(e => e.Category).IsRequired();
        entity.Property(e => e.Value).IsRequired();
        entity.Property(e => e.CreatedAt).IsRequired();

        entity.HasIndex(e => e.AgentId).HasDatabaseName("idx_agent_memories_agent");
        entity.HasIndex(e => e.Category).HasDatabaseName("idx_agent_memories_category");
        entity.HasIndex(e => e.ExpiresAt).HasDatabaseName("idx_agent_memories_expires");
    }
}

internal sealed class AgentErrorConfiguration : IEntityTypeConfiguration<AgentErrorEntity>
{
    public void Configure(EntityTypeBuilder<AgentErrorEntity> entity)
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
    }
}
