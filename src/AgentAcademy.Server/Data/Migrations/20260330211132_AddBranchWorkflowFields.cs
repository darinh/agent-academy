using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBranchWorkflowFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MergeCommitSha",
                table: "tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaskId",
                table: "breakout_rooms",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MergeCommitSha",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "TaskId",
                table: "breakout_rooms");
        }
    }
}
