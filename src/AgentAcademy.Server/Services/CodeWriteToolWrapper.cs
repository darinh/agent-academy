using System.ComponentModel;
using System.Diagnostics;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Wrapper that captures agent identity for code-write tool functions.
/// Enforces path restrictions: files must be within <c>src/</c> and cannot
/// modify protected infrastructure files.
/// </summary>
internal sealed class CodeWriteToolWrapper
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly string _agentId;
    private readonly string _agentName;
    private readonly AgentGitIdentity? _gitIdentity;
    private readonly string? _roomId;

    // Files that agents must never modify (core infrastructure).
    private static readonly string[] ProtectedPaths =
    [
        "Services/AgentToolFunctions.cs",
        "Services/AgentToolRegistry.cs",
        "Services/IAgentToolRegistry.cs",
        "Services/CopilotExecutor.cs",
        "Services/AgentOrchestrator.cs",
        "Services/GitService.cs",
        "Program.cs",
    ];

    private const int MaxContentLength = 100_000; // 100 KB

    internal CodeWriteToolWrapper(
        IServiceScopeFactory scopeFactory, ILogger logger,
        string agentId, string agentName, AgentGitIdentity? gitIdentity = null, string? roomId = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _agentId = agentId;
        _agentName = agentName;
        _gitIdentity = gitIdentity;
        _roomId = roomId;
    }

    [Description("Write content to a file in the project. Creates the file if it doesn't exist, overwrites if it does. " +
                 "The file is automatically staged for commit. Paths must be within src/ and relative to the project root.")]
    internal async Task<string> WriteFileAsync(
        [Description("File path relative to the project root (e.g., src/AgentAcademy.Server/Models/MyModel.cs)")]
        string path,
        [Description("The full content to write to the file")]
        string content)
    {
        _logger.LogInformation("Tool call: write_file by {AgentId} (path={Path}, length={Length})",
            _agentId, path, content?.Length ?? 0);

        if (string.IsNullOrWhiteSpace(path))
            return "Error: path is required.";
        if (content is null)
            return "Error: content is required (use empty string for empty file).";
        if (content.Length > MaxContentLength)
            return $"Error: Content too large ({content.Length:N0} chars). Maximum is {MaxContentLength:N0} chars.";

        // Reject binary content (null bytes)
        if (content.Contains('\0'))
            return "Error: Binary content detected (null bytes). Only text files are supported.";

        var projectRoot = AgentToolFunctions.FindProjectRoot();
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, path));

        // Security: path must be within the project directory
        var rootWithSep = projectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSep, StringComparison.Ordinal))
            return "Error: Path traversal denied — file must be within the project directory.";

        // Restrict writes to src/ directory only
        var relativePath = Path.GetRelativePath(projectRoot, fullPath);
        if (!relativePath.StartsWith("src" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return "Error: Writes are restricted to the src/ directory. Cannot write to: " + relativePath;

        // Block protected infrastructure files
        // Normalize separators to forward slashes for cross-platform comparison
        var normalizedRelative = relativePath.Replace('\\', '/');
        foreach (var protectedPath in ProtectedPaths)
        {
            if (normalizedRelative.EndsWith(protectedPath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Agent {AgentId} attempted to write protected file: {Path}",
                    _agentId, relativePath);
                return $"Error: {Path.GetFileName(protectedPath)} is a protected infrastructure file and cannot be modified by agents.";
            }
        }

        try
        {
            // Create parent directories if needed
            var directory = Path.GetDirectoryName(fullPath);
            if (directory is not null && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var isNew = !File.Exists(fullPath);
            await File.WriteAllTextAsync(fullPath, content);

            _logger.LogInformation(
                "Agent {AgentId} ({AgentName}) wrote file: {Path} ({Length} chars, new={IsNew})",
                _agentId, _agentName, relativePath, content.Length, isNew);

            // Stage the file for commit
            var staged = await StageFileAsync(projectRoot, relativePath);

            var action = isNew ? "Created" : "Updated";
            var stageStatus = staged ? "staged for commit" : "written but NOT staged (git add failed)";

            await RecordArtifactAsync(relativePath, action);

            return $"{action}: {relativePath} ({content.Length:N0} chars, {stageStatus})";
        }
        catch (UnauthorizedAccessException)
        {
            return $"Error: Permission denied writing to {relativePath}.";
        }
        catch (IOException ex)
        {
            return $"Error writing file: {ex.Message}";
        }
    }

    private async Task<bool> StageFileAsync(string projectRoot, string relativePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = projectRoot
            };
            psi.ArgumentList.Add("add");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(relativePath);

            using var process = Process.Start(psi);
            if (process is not null)
            {
                await process.WaitForExitAsync();
                if (process.ExitCode != 0)
                {
                    var stderr = await process.StandardError.ReadToEndAsync();
                    _logger.LogWarning("git add failed for {Path}: {Error}", relativePath, stderr);
                    return false;
                }
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stage file {Path} — file was written but not staged", relativePath);
            return false;
        }
    }

    [Description("Commit all staged changes with a conventional commit message. " +
                 "Use after write_file to persist your changes. Returns the commit SHA on success.")]
    internal async Task<string> CommitChangesAsync(
        [Description("Conventional commit message (e.g., 'feat: add user endpoint with validation'). " +
                     "Use prefixes: feat:, fix:, refactor:, test:, docs:")]
        string message)
    {
        _logger.LogInformation("Tool call: commit_changes by {AgentId} (message={Message})",
            _agentId, message);

        if (string.IsNullOrWhiteSpace(message))
            return "Error: message is required. Provide a conventional commit message (e.g., 'feat: add ITimeProvider abstraction').";

        if (message.Length > 5000)
            return "Error: Commit message exceeds 5000 characters.";

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var gitService = scope.ServiceProvider.GetRequiredService<GitService>();

            var commitSha = await gitService.CommitAsync(message, _gitIdentity);

            _logger.LogInformation(
                "commit_changes by {AgentId} ({AgentName}): {CommitSha} — {Message}",
                _agentId, _agentName, commitSha, message);

            await RecordCommitArtifactAsync(scope, commitSha.Trim());

            return $"Committed: {commitSha.Trim()}\nMessage: {message}";
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("no changes", StringComparison.OrdinalIgnoreCase))
        {
            return "Error: Nothing to commit. Stage files first (write_file auto-stages, or use other file operations).";
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "commit_changes failed for {AgentId}", _agentId);
            return $"Error: Commit failed — {ex.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in commit_changes for {AgentId}", _agentId);
            return $"Error: Unexpected failure — {ex.Message}";
        }
    }

    private async Task RecordArtifactAsync(string filePath, string operation)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var tracker = scope.ServiceProvider.GetRequiredService<RoomArtifactTracker>();
            await tracker.RecordAsync(_roomId, _agentId, filePath, operation);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record artifact {Operation} for {Path}", operation, filePath);
        }
    }

    private async Task RecordCommitArtifactAsync(IServiceScope scope, string commitSha)
    {
        try
        {
            var gitService = scope.ServiceProvider.GetRequiredService<GitService>();
            var tracker = scope.ServiceProvider.GetRequiredService<RoomArtifactTracker>();
            var files = await gitService.GetFilesInCommitAsync(commitSha);
            await tracker.RecordCommitAsync(_roomId, _agentId, commitSha, files);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record commit artifacts for {Sha}", commitSha);
        }
    }
}
