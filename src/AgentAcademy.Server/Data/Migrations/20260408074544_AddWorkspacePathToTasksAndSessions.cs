using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspacePathToTasksAndSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WorkspacePath",
                table: "tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkspacePath",
                table: "conversation_sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_tasks_workspace",
                table: "tasks",
                column: "WorkspacePath");

            migrationBuilder.CreateIndex(
                name: "idx_conversation_sessions_workspace",
                table: "conversation_sessions",
                column: "WorkspacePath");

            // Backfill: stamp WorkspacePath from the associated room
            migrationBuilder.Sql(
                """
                UPDATE tasks
                SET WorkspacePath = (SELECT r.WorkspacePath FROM rooms r WHERE r.Id = tasks.RoomId)
                WHERE RoomId IS NOT NULL;
                """);

            migrationBuilder.Sql(
                """
                UPDATE conversation_sessions
                SET WorkspacePath = (SELECT r.WorkspacePath FROM rooms r WHERE r.Id = conversation_sessions.RoomId);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_tasks_workspace",
                table: "tasks");

            migrationBuilder.DropIndex(
                name: "idx_conversation_sessions_workspace",
                table: "conversation_sessions");

            migrationBuilder.DropColumn(
                name: "WorkspacePath",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "WorkspacePath",
                table: "conversation_sessions");
        }
    }
}
