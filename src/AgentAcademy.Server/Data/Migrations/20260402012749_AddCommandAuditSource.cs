using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCommandAuditSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "command_audits",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_cmd_audits_source",
                table: "command_audits",
                column: "Source");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_cmd_audits_source",
                table: "command_audits");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "command_audits");
        }
    }
}
