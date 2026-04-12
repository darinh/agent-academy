using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveSprintUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Resolve any pre-existing duplicate active sprints per workspace:
            // keep the highest-numbered active sprint, cancel the rest.
            migrationBuilder.Sql("""
                UPDATE sprints SET "Status" = 'Cancelled', "CompletedAt" = datetime('now')
                WHERE "Status" = 'Active'
                  AND "Id" NOT IN (
                    SELECT s."Id" FROM sprints s
                    WHERE s."Status" = 'Active'
                    GROUP BY s."WorkspacePath"
                    HAVING s."Number" = MAX(s."Number")
                  );
                """);

            migrationBuilder.DropIndex(
                name: "idx_sprints_workspace",
                table: "sprints");

            migrationBuilder.CreateIndex(
                name: "idx_sprints_one_active_per_workspace",
                table: "sprints",
                column: "WorkspacePath",
                unique: true,
                filter: "\"Status\" = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_sprints_one_active_per_workspace",
                table: "sprints");

            migrationBuilder.CreateIndex(
                name: "idx_sprints_workspace",
                table: "sprints",
                column: "WorkspacePath");
        }
    }
}
