using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCommandAuditsAndAgentMemories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_memories",
                columns: table => new
                {
                    AgentId = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_memories", x => new { x.AgentId, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "command_audits",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentId = table.Column<string>(type: "TEXT", nullable: false),
                    Command = table.Column<string>(type: "TEXT", nullable: false),
                    ArgsJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    Status = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "Success"),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    RoomId = table.Column<string>(type: "TEXT", nullable: true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_command_audits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_agent_memories_agent",
                table: "agent_memories",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "idx_agent_memories_category",
                table: "agent_memories",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "idx_cmd_audits_agent",
                table: "command_audits",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "idx_cmd_audits_correlation",
                table: "command_audits",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "idx_cmd_audits_time",
                table: "command_audits",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_memories");

            migrationBuilder.DropTable(
                name: "command_audits");
        }
    }
}
