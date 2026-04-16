namespace AgentAcademy.Server.Commands;

internal sealed record ShellCommand(
    string Operation,
    string? Branch,
    string? Message,
    string? Reason)
{
    private static readonly HashSet<string> SupportedOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "git-stash-pop",
        "git-commit",
        "git-checkout",
        "restart-server",
        "dotnet-build",
        "dotnet-test"
    };

    public static bool TryParse(
        IReadOnlyDictionary<string, object?> args,
        out ShellCommand? command,
        out string? error)
    {
        command = null;
        error = null;

        var operation = GetTrimmed(args, "operation") ?? GetTrimmed(args, "value");
        if (string.IsNullOrWhiteSpace(operation))
        {
            error = "Missing required argument: operation";
            return false;
        }

        if (!SupportedOperations.Contains(operation))
        {
            error = $"Unsupported SHELL operation '{operation}'. Supported operations: {string.Join(", ", SupportedOperations.OrderBy(x => x))}.";
            return false;
        }

        var normalizedOperation = operation.Trim().ToLowerInvariant();
        var branch = GetTrimmed(args, "branch");
        var message = GetTrimmed(args, "message");
        var reason = GetTrimmed(args, "reason");

        var allowedArgs = normalizedOperation switch
        {
            "git-stash-pop" => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "operation", "value", "branch" },
            "git-commit" => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "operation", "value", "message" },
            "git-checkout" => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "operation", "value", "branch" },
            "restart-server" => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "operation", "value", "reason" },
            "dotnet-build" => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "operation", "value" },
            "dotnet-test" => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "operation", "value" },
            _ => throw new InvalidOperationException($"Unreachable: '{normalizedOperation}' passed SupportedOperations guard but has no allowlist case.")
        };

        var unexpectedArgs = args.Keys
            .Where(key => !allowedArgs.Contains(key))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unexpectedArgs.Length > 0)
        {
            error = $"Unsupported argument(s) for operation '{normalizedOperation}': {string.Join(", ", unexpectedArgs)}";
            return false;
        }

        switch (normalizedOperation)
        {
            case "git-stash-pop":
            case "git-checkout":
                if (string.IsNullOrWhiteSpace(branch))
                {
                    error = "Missing required argument: branch";
                    return false;
                }

                if (!IsSafeGitRef(branch))
                {
                    error = "Invalid branch value. Branch names may contain letters, numbers, '.', '-', '_', and '/'; they cannot start with '-' or contain '..'.";
                    return false;
                }
                break;

            case "git-commit":
                if (string.IsNullOrWhiteSpace(message))
                {
                    error = "Missing required argument: message";
                    return false;
                }

                if (message.Length > 5000)
                {
                    error = "Commit message exceeds 5000 characters.";
                    return false;
                }
                break;

            case "restart-server":
                if (string.IsNullOrWhiteSpace(reason))
                {
                    error = "Missing required argument: reason";
                    return false;
                }

                if (reason.Length > 1000)
                {
                    error = "Restart reason exceeds 1000 characters.";
                    return false;
                }
                break;
        }

        command = new ShellCommand(normalizedOperation, branch, message, reason);
        return true;
    }

    private static string? GetTrimmed(IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var value) && value is string text
            ? text.Trim()
            : null;

    private static bool IsSafeGitRef(string value)
    {
        if (value.StartsWith("-", StringComparison.Ordinal) || value.Contains("..", StringComparison.Ordinal))
            return false;

        return value.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' or '/');
    }
}
