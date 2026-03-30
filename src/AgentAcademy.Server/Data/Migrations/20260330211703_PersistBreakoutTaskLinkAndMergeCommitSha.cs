using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersistBreakoutTaskLinkAndMergeCommitSha : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "idx_breakout_rooms_task",
                table: "breakout_rooms",
                column: "TaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_breakout_rooms_task",
                table: "breakout_rooms");
        }
    }
}
