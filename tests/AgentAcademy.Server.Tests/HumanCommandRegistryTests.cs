using AgentAcademy.Server.Commands;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Tests;

public sealed class HumanCommandRegistryTests
{
    [Fact]
    public void GetAll_ReturnsNonEmptyList()
    {
        var all = HumanCommandRegistry.GetAll();

        Assert.True(all.Count > 0, "Registry should contain at least one command");
    }

    [Fact]
    public void GetAll_ContainsExpectedCommands()
    {
        var all = HumanCommandRegistry.GetAll();
        var names = all.Select(m => m.Command).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("READ_FILE", names);
        Assert.Contains("SEARCH_CODE", names);
        Assert.Contains("LIST_ROOMS", names);
        Assert.Contains("LIST_AGENTS", names);
        Assert.Contains("LIST_TASKS", names);
        Assert.Contains("LIST_COMMANDS", names);
        Assert.Contains("SHOW_DIFF", names);
        Assert.Contains("GIT_LOG", names);
        Assert.Contains("SHOW_REVIEW_QUEUE", names);
        Assert.Contains("ROOM_HISTORY", names);
        Assert.Contains("ROOM_TOPIC", names);
        Assert.Contains("CREATE_ROOM", names);
        Assert.Contains("REOPEN_ROOM", names);
        Assert.Contains("CLOSE_ROOM", names);
        Assert.Contains("CLEANUP_ROOMS", names);
        Assert.Contains("INVITE_TO_ROOM", names);
        Assert.Contains("RUN_BUILD", names);
        Assert.Contains("RUN_TESTS", names);
        Assert.Contains("RUN_FORGE", names);
        Assert.Contains("FORGE_STATUS", names);
        Assert.Contains("LIST_FORGE_RUNS", names);
    }

    [Fact]
    public void Get_ReturnsMetadataForKnownCommand()
    {
        var meta = HumanCommandRegistry.Get("READ_FILE");

        Assert.NotNull(meta);
        Assert.Equal("READ_FILE", meta!.Command);
        Assert.Equal("Read file", meta.Title);
        Assert.Equal("code", meta.Category);
        Assert.False(meta.IsAsync);
        Assert.True(meta.Fields.Count > 0);
    }

    [Fact]
    public void Get_IsCaseInsensitive()
    {
        Assert.NotNull(HumanCommandRegistry.Get("read_file"));
        Assert.NotNull(HumanCommandRegistry.Get("Read_File"));
    }

    [Fact]
    public void Get_ReturnsNullForUnknownCommand()
    {
        Assert.Null(HumanCommandRegistry.Get("DOES_NOT_EXIST"));
    }

    [Fact]
    public void AllCommands_HaveRequiredMetadata()
    {
        foreach (var cmd in HumanCommandRegistry.GetAll())
        {
            Assert.False(string.IsNullOrWhiteSpace(cmd.Command), $"Command name is empty");
            Assert.False(string.IsNullOrWhiteSpace(cmd.Title), $"{cmd.Command} has empty title");
            Assert.False(string.IsNullOrWhiteSpace(cmd.Category), $"{cmd.Command} has empty category");
            Assert.Contains(cmd.Category, new[] { "workspace", "code", "git", "operations", "analytics", "forge" });
            Assert.False(string.IsNullOrWhiteSpace(cmd.Description), $"{cmd.Command} has empty description");
            Assert.False(string.IsNullOrWhiteSpace(cmd.Detail), $"{cmd.Command} has empty detail");
            Assert.NotNull(cmd.Fields);

            foreach (var field in cmd.Fields)
            {
                Assert.False(string.IsNullOrWhiteSpace(field.Name), $"{cmd.Command} has a field with empty name");
                Assert.False(string.IsNullOrWhiteSpace(field.Label), $"{cmd.Command}.{field.Name} has empty label");
                Assert.Contains(field.Kind, new[] { "text", "textarea", "number" });
                Assert.False(string.IsNullOrWhiteSpace(field.Description),
                    $"{cmd.Command}.{field.Name} has empty description");
            }
        }
    }

    [Fact]
    public void AsyncCommands_AreCorrectlyFlagged()
    {
        var runBuild = HumanCommandRegistry.Get("RUN_BUILD");
        var runTests = HumanCommandRegistry.Get("RUN_TESTS");
        var readFile = HumanCommandRegistry.Get("READ_FILE");

        Assert.True(runBuild!.IsAsync);
        Assert.True(runTests!.IsAsync);
        Assert.False(readFile!.IsAsync);
    }

    [Fact]
    public void RequiredFields_AreMarkedCorrectly()
    {
        var readFile = HumanCommandRegistry.Get("READ_FILE");
        var pathField = readFile!.Fields.Single(f => f.Name == "path");
        var startLineField = readFile.Fields.Single(f => f.Name == "startLine");

        Assert.True(pathField.Required);
        Assert.False(startLineField.Required);
    }

    [Fact]
    public void ForgeCommands_HaveExpectedMetadata()
    {
        var runForge = HumanCommandRegistry.Get("RUN_FORGE");
        var forgeStatus = HumanCommandRegistry.Get("FORGE_STATUS");
        var listForgeRuns = HumanCommandRegistry.Get("LIST_FORGE_RUNS");

        Assert.NotNull(runForge);
        Assert.Equal("forge", runForge!.Category);
        Assert.True(runForge.IsAsync);
        Assert.True(runForge.Fields.Single(f => f.Name == "title").Required);
        Assert.True(runForge.Fields.Single(f => f.Name == "description").Required);
        Assert.False(runForge.Fields.Single(f => f.Name == "methodology").Required);

        Assert.NotNull(forgeStatus);
        Assert.False(forgeStatus!.IsAsync);
        Assert.False(forgeStatus.Fields.Single(f => f.Name == "jobId").Required);

        Assert.NotNull(listForgeRuns);
        Assert.False(listForgeRuns!.IsAsync);
        Assert.False(listForgeRuns.Fields.Single(f => f.Name == "status").Required);
    }

    [Fact]
    public void CommandNames_MatchGetAllCommands()
    {
        var allNames = HumanCommandRegistry.GetAll().Select(c => c.Command).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registeredNames = HumanCommandRegistry.CommandNames;

        Assert.Equal(allNames.Count, registeredNames.Count);
        foreach (var name in registeredNames)
            Assert.Contains(name, allNames);
    }
}
