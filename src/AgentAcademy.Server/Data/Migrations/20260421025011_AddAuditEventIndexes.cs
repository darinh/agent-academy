using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditEventIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "idx_activity_correlation",
                table: "activity_events",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "idx_activity_severity_time",
                table: "activity_events",
                columns: new[] { "Severity", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_activity_correlation",
                table: "activity_events");

            migrationBuilder.DropIndex(
                name: "idx_activity_severity_time",
                table: "activity_events");
        }
    }
}
