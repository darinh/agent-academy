using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for Tier 2D Code &amp; Spec command handlers:
/// OpenSpecHandler, SearchSpecHandler, OpenComponentHandler, FindReferencesHandler.
/// </summary>
[Collection("CwdMutating")]
public sealed class Tier2CodeSpecCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _specsDir;
    private readonly string _srcDir;

    public Tier2CodeSpecCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tier2d-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "AgentAcademy.sln"), "");

        // Create specs structure
        _specsDir = Path.Combine(_tempDir, "specs");
        Directory.CreateDirectory(Path.Combine(_specsDir, "000-system-overview"));
        File.WriteAllText(Path.Combine(_specsDir, "000-system-overview", "spec.md"),
            "# 000 — System Overview\n\n## Purpose\nDescribes the overall system architecture.\n\n## Details\nLine 4\nLine 5\nLine 6\nLine 7\nLine 8\n");
        Directory.CreateDirectory(Path.Combine(_specsDir, "007-agent-commands"));
        File.WriteAllText(Path.Combine(_specsDir, "007-agent-commands", "spec.md"),
            "# 007 — Agent Command System\n\n## Purpose\nDefines the command pipeline.\n\n## Implementation\nCommandPipeline handles execution.\nCommandParser extracts commands.\n");
        Directory.CreateDirectory(Path.Combine(_specsDir, "014-database-schema"));
        File.WriteAllText(Path.Combine(_specsDir, "014-database-schema", "spec.md"),
            "# 014 — Database Schema\n\n## Purpose\nDefines the SQLite schema.\n");

        // Create src structure
        _srcDir = Path.Combine(_tempDir, "src");
        Directory.CreateDirectory(Path.Combine(_srcDir, "AgentAcademy.Server", "Commands"));
        File.WriteAllText(Path.Combine(_srcDir, "AgentAcademy.Server", "Commands", "CommandParser.cs"),
            "namespace AgentAcademy.Server.Commands;\n\npublic sealed class CommandParser\n{\n    // Implementation here\n}\n");
        Directory.CreateDirectory(Path.Combine(_srcDir, "AgentAcademy.Server", "Services"));
        File.WriteAllText(Path.Combine(_srcDir, "AgentAcademy.Server", "Services", "TaskQueryService.cs"),
            "namespace AgentAcademy.Server.Services;\n\npublic sealed class TaskQueryService : ITaskQueryService\n{\n    public void Query() { }\n}\n");
        Directory.CreateDirectory(Path.Combine(_srcDir, "agent-academy-client", "src", "components"));
        File.WriteAllText(Path.Combine(_srcDir, "agent-academy-client", "src", "components", "TaskBoard.tsx"),
            "import React from 'react';\n\nexport const TaskBoard = () => {\n  return <div>TaskBoard</div>;\n};\n");

        // Initialize git repo so git grep/ls-files work
        RunGit("init");
        RunGit("config user.email test@test.com");
        RunGit("config user.name Test");
        RunGit("add -A");
        RunGit("commit -m init");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private void RunGit(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = _tempDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
    }

    private CommandContext MakeContext(IServiceProvider? services = null) => new(
        AgentId: "test-agent",
        AgentName: "Tester",
        AgentRole: "SoftwareEngineer",
        RoomId: "main",
        BreakoutRoomId: null,
        Services: services ?? BuildSpecServices()
    );

    private ServiceProvider BuildSpecServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISpecManager>(new SpecManager(_specsDir));
        return services.BuildServiceProvider();
    }

    private static CommandEnvelope MakeCommand(string name, Dictionary<string, object?> args) => new(
        Command: name,
        Args: args,
        Status: CommandStatus.Success,
        Result: null,
        Error: null,
        CorrelationId: Guid.NewGuid().ToString(),
        Timestamp: DateTime.UtcNow,
        ExecutedBy: "test-agent"
    );

    // ==================== OPEN_SPEC ====================

    [Fact]
    public async Task OpenSpec_ByFullId_ReturnsContent()
    {
        var handler = new OpenSpecHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("OPEN_SPEC", new() { ["id"] = "007-agent-commands" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.Equal("007-agent-commands", dict["sectionId"]);
            Assert.Contains("Agent Command System", dict["heading"]?.ToString());
            Assert.Contains("CommandPipeline", dict["content"]?.ToString());
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task OpenSpec_ByNumericPrefix_ReturnsContent()
    {
        var handler = new OpenSpecHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("OPEN_SPEC", new() { ["id"] = "007" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.Equal("007-agent-commands", dict["sectionId"]);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task OpenSpec_ByPositionalValue_ReturnsContent()
    {
        var handler = new OpenSpecHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("OPEN_SPEC", new() { ["value"] = "014" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.Equal("014-database-schema", dict["sectionId"]);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task OpenSpec_NotFound_ReturnsErrorWithAvailable()
    {
        var handler = new OpenSpecHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("OPEN_SPEC", new() { ["id"] = "999" }),
                MakeContext());

            Assert.Equal(CommandStatus.Error, result.Status);
            Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
            Assert.Contains("not found", result.Error);
            Assert.Contains("007-agent-commands", result.Error);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task OpenSpec_AmbiguousPrefix_ReturnsError()
    {
        var handler = new OpenSpecHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            // "0" matches both 000 and 007 and 014
            var result = await handler.ExecuteAsync(
                MakeCommand("OPEN_SPEC", new() { ["id"] = "0" }),
                MakeContext());

            Assert.Equal(CommandStatus.Error, result.Status);
            Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
            Assert.Contains("Ambiguous", result.Error);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task OpenSpec_MissingId_ReturnsValidationError()
    {
        var handler = new OpenSpecHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("OPEN_SPEC", new()),
            MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task OpenSpec_WithLineRange_ReturnsSubset()
    {
        var handler = new OpenSpecHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("OPEN_SPEC", new() { ["id"] = "000", ["startLine"] = "1", ["endLine"] = "3" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.Equal(1, dict["startLine"]);
            Assert.Equal(3, dict["endLine"]);
            var content = dict["content"]!.ToString()!;
            var lineCount = content.Split('\n').Length;
            Assert.Equal(3, lineCount);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    // ==================== SEARCH_SPEC ====================

    [Fact]
    public async Task SearchSpec_FindsMatchesInSpecFiles()
    {
        var handler = new SearchSpecHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("SEARCH_SPEC", new() { ["query"] = "CommandPipeline" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            var count = (int)dict["count"]!;
            Assert.True(count > 0);
            var matches = (List<Dictionary<string, object?>>)dict["matches"]!;
            Assert.All(matches, m => Assert.StartsWith("specs/", m["file"]!.ToString()));
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task SearchSpec_NoMatches_ReturnsEmptyResults()
    {
        var handler = new SearchSpecHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("SEARCH_SPEC", new() { ["query"] = "xyznonexistent123" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.Equal(0, dict["count"]);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task SearchSpec_MissingQuery_ReturnsValidationError()
    {
        var handler = new SearchSpecHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("SEARCH_SPEC", new()),
            MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task SearchSpec_AcceptsPositionalValue()
    {
        var handler = new SearchSpecHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("SEARCH_SPEC", new() { ["value"] = "Purpose" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.True((int)dict["count"]! > 0);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task SearchSpec_CaseInsensitive_FindsMatches()
    {
        var handler = new SearchSpecHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("SEARCH_SPEC", new() { ["query"] = "commandpipeline", ["ignoreCase"] = "true" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.True((int)dict["count"]! > 0);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    // ==================== OPEN_COMPONENT ====================

    [Fact]
    public async Task OpenComponent_ExactMatch_ReturnsContent()
    {
        var handler = new OpenComponentHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("OPEN_COMPONENT", new() { ["name"] = "CommandParser" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.Contains("CommandParser.cs", dict["path"]!.ToString());
            Assert.Contains("class CommandParser", dict["content"]!.ToString());
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task OpenComponent_CaseInsensitive_FindsFile()
    {
        var handler = new OpenComponentHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("OPEN_COMPONENT", new() { ["name"] = "commandparser" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.Contains("CommandParser.cs", dict["path"]!.ToString());
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task OpenComponent_TsxFile_Found()
    {
        var handler = new OpenComponentHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("OPEN_COMPONENT", new() { ["name"] = "TaskBoard" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.Contains("TaskBoard.tsx", dict["path"]!.ToString());
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task OpenComponent_NotFound_ReturnsError()
    {
        var handler = new OpenComponentHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("OPEN_COMPONENT", new() { ["name"] = "NonExistentWidget" }),
                MakeContext());

            Assert.Equal(CommandStatus.Error, result.Status);
            Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task OpenComponent_PartialMatch_SuggestsFiles()
    {
        var handler = new OpenComponentHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            // "Task" partially matches "TaskQueryService" and "TaskBoard"
            var result = await handler.ExecuteAsync(
                MakeCommand("OPEN_COMPONENT", new() { ["name"] = "Task" }),
                MakeContext());

            // Should be NOT_FOUND with partial match suggestions (since no exact "Task.cs/tsx/ts" file)
            Assert.Equal(CommandStatus.Error, result.Status);
            Assert.Equal(CommandErrorCode.NotFound, result.ErrorCode);
            Assert.Contains("Similar files", result.Error);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task OpenComponent_AcceptsPositionalValue()
    {
        var handler = new OpenComponentHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("OPEN_COMPONENT", new() { ["value"] = "TaskQueryService" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task OpenComponent_MissingName_ReturnsValidationError()
    {
        var handler = new OpenComponentHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("OPEN_COMPONENT", new()),
            MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task OpenComponent_WithLineRange_ReturnsSubset()
    {
        var handler = new OpenComponentHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("OPEN_COMPONENT", new() { ["name"] = "CommandParser", ["startLine"] = "1", ["endLine"] = "2" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.Equal(1, dict["startLine"]);
            Assert.Equal(2, dict["endLine"]);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    // ==================== FIND_REFERENCES ====================

    [Fact]
    public async Task FindReferences_FindsSymbolUsages()
    {
        var handler = new FindReferencesHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("FIND_REFERENCES", new() { ["symbol"] = "CommandParser" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.Equal("CommandParser", dict["symbol"]);
            var totalMatches = (int)dict["totalMatches"]!;
            Assert.True(totalMatches > 0);
            var files = (List<Dictionary<string, object?>>)dict["files"]!;
            Assert.NotEmpty(files);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task FindReferences_NoMatches_ReturnsEmpty()
    {
        var handler = new FindReferencesHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("FIND_REFERENCES", new() { ["symbol"] = "XyzNonExistentSymbol123" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.Equal(0, dict["totalMatches"]);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task FindReferences_MissingSymbol_ReturnsValidationError()
    {
        var handler = new FindReferencesHandler();
        var result = await handler.ExecuteAsync(
            MakeCommand("FIND_REFERENCES", new()),
            MakeContext());

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Equal(CommandErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task FindReferences_PathTraversal_Denied()
    {
        var handler = new FindReferencesHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("FIND_REFERENCES", new() { ["symbol"] = "test", ["path"] = "../../../etc" }),
                MakeContext());

            Assert.Equal(CommandStatus.Denied, result.Status);
            Assert.Equal(CommandErrorCode.Permission, result.ErrorCode);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task FindReferences_WithSubdirectoryScope()
    {
        var handler = new FindReferencesHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("FIND_REFERENCES", new()
                {
                    ["symbol"] = "TaskQueryService",
                    ["path"] = "src/AgentAcademy.Server/Services"
                }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.True((int)dict["totalMatches"]! > 0);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task FindReferences_AcceptsPositionalValue()
    {
        var handler = new FindReferencesHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("FIND_REFERENCES", new() { ["value"] = "ITaskQueryService" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            Assert.True((int)dict["totalMatches"]! > 0);
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task FindReferences_FixedStringNotRegex()
    {
        var handler = new FindReferencesHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            // "." in regex matches any char; with -F it's literal
            var result = await handler.ExecuteAsync(
                MakeCommand("FIND_REFERENCES", new() { ["symbol"] = "ITaskQueryService", ["wholeWord"] = "false" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            // Should only match literal "ITaskQueryService"
            var files = (List<Dictionary<string, object?>>)dict["files"]!;
            foreach (var file in files)
            {
                var fileLines = (List<Dictionary<string, object?>>)file["lines"]!;
                Assert.All(fileLines, l => Assert.Contains("ITaskQueryService", l["text"]!.ToString()));
            }
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    [Fact]
    public async Task FindReferences_GroupsByFile()
    {
        var handler = new FindReferencesHandler();
        var oldDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            var result = await handler.ExecuteAsync(
                MakeCommand("FIND_REFERENCES", new() { ["symbol"] = "class" }),
                MakeContext());

            Assert.Equal(CommandStatus.Success, result.Status);
            var dict = (Dictionary<string, object?>)result.Result!;
            var files = (List<Dictionary<string, object?>>)dict["files"]!;
            // Each file group should have a "file", "count", and "lines" key
            foreach (var fg in files)
            {
                Assert.NotNull(fg["file"]);
                Assert.NotNull(fg["count"]);
                Assert.NotNull(fg["lines"]);
            }
        }
        finally { Directory.SetCurrentDirectory(oldDir); }
    }

    // ==================== PARSER REGISTRATION ====================

    [Fact]
    public void CommandParser_KnowsTier2DCommands()
    {
        Assert.Contains("OPEN_SPEC", CommandParser.KnownCommands);
        Assert.Contains("SEARCH_SPEC", CommandParser.KnownCommands);
        Assert.Contains("OPEN_COMPONENT", CommandParser.KnownCommands);
        Assert.Contains("FIND_REFERENCES", CommandParser.KnownCommands);
    }

    [Fact]
    public void CommandParser_ParsesTier2DCommands()
    {
        var parser = new CommandParser();

        var result1 = parser.Parse("OPEN_SPEC: id=007");
        Assert.Single(result1.Commands);
        Assert.Equal("OPEN_SPEC", result1.Commands[0].Command);
        Assert.Equal("007", result1.Commands[0].Args["id"]);

        var result2 = parser.Parse("SEARCH_SPEC: query=CommandPipeline");
        Assert.Single(result2.Commands);
        Assert.Equal("SEARCH_SPEC", result2.Commands[0].Command);

        var result3 = parser.Parse("OPEN_COMPONENT: name=TaskBoard");
        Assert.Single(result3.Commands);
        Assert.Equal("OPEN_COMPONENT", result3.Commands[0].Command);

        var result4 = parser.Parse("FIND_REFERENCES: symbol=ISpecManager");
        Assert.Single(result4.Commands);
        Assert.Equal("FIND_REFERENCES", result4.Commands[0].Command);
    }
}
