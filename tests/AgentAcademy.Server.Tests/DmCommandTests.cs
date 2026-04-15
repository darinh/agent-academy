using System.Security.Claims;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Commands.Handlers;
using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Notifications;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
        services.AddSingleton<MessageBroadcaster>();
        services.AddScoped<ActivityPublisher>();
        services.AddSingleton(_catalog);
        services.AddSingleton<IAgentCatalog>(_catalog);
        services.AddScoped<TaskDependencyService>();
        services.AddScoped<ITaskDependencyService>(sp => sp.GetRequiredService<TaskDependencyService>());
        services.AddScoped<TaskQueryService>();
        services.AddScoped<ITaskQueryService>(sp => sp.GetRequiredService<TaskQueryService>());
        services.AddScoped<TaskLifecycleService>();
        services.AddScoped<ITaskLifecycleService>(sp => sp.GetRequiredService<TaskLifecycleService>());
        services.AddScoped<MessageService>();
        services.AddScoped<IMessageService>(sp => sp.GetRequiredService<MessageService>());
        services.AddScoped<AgentLocationService>();
        services.AddScoped<IAgentLocationService>(sp => sp.GetRequiredService<AgentLocationService>());
        services.AddScoped<PlanService>();
        services.AddScoped<BreakoutRoomService>();
        services.AddScoped<IBreakoutRoomService>(sp => sp.GetRequiredService<BreakoutRoomService>());
        services.AddSingleton<ILogger<TaskItemService>>(NullLogger<TaskItemService>.Instance);
        services.AddSingleton<ILogger<RoomService>>(NullLogger<RoomService>.Instance);
        services.AddScoped<TaskItemService>();
        services.AddScoped<ITaskItemService>(sp => sp.GetRequiredService<TaskItemService>());
        services.AddScoped<PhaseTransitionValidator>();
        services.AddScoped<RoomService>();
        services.AddScoped<IRoomService>(sp => sp.GetRequiredService<RoomService>());
        services.AddScoped<RoomSnapshotBuilder>();

        services.AddScoped<IRoomSnapshotBuilder>(sp => sp.GetRequiredService<RoomSnapshotBuilder>());
        services.AddSingleton<ILogger<WorkspaceRoomService>>(NullLogger<WorkspaceRoomService>.Instance);
        services.AddScoped<WorkspaceRoomService>();

        services.AddScoped<IWorkspaceRoomService>(sp => sp.GetRequiredService<WorkspaceRoomService>());
        services.AddSingleton<ILogger<RoomLifecycleService>>(NullLogger<RoomLifecycleService>.Instance);
        services.AddScoped<RoomLifecycleService>();
        services.AddScoped<IRoomLifecycleService>(sp => sp.GetRequiredService<RoomLifecycleService>());
        services.AddScoped<CrashRecoveryService>();
        services.AddScoped<ICrashRecoveryService>(sp => sp.GetRequiredService<CrashRecoveryService>());
        services.AddSingleton<ILogger<CrashRecoveryService>>(NullLogger<CrashRecoveryService>.Instance);
        services.AddScoped<InitializationService>();
        services.AddSingleton<ILogger<InitializationService>>(NullLogger<InitializationService>.Instance);
        services.AddScoped<TaskOrchestrationService>();
        services.AddScoped<ITaskOrchestrationService>(sp => sp.GetRequiredService<TaskOrchestrationService>());
        services.AddSingleton<ILogger<TaskOrchestrationService>>(NullLogger<TaskOrchestrationService>.Instance);
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
        services.AddSingleton<AgentMemoryLoader>();
        services.AddSingleton<BreakoutCompletionService>();
        services.AddSingleton<IBreakoutCompletionService>(sp => sp.GetRequiredService<BreakoutCompletionService>());
        services.AddSingleton<BreakoutLifecycleService>();
        services.AddSingleton<IBreakoutLifecycleService>(sp => sp.GetRequiredService<BreakoutLifecycleService>());
        services.AddSingleton<TaskAssignmentHandler>();
        services.AddSingleton<ITaskAssignmentHandler>(sp => sp.GetRequiredService<TaskAssignmentHandler>());
        services.AddSingleton<AgentTurnRunner>();
        services.AddSingleton<ConversationRoundRunner>();
        services.AddSingleton<IConversationRoundRunner>(sp => sp.GetRequiredService<ConversationRoundRunner>());
        services.AddSingleton<DirectMessageRouter>();
        services.AddSingleton<IDirectMessageRouter>(sp => sp.GetRequiredService<DirectMessageRouter>());
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
        var messageService = scope.ServiceProvider.GetRequiredService<IMessageService>();
        var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();
        

        var messageId = await messageService.SendDirectMessageAsync(
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
        var messageService = scope.ServiceProvider.GetRequiredService<IMessageService>();
        var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();
        

        await messageService.SendDirectMessageAsync(
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
        var messageService = scope.ServiceProvider.GetRequiredService<IMessageService>();
        var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();
        

        // Send DM from planner to engineer
        await messageService.SendDirectMessageAsync(
            "planner-1", "Aristotle", "Planner", "engineer-1",
            "Hey Hephaestus!", "main");

        // Send DM from engineer to planner
        await messageService.SendDirectMessageAsync(
            "engineer-1", "Hephaestus", "SoftwareEngineer", "planner-1",
            "Hey Aristotle!", "main");

        // Default (unreadOnly=true): planner sees only the DM sent TO them
        var unread = await messageService.GetDirectMessagesForAgentAsync("planner-1");
        Assert.Single(unread);
        Assert.Equal("Hey Aristotle!", unread[0].Content);

        // unreadOnly=false: planner sees both sent and received
        var all = await messageService.GetDirectMessagesForAgentAsync("planner-1", unreadOnly: false);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task RoomMessages_ExcludeDMs()
    {
        using var scope = _serviceProvider.CreateScope();
        var messageService = scope.ServiceProvider.GetRequiredService<IMessageService>();
        var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();
        

        // Post a regular message
        await messageService.PostMessageAsync(new PostMessageRequest(
            RoomId: "main",
            SenderId: "planner-1",
            Content: "Regular room message",
            Kind: MessageKind.Coordination
        ));

        // Send a DM
        await messageService.SendDirectMessageAsync(
            "planner-1", "Aristotle", "Planner", "engineer-1",
            "Private DM", "main");

        var room = await roomService.GetRoomAsync("main");
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
        var messageService = scope.ServiceProvider.GetRequiredService<IMessageService>();
        var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();
        

        // Agent DMs human
        await messageService.SendDirectMessageAsync(
            "planner-1", "Aristotle", "Planner", "human",
            "Question for you.", "main");

        // Human DMs agent
        await messageService.SendDirectMessageAsync(
            "human", "Human", "Human", "planner-1",
            "Here's my answer.", "main");

        var threads = await messageService.GetDmThreadsForHumanAsync();

        Assert.Single(threads);
        Assert.Equal("planner-1", threads[0].AgentId);
        Assert.Equal("Aristotle", threads[0].AgentName);
        Assert.Equal(2, threads[0].MessageCount);
    }

    [Fact]
    public async Task GetDmThreadMessages_ReturnsConversation()
    {
        using var scope = _serviceProvider.CreateScope();
        var messageService = scope.ServiceProvider.GetRequiredService<IMessageService>();
        var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();
        

        await messageService.SendDirectMessageAsync(
            "planner-1", "Aristotle", "Planner", "human",
            "Hello human!", "main");

        await messageService.SendDirectMessageAsync(
            "human", "Human", "Human", "planner-1",
            "Hello agent!", "main");

        var messages = await messageService.GetDmThreadMessagesAsync("planner-1");

        Assert.Equal(2, messages.Count);
        Assert.Equal("Hello human!", messages[0].Content);
        Assert.Equal("Hello agent!", messages[1].Content);
    }

    [Fact]
    public async Task ConsultantDm_StoresWithConsultantRole()
    {
        using var scope = _serviceProvider.CreateScope();
        var messageService = scope.ServiceProvider.GetRequiredService<IMessageService>();
        var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();

        await messageService.SendDirectMessageAsync(
            "consultant", "Consultant", "Consultant", "planner-1",
            "I need your analysis.", "main");

        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var msg = await db.Messages
            .Where(m => m.Content == "I need your analysis.")
            .FirstOrDefaultAsync();

        Assert.NotNull(msg);
        Assert.Equal("consultant", msg!.SenderId);
        Assert.Equal("Consultant", msg.SenderName);
        Assert.Equal("Consultant", msg.SenderRole);
    }

    [Fact]
    public async Task GetDmThreadsForHuman_IncludesConsultantMessages()
    {
        using var scope = _serviceProvider.CreateScope();
        var messageService = scope.ServiceProvider.GetRequiredService<IMessageService>();
        var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();

        // Consultant DMs agent
        await messageService.SendDirectMessageAsync(
            "consultant", "Consultant", "Consultant", "planner-1",
            "Consultant question.", "main");

        // Agent replies to human mailbox
        await messageService.SendDirectMessageAsync(
            "planner-1", "Aristotle", "Planner", "human",
            "Answer from agent.", "main");

        var threads = await messageService.GetDmThreadsForHumanAsync();

        // Both messages should appear in the agent's thread
        var aristThread = threads.FirstOrDefault(t => t.AgentId == "planner-1");
        Assert.NotNull(aristThread);
        Assert.True(aristThread!.MessageCount >= 1);
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
        var messageService = scope.ServiceProvider.GetRequiredService<IMessageService>();
        var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();

        await messageService.SendDirectMessageAsync(
            "planner-1", "Aristotle", "Planner", "engineer-1",
            "Check the auth module.", "main");

        await messageService.SendDirectMessageAsync(
            "engineer-1", "Hephaestus", "SoftwareEngineer", "planner-1",
            "Will do!", "main");

        // Before ack: engineer has 1 unread (the one sent TO them)
        var before = await messageService.GetDirectMessagesForAgentAsync("engineer-1");
        Assert.Single(before);
        Assert.Equal("Check the auth module.", before[0].Content);

        // Acknowledge by explicit IDs
        await messageService.AcknowledgeDirectMessagesAsync("engineer-1", before.Select(m => m.Id).ToList());

        // After ack: no unread DMs
        var after = await messageService.GetDirectMessagesForAgentAsync("engineer-1");
        Assert.Empty(after);
    }

    [Fact]
    public async Task GetDirectMessages_UnreadOnlyFalse_ReturnsAll()
    {
        using var scope = _serviceProvider.CreateScope();
        var messageService = scope.ServiceProvider.GetRequiredService<IMessageService>();
        var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();

        await messageService.SendDirectMessageAsync(
            "planner-1", "Aristotle", "Planner", "engineer-1",
            "Message 1", "main");

        var first = await messageService.GetDirectMessagesForAgentAsync("engineer-1");
        await messageService.AcknowledgeDirectMessagesAsync("engineer-1", first.Select(m => m.Id).ToList());

        await messageService.SendDirectMessageAsync(
            "planner-1", "Aristotle", "Planner", "engineer-1",
            "Message 2", "main");

        // unreadOnly=true: only the new message
        var unread = await messageService.GetDirectMessagesForAgentAsync("engineer-1", unreadOnly: true);
        Assert.Single(unread);
        Assert.Equal("Message 2", unread[0].Content);

        // unreadOnly=false: both messages (sent + received)
        var all = await messageService.GetDirectMessagesForAgentAsync("engineer-1", unreadOnly: false);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task AcknowledgeDirectMessages_OnlyAffectsTargetAgent()
    {
        using var scope = _serviceProvider.CreateScope();
        var messageService = scope.ServiceProvider.GetRequiredService<IMessageService>();
        var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();

        // Both agents receive a DM
        await messageService.SendDirectMessageAsync(
            "human", "Human", "Human", "engineer-1",
            "Hey engineer!", "main");
        await messageService.SendDirectMessageAsync(
            "human", "Human", "Human", "planner-1",
            "Hey planner!", "main");

        // Ack only engineer's DMs
        var engBefore = await messageService.GetDirectMessagesForAgentAsync("engineer-1");
        await messageService.AcknowledgeDirectMessagesAsync("engineer-1", engBefore.Select(m => m.Id).ToList());

        // Engineer: no unread
        var engDms = await messageService.GetDirectMessagesForAgentAsync("engineer-1");
        Assert.Empty(engDms);

        // Planner: still has unread
        var planDms = await messageService.GetDirectMessagesForAgentAsync("planner-1");
        Assert.Single(planDms);
    }

    [Fact]
    public async Task AcknowledgeDirectMessages_DoesNotAckSenderMessages()
    {
        using var scope = _serviceProvider.CreateScope();
        var messageService = scope.ServiceProvider.GetRequiredService<IMessageService>();
        var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();

        // Planner sends DM to engineer
        await messageService.SendDirectMessageAsync(
            "planner-1", "Aristotle", "Planner", "engineer-1",
            "Please review.", "main");

        // Planner should NOT see this as their unread DM (they're the sender)
        var plannerUnread = await messageService.GetDirectMessagesForAgentAsync("planner-1");
        Assert.Empty(plannerUnread);

        // Engineer should see it
        var engUnread = await messageService.GetDirectMessagesForAgentAsync("engineer-1");
        Assert.Single(engUnread);
    }

    // ── DmController: Consultant Identity ───────────────────────────

    private DmController CreateDmController(ClaimsPrincipal? user = null)
    {
        var scope = _serviceProvider.CreateScope();
        var messageService = scope.ServiceProvider.GetRequiredService<IMessageService>();
        var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();
        var messageBroadcaster = scope.ServiceProvider.GetRequiredService<MessageBroadcaster>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<AgentOrchestrator>();
        var logger = NullLogger<DmController>.Instance;

        var controller = new DmController(messageService, roomService, messageBroadcaster, _catalog, orchestrator, logger);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = user ?? new ClaimsPrincipal(new ClaimsIdentity())
            }
        };
        return controller;
    }

    private static ClaimsPrincipal CreateConsultantUser()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "consultant-agent"),
            new Claim(ClaimTypes.Role, "Consultant"),
        ], "SharedSecret");
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal CreateHumanUser()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "darin"),
        ], "Cookies");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task DmController_SendMessage_ConsultantGetsConsultantIdentity()
    {
        var controller = CreateDmController(CreateConsultantUser());

        var result = await controller.SendMessage(
            "planner-1", new SendDmRequest("Hello from consultant"));

        var created = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(201, created.StatusCode);

        var msg = Assert.IsType<DmMessage>(created.Value);
        Assert.Equal("consultant", msg.SenderId);
        Assert.Equal("Consultant", msg.SenderName);
        Assert.Equal("Consultant", msg.SenderRole);
        Assert.True(msg.IsFromHuman);
    }

    [Fact]
    public async Task DmController_SendMessage_HumanGetsHumanIdentity()
    {
        var controller = CreateDmController(CreateHumanUser());

        var result = await controller.SendMessage(
            "planner-1", new SendDmRequest("Hello from human"));

        var created = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(201, created.StatusCode);

        var msg = Assert.IsType<DmMessage>(created.Value);
        Assert.Equal("human", msg.SenderId);
        Assert.Equal("Human", msg.SenderName);
        Assert.Equal("Human", msg.SenderRole);
        Assert.True(msg.IsFromHuman);
    }

    [Fact]
    public async Task DmController_GetThreadMessages_MapsSenderRoleForConsultant()
    {
        // Seed a consultant message via runtime
        using (var scope = _serviceProvider.CreateScope())
        {
            var messageService = scope.ServiceProvider.GetRequiredService<IMessageService>();
            var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();
            await messageService.SendDirectMessageAsync(
                "consultant", "Consultant", "Consultant", "planner-1",
                "Consultant message via API.", "main");
        }

        var controller = CreateDmController(CreateHumanUser());

        var result = await controller.GetThreadMessages("planner-1");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var messages = Assert.IsType<List<DmMessage>>(ok.Value);

        var consultantMsg = messages.FirstOrDefault(m => m.SenderId == "consultant");
        Assert.NotNull(consultantMsg);
        Assert.Equal("Consultant", consultantMsg!.SenderRole);
        Assert.True(consultantMsg.IsFromHuman);
    }

    [Fact]
    public async Task DmController_GetThreadMessages_ConsultantMessagesAreFromHuman()
    {
        // Seed both consultant and agent messages
        using (var scope = _serviceProvider.CreateScope())
        {
            var messageService = scope.ServiceProvider.GetRequiredService<IMessageService>();
            var roomService = scope.ServiceProvider.GetRequiredService<RoomService>();
            await messageService.SendDirectMessageAsync(
                "consultant", "Consultant", "Consultant", "engineer-1",
                "Consultant asks a question.", "main");
            await messageService.SendDirectMessageAsync(
                "engineer-1", "Hephaestus", "SoftwareEngineer", "human",
                "Agent replies.", "main");
        }

        var controller = CreateDmController(CreateHumanUser());

        var result = await controller.GetThreadMessages("engineer-1");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var messages = Assert.IsType<List<DmMessage>>(ok.Value);

        Assert.Equal(2, messages.Count);

        // Consultant message → IsFromHuman = true
        var cMsg = messages.First(m => m.SenderId == "consultant");
        Assert.True(cMsg.IsFromHuman);

        // Agent message → IsFromHuman = false
        var aMsg = messages.First(m => m.SenderId == "engineer-1");
        Assert.False(aMsg.IsFromHuman);
    }
}
