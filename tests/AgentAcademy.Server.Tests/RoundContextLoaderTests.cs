using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

public sealed class RoundContextLoaderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public RoundContextLoaderTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddSingleton(new AgentCatalogOptions("main", "Main Room", []));
        services.AddSingleton<IAgentCatalog>(sp => sp.GetRequiredService<AgentCatalogOptions>());
        services.AddSingleton<ActivityBroadcaster>();
        services.AddSingleton<IActivityBroadcaster>(sp => sp.GetRequiredService<ActivityBroadcaster>());
        services.AddSingleton<MessageBroadcaster>();
        services.AddSingleton<IMessageBroadcaster>(sp => sp.GetRequiredService<MessageBroadcaster>());
        services.AddSingleton<SpecManager>();
        services.AddSingleton(Substitute.For<IAgentExecutor>());
        services.AddDomainServices();
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task LoadAsync_ReturnsAllNullFields_WhenNoDataExists()
    {
        using var scope = _serviceProvider.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<RoundContextLoader>();

        var ctx = await sut.LoadAsync("nonexistent-room");

        Assert.Null(ctx.SpecContext);
        Assert.Null(ctx.SpecVersion);
        Assert.Null(ctx.SessionSummary);
        Assert.Null(ctx.SprintPreamble);
        Assert.Null(ctx.ActiveSprintStage);
    }

    [Fact]
    public async Task LoadAsync_ReturnsSessionSummary_WhenArchivedSessionExists()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Rooms.Add(new RoomEntity { Id = "test-room", Name = "Test Room" });
            db.ConversationSessions.Add(new ConversationSessionEntity
            {
                Id = "session-1",
                RoomId = "test-room",
                Status = "Archived",
                Summary = "Previous session summary",
                CreatedAt = DateTime.UtcNow.AddHours(-2),
                ArchivedAt = DateTime.UtcNow.AddHours(-1)
            });
            await db.SaveChangesAsync();
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var sut = scope.ServiceProvider.GetRequiredService<RoundContextLoader>();
            var ctx = await sut.LoadAsync("test-room");

            Assert.Equal("Previous session summary", ctx.SessionSummary);
        }
    }

    [Fact]
    public async Task LoadAsync_ReturnsSprintPreamble_WhenActiveSprintExists()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Workspaces.Add(new WorkspaceEntity
            {
                Path = "/tmp/test-workspace",
                ProjectName = "Test",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });
            db.Rooms.Add(new RoomEntity
            {
                Id = "test-room",
                Name = "Test Room",
                WorkspacePath = "/tmp/test-workspace"
            });
            db.Sprints.Add(new SprintEntity
            {
                Id = "sprint-1",
                Number = 1,
                WorkspacePath = "/tmp/test-workspace",
                CurrentStage = "Intake",
                Status = "Active",
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var sut = scope.ServiceProvider.GetRequiredService<RoundContextLoader>();
            var ctx = await sut.LoadAsync("test-room");

            Assert.NotNull(ctx.SprintPreamble);
            Assert.Contains("SPRINT #1", ctx.SprintPreamble);
            Assert.Equal("Intake", ctx.ActiveSprintStage);
        }
    }

    [Fact]
    public async Task LoadAsync_NoSprintPreamble_WhenNoWorkspacePath()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Rooms.Add(new RoomEntity { Id = "test-room", Name = "Test Room" });
            await db.SaveChangesAsync();
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var sut = scope.ServiceProvider.GetRequiredService<RoundContextLoader>();
            var ctx = await sut.LoadAsync("test-room");

            Assert.Null(ctx.SprintPreamble);
            Assert.Null(ctx.ActiveSprintStage);
        }
    }

    [Fact]
    public async Task LoadAsync_FieldsFailIndependently()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.Rooms.Add(new RoomEntity { Id = "test-room", Name = "Test Room" });
            db.ConversationSessions.Add(new ConversationSessionEntity
            {
                Id = "session-1",
                RoomId = "test-room",
                Status = "Archived",
                Summary = "Has session data",
                CreatedAt = DateTime.UtcNow.AddHours(-2),
                ArchivedAt = DateTime.UtcNow.AddHours(-1)
            });
            await db.SaveChangesAsync();
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var sut = scope.ServiceProvider.GetRequiredService<RoundContextLoader>();
            var ctx = await sut.LoadAsync("test-room");

            // Session summary loads despite no spec or sprint
            Assert.Equal("Has session data", ctx.SessionSummary);
            Assert.Null(ctx.SprintPreamble);
        }
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}
