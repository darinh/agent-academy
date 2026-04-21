using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles SHOW_ROUTES — introspects registered ASP.NET Core endpoints and returns
/// a structured list of API routes. Supports optional prefix and method filters.
/// Helps agents discover available API surface area at runtime.
/// </summary>
public sealed class ShowRoutesHandler : ICommandHandler
{
    public string CommandName => "SHOW_ROUTES";
    public bool IsRetrySafe => true;

    public Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var endpointSources = context.Services.GetServices<EndpointDataSource>();
        var routes = new List<Dictionary<string, object?>>();

        foreach (var source in endpointSources)
        {
            foreach (var endpoint in source.Endpoints)
            {
                if (endpoint is not RouteEndpoint routeEndpoint)
                    continue;

                var pattern = routeEndpoint.RoutePattern.RawText;
                if (string.IsNullOrEmpty(pattern))
                    continue;

                var httpMethodMetadata = endpoint.Metadata.GetMetadata<HttpMethodMetadata>();
                var methods = httpMethodMetadata?.HttpMethods.ToList() ?? new List<string>();

                // Extract controller and action from display name (format: "Namespace.Controller.Action (Assembly)")
                var (controller, action) = ParseDisplayName(endpoint.DisplayName);

                routes.Add(new Dictionary<string, object?>
                {
                    ["path"] = pattern,
                    ["methods"] = methods,
                    ["controller"] = controller,
                    ["action"] = action
                });
            }
        }

        // Apply optional prefix filter
        var filterPrefix = command.Args.TryGetValue("prefix", out var prefixVal)
            ? prefixVal?.ToString()
            : null;

        if (!string.IsNullOrEmpty(filterPrefix))
        {
            routes = routes
                .Where(r => r["path"]?.ToString()?
                    .StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase) == true)
                .ToList();
        }

        // Apply optional HTTP method filter
        var filterMethod = command.Args.TryGetValue("method", out var methodVal)
            ? methodVal?.ToString()?.ToUpperInvariant()
            : null;

        if (!string.IsNullOrEmpty(filterMethod))
        {
            routes = routes
                .Where(r => r["methods"] is List<string> m
                    && m.Contains(filterMethod, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        routes.Sort((a, b) => string.Compare(
            a["path"]?.ToString(), b["path"]?.ToString(), StringComparison.Ordinal));

        var result = new Dictionary<string, object?>
        {
            ["routes"] = routes,
            ["count"] = routes.Count
        };

        return Task.FromResult(command with
        {
            Status = CommandStatus.Success,
            Result = result
        });
    }

    /// <summary>
    /// Extracts controller and action names from the endpoint display name.
    /// ASP.NET Core formats these as "Namespace.ControllerName.ActionName (AssemblyName)".
    /// </summary>
    internal static (string? Controller, string? Action) ParseDisplayName(string? displayName)
    {
        if (string.IsNullOrEmpty(displayName))
            return (null, null);

        // Strip assembly suffix: "Namespace.Controller.Action (Assembly)" → "Namespace.Controller.Action"
        var parenIndex = displayName.IndexOf(" (", StringComparison.Ordinal);
        var qualifiedName = parenIndex >= 0 ? displayName[..parenIndex] : displayName;

        var parts = qualifiedName.Split('.');
        if (parts.Length < 2)
            return (null, null);

        var action = parts[^1];
        var controller = parts[^2];

        // Strip "Controller" suffix if present
        if (controller.EndsWith("Controller", StringComparison.Ordinal))
            controller = controller[..^"Controller".Length];

        return (controller, action);
    }
}
