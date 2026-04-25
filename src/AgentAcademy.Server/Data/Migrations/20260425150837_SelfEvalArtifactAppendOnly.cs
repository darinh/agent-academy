using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SelfEvalArtifactAppendOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_sprint_artifacts_sprint_stage_type_unique",
                table: "sprint_artifacts");

            migrationBuilder.CreateIndex(
                name: "idx_sprint_artifacts_sprint_stage_type_unique",
                table: "sprint_artifacts",
                columns: new[] { "SprintId", "Stage", "Type" },
                unique: true,
                filter: "\"Type\" != 'SelfEvaluationReport'");

            migrationBuilder.CreateIndex(
                name: "idx_sprint_artifacts_sprint_type_created_desc",
                table: "sprint_artifacts",
                columns: new[] { "SprintId", "Type", "CreatedAt" },
                descending: new[] { false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_sprint_artifacts_sprint_stage_type_unique",
                table: "sprint_artifacts");

            migrationBuilder.DropIndex(
                name: "idx_sprint_artifacts_sprint_type_created_desc",
                table: "sprint_artifacts");

            migrationBuilder.CreateIndex(
                name: "idx_sprint_artifacts_sprint_stage_type_unique",
                table: "sprint_artifacts",
                columns: new[] { "SprintId", "Stage", "Type" },
                unique: true);
        }
    }
}
