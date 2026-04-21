using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using AgentAcademy.Server.Data;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles QUERY_DB — executes a read-only SQL query against the application
/// database. Uses a separate read-only SQLite connection to enforce safety.
/// Restricted to Human role only.
/// </summary>
public sealed class QueryDbHandler : ICommandHandler
{
    public string CommandName => "QUERY_DB";
    public bool IsRetrySafe => true;

    private const int DefaultLimit = 100;
    private const int MaxLimit = 1000;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    private static readonly HashSet<string> DeniedTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "AgentMemories",
        "NotificationConfigs",
        "SystemSettings",
        "AgentConfigs",
        "InstructionTemplates",
    };

    private static readonly Regex ForbiddenPattern = new(
        @"\b(INSERT|UPDATE|DELETE|DROP|CREATE|ALTER|ATTACH|DETACH|VACUUM|REINDEX)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PragmaWritePattern = new(
        @"\bPRAGMA\b[^;]*=",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        // In-handler role gate — Human only
        if (!string.Equals(context.AgentRole, "Human", StringComparison.OrdinalIgnoreCase))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Permission,
                Error = $"QUERY_DB is restricted to Human role. Your role: {context.AgentRole}"
            };
        }

        if (!command.Args.TryGetValue("query", out var queryObj) ||
            queryObj is not string sql || string.IsNullOrWhiteSpace(sql))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Required argument 'query' is missing. Usage: QUERY_DB: query=SELECT * FROM Tasks LIMIT 10"
            };
        }

        // Reject multiple statements (semicolons outside strings)
        var trimmed = sql.Trim().TrimEnd(';');
        if (trimmed.Contains(';'))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Only a single SQL statement is allowed."
            };
        }

        // Reject forbidden keywords
        if (ForbiddenPattern.IsMatch(sql))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Only SELECT queries are allowed. Detected a forbidden statement keyword."
            };
        }

        // Reject PRAGMA writes
        if (PragmaWritePattern.IsMatch(sql))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "PRAGMA write operations are not allowed."
            };
        }

        // Check denied tables
        foreach (var table in DeniedTables)
        {
            if (sql.Contains(table, StringComparison.OrdinalIgnoreCase))
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Permission,
                    Error = $"Access to table '{table}' is denied. Restricted tables: {string.Join(", ", DeniedTables)}"
                };
            }
        }

        // Parse limit
        var limit = DefaultLimit;
        if (command.Args.TryGetValue("limit", out var limitObj))
        {
            limit = limitObj switch
            {
                int i => i,
                long l => (int)l,
                string s when int.TryParse(s, out var parsed) => parsed,
                _ => DefaultLimit
            };
            limit = Math.Clamp(limit, 1, MaxLimit);
        }

        // Get the connection string from EF Core's registered context
        using var scope = context.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetService<AgentAcademyDbContext>();
        if (dbContext is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = "Database context is not available."
            };
        }

        var connString = dbContext.Database.GetConnectionString();
        if (string.IsNullOrEmpty(connString))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = "Could not determine database connection string."
            };
        }

        // Force a read-only connection via SqliteConnectionStringBuilder
        var builder = new SqliteConnectionStringBuilder(connString)
        {
            Mode = SqliteOpenMode.ReadOnly
        };
        var readOnlyConnString = builder.ToString();

        try
        {
            await using var connection = new SqliteConnection(readOnlyConnString);
            await connection.OpenAsync();

            using var cts = new CancellationTokenSource(QueryTimeout);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;

            await using var reader = await cmd.ExecuteReaderAsync(cts.Token);

            var columns = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
                columns.Add(reader.GetName(i));

            var rows = new List<Dictionary<string, object?>>();
            var rowCount = 0;
            while (rowCount < limit && await reader.ReadAsync(cts.Token))
            {
                var row = new Dictionary<string, object?>();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
                rowCount++;
            }

            // Check if there are more rows beyond the limit
            var hasMore = rowCount == limit && await reader.ReadAsync(cts.Token);

            // Truncate output if too large
            var resultJson = System.Text.Json.JsonSerializer.Serialize(rows);
            var truncated = resultJson.Length > 8000;

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["columns"] = columns,
                    ["rows"] = rows,
                    ["rowCount"] = rowCount,
                    ["hasMore"] = hasMore,
                    ["truncated"] = truncated,
                    ["limit"] = limit
                }
            };
        }
        catch (OperationCanceledException)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Timeout,
                Error = $"Query timed out after {QueryTimeout.TotalSeconds}s."
            };
        }
        catch (SqliteException ex)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = $"SQL error: {ex.Message}"
            };
        }
    }
}
