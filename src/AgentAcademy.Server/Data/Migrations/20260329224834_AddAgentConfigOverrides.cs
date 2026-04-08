using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentConfigOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "instruction_templates",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_instruction_templates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "agent_configs",
                columns: table => new
                {
                    AgentId = table.Column<string>(type: "TEXT", nullable: false),
                    StartupPromptOverride = table.Column<string>(type: "TEXT", nullable: true),
                    ModelOverride = table.Column<string>(type: "TEXT", nullable: true),
                    CustomInstructions = table.Column<string>(type: "TEXT", nullable: true),
                    InstructionTemplateId = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_configs", x => x.AgentId);
                    table.ForeignKey(
                        name: "FK_agent_configs_instruction_templates_InstructionTemplateId",
                        column: x => x.InstructionTemplateId,
                        principalTable: "instruction_templates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_configs_InstructionTemplateId",
                table: "agent_configs",
                column: "InstructionTemplateId");

            migrationBuilder.CreateIndex(
                name: "idx_instruction_templates_name",
                table: "instruction_templates",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_configs");

            migrationBuilder.DropTable(
                name: "instruction_templates");
        }
    }
}
