using System.Diagnostics;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles OPEN_COMPONENT — finds and reads a source file by component/class name.
/// Uses git ls-files to search tracked files in src/, avoiding build outputs.
/// </summary>
public sealed class OpenComponentHandler : ICommandHandler
{
    public string CommandName => "OPEN_COMPONENT";
    public bool IsRetrySafe => true;

    private const int MaxContentLength = 12_000;

    private static readonly string[] SearchExtensions = [".cs", ".tsx", ".ts", ".jsx", ".js"];

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        if (!command.Args.TryGetValue("name", out var nameObj) || nameObj is not string name || string.IsNullOrWhiteSpace(name))
        {
            if (!command.Args.TryGetValue("value", out nameObj) || nameObj is not string val || string.IsNullOrWhiteSpace(val))
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Validation,
                    Error = "Missing required argument: name (component or class name, e.g. \"CommandParser\" or \"TaskQueryService\")"
                };
            }
            name = val;
        }

        name = name.Trim();
        var projectRoot = context.WorkingDirectory ?? FindProjectRoot();

        // Use git ls-files to find tracked files in src/ matching the name
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = projectRoot
        };
        psi.ArgumentList.Add("ls-files");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("src/");

        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Execution,
                Error = $"Failed to list source files: {stderr.Trim()}"
            };
        }

        var allFiles = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Find files that match the component name (case-insensitive filename comparison)
        var matches = allFiles
            .Where(f =>
            {
                var fileName = Path.GetFileNameWithoutExtension(f);
                var ext = Path.GetExtension(f);
                return fileName.Equals(name, StringComparison.OrdinalIgnoreCase)
                    && SearchExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
            })
            .ToList();

        if (matches.Count == 0)
        {
            // Try partial match as fallback
            var partialMatches = allFiles
                .Where(f =>
                {
                    var fileName = Path.GetFileNameWithoutExtension(f);
                    var ext = Path.GetExtension(f);
                    return fileName.Contains(name, StringComparison.OrdinalIgnoreCase)
                        && SearchExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
                })
                .Take(10)
                .ToList();

            if (partialMatches.Count > 0)
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.NotFound,
                    Error = $"No exact match for \"{name}\". Similar files: {string.Join(", ", partialMatches)}. " +
                            "Use READ_FILE with the full path, or refine your component name."
                };
            }

            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"Component \"{name}\" not found in src/. Use SEARCH_CODE to locate it."
            };
        }

        if (matches.Count > 1)
        {
            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["ambiguous"] = true,
                    ["name"] = name,
                    ["matches"] = matches,
                    ["count"] = matches.Count,
                    ["hint"] = "Multiple files match. Use READ_FILE with the full path."
                }
            };
        }

        // Exactly one match — read the file
        var filePath = matches[0];
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, filePath));

        // Security: ensure within project
        var projectRootWithSep = projectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(projectRootWithSep, StringComparison.Ordinal))
        {
            return command with
            {
                Status = CommandStatus.Denied,
                ErrorCode = CommandErrorCode.Permission,
                Error = "Path traversal denied."
            };
        }

        if (!File.Exists(fullPath))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = $"File listed by git but missing from disk: {filePath}"
            };
        }

        var lines = await File.ReadAllLinesAsync(fullPath);
        var totalLines = lines.Length;

        int startLine = 1, endLine = totalLines;
        if (command.Args.TryGetValue("startLine", out var startObj))
            int.TryParse(startObj?.ToString(), out startLine);
        if (command.Args.TryGetValue("endLine", out var endObj))
            int.TryParse(endObj?.ToString(), out endLine);

        startLine = Math.Max(1, startLine);
        endLine = Math.Min(totalLines, endLine);

        var selectedLines = lines.Skip(startLine - 1).Take(endLine - startLine + 1).ToArray();
        var content = string.Join('\n', selectedLines);

        var truncated = false;
        var truncatedAtLine = endLine;
        if (content.Length > MaxContentLength)
        {
            truncated = true;
            content = content[..MaxContentLength];
            var lastNewline = content.LastIndexOf('\n');
            if (lastNewline > 0)
                content = content[..lastNewline];
            truncatedAtLine = startLine + content.Split('\n').Length - 1;
        }

        var result = new Dictionary<string, object?>
        {
            ["path"] = filePath,
            ["name"] = Path.GetFileNameWithoutExtension(filePath),
            ["content"] = content,
            ["totalLines"] = totalLines,
            ["startLine"] = startLine,
            ["endLine"] = truncated ? truncatedAtLine : endLine
        };

        if (truncated)
        {
            result["truncated"] = true;
            result["hint"] = $"Content truncated at line {truncatedAtLine} of {totalLines}. " +
                             $"Use startLine: {truncatedAtLine + 1} to continue reading.";
        }

        return command with
        {
            Status = CommandStatus.Success,
            Result = result
        };
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
