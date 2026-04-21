using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddForgeJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "forge_jobs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "queued"),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    TaskBriefJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    MethodologyJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_forge_jobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_forge_jobs_created",
                table: "forge_jobs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "idx_forge_jobs_status",
                table: "forge_jobs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "forge_jobs");
        }
    }
}
