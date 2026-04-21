using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles READ_FILE — reads file content with optional line range.
/// Restricted to the project directory for safety.
/// </summary>
public sealed class ReadFileHandler : ICommandHandler
{
    public string CommandName => "READ_FILE";
    public bool IsRetrySafe => true;

    public Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("path", out var pathObj) || pathObj is not string path || string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
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
                ErrorCode = CommandErrorCode.Permission,
                Error = "Path traversal denied: file must be within the project directory."
            });
        }

        if (!File.Exists(fullPath))
        {
            // If it's a directory, list its contents instead
            if (Directory.Exists(fullPath))
            {
                try
                {
                    var entries = Directory.GetFileSystemEntries(fullPath)
                        .Select(e => Path.GetRelativePath(projectRoot, e))
                        .OrderBy(e => e)
                        .ToList();

                    return Task.FromResult(command with
                    {
                        Status = CommandStatus.Success,
                        Result = new Dictionary<string, object?>
                        {
                            ["type"] = "directory",
                            ["path"] = path,
                            ["entries"] = entries,
                            ["count"] = entries.Count
                        }
                    });
                }
                catch (UnauthorizedAccessException ex)
                {
                    return Task.FromResult(command with
                    {
                        Status = CommandStatus.Error,
                        ErrorCode = CommandErrorCode.Permission,
                        Error = $"Cannot read directory: {ex.Message}"
                    });
                }
                catch (IOException ex)
                {
                    return Task.FromResult(command with
                    {
                        Status = CommandStatus.Error,
                        ErrorCode = CommandErrorCode.Execution,
                        Error = $"Cannot read directory: {ex.Message}"
                    });
                }
            }

            return Task.FromResult(command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
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

        var selectedLines = lines.Skip(startLine - 1).Take(endLine - startLine + 1).ToArray();
        var content = string.Join('\n', selectedLines);

        // Truncate if content exceeds max size
        const int MaxContentLength = 12_000;
        var truncated = false;
        var truncatedAtLine = endLine;
        if (content.Length > MaxContentLength)
        {
            truncated = true;
            content = content[..MaxContentLength];
            // Find the last complete line within the truncated content
            var lastNewline = content.LastIndexOf('\n');
            if (lastNewline > 0)
                content = content[..lastNewline];
            // Calculate what line we actually stopped at
            truncatedAtLine = startLine + content.Split('\n').Length - 1;
        }

        var result = new Dictionary<string, object?>
        {
            ["content"] = content,
            ["totalLines"] = totalLines,
            ["startLine"] = startLine,
            ["endLine"] = truncated ? truncatedAtLine : endLine,
            ["path"] = path
        };

        if (truncated)
        {
            result["truncated"] = true;
            result["hint"] = $"Content truncated at line {truncatedAtLine} of {totalLines}. " +
                             $"Use startLine: {truncatedAtLine + 1} to continue reading.";
        }

        return Task.FromResult(command with
        {
            Status = CommandStatus.Success,
            Result = result
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
