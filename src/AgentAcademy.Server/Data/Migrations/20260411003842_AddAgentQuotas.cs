using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentQuotas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "MaxCostPerHour",
                table: "agent_configs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxRequestsPerHour",
                table: "agent_configs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MaxTokensPerHour",
                table: "agent_configs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_llm_usage_agent_time",
                table: "llm_usage",
                columns: new[] { "AgentId", "RecordedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_llm_usage_agent_time",
                table: "llm_usage");

            migrationBuilder.DropColumn(
                name: "MaxCostPerHour",
                table: "agent_configs");

            migrationBuilder.DropColumn(
                name: "MaxRequestsPerHour",
                table: "agent_configs");

            migrationBuilder.DropColumn(
                name: "MaxTokensPerHour",
                table: "agent_configs");
        }
    }
}
