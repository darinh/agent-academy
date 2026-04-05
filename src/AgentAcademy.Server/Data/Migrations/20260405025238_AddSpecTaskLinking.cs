using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSpecTaskLinking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "spec_task_links",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", nullable: false),
                    SpecSectionId = table.Column<string>(type: "TEXT", nullable: false),
                    LinkType = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "Implements"),
                    LinkedByAgentId = table.Column<string>(type: "TEXT", nullable: false),
                    LinkedByAgentName = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_spec_task_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_spec_task_links_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_spec_task_links_spec",
                table: "spec_task_links",
                column: "SpecSectionId");

            migrationBuilder.CreateIndex(
                name: "idx_spec_task_links_task",
                table: "spec_task_links",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "idx_spec_task_links_unique",
                table: "spec_task_links",
                columns: new[] { "TaskId", "SpecSectionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "spec_task_links");
        }
    }
}
