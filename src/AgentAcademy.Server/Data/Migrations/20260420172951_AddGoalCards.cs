using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGoalCards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "goal_cards",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    AgentId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentName = table.Column<string>(type: "TEXT", nullable: false),
                    RoomId = table.Column<string>(type: "TEXT", nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", nullable: true),
                    TaskDescription = table.Column<string>(type: "TEXT", nullable: false),
                    Intent = table.Column<string>(type: "TEXT", nullable: false),
                    Divergence = table.Column<string>(type: "TEXT", nullable: false),
                    Steelman = table.Column<string>(type: "TEXT", nullable: false),
                    Strawman = table.Column<string>(type: "TEXT", nullable: false),
                    Verdict = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "Proceed"),
                    FreshEyes1 = table.Column<string>(type: "TEXT", nullable: false),
                    FreshEyes2 = table.Column<string>(type: "TEXT", nullable: false),
                    FreshEyes3 = table.Column<string>(type: "TEXT", nullable: false),
                    PromptVersion = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    Status = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "Active"),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_goal_cards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_goal_cards_rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_goal_cards_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "idx_goal_cards_agent",
                table: "goal_cards",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "idx_goal_cards_created",
                table: "goal_cards",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "idx_goal_cards_room",
                table: "goal_cards",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "idx_goal_cards_status",
                table: "goal_cards",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "idx_goal_cards_task",
                table: "goal_cards",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "idx_goal_cards_verdict",
                table: "goal_cards",
                column: "Verdict");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "goal_cards");
        }
    }
}
