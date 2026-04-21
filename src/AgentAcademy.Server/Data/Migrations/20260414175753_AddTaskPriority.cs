using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskPriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.CreateIndex(
                name: "idx_tasks_priority",
                table: "tasks",
                column: "Priority");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_tasks_priority",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "tasks");
        }
    }
}
