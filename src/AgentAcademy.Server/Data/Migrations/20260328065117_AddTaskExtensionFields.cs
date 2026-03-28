using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskExtensionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssignedAgentId",
                table: "tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignedAgentName",
                table: "tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BranchName",
                table: "tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CommitCount",
                table: "tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedAt",
                table: "tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FleetModels",
                table: "tasks",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "PullRequestNumber",
                table: "tasks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PullRequestStatus",
                table: "tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PullRequestUrl",
                table: "tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewRounds",
                table: "tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ReviewerAgentId",
                table: "tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Size",
                table: "tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartedAt",
                table: "tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TestsCreated",
                table: "tasks",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<bool>(
                name: "UsedFleet",
                table: "tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "idx_tasks_agent",
                table: "tasks",
                column: "AssignedAgentId");

            migrationBuilder.CreateIndex(
                name: "idx_tasks_status",
                table: "tasks",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_tasks_agent",
                table: "tasks");

            migrationBuilder.DropIndex(
                name: "idx_tasks_status",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "AssignedAgentId",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "AssignedAgentName",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "BranchName",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "CommitCount",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "FleetModels",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "PullRequestNumber",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "PullRequestStatus",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "PullRequestUrl",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "ReviewRounds",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "ReviewerAgentId",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "Size",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "StartedAt",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "TestsCreated",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "UsedFleet",
                table: "tasks");
        }
    }
}
