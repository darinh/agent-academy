using AgentAcademy.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentAcademy.Server.Data.Configurations;

internal sealed class BreakoutRoomConfiguration : IEntityTypeConfiguration<BreakoutRoomEntity>
{
    public void Configure(EntityTypeBuilder<BreakoutRoomEntity> entity)
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
    }
}

internal sealed class BreakoutMessageConfiguration : IEntityTypeConfiguration<BreakoutMessageEntity>
{
    public void Configure(EntityTypeBuilder<BreakoutMessageEntity> entity)
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
    }
}
