using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLearningDigests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "learning_digests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    MemoriesCreated = table.Column<int>(type: "INTEGER", nullable: false),
                    RetrospectivesProcessed = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "Pending")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_digests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "learning_digest_sources",
                columns: table => new
                {
                    DigestId = table.Column<int>(type: "INTEGER", nullable: false),
                    RetrospectiveCommentId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_digest_sources", x => new { x.DigestId, x.RetrospectiveCommentId });
                    table.ForeignKey(
                        name: "FK_learning_digest_sources_learning_digests_DigestId",
                        column: x => x.DigestId,
                        principalTable: "learning_digests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_learning_digest_sources_task_comments_RetrospectiveCommentId",
                        column: x => x.RetrospectiveCommentId,
                        principalTable: "task_comments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_digest_sources_retro_unique",
                table: "learning_digest_sources",
                column: "RetrospectiveCommentId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "learning_digest_sources");
            migrationBuilder.DropTable(name: "learning_digests");
        }
    }
}
