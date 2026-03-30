using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeedInstructionTemplates : Migration
    {
        // Stable GUIDs so the migration is idempotent across environments
        private const string VerificationFirstId = "b1a2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d";
        private const string PushbackEnabledId = "c2b3d4e5-f6a7-4b8c-9d0e-1f2a3b4c5d6e";
        private const string CodeReviewFocusId = "d3c4e5f6-a7b8-4c9d-0e1f-2a3b4c5d6e7f";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var now = new DateTime(2026, 3, 30, 0, 0, 0, DateTimeKind.Utc);

            migrationBuilder.InsertData(
                table: "instruction_templates",
                columns: new[] { "Id", "Name", "Description", "Content", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    {
                        VerificationFirstId,
                        "Verification-First",
                        "Agents verify all code changes with builds, tests, and type checks before presenting results.",
                        "You must verify all code changes before presenting them. Run the project's build command, type checker, and test suite after every change. Never show broken code to the user. If a check fails, fix the issue and re-verify before responding. Capture evidence of passing checks (exit codes, test counts) and include them in your response.",
                        now,
                        now
                    },
                    {
                        PushbackEnabledId,
                        "Pushback-Enabled",
                        "Agents evaluate requests critically and push back on problematic approaches before implementing.",
                        "Before executing any request, evaluate whether it is a good idea — at both the implementation and requirements level. If the request will introduce tech debt, duplication, or unnecessary complexity, say so. If there is a simpler approach, recommend it. If the request conflicts with existing behavior or has dangerous edge cases, flag it and wait for confirmation before proceeding. You are a senior engineer, not an order taker.",
                        now,
                        now
                    },
                    {
                        CodeReviewFocusId,
                        "Code Review Focus",
                        "Agents focus on finding real bugs and security issues, ignoring style and formatting.",
                        "Focus on finding bugs, security vulnerabilities, logic errors, race conditions, edge cases, and missing error handling. Ignore style, formatting, naming preferences, and minor code organization issues. For each issue you find, explain what the bug is, why it matters, and provide a concrete fix. If you find nothing wrong, say so clearly rather than inventing minor issues.",
                        now,
                        now
                    }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "instruction_templates",
                keyColumn: "Id",
                keyValues: new object[] { VerificationFirstId, PushbackEnabledId, CodeReviewFocusId });
        }
    }
}
