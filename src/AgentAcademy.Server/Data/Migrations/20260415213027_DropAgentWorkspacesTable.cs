using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropAgentWorkspacesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_workspaces");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_workspaces",
                columns: table => new
                {
                    WorkspacePath = table.Column<string>(type: "TEXT", nullable: false),
                    AgentId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CurrentBranch = table.Column<string>(type: "TEXT", nullable: true),
                    LastAccessedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    WorktreePath = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_workspaces", x => new { x.WorkspacePath, x.AgentId });
                    table.ForeignKey(
                        name: "FK_agent_workspaces_workspaces_WorkspacePath",
                        column: x => x.WorkspacePath,
                        principalTable: "workspaces",
                        principalColumn: "Path",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_agent_workspaces_agent",
                table: "agent_workspaces",
                column: "AgentId");
        }
    }
}
