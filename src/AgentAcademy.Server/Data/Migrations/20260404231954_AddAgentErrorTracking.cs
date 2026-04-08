using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentErrorTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_errors",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    AgentId = table.Column<string>(type: "TEXT", nullable: false),
                    RoomId = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorType = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Recoverable = table.Column<bool>(type: "INTEGER", nullable: false),
                    Retried = table.Column<bool>(type: "INTEGER", nullable: false),
                    RetryAttempt = table.Column<int>(type: "INTEGER", nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_errors", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_agent_errors_agent",
                table: "agent_errors",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "idx_agent_errors_room",
                table: "agent_errors",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "idx_agent_errors_time",
                table: "agent_errors",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "idx_agent_errors_type",
                table: "agent_errors",
                column: "ErrorType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_errors");
        }
    }
}
