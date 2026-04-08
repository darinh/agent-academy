using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSprintWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SprintId",
                table: "tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SprintId",
                table: "plans",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SprintId",
                table: "conversation_sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SprintStage",
                table: "conversation_sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "sprints",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkspacePath = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "Active"),
                    CurrentStage = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "Intake"),
                    OverflowFromSprintId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sprints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sprint_artifacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SprintId = table.Column<string>(type: "TEXT", nullable: false),
                    Stage = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedByAgentId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sprint_artifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sprint_artifacts_sprints_SprintId",
                        column: x => x.SprintId,
                        principalTable: "sprints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_sprint_artifacts_sprint",
                table: "sprint_artifacts",
                column: "SprintId");

            migrationBuilder.CreateIndex(
                name: "idx_sprint_artifacts_sprint_stage",
                table: "sprint_artifacts",
                columns: new[] { "SprintId", "Stage" });

            migrationBuilder.CreateIndex(
                name: "idx_sprint_artifacts_sprint_stage_type_unique",
                table: "sprint_artifacts",
                columns: new[] { "SprintId", "Stage", "Type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_sprint_artifacts_sprint_type",
                table: "sprint_artifacts",
                columns: new[] { "SprintId", "Type" });

            migrationBuilder.CreateIndex(
                name: "idx_sprints_workspace",
                table: "sprints",
                column: "WorkspacePath");

            migrationBuilder.CreateIndex(
                name: "idx_sprints_workspace_number_unique",
                table: "sprints",
                columns: new[] { "WorkspacePath", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_sprints_workspace_status",
                table: "sprints",
                columns: new[] { "WorkspacePath", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sprint_artifacts");

            migrationBuilder.DropTable(
                name: "sprints");

            migrationBuilder.DropColumn(
                name: "SprintId",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "SprintId",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "SprintId",
                table: "conversation_sessions");

            migrationBuilder.DropColumn(
                name: "SprintStage",
                table: "conversation_sessions");
        }
    }
}
