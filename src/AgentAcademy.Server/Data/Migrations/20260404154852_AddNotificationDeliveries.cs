using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Channel = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Body = table.Column<string>(type: "TEXT", nullable: true),
                    RoomId = table.Column<string>(type: "TEXT", nullable: true),
                    AgentId = table.Column<string>(type: "TEXT", nullable: true),
                    ProviderId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "Delivered"),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    AttemptedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_deliveries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_notification_deliveries_channel",
                table: "notification_deliveries",
                column: "Channel");

            migrationBuilder.CreateIndex(
                name: "idx_notification_deliveries_provider",
                table: "notification_deliveries",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "idx_notification_deliveries_room",
                table: "notification_deliveries",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "idx_notification_deliveries_time",
                table: "notification_deliveries",
                column: "AttemptedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_deliveries");
        }
    }
}
