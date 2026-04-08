using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSprintForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "idx_tasks_sprint",
                table: "tasks",
                column: "SprintId");

            migrationBuilder.CreateIndex(
                name: "IX_sprints_OverflowFromSprintId",
                table: "sprints",
                column: "OverflowFromSprintId");

            migrationBuilder.CreateIndex(
                name: "IX_plans_SprintId",
                table: "plans",
                column: "SprintId");

            migrationBuilder.CreateIndex(
                name: "idx_conversation_sessions_sprint",
                table: "conversation_sessions",
                column: "SprintId");

            migrationBuilder.AddForeignKey(
                name: "FK_conversation_sessions_sprints_SprintId",
                table: "conversation_sessions",
                column: "SprintId",
                principalTable: "sprints",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_plans_sprints_SprintId",
                table: "plans",
                column: "SprintId",
                principalTable: "sprints",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_sprints_sprints_OverflowFromSprintId",
                table: "sprints",
                column: "OverflowFromSprintId",
                principalTable: "sprints",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_tasks_sprints_SprintId",
                table: "tasks",
                column: "SprintId",
                principalTable: "sprints",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_conversation_sessions_sprints_SprintId",
                table: "conversation_sessions");

            migrationBuilder.DropForeignKey(
                name: "FK_plans_sprints_SprintId",
                table: "plans");

            migrationBuilder.DropForeignKey(
                name: "FK_sprints_sprints_OverflowFromSprintId",
                table: "sprints");

            migrationBuilder.DropForeignKey(
                name: "FK_tasks_sprints_SprintId",
                table: "tasks");

            migrationBuilder.DropIndex(
                name: "idx_tasks_sprint",
                table: "tasks");

            migrationBuilder.DropIndex(
                name: "IX_sprints_OverflowFromSprintId",
                table: "sprints");

            migrationBuilder.DropIndex(
                name: "IX_plans_SprintId",
                table: "plans");

            migrationBuilder.DropIndex(
                name: "idx_conversation_sessions_sprint",
                table: "conversation_sessions");
        }
    }
}
