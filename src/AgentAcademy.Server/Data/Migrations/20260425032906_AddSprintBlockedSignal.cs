using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSprintBlockedSignal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BlockReason",
                table: "sprints",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BlockedAt",
                table: "sprints",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlockReason",
                table: "sprints");

            migrationBuilder.DropColumn(
                name: "BlockedAt",
                table: "sprints");
        }
    }
}
