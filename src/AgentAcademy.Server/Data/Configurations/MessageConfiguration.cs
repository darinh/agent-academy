using AgentAcademy.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentAcademy.Server.Data.Configurations;

internal sealed class MessageConfiguration : IEntityTypeConfiguration<MessageEntity>
{
    public void Configure(EntityTypeBuilder<MessageEntity> entity)
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
    }
}
