using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Notifications;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for the DM command handler, runtime DM methods, and ASK_HUMAN deprecation.
/// </summary>
public class DmCommandTests : IDisposable
{
    private readonly DmHandler _handler = new();
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly AgentCatalogOptions _catalog;

    public DmCommandTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Collaboration Room",
            Agents:
            [
                new AgentDefinition(
                    Id: "planner-1", Name: "Aristotle", Role: "Planner",
                    Summary: "Planner", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(["DM", "LIST_*"], [])),
                new AgentDefinition(
                    Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                    Summary: "Engineer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true,
                    Permissions: new CommandPermissionSet(["DM", "LIST_*"], [])),
                new AgentDefinition(
                    Id: "reviewer-1", Name: "Socrates", Role: "Reviewer",
                    Summary: "Reviewer", StartupPrompt: "prompt", Model: null,
                    CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: false,
                    Permissions: new CommandPermissionSet(["DM", "LIST_*"], []))
            ]
        );

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton<ActivityBroadcaster>();
        services.AddScoped<ActivityPublisher>();
        services.AddSingleton(_catalog);
        services.AddScoped<TaskQueryService>();
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<MessageService>();
        services.AddScoped<WorkspaceRuntime>();
        services.AddScoped<SystemSettingsService>();
        services.AddScoped<ConversationSessionService>();

        // Real NotificationManager with no providers (SendAgentQuestionAsync returns false)
        services.AddSingleton<NotificationManager>();

        // Real services needed by AgentOrchestrator
        services.AddSingleton<IAgentExecutor>(Substitute.For<IAgentExecutor>());
        services.AddSingleton<SpecManager>();
        services.AddSingleton<CommandAuthorizer>();
        services.AddSingleton<CommandPipeline>();
        services.AddSingleton<GitService>();
        services.AddSingleton(new WorktreeService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorktreeService>.Instance,
            repositoryRoot: "/tmp/test-repo"));
        services.AddSingleton<AgentOrchestrator>();

        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();

        // Create default room
        db.Rooms.Add(new RoomEntity
        {
            Id = "main",
            Name = "Main Collaboration Room",
            Status = "Active",
            CurrentPhase = "Intake",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            WorkspacePath = "/test/workspace"
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private CommandContext MakeContext(string agentId, string? agentName = null, string? role = null)
    {
        var scope = _serviceProvider.CreateScope();
        return new CommandContext(
            AgentId: agentId,
            AgentName: agentName ?? agentId,
            AgentRole: role ?? "Agent",
            RoomId: "main",
            BreakoutRoomId: null,
            Services: scope.ServiceProvider
        );
    }

    private CommandEnvelope MakeEnvelope(Dictionary<string, object?> args) => new(
        Command: "DM",
        Args: args,
        Status: CommandStatus.Success,
        Result: null,
        Error: null,
        CorrelationId: Guid.NewGuid().ToString("N"),
        Timestamp: DateTime.UtcNow,
        ExecutedBy: "test-agent"
    );

    // ── Handler Tests ────────────────────────────────────────────────

    [Fact]
    public async Task DmToHuman_RoutesNotification_ReturnsSuccess()
    {
        var envelope = MakeEnvelope(new Dictionary<string, object?>
        {
            ["recipient"] = "@Human",
            ["message"] = "What auth strategy should we use?"
        });
        var ctx = MakeContext("planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(envelope, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.NotNull(result.Result);
        // No notification providers configured, so DM is stored but not delivered via Discord
        Assert.Equal("stored", result.Result!["status"]);
    }

    [Fact]
    public async Task DmToHuman_StoresMessage()
    {
        var envelope = MakeEnvelope(new Dictionary<string, object?>
        {
            ["recipient"] = "Human",
            ["message"] = "Need clarification on requirements."
        });
        var ctx = MakeContext("planner-1", "Aristotle", "Planner");

        await _handler.ExecuteAsync(envelope, ctx);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var dm = await db.Messages.FirstOrDefaultAsync(m => m.RecipientId == "human");
        Assert.NotNull(dm);
        Assert.Equal("Need clarification on requirements.", dm!.Content);
        Assert.Equal("planner-1", dm.SenderId);
        Assert.Equal(nameof(MessageKind.DirectMessage), dm.Kind);
    }

    [Fact]
    public async Task DmToAgent_StoresMessageAndTriggersOrchestrator()
    {
        var envelope = MakeEnvelope(new Dictionary<string, object?>
        {
            ["recipient"] = "@Hephaestus",
            ["message"] = "Please review the auth module."
        });
        var ctx = MakeContext("planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(envelope, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("delivered", result.Result!["status"]);
        Assert.Equal("Hephaestus", result.Result["recipient"]);

        // Verify message stored
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var dm = await db.Messages.FirstOrDefaultAsync(m => m.RecipientId == "engineer-1");
        Assert.NotNull(dm);
        Assert.Equal("Please review the auth module.", dm!.Content);
    }

    [Fact]
    public async Task DmToAgent_MatchesByName_CaseInsensitive()
    {
        var envelope = MakeEnvelope(new Dictionary<string, object?>
        {
            ["recipient"] = "hephaestus",
            ["message"] = "Test message"
        });
        var ctx = MakeContext("planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(envelope, ctx);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal("engineer-1", result.Result!["recipientId"]);
    }

    [Fact]
    public async Task DmToUnknownAgent_ReturnsError()
    {
        var envelope = MakeEnvelope(new Dictionary<string, object?>
        {
            ["recipient"] = "@NonExistent",
            ["message"] = "Hello?"
        });
        var ctx = MakeContext("planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(envelope, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("Unknown recipient", result.Error);
        Assert.Contains("Aristotle", result.Error); // Lists available agents
    }

    [Fact]
    public async Task DmToSelf_ReturnsError()
    {
        var envelope = MakeEnvelope(new Dictionary<string, object?>
        {
            ["recipient"] = "@Aristotle",
            ["message"] = "Talking to myself"
        });
        var ctx = MakeContext("planner-1", "Aristotle", "Planner");

        var result = await _handler.ExecuteAsync(envelope, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("yourself", result.Error);
    }

    [Fact]
    public async Task DmMissingRecipient_ReturnsError()
    {
        var envelope = MakeEnvelope(new Dictionary<string, object?>
        {
            ["message"] = "No recipient specified"
        });
        var ctx = MakeContext("planner-1");

        var result = await _handler.ExecuteAsync(envelope, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("recipient", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DmMissingMessage_ReturnsError()
    {
        var envelope = MakeEnvelope(new Dictionary<string, object?>
        {
            ["recipient"] = "@Human"
        });
        var ctx = MakeContext("planner-1");

        var result = await _handler.ExecuteAsync(envelope, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
        Assert.Contains("message", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DmEmptyMessage_ReturnsError()
    {
        var envelope = MakeEnvelope(new Dictionary<string, object?>
        {
            ["recipient"] = "@Human",
            ["message"] = "   "
        });
        var ctx = MakeContext("planner-1");

        var result = await _handler.ExecuteAsync(envelope, ctx);

        Assert.Equal(CommandStatus.Error, result.Status);
    }

    // ── Runtime DM Methods ──────────────────────────────────────────

    [Fact]
    public async Task SendDirectMessage_StoresWithRecipientId()
    {
        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        

        var messageId = await runtime.SendDirectMessageAsync(
            "planner-1", "Aristotle", "Planner", "engineer-1",
            "Check the auth module.", "main");

        Assert.False(string.IsNullOrEmpty(messageId));

        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var msg = await db.Messages.FindAsync(messageId);
        Assert.NotNull(msg);
        Assert.Equal("engineer-1", msg!.RecipientId);
        Assert.Equal(nameof(MessageKind.DirectMessage), msg.Kind);
    }

    [Fact]
    public async Task SendDirectMessage_PostsSystemNotification()
    {
        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        

        await runtime.SendDirectMessageAsync(
            "planner-1", "Aristotle", "Planner", "engineer-1",
            "Check the auth module.", "main");

        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var sysMsg = await db.Messages
            .Where(m => m.Kind == nameof(MessageKind.System) && m.Content.Contains("direct message"))
            .FirstOrDefaultAsync();
        Assert.NotNull(sysMsg);
        Assert.Contains("Aristotle", sysMsg!.Content);
    }

    [Fact]
    public async Task GetDirectMessagesForAgent_UnreadOnly_ReturnsReceivedOnly()
    {
        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        

        // Send DM from planner to engineer
        await runtime.SendDirectMessageAsync(
            "planner-1", "Aristotle", "Planner", "engineer-1",
            "Hey Hephaestus!", "main");

        // Send DM from engineer to planner
        await runtime.SendDirectMessageAsync(
            "engineer-1", "Hephaestus", "SoftwareEngineer", "planner-1",
            "Hey Aristotle!", "main");

        // Default (unreadOnly=true): planner sees only the DM sent TO them
        var unread = await runtime.GetDirectMessagesForAgentAsync("planner-1");
        Assert.Single(unread);
        Assert.Equal("Hey Aristotle!", unread[0].Content);

        // unreadOnly=false: planner sees both sent and received
        var all = await runtime.GetDirectMessagesForAgentAsync("planner-1", unreadOnly: false);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task RoomMessages_ExcludeDMs()
    {
        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        

        // Post a regular message
        await runtime.PostMessageAsync(new PostMessageRequest(
            RoomId: "main",
            SenderId: "planner-1",
            Content: "Regular room message",
            Kind: MessageKind.Coordination
        ));

        // Send a DM
        await runtime.SendDirectMessageAsync(
            "planner-1", "Aristotle", "Planner", "engineer-1",
            "Private DM", "main");

        var room = await runtime.GetRoomAsync("main");
        Assert.NotNull(room);

        // Room should contain the regular message but NOT the DM content
        var hasRegular = room!.RecentMessages.Any(m => m.Content == "Regular room message");
        var hasDm = room.RecentMessages.Any(m => m.Content == "Private DM");
        Assert.True(hasRegular);
        Assert.False(hasDm);
    }

    [Fact]
    public async Task GetDmThreadsForHuman_GroupsByAgent()
    {
        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        

        // Agent DMs human
        await runtime.SendDirectMessageAsync(
            "planner-1", "Aristotle", "Planner", "human",
            "Question for you.", "main");

        // Human DMs agent
        await runtime.SendDirectMessageAsync(
            "human", "Human", "Human", "planner-1",
            "Here's my answer.", "main");

        var threads = await runtime.GetDmThreadsForHumanAsync();

        Assert.Single(threads);
        Assert.Equal("planner-1", threads[0].AgentId);
        Assert.Equal("Aristotle", threads[0].AgentName);
        Assert.Equal(2, threads[0].MessageCount);
    }

    [Fact]
    public async Task GetDmThreadMessages_ReturnsConversation()
    {
        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();
        

        await runtime.SendDirectMessageAsync(
            "planner-1", "Aristotle", "Planner", "human",
            "Hello human!", "main");

        await runtime.SendDirectMessageAsync(
            "human", "Human", "Human", "planner-1",
            "Hello agent!", "main");

        var messages = await runtime.GetDmThreadMessagesAsync("planner-1");

        Assert.Equal(2, messages.Count);
        Assert.Equal("Hello human!", messages[0].Content);
        Assert.Equal("Hello agent!", messages[1].Content);
    }

    // ── ASK_HUMAN Deprecation ───────────────────────────────────────

    [Fact]
    public void AskHuman_NotInKnownCommands()
    {
        var parser = new CommandParser();
        var result = parser.Parse("ASK_HUMAN:\n  Question: test");

        // ASK_HUMAN should not be parsed as a command anymore
        Assert.Empty(result.Commands);
    }

    [Fact]
    public void DmCommand_InKnownCommands()
    {
        var parser = new CommandParser();
        var result = parser.Parse("DM:\n  Recipient: @Human\n  Message: test");

        Assert.Single(result.Commands);
        Assert.Equal("DM", result.Commands[0].Command);
    }

    [Fact]
    public void DmHandler_CommandName_IsDM()
    {
        Assert.Equal("DM", _handler.CommandName);
    }

    // ── DM Acknowledgment ───────────────────────────────────────────

    [Fact]
    public async Task AcknowledgeDirectMessages_MarksAsRead()
    {
        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        await runtime.SendDirectMessageAsync(
            "planner-1", "Aristotle", "Planner", "engineer-1",
            "Check the auth module.", "main");

        await runtime.SendDirectMessageAsync(
            "engineer-1", "Hephaestus", "SoftwareEngineer", "planner-1",
            "Will do!", "main");

        // Before ack: engineer has 1 unread (the one sent TO them)
        var before = await runtime.GetDirectMessagesForAgentAsync("engineer-1");
        Assert.Single(before);
        Assert.Equal("Check the auth module.", before[0].Content);

        // Acknowledge by explicit IDs
        await runtime.AcknowledgeDirectMessagesAsync("engineer-1", before.Select(m => m.Id).ToList());

        // After ack: no unread DMs
        var after = await runtime.GetDirectMessagesForAgentAsync("engineer-1");
        Assert.Empty(after);
    }

    [Fact]
    public async Task GetDirectMessages_UnreadOnlyFalse_ReturnsAll()
    {
        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        await runtime.SendDirectMessageAsync(
            "planner-1", "Aristotle", "Planner", "engineer-1",
            "Message 1", "main");

        var first = await runtime.GetDirectMessagesForAgentAsync("engineer-1");
        await runtime.AcknowledgeDirectMessagesAsync("engineer-1", first.Select(m => m.Id).ToList());

        await runtime.SendDirectMessageAsync(
            "planner-1", "Aristotle", "Planner", "engineer-1",
            "Message 2", "main");

        // unreadOnly=true: only the new message
        var unread = await runtime.GetDirectMessagesForAgentAsync("engineer-1", unreadOnly: true);
        Assert.Single(unread);
        Assert.Equal("Message 2", unread[0].Content);

        // unreadOnly=false: both messages (sent + received)
        var all = await runtime.GetDirectMessagesForAgentAsync("engineer-1", unreadOnly: false);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task AcknowledgeDirectMessages_OnlyAffectsTargetAgent()
    {
        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        // Both agents receive a DM
        await runtime.SendDirectMessageAsync(
            "human", "Human", "Human", "engineer-1",
            "Hey engineer!", "main");
        await runtime.SendDirectMessageAsync(
            "human", "Human", "Human", "planner-1",
            "Hey planner!", "main");

        // Ack only engineer's DMs
        var engBefore = await runtime.GetDirectMessagesForAgentAsync("engineer-1");
        await runtime.AcknowledgeDirectMessagesAsync("engineer-1", engBefore.Select(m => m.Id).ToList());

        // Engineer: no unread
        var engDms = await runtime.GetDirectMessagesForAgentAsync("engineer-1");
        Assert.Empty(engDms);

        // Planner: still has unread
        var planDms = await runtime.GetDirectMessagesForAgentAsync("planner-1");
        Assert.Single(planDms);
    }

    [Fact]
    public async Task AcknowledgeDirectMessages_DoesNotAckSenderMessages()
    {
        using var scope = _serviceProvider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<WorkspaceRuntime>();

        // Planner sends DM to engineer
        await runtime.SendDirectMessageAsync(
            "planner-1", "Aristotle", "Planner", "engineer-1",
            "Please review.", "main");

        // Planner should NOT see this as their unread DM (they're the sender)
        var plannerUnread = await runtime.GetDirectMessagesForAgentAsync("planner-1");
        Assert.Empty(plannerUnread);

        // Engineer should see it
        var engUnread = await runtime.GetDirectMessagesForAgentAsync("engineer-1");
        Assert.Single(engUnread);
    }
}
