using AgentAcademy.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentAcademy.Server.Data.Configurations;

internal sealed class NotificationConfigConfiguration : IEntityTypeConfiguration<NotificationConfigEntity>
{
    public void Configure(EntityTypeBuilder<NotificationConfigEntity> entity)
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
    }
}

internal sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDeliveryEntity>
{
    public void Configure(EntityTypeBuilder<NotificationDeliveryEntity> entity)
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
    }
}

internal sealed class InstructionTemplateConfiguration : IEntityTypeConfiguration<InstructionTemplateEntity>
{
    public void Configure(EntityTypeBuilder<InstructionTemplateEntity> entity)
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
    }
}
