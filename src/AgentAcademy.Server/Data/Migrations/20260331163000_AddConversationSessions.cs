using AgentAcademy.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AgentAcademyDbContext))]
    [Migration("20260331163000_AddConversationSessions")]
    public partial class AddConversationSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE "conversation_sessions" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_conversation_sessions" PRIMARY KEY,
                    "RoomId" TEXT NOT NULL,
                    "RoomType" TEXT NOT NULL DEFAULT 'Main',
                    "SequenceNumber" INTEGER NOT NULL DEFAULT 1,
                    "Status" TEXT NOT NULL DEFAULT 'Active',
                    "Summary" TEXT,
                    "MessageCount" INTEGER NOT NULL DEFAULT 0,
                    "CreatedAt" TEXT NOT NULL,
                    "ArchivedAt" TEXT
                );

                CREATE INDEX "idx_conversation_sessions_room_status"
                    ON "conversation_sessions" ("RoomId", "Status");

                CREATE TABLE "system_settings" (
                    "Key" TEXT NOT NULL CONSTRAINT "PK_system_settings" PRIMARY KEY,
                    "Value" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );

                ALTER TABLE "messages" ADD COLUMN "SessionId" TEXT;
                ALTER TABLE "breakout_messages" ADD COLUMN "SessionId" TEXT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS "conversation_sessions";
                DROP TABLE IF EXISTS "system_settings";

                -- SQLite doesn't support DROP COLUMN before 3.35.0,
                -- so we recreate the tables without SessionId.

                CREATE TABLE "__messages_old" AS SELECT
                    "Id", "RoomId", "SenderId", "SenderName", "SenderRole",
                    "SenderKind", "Kind", "Content", "SentAt",
                    "RecipientId", "CorrelationId", "ReplyToMessageId"
                FROM "messages";
                DROP TABLE "messages";
                ALTER TABLE "__messages_old" RENAME TO "messages";

                CREATE TABLE "__breakout_messages_old" AS SELECT
                    "Id", "BreakoutRoomId", "SenderId", "SenderName", "SenderRole",
                    "SenderKind", "Kind", "Content", "SentAt"
                FROM "breakout_messages";
                DROP TABLE "breakout_messages";
                ALTER TABLE "__breakout_messages_old" RENAME TO "breakout_messages";
                """);
        }
    }
}
