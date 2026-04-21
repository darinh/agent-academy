using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSprintSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "learning_digests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    MemoriesCreated = table.Column<int>(type: "INTEGER", nullable: false),
                    RetrospectivesProcessed = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_digests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sprint_schedules",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    WorkspacePath = table.Column<string>(type: "TEXT", nullable: false),
                    CronExpression = table.Column<string>(type: "TEXT", nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "UTC"),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    NextRunAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastTriggeredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastEvaluatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastOutcome = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sprint_schedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "task_dependencies",
                columns: table => new
                {
                    TaskId = table.Column<string>(type: "TEXT", nullable: false),
                    DependsOnTaskId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_dependencies", x => new { x.TaskId, x.DependsOnTaskId });
                    table.ForeignKey(
                        name: "FK_task_dependencies_tasks_DependsOnTaskId",
                        column: x => x.DependsOnTaskId,
                        principalTable: "tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_task_dependencies_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "learning_digest_sources",
                columns: table => new
                {
                    DigestId = table.Column<int>(type: "INTEGER", nullable: false),
                    RetrospectiveCommentId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_digest_sources", x => new { x.DigestId, x.RetrospectiveCommentId });
                    table.ForeignKey(
                        name: "FK_learning_digest_sources_learning_digests_DigestId",
                        column: x => x.DigestId,
                        principalTable: "learning_digests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_learning_digest_sources_task_comments_RetrospectiveCommentId",
                        column: x => x.RetrospectiveCommentId,
                        principalTable: "task_comments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_tasks_completed",
                table: "tasks",
                column: "CompletedAt");

            migrationBuilder.CreateIndex(
                name: "idx_tasks_created",
                table: "tasks",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "idx_digest_sources_retro_unique",
                table: "learning_digest_sources",
                column: "RetrospectiveCommentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_sprint_schedules_enabled_next_run",
                table: "sprint_schedules",
                columns: new[] { "Enabled", "NextRunAtUtc" });

            migrationBuilder.CreateIndex(
                name: "idx_sprint_schedules_workspace_unique",
                table: "sprint_schedules",
                column: "WorkspacePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_task_deps_depends_on",
                table: "task_dependencies",
                column: "DependsOnTaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "learning_digest_sources");

            migrationBuilder.DropTable(
                name: "sprint_schedules");

            migrationBuilder.DropTable(
                name: "task_dependencies");

            migrationBuilder.DropTable(
                name: "learning_digests");

            migrationBuilder.DropIndex(
                name: "idx_tasks_completed",
                table: "tasks");

            migrationBuilder.DropIndex(
                name: "idx_tasks_created",
                table: "tasks");
        }
    }
}
