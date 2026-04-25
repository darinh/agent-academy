using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSprintSelfDriveCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastRoundCompletedAt",
                table: "sprints",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxRoundsOverride",
                table: "sprints",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RoundsThisSprint",
                table: "sprints",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RoundsThisStage",
                table: "sprints",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SelfDriveContinuations",
                table: "sprints",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastRoundCompletedAt",
                table: "sprints");

            migrationBuilder.DropColumn(
                name: "MaxRoundsOverride",
                table: "sprints");

            migrationBuilder.DropColumn(
                name: "RoundsThisSprint",
                table: "sprints");

            migrationBuilder.DropColumn(
                name: "RoundsThisStage",
                table: "sprints");

            migrationBuilder.DropColumn(
                name: "SelfDriveContinuations",
                table: "sprints");
        }
    }
}
