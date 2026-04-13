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

            migrationBuilder.CreateIndex(
                name: "idx_sprint_schedules_workspace_unique",
                table: "sprint_schedules",
                column: "WorkspacePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_sprint_schedules_enabled_next_run",
                table: "sprint_schedules",
                columns: new[] { "Enabled", "NextRunAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "sprint_schedules");
        }
    }
}
