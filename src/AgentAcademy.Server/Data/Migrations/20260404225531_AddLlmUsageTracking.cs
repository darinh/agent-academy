using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmUsageTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "llm_usage",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    AgentId = table.Column<string>(type: "TEXT", nullable: false),
                    RoomId = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheWriteTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    Cost = table.Column<double>(type: "REAL", nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: true),
                    ApiCallId = table.Column<string>(type: "TEXT", nullable: true),
                    Initiator = table.Column<string>(type: "TEXT", nullable: true),
                    ReasoningEffort = table.Column<string>(type: "TEXT", nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_llm_usage", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_llm_usage_agent",
                table: "llm_usage",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "idx_llm_usage_room",
                table: "llm_usage",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "idx_llm_usage_time",
                table: "llm_usage",
                column: "RecordedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "llm_usage");
        }
    }
}
