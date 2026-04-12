using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchFts5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Messages FTS5 ────────────────────────────────────────
            // Index SenderName + Content for full-text search.
            migrationBuilder.Sql("""
                CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts
                USING fts5(
                    SenderName,
                    Content,
                    content='messages',
                    content_rowid='rowid'
                );
            """);

            migrationBuilder.Sql("""
                INSERT INTO messages_fts(rowid, SenderName, Content)
                SELECT rowid, SenderName, Content FROM messages;
            """);

            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS messages_fts_ai AFTER INSERT ON messages BEGIN
                    INSERT INTO messages_fts(rowid, SenderName, Content)
                    VALUES (new.rowid, new.SenderName, new.Content);
                END;
            """);

            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS messages_fts_ad AFTER DELETE ON messages BEGIN
                    INSERT INTO messages_fts(messages_fts, rowid, SenderName, Content)
                    VALUES ('delete', old.rowid, old.SenderName, old.Content);
                END;
            """);

            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS messages_fts_au AFTER UPDATE ON messages BEGIN
                    INSERT INTO messages_fts(messages_fts, rowid, SenderName, Content)
                    VALUES ('delete', old.rowid, old.SenderName, old.Content);
                    INSERT INTO messages_fts(rowid, SenderName, Content)
                    VALUES (new.rowid, new.SenderName, new.Content);
                END;
            """);

            // ── Breakout Messages FTS5 ───────────────────────────────
            migrationBuilder.Sql("""
                CREATE VIRTUAL TABLE IF NOT EXISTS breakout_messages_fts
                USING fts5(
                    SenderName,
                    Content,
                    content='breakout_messages',
                    content_rowid='rowid'
                );
            """);

            migrationBuilder.Sql("""
                INSERT INTO breakout_messages_fts(rowid, SenderName, Content)
                SELECT rowid, SenderName, Content FROM breakout_messages;
            """);

            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS breakout_messages_fts_ai AFTER INSERT ON breakout_messages BEGIN
                    INSERT INTO breakout_messages_fts(rowid, SenderName, Content)
                    VALUES (new.rowid, new.SenderName, new.Content);
                END;
            """);

            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS breakout_messages_fts_ad AFTER DELETE ON breakout_messages BEGIN
                    INSERT INTO breakout_messages_fts(breakout_messages_fts, rowid, SenderName, Content)
                    VALUES ('delete', old.rowid, old.SenderName, old.Content);
                END;
            """);

            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS breakout_messages_fts_au AFTER UPDATE ON breakout_messages BEGIN
                    INSERT INTO breakout_messages_fts(breakout_messages_fts, rowid, SenderName, Content)
                    VALUES ('delete', old.rowid, old.SenderName, old.Content);
                    INSERT INTO breakout_messages_fts(rowid, SenderName, Content)
                    VALUES (new.rowid, new.SenderName, new.Content);
                END;
            """);

            // ── Tasks FTS5 ───────────────────────────────────────────
            migrationBuilder.Sql("""
                CREATE VIRTUAL TABLE IF NOT EXISTS tasks_fts
                USING fts5(
                    Title,
                    Description,
                    SuccessCriteria,
                    content='tasks',
                    content_rowid='rowid'
                );
            """);

            migrationBuilder.Sql("""
                INSERT INTO tasks_fts(rowid, Title, Description, SuccessCriteria)
                SELECT rowid, Title, Description, SuccessCriteria FROM tasks;
            """);

            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS tasks_fts_ai AFTER INSERT ON tasks BEGIN
                    INSERT INTO tasks_fts(rowid, Title, Description, SuccessCriteria)
                    VALUES (new.rowid, new.Title, new.Description, new.SuccessCriteria);
                END;
            """);

            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS tasks_fts_ad AFTER DELETE ON tasks BEGIN
                    INSERT INTO tasks_fts(tasks_fts, rowid, Title, Description, SuccessCriteria)
                    VALUES ('delete', old.rowid, old.Title, old.Description, old.SuccessCriteria);
                END;
            """);

            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS tasks_fts_au AFTER UPDATE ON tasks BEGIN
                    INSERT INTO tasks_fts(tasks_fts, rowid, Title, Description, SuccessCriteria)
                    VALUES ('delete', old.rowid, old.Title, old.Description, old.SuccessCriteria);
                    INSERT INTO tasks_fts(rowid, Title, Description, SuccessCriteria)
                    VALUES (new.rowid, new.Title, new.Description, new.SuccessCriteria);
                END;
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Tasks
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tasks_fts_au;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tasks_fts_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tasks_fts_ai;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS tasks_fts;");

            // Breakout messages
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS breakout_messages_fts_au;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS breakout_messages_fts_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS breakout_messages_fts_ai;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS breakout_messages_fts;");

            // Messages
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS messages_fts_au;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS messages_fts_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS messages_fts_ai;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS messages_fts;");
        }
    }
}
