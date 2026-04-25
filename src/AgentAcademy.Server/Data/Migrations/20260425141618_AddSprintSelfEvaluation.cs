using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSprintSelfEvaluation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastSelfEvalAt",
                table: "sprints",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSelfEvalVerdict",
                table: "sprints",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SelfEvalAttempts",
                table: "sprints",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "SelfEvaluationInFlight",
                table: "sprints",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSelfEvalAt",
                table: "sprints");

            migrationBuilder.DropColumn(
                name: "LastSelfEvalVerdict",
                table: "sprints");

            migrationBuilder.DropColumn(
                name: "SelfEvalAttempts",
                table: "sprints");

            migrationBuilder.DropColumn(
                name: "SelfEvaluationInFlight",
                table: "sprints");
        }
    }
}
