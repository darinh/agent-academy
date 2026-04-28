using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSprintTerminalCeremonyColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FinalSynthesisEnteredAt",
                table: "sprints",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SelfEvalStartedAt",
                table: "sprints",
                type: "TEXT",
                nullable: true);

            // §6.6 historical backfill (sprint-terminal-stage-handler-design.md):
            // Repair the state-machine invariant Status='Completed' ⇒
            // CurrentStage='FinalSynthesis' for any historical Completed sprints
            // that were force-completed at a non-terminal stage (e.g., Sprint #11
            // documented in the design doc §1). Stamps FinalSynthesisEnteredAt =
            // CompletedAt as a best-effort audit value (we don't know when those
            // sprints actually entered FinalSynthesis; CompletedAt is the most
            // defensible proxy). Idempotent: the WHERE clause excludes already-
            // repaired rows, so re-running the migration is a no-op.
            migrationBuilder.Sql(@"
                UPDATE sprints
                SET CurrentStage = 'FinalSynthesis',
                    FinalSynthesisEnteredAt = COALESCE(FinalSynthesisEnteredAt, CompletedAt)
                WHERE Status = 'Completed'
                  AND CurrentStage <> 'FinalSynthesis';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FinalSynthesisEnteredAt",
                table: "sprints");

            migrationBuilder.DropColumn(
                name: "SelfEvalStartedAt",
                table: "sprints");
        }
    }
}
