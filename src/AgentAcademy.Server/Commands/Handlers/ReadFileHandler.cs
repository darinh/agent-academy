using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles READ_FILE — reads file content with optional line range.
/// Restricted to the project directory for safety.
/// </summary>
public sealed class ReadFileHandler : ICommandHandler
{
    public string CommandName => "READ_FILE";

    public Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("path", out var pathObj) || pathObj is not string path || string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(command with
            {
                Status = CommandStatus.Error,
                Error = "Missing required argument: path"
            });
        }

        // Resolve relative to project root
        var projectRoot = FindProjectRoot();
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, path));

        // Security: ensure the path is within the project directory
        var projectRootWithSep = projectRoot.EndsWith(Path.DirectorySeparatorChar)
            ? projectRoot : projectRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(projectRootWithSep, StringComparison.Ordinal) &&
            !fullPath.Equals(projectRoot, StringComparison.Ordinal))
        {
            return Task.FromResult(command with
            {
                Status = CommandStatus.Denied,
                Error = "Path traversal denied: file must be within the project directory."
            });
        }

        if (!File.Exists(fullPath))
        {
            return Task.FromResult(command with
            {
                Status = CommandStatus.Error,
                Error = $"File not found: {path}"
            });
        }

        var lines = File.ReadAllLines(fullPath);
        var totalLines = lines.Length;

        // Apply optional line range
        int startLine = 1, endLine = totalLines;
        if (command.Args.TryGetValue("startLine", out var startObj))
            int.TryParse(startObj?.ToString(), out startLine);
        if (command.Args.TryGetValue("endLine", out var endObj))
            int.TryParse(endObj?.ToString(), out endLine);

        startLine = Math.Max(1, startLine);
        endLine = Math.Min(totalLines, endLine);

        var selectedLines = lines.Skip(startLine - 1).Take(endLine - startLine + 1);
        var content = string.Join('\n', selectedLines);

        return Task.FromResult(command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["content"] = content,
                ["totalLines"] = totalLines,
                ["startLine"] = startLine,
                ["endLine"] = endLine,
                ["path"] = path
            }
        });
    }

    private static string FindProjectRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "AgentAcademy.sln")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
