using AgentAcademy.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AgentAcademyDbContext))]
    [Migration("20260331005500_AllowBreakoutRoomPlans")]
    public partial class AllowBreakoutRoomPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE "__plans_new" (
                    "RoomId" TEXT NOT NULL CONSTRAINT "PK_plans" PRIMARY KEY,
                    "Content" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );

                INSERT INTO "__plans_new" ("RoomId", "Content", "UpdatedAt")
                SELECT "RoomId", "Content", "UpdatedAt"
                FROM "plans";

                DROP TABLE "plans";
                ALTER TABLE "__plans_new" RENAME TO "plans";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE "__plans_old" (
                    "RoomId" TEXT NOT NULL CONSTRAINT "PK_plans" PRIMARY KEY,
                    "Content" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    CONSTRAINT "FK_plans_rooms_RoomId" FOREIGN KEY ("RoomId") REFERENCES "rooms" ("Id") ON DELETE CASCADE
                );

                INSERT INTO "__plans_old" ("RoomId", "Content", "UpdatedAt")
                SELECT p."RoomId", p."Content", p."UpdatedAt"
                FROM "plans" p
                WHERE EXISTS (
                    SELECT 1
                    FROM "rooms" r
                    WHERE r."Id" = p."RoomId"
                );

                DROP TABLE "plans";
                ALTER TABLE "__plans_old" RENAME TO "plans";
                """);
        }
    }
}
