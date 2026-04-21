using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles CALL_ENDPOINT — makes an HTTP GET request to the running server.
/// v1 is GET-only with path restrictions to prevent abuse.
/// Restricted to Planner and Reviewer roles.
/// </summary>
public sealed class CallEndpointHandler : ICommandHandler
{
    public string CommandName => "CALL_ENDPOINT";

    private static readonly HashSet<string> AllowedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Planner", "Reviewer", "Human"
    };

    private static readonly HashSet<string> DeniedPathPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/auth",
        "/api/commands",
    };

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        // Role gate — enforce in handler, not just agents.json
        if (!AllowedRoles.Contains(context.AgentRole))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Permission,
                Error = $"CALL_ENDPOINT is restricted to Planner and Reviewer roles. Your role: {context.AgentRole}"
            };
        }

        // Parse args
        if (!command.Args.TryGetValue("path", out var pathObj) || pathObj is not string path || string.IsNullOrWhiteSpace(path))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Required argument 'path' is missing. Usage: CALL_ENDPOINT: path=/api/rooms"
            };
        }

        // Validate path: must start with /, no double-slash, no backslash
        if (!path.StartsWith('/') || path.Contains("//") || path.Contains('\\'))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Path must start with '/' and cannot contain '//' or '\\'."
            };
        }

        // Deny restricted paths
        if (DeniedPathPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Permission,
                Error = $"Path '{path}' is restricted. Denied prefixes: {string.Join(", ", DeniedPathPrefixes)}"
            };
        }

        // Resolve the server's own address
        var baseUrl = ResolveBaseUrl(context.Services);
        if (baseUrl is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = "Could not determine server address."
            };
        }

        try
        {
            using var httpClient = new HttpClient { Timeout = RequestTimeout };
            var url = $"{baseUrl}{path}";

            var response = await httpClient.GetAsync(url);

            // Read with a size limit to prevent memory pressure from large responses
            const int maxResponseBytes = 64 * 1024; // 64KB
            var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            var buffer = new char[maxResponseBytes];
            var charsRead = await reader.ReadAsync(buffer, 0, maxResponseBytes);
            var body = new string(buffer, 0, charsRead);
            var truncatedByRead = !reader.EndOfStream;

            if (body.Length > 4000)
                body = body[..2000] + "\n... (truncated) ...\n" + body[^2000..];
            else if (truncatedByRead)
                body += "\n... (response truncated at 64KB) ...";

            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["statusCode"] = (int)response.StatusCode,
                    ["contentType"] = response.Content.Headers.ContentType?.ToString(),
                    ["body"] = body,
                    ["method"] = "GET"
                }
            };
        }
        catch (TaskCanceledException)
        {
            return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Timeout, Error = $"Request timed out after {RequestTimeout.TotalSeconds}s." };
        }
        catch (HttpRequestException ex)
        {
            return command with { Status = CommandStatus.Error, ErrorCode = CommandErrorCode.Execution, Error = $"Request failed: {ex.Message}" };
        }
    }

    private static string? ResolveBaseUrl(IServiceProvider services)
    {
        try
        {
            var server = services.GetService<IServer>();
            var addresses = server?.Features.Get<IServerAddressesFeature>();
            var address = addresses?.Addresses.FirstOrDefault();
            if (address is not null)
            {
                // Extract port and rebuild as 127.0.0.1 to avoid DNS rebinding
                var uri = new Uri(address);
                return $"http://127.0.0.1:{uri.Port}";
            }
        }
        catch { }

        // Fallback: default dev port
        return "http://127.0.0.1:5066";
    }
}
