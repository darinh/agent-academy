using AgentAcademy.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AgentAcademyDbContext))]
    [Migration("20260401025000_AddBreakoutCloseReason")]
    public partial class AddBreakoutCloseReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CloseReason",
                table: "breakout_rooms",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE "__breakout_rooms_old" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_breakout_rooms" PRIMARY KEY,
                    "Name" TEXT NOT NULL,
                    "ParentRoomId" TEXT NOT NULL,
                    "AssignedAgentId" TEXT NOT NULL,
                    "Status" TEXT NOT NULL DEFAULT 'Active',
                    "TaskId" TEXT,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    CONSTRAINT "FK_breakout_rooms_rooms_ParentRoomId"
                        FOREIGN KEY ("ParentRoomId") REFERENCES "rooms" ("Id") ON DELETE CASCADE
                );

                INSERT INTO "__breakout_rooms_old" (
                    "Id", "Name", "ParentRoomId", "AssignedAgentId", "Status", "TaskId", "CreatedAt", "UpdatedAt"
                )
                SELECT
                    "Id", "Name", "ParentRoomId", "AssignedAgentId", "Status", "TaskId", "CreatedAt", "UpdatedAt"
                FROM "breakout_rooms";

                DROP TABLE "breakout_rooms";
                ALTER TABLE "__breakout_rooms_old" RENAME TO "breakout_rooms";

                CREATE INDEX "idx_breakout_rooms_parent"
                    ON "breakout_rooms" ("ParentRoomId");
                CREATE INDEX "idx_breakout_rooms_task"
                    ON "breakout_rooms" ("TaskId");
                """);
        }
    }
}
