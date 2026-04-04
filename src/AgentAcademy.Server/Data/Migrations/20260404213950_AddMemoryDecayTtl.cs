using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoryDecayTtl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAt",
                table: "agent_memories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastAccessedAt",
                table: "agent_memories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_agent_memories_expires",
                table: "agent_memories",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_agent_memories_expires",
                table: "agent_memories");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "agent_memories");

            migrationBuilder.DropColumn(
                name: "LastAccessedAt",
                table: "agent_memories");
        }
    }
}
