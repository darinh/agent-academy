using AgentAcademy.Server.Commands;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Tests;

public class CommandAuthorizerTests
{
    private readonly CommandAuthorizer _authorizer = new();

    private static AgentDefinition MakeAgent(
        string id = "test-1",
        string name = "TestAgent",
        CommandPermissionSet? permissions = null) =>
        new(id, name, "SoftwareEngineer", "Test", "prompt", null,
            new List<string>(), new List<string>(), true, null, permissions);

    private static CommandEnvelope MakeCommand(string command) =>
        new(command, new Dictionary<string, object?>(), CommandStatus.Success,
            null, null, "test-corr", DateTime.UtcNow, "test-1");

    // ── No Permissions ─────────────────────────────────────────

    [Fact]
    public void Authorize_NoPermissions_Denies()
    {
        var agent = MakeAgent(permissions: null);
        var command = MakeCommand("LIST_ROOMS");

        var result = _authorizer.Authorize(command, agent);

        Assert.NotNull(result);
        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Contains("no command permissions", result.Error);
    }

    // ── Exact Match ────────────────────────────────────────────

    [Fact]
    public void Authorize_ExactMatch_Allows()
    {
        var agent = MakeAgent(permissions: new CommandPermissionSet(
            Allowed: new List<string> { "LIST_ROOMS" },
            Denied: new List<string>()));
        var command = MakeCommand("LIST_ROOMS");

        var result = _authorizer.Authorize(command, agent);

        Assert.Null(result); // null = authorized
    }

    [Fact]
    public void Authorize_ExactMatch_CaseInsensitive()
    {
        var agent = MakeAgent(permissions: new CommandPermissionSet(
            Allowed: new List<string> { "list_rooms" },
            Denied: new List<string>()));
        var command = MakeCommand("LIST_ROOMS");

        Assert.Null(_authorizer.Authorize(command, agent));
    }

    // ── Wildcard Match ─────────────────────────────────────────

    [Fact]
    public void Authorize_WildcardPrefix_Allows()
    {
        var agent = MakeAgent(permissions: new CommandPermissionSet(
            Allowed: new List<string> { "LIST_*" },
            Denied: new List<string>()));

        Assert.Null(_authorizer.Authorize(MakeCommand("LIST_ROOMS"), agent));
        Assert.Null(_authorizer.Authorize(MakeCommand("LIST_AGENTS"), agent));
        Assert.Null(_authorizer.Authorize(MakeCommand("LIST_TASKS"), agent));
    }

    [Fact]
    public void Authorize_FullWildcard_AllowsAll()
    {
        var agent = MakeAgent(permissions: new CommandPermissionSet(
            Allowed: new List<string> { "*" },
            Denied: new List<string>()));

        Assert.Null(_authorizer.Authorize(MakeCommand("ANY_COMMAND"), agent));
    }

    [Fact]
    public void Authorize_WildcardDoesNotMatchDifferentPrefix()
    {
        var agent = MakeAgent(permissions: new CommandPermissionSet(
            Allowed: new List<string> { "LIST_*" },
            Denied: new List<string>()));

        var result = _authorizer.Authorize(MakeCommand("READ_FILE"), agent);

        Assert.NotNull(result);
        Assert.Equal(CommandStatus.Denied, result.Status);
    }

    // ── Deny Takes Priority ────────────────────────────────────

    [Fact]
    public void Authorize_DeniedOverridesAllowed()
    {
        var agent = MakeAgent(permissions: new CommandPermissionSet(
            Allowed: new List<string> { "*" },
            Denied: new List<string> { "RUN_BUILD" }));

        var result = _authorizer.Authorize(MakeCommand("RUN_BUILD"), agent);

        Assert.NotNull(result);
        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Contains("explicitly denied", result.Error);
    }

    [Fact]
    public void Authorize_DeniedWildcard()
    {
        var agent = MakeAgent(permissions: new CommandPermissionSet(
            Allowed: new List<string> { "*" },
            Denied: new List<string> { "RUN_*" }));

        Assert.NotNull(_authorizer.Authorize(MakeCommand("RUN_BUILD"), agent));
        Assert.NotNull(_authorizer.Authorize(MakeCommand("RUN_TESTS"), agent));
        Assert.Null(_authorizer.Authorize(MakeCommand("LIST_ROOMS"), agent));
    }

    // ── Default Deny ───────────────────────────────────────────

    [Fact]
    public void Authorize_NotInAllowList_Denies()
    {
        var agent = MakeAgent(permissions: new CommandPermissionSet(
            Allowed: new List<string> { "LIST_ROOMS" },
            Denied: new List<string>()));

        var result = _authorizer.Authorize(MakeCommand("READ_FILE"), agent);

        Assert.NotNull(result);
        Assert.Equal(CommandStatus.Denied, result.Status);
        Assert.Contains("not authorized", result.Error);
    }
}
