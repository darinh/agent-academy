using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentAcademy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoryFts5Search : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FTS5 virtual table for full-text search on agent memories.
            // Only key and value are indexed — agent_id filtering is done via
            // JOIN to the main table, so it doesn't pollute FTS5 match results.
            migrationBuilder.Sql("""
                CREATE VIRTUAL TABLE IF NOT EXISTS agent_memories_fts
                USING fts5(
                    key,
                    value,
                    content='agent_memories',
                    content_rowid='rowid'
                );
            """);

            // Populate from existing data
            migrationBuilder.Sql("""
                INSERT INTO agent_memories_fts(rowid, key, value)
                SELECT rowid, Key, Value FROM agent_memories;
            """);

            // Keep FTS index in sync: INSERT trigger
            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS agent_memories_ai AFTER INSERT ON agent_memories BEGIN
                    INSERT INTO agent_memories_fts(rowid, key, value)
                    VALUES (new.rowid, new.Key, new.Value);
                END;
            """);

            // Keep FTS index in sync: DELETE trigger
            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS agent_memories_ad AFTER DELETE ON agent_memories BEGIN
                    INSERT INTO agent_memories_fts(agent_memories_fts, rowid, key, value)
                    VALUES ('delete', old.rowid, old.Key, old.Value);
                END;
            """);

            // Keep FTS index in sync: UPDATE trigger
            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS agent_memories_au AFTER UPDATE ON agent_memories BEGIN
                    INSERT INTO agent_memories_fts(agent_memories_fts, rowid, key, value)
                    VALUES ('delete', old.rowid, old.Key, old.Value);
                    INSERT INTO agent_memories_fts(rowid, key, value)
                    VALUES (new.rowid, new.Key, new.Value);
                END;
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS agent_memories_au;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS agent_memories_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS agent_memories_ai;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS agent_memories_fts;");
        }
    }
}
