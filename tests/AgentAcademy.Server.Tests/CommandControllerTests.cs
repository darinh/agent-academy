using System.Security.Claims;
using System.Text.Json;
using AgentAcademy.Server.Auth;
using AgentAcademy.Server.Commands;
using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public sealed class CommandControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public CommandControllerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(options => options.UseSqlite(_connection));
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Execute_SyncCommand_ReturnsResultAndAuditsHumanUi()
    {
        var handler = new CapturingHandler("LIST_ROOMS");
        var controller = CreateController(handler);

        var result = await controller.Execute(new ExecuteCommandRequest(
            Command: "LIST_ROOMS",
            Args: ParseArgs("""{"status":"Active"}""")));

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ExecuteCommandResponse>(ok.Value);

        Assert.Equal("completed", payload.Status);
        Assert.Equal("human", payload.ExecutedBy);
        Assert.Equal("Active", handler.LastEnvelope!.Args["status"]);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var audit = await db.CommandAudits.SingleAsync();
        Assert.Equal("human-ui", audit.Source);
        Assert.Equal("LIST_ROOMS", audit.Command);
        Assert.Equal("Success", audit.Status);
    }

    [Fact]
    public async Task Execute_WhenUnauthenticated_ReturnsUnauthorized()
    {
        var controller = CreateController(new CapturingHandler("LIST_ROOMS"), authenticated: false);

        var result = await controller.Execute(new ExecuteCommandRequest("LIST_ROOMS", null));

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Execute_Unauthenticated_WhenAuthDisabled_AllowsAnonymous()
    {
        var handler = new CapturingHandler("LIST_ROOMS");
        var controller = new CommandController(
            new[] { handler },
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CommandController>.Instance,
            new AppAuthSetup(false, false, "http://localhost:5173"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };

        var result = await controller.Execute(new ExecuteCommandRequest("LIST_ROOMS", null));

        Assert.IsNotType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Execute_AsyncCommand_ReturnsAcceptedAndPollingTracksCompletion()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new BlockingHandler("CREATE_PR", gate);
        var controller = CreateController(handler);

        var executeResult = await controller.Execute(new ExecuteCommandRequest(
            Command: "CREATE_PR",
            Args: null));

        var accepted = Assert.IsType<AcceptedResult>(executeResult);
        var acceptedPayload = Assert.IsType<ExecuteCommandResponse>(accepted.Value);
        Assert.Equal("pending", acceptedPayload.Status);

        var pendingResult = await controller.GetStatus(acceptedPayload.CorrelationId);
        var pendingOk = Assert.IsType<OkObjectResult>(pendingResult);
        var pendingPayload = Assert.IsType<ExecuteCommandResponse>(pendingOk.Value);
        Assert.Equal("pending", pendingPayload.Status);

        gate.SetResult();

        ExecuteCommandResponse? completedPayload = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(25);
            var statusResult = await controller.GetStatus(acceptedPayload.CorrelationId);
            var statusOk = Assert.IsType<OkObjectResult>(statusResult);
            var payload = Assert.IsType<ExecuteCommandResponse>(statusOk.Value);
            if (payload.Status != "pending")
            {
                completedPayload = payload;
                break;
            }
        }

        Assert.NotNull(completedPayload);
        Assert.Equal("completed", completedPayload!.Status);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var audit = await db.CommandAudits.SingleAsync(a => a.CorrelationId == acceptedPayload.CorrelationId);
        Assert.Equal("Success", audit.Status);
        Assert.Equal("human-ui", audit.Source);
    }

    [Fact]
    public async Task Execute_RunForge_PassesTaskIdToHandler()
    {
        var handler = new CapturingHandler("RUN_FORGE");
        var controller = CreateController(handler);

        var result = await controller.Execute(new ExecuteCommandRequest(
            Command: "RUN_FORGE",
            Args: ParseArgs("""{"title":"Build feature","description":"Build login","methodologyPath":"methodology.json","taskId":"my-task-42"}""")));

        var accepted = Assert.IsType<AcceptedResult>(result);
        var payload = Assert.IsType<ExecuteCommandResponse>(accepted.Value);
        Assert.Equal("pending", payload.Status);

        ExecuteCommandResponse? completed = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(25);
            var statusResult = await controller.GetStatus(payload.CorrelationId);
            var statusOk = Assert.IsType<OkObjectResult>(statusResult);
            var statusPayload = Assert.IsType<ExecuteCommandResponse>(statusOk.Value);
            if (statusPayload.Status != "pending")
            {
                completed = statusPayload;
                break;
            }
        }

        Assert.NotNull(completed);
        Assert.Equal("completed", completed!.Status);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var audit = await db.CommandAudits.SingleAsync(a => a.CorrelationId == payload.CorrelationId);
        using var args = JsonDocument.Parse(audit.ArgsJson!);
        Assert.Equal("my-task-42", args.RootElement.GetProperty("taskId").GetString());
    }

    [Fact]
    public void GetMetadata_WhenAuthenticated_ReturnsAllowlistedCommands()
    {
        var handlers = new ICommandHandler[]
        {
            new CapturingHandler("LIST_ROOMS"),
            new CapturingHandler("LIST_TASKS"),
            new CapturingHandler("CREATE_PR"),
        };
        var controller = CreateController(handlers);

        var result = controller.GetMetadata();

        var ok = Assert.IsType<OkObjectResult>(result);
        var metadata = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value);
        var list = metadata.ToList();

        // Must return only commands that are in the allowlist
        Assert.True(list.Count > 0, "Metadata should not be empty");

        // Verify well-known commands are present
        var commands = list.Cast<AgentAcademy.Shared.Models.HumanCommandMetadata>().ToList();
        Assert.Contains(commands, m => m.Command == "LIST_ROOMS");
        Assert.Contains(commands, m => m.Command == "LIST_TASKS");
        Assert.Contains(commands, m => m.Command == "CREATE_PR");

        // Verify field metadata is populated for a known command
        var listRooms = commands.Single(m => m.Command == "LIST_ROOMS");
        Assert.False(listRooms.IsAsync);

        // Verify async commands are flagged
        var createPr = commands.Single(m => m.Command == "CREATE_PR");
        Assert.True(createPr.IsAsync);
    }

    [Fact]
    public void GetMetadata_IncludesForgeCommands_WhenHandlersRegistered()
    {
        var handlers = new ICommandHandler[]
        {
            new CapturingHandler("RUN_FORGE"),
            new CapturingHandler("FORGE_STATUS"),
            new CapturingHandler("LIST_FORGE_RUNS"),
        };
        var controller = CreateController(handlers);

        var result = controller.GetMetadata();

        var ok = Assert.IsType<OkObjectResult>(result);
        var commands = Assert.IsAssignableFrom<List<AgentAcademy.Shared.Models.HumanCommandMetadata>>(ok.Value);

        var runForge = commands.Single(c => c.Command == "RUN_FORGE");
        var forgeStatus = commands.Single(c => c.Command == "FORGE_STATUS");
        var listForgeRuns = commands.Single(c => c.Command == "LIST_FORGE_RUNS");

        Assert.True(runForge.IsAsync);
        Assert.Equal("forge", runForge.Category);
        Assert.False(forgeStatus.IsAsync);
        Assert.False(listForgeRuns.IsAsync);
    }

    [Fact]
    public void GetMetadata_WhenUnauthenticated_ReturnsUnauthorized()
    {
        var controller = CreateController(new CapturingHandler("LIST_ROOMS"), authenticated: false);

        var result = controller.GetMetadata();

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public void GetMetadata_Unauthenticated_WhenAuthDisabled_AllowsAnonymous()
    {
        var handler = new CapturingHandler("LIST_ROOMS");
        var controller = new CommandController(
            new[] { (ICommandHandler)handler },
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CommandController>.Instance,
            new AppAuthSetup(false, false, "http://localhost:5173"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };

        var result = controller.GetMetadata();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task GetStatus_Unauthenticated_WhenAuthDisabled_AllowsAnonymous()
    {
        var handler = new CapturingHandler("LIST_ROOMS");
        var controller = new CommandController(
            new[] { (ICommandHandler)handler },
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CommandController>.Instance,
            new AppAuthSetup(false, false, "http://localhost:5173"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };

        var result = await controller.GetStatus("nonexistent-correlation-id");

        Assert.IsNotType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public void GetMetadata_OnlyReturnsAllowlistedCommands()
    {
        var controller = CreateController(new CapturingHandler("LIST_ROOMS"));

        var result = controller.GetMetadata();

        var ok = Assert.IsType<OkObjectResult>(result);
        var commands = Assert.IsAssignableFrom<List<AgentAcademy.Shared.Models.HumanCommandMetadata>>(ok.Value);

        // None of the agent-only commands should appear
        Assert.DoesNotContain(commands, m => m.Command == "SHELL");
        Assert.DoesNotContain(commands, m => m.Command == "REMEMBER");
        Assert.DoesNotContain(commands, m => m.Command == "DM");
        Assert.DoesNotContain(commands, m => m.Command == "MOVE_TO_ROOM");
    }

    [Fact]
    public void GetMetadata_ExcludesCommandsWithoutRegisteredHandler()
    {
        // Only register a handler for LIST_ROOMS — metadata should not include
        // any allowlisted command that lacks a handler.
        var controller = CreateController(new CapturingHandler("LIST_ROOMS"));

        var result = controller.GetMetadata();

        var ok = Assert.IsType<OkObjectResult>(result);
        var commands = Assert.IsAssignableFrom<List<AgentAcademy.Shared.Models.HumanCommandMetadata>>(ok.Value);

        // Only LIST_ROOMS has a handler registered
        Assert.Single(commands);
        Assert.Equal("LIST_ROOMS", commands[0].Command);
    }

    // ── Audit Log Endpoint Tests ─────────────────────────────────────────

    [Fact]
    public async Task GetAuditLog_ReturnsEmptyWhenNoAudits()
    {
        var controller = CreateAuditController();

        var result = await controller.GetAuditLog();

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AuditLogResponse>(ok.Value);
        Assert.Empty(response.Records);
        Assert.Equal(0, response.Total);
    }

    [Fact]
    public async Task GetAuditLog_ReturnsRecordsAfterCommandExecution()
    {
        var cmdController = CreateController(new CapturingHandler("LIST_ROOMS"));
        await cmdController.Execute(new ExecuteCommandRequest("LIST_ROOMS", null));

        var controller = CreateAuditController();
        var result = await controller.GetAuditLog();

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AuditLogResponse>(ok.Value);
        Assert.Single(response.Records);
        Assert.Equal("LIST_ROOMS", response.Records[0].Command);
        Assert.Equal("human", response.Records[0].AgentId);
        Assert.Equal("Success", response.Records[0].Status);
        Assert.Equal(1, response.Total);
    }

    [Fact]
    public async Task GetAuditLog_FiltersbyAgentId()
    {
        await SeedAuditRecords();
        var controller = CreateAuditController();

        var result = await controller.GetAuditLog(agentId: "architect");

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AuditLogResponse>(ok.Value);
        Assert.All(response.Records, r => Assert.Equal("architect", r.AgentId));
        Assert.True(response.Total > 0);
    }

    [Fact]
    public async Task GetAuditLog_FiltersByCommand()
    {
        await SeedAuditRecords();
        var controller = CreateAuditController();

        var result = await controller.GetAuditLog(command: "READ_FILE");

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AuditLogResponse>(ok.Value);
        Assert.All(response.Records, r => Assert.Equal("READ_FILE", r.Command));
    }

    [Fact]
    public async Task GetAuditLog_FiltersByStatus()
    {
        await SeedAuditRecords();
        var controller = CreateAuditController();

        var result = await controller.GetAuditLog(status: "Error");

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AuditLogResponse>(ok.Value);
        Assert.All(response.Records, r => Assert.Equal("Error", r.Status));
    }

    [Fact]
    public async Task GetAuditLog_StatusFilterIsCaseInsensitive()
    {
        await SeedAuditRecords();
        var controller = CreateAuditController();

        var result = await controller.GetAuditLog(status: "error");

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AuditLogResponse>(ok.Value);
        Assert.All(response.Records, r => Assert.Equal("Error", r.Status));
        Assert.True(response.Total > 0, "Case-insensitive status should find Error records");
    }

    [Fact]
    public async Task GetAuditLog_FiltersByHoursBack()
    {
        await SeedAuditRecords(includeOld: true);
        var controller = CreateAuditController();

        var result = await controller.GetAuditLog(hoursBack: 1);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AuditLogResponse>(ok.Value);
        // Old record (48h ago) should be excluded
        Assert.All(response.Records, r => Assert.True(
            (DateTime.UtcNow - r.Timestamp).TotalHours < 2));
    }

    [Fact]
    public async Task GetAuditLog_PaginatesCorrectly()
    {
        await SeedAuditRecords();
        var controller = CreateAuditController();

        var result = await controller.GetAuditLog(limit: 2, offset: 0);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AuditLogResponse>(ok.Value);
        Assert.Equal(2, response.Records.Count);
        Assert.Equal(2, response.Limit);
        Assert.Equal(0, response.Offset);
        Assert.True(response.Total >= 3); // we seed at least 3
    }

    [Fact]
    public async Task GetAuditLog_InvalidHoursBack_ReturnsBadRequest()
    {
        var controller = CreateAuditController();

        var result = await controller.GetAuditLog(hoursBack: 0);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetAuditLog_WhenUnauthenticated_ReturnsUnauthorized()
    {
        var controller = CreateAuditController(authenticated: false);

        var result = await controller.GetAuditLog();

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task GetAuditLog_Unauthenticated_WhenAuthDisabled_AllowsAnonymous()
    {
        var controller = new CommandAuditController(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            new AppAuthSetup(false, false, "http://localhost:5173"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };

        var result = await controller.GetAuditLog();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task GetAuditLog_OrdersNewestFirst()
    {
        await SeedAuditRecords();
        var controller = CreateAuditController();

        var result = await controller.GetAuditLog();

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AuditLogResponse>(ok.Value);
        for (var i = 1; i < response.Records.Count; i++)
        {
            Assert.True(response.Records[i - 1].Timestamp >= response.Records[i].Timestamp,
                "Records should be ordered newest-first");
        }
    }

    // ── Audit Stats Endpoint Tests ───────────────────────────────────────

    [Fact]
    public async Task GetAuditStats_ReturnsZerosWhenEmpty()
    {
        var controller = CreateAuditController();

        var result = await controller.GetAuditStats();

        var ok = Assert.IsType<OkObjectResult>(result);
        var stats = Assert.IsType<AuditStatsResponse>(ok.Value);
        Assert.Equal(0, stats.TotalCommands);
        Assert.Empty(stats.ByStatus);
        Assert.Empty(stats.ByAgent);
        Assert.Empty(stats.ByCommand);
    }

    [Fact]
    public async Task GetAuditStats_ReturnsCorrectAggregates()
    {
        await SeedAuditRecords();
        var controller = CreateAuditController();

        var result = await controller.GetAuditStats();

        var ok = Assert.IsType<OkObjectResult>(result);
        var stats = Assert.IsType<AuditStatsResponse>(ok.Value);
        Assert.True(stats.TotalCommands >= 3);
        Assert.True(stats.ByStatus.ContainsKey("Success"));
        Assert.True(stats.ByAgent.ContainsKey("architect"));
        Assert.True(stats.ByCommand.ContainsKey("READ_FILE"));
    }

    [Fact]
    public async Task GetAuditStats_FiltersByHoursBack()
    {
        await SeedAuditRecords(includeOld: true);
        var controller = CreateAuditController();

        var resultAll = await controller.GetAuditStats();
        var resultRecent = await controller.GetAuditStats(hoursBack: 1);

        var allOk = Assert.IsType<OkObjectResult>(resultAll);
        var allStats = Assert.IsType<AuditStatsResponse>(allOk.Value);
        var recentOk = Assert.IsType<OkObjectResult>(resultRecent);
        var recentStats = Assert.IsType<AuditStatsResponse>(recentOk.Value);

        Assert.True(allStats.TotalCommands > recentStats.TotalCommands,
            "All-time should include more records than recent-only");
    }

    [Fact]
    public async Task GetAuditStats_WhenUnauthenticated_ReturnsUnauthorized()
    {
        var controller = CreateAuditController(authenticated: false);

        var result = await controller.GetAuditStats();

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task GetAuditStats_Unauthenticated_WhenAuthDisabled_AllowsAnonymous()
    {
        var controller = new CommandAuditController(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            new AppAuthSetup(false, false, "http://localhost:5173"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };

        var result = await controller.GetAuditStats();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task GetAuditStats_InvalidHoursBack_ReturnsBadRequest()
    {
        var controller = CreateAuditController();

        var result = await controller.GetAuditStats(hoursBack: -5);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    private async Task SeedAuditRecords(bool includeOld = false)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();

        db.CommandAudits.AddRange(
            new CommandAuditEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                CorrelationId = $"cmd-{Guid.NewGuid():N}",
                AgentId = "architect",
                Command = "READ_FILE",
                ArgsJson = """{"path":"src/main.cs"}""",
                Status = "Success",
                Timestamp = DateTime.UtcNow.AddMinutes(-5)
            },
            new CommandAuditEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                CorrelationId = $"cmd-{Guid.NewGuid():N}",
                AgentId = "architect",
                Command = "SEARCH_CODE",
                ArgsJson = """{"query":"TODO"}""",
                Status = "Success",
                Timestamp = DateTime.UtcNow.AddMinutes(-10)
            },
            new CommandAuditEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                CorrelationId = $"cmd-{Guid.NewGuid():N}",
                AgentId = "coder",
                Command = "RUN_BUILD",
                ArgsJson = "{}",
                Status = "Error",
                ErrorMessage = "Build failed",
                ErrorCode = "EXECUTION",
                Timestamp = DateTime.UtcNow.AddMinutes(-15)
            });

        if (includeOld)
        {
            db.CommandAudits.Add(new CommandAuditEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                CorrelationId = $"cmd-{Guid.NewGuid():N}",
                AgentId = "reviewer",
                Command = "READ_FILE",
                ArgsJson = """{"path":"old.txt"}""",
                Status = "Success",
                Timestamp = DateTime.UtcNow.AddHours(-48)
            });
        }

        await db.SaveChangesAsync();
    }

    private CommandController CreateController(ICommandHandler handler, bool authenticated = true) =>
        CreateController(new[] { handler }, authenticated);

    private CommandController CreateController(IEnumerable<ICommandHandler> handlers, bool authenticated = true)
    {
        var controller = new CommandController(
            handlers,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CommandController>.Instance,
            new AppAuthSetup(true, false, "http://localhost:5173"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = authenticated ? CreateAuthenticatedUser() : new ClaimsPrincipal(new ClaimsIdentity())
            }
        };

        return controller;
    }

    private CommandAuditController CreateAuditController(bool authenticated = true)
    {
        var controller = new CommandAuditController(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            new AppAuthSetup(true, false, "http://localhost:5173"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = authenticated ? CreateAuthenticatedUser() : new ClaimsPrincipal(new ClaimsIdentity())
            }
        };

        return controller;
    }

    private static ClaimsPrincipal CreateAuthenticatedUser()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "hephaestus"),
            new Claim("urn:github:name", "Hephaestus"),
        ], "Cookies");

        return new ClaimsPrincipal(identity);
    }

    private static Dictionary<string, JsonElement> ParseArgs(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CapturingHandler(string commandName) : ICommandHandler
    {
        public string CommandName => commandName;
        public CommandEnvelope? LastEnvelope { get; private set; }

        public Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
        {
            LastEnvelope = command;
            return Task.FromResult(command with
            {
                Result = new Dictionary<string, object?>
                {
                    ["echo"] = command.Args
                }
            });
        }
    }

    private sealed class BlockingHandler(string commandName, TaskCompletionSource gate) : ICommandHandler
    {
        public string CommandName => commandName;

        public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
        {
            await gate.Task;
            return command with
            {
                Result = new Dictionary<string, object?>
                {
                    ["success"] = true
                }
            };
        }
    }

    private sealed class DestructiveCapturingHandler(string commandName) : ICommandHandler
    {
        public string CommandName => commandName;
        public bool IsDestructive => true;
        public string DestructiveWarning => $"{commandName} is a destructive test command.";
        public CommandEnvelope? LastEnvelope { get; private set; }

        public Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
        {
            LastEnvelope = command;
            return Task.FromResult(command with
            {
                Result = new Dictionary<string, object?>
                {
                    ["executed"] = true
                }
            });
        }
    }

    [Fact]
    public async Task Execute_DestructiveCommand_WithoutConfirm_ReturnsConfirmationRequired()
    {
        var handler = new DestructiveCapturingHandler("CLOSE_ROOM");
        var controller = CreateController(handler);

        var result = await controller.Execute(new ExecuteCommandRequest(
            Command: "CLOSE_ROOM",
            Args: ParseArgs("""{"roomId":"room-123"}""")));

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ExecuteCommandResponse>(ok.Value);

        Assert.Equal("confirmation_required", payload.Status);
        Assert.Equal(CommandErrorCode.ConfirmationRequired, payload.ErrorCode);
        Assert.NotNull(payload.Error);
        Assert.Contains("destructive", payload.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.LastEnvelope); // handler was NOT called
    }

    [Fact]
    public async Task Execute_DestructiveCommand_WithConfirmTrue_ExecutesNormally()
    {
        var handler = new DestructiveCapturingHandler("CLOSE_ROOM");
        var controller = CreateController(handler);

        var result = await controller.Execute(new ExecuteCommandRequest(
            Command: "CLOSE_ROOM",
            Args: ParseArgs("""{"roomId":"room-123","confirm":"true"}""")));

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ExecuteCommandResponse>(ok.Value);

        Assert.Equal("completed", payload.Status);
        Assert.NotNull(handler.LastEnvelope); // handler WAS called
    }

    [Fact]
    public async Task Execute_NonDestructiveCommand_ExecutesWithoutConfirm()
    {
        var handler = new CapturingHandler("LIST_ROOMS");
        var controller = CreateController(handler);

        var result = await controller.Execute(new ExecuteCommandRequest(
            Command: "LIST_ROOMS",
            Args: null));

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ExecuteCommandResponse>(ok.Value);

        Assert.Equal("completed", payload.Status);
        Assert.NotNull(handler.LastEnvelope);
    }

    [Fact]
    public async Task Execute_DestructiveCommand_ConfirmationIsAudited()
    {
        var handler = new DestructiveCapturingHandler("CANCEL_TASK");
        var controller = CreateController(handler);

        await controller.Execute(new ExecuteCommandRequest(
            Command: "CANCEL_TASK",
            Args: ParseArgs("""{"taskId":"task-1"}""")));

        // Confirmation-required responses ARE audited for traceability
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var audits = await db.CommandAudits.Where(a => a.Command == "CANCEL_TASK").ToListAsync();
        Assert.Single(audits);
        Assert.Equal("Denied", audits[0].Status);
        Assert.Equal(CommandErrorCode.ConfirmationRequired, audits[0].ErrorCode);
        Assert.Equal("human-ui", audits[0].Source);
    }

    [Fact]
    public Task GetMetadata_IncludesDestructiveFlags()
    {
        var handler = new DestructiveCapturingHandler("CLOSE_ROOM");
        var controller = CreateController(handler);

        var result = controller.GetMetadata();
        var ok = Assert.IsType<OkObjectResult>(result);
        var metadata = Assert.IsAssignableFrom<IEnumerable<HumanCommandMetadata>>(ok.Value);

        var closeRoom = metadata.FirstOrDefault(m => m.Command == "CLOSE_ROOM");
        Assert.NotNull(closeRoom);
        Assert.True(closeRoom.IsDestructive);
        Assert.NotNull(closeRoom.DestructiveWarning);
        return Task.CompletedTask;
    }
}
