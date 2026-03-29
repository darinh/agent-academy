using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspacePathToRooms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WorkspacePath",
                table: "rooms",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_rooms_workspace",
                table: "rooms",
                column: "WorkspacePath");

            // Backfill: associate all existing rooms with the currently active workspace
            migrationBuilder.Sql(@"
                UPDATE rooms
                SET WorkspacePath = (SELECT Path FROM workspaces WHERE IsActive = 1 LIMIT 1)
                WHERE WorkspacePath IS NULL
                  AND EXISTS (SELECT 1 FROM workspaces WHERE IsActive = 1);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_rooms_workspace",
                table: "rooms");

            migrationBuilder.DropColumn(
                name: "WorkspacePath",
                table: "rooms");
        }
    }
}
