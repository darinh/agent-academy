using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Covers the DbUpdateException retry path in LinkTaskToSpecAsync
/// (the ChangeTracker.Clear + re-invocation when a concurrent insert
/// races on the (TaskId, SpecSectionId) unique constraint).
/// </summary>
public sealed class SpecTaskLinkRetryTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public SpecTaskLinkRetryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task LinkTaskToSpec_DbUpdateExceptionOnFirstSave_ClearsTrackerAndRetries()
    {
        // Seed a task in the DB.
        await using (var seedCtx = CreateContext(interceptor: null))
        {
            await seedCtx.Database.EnsureCreatedAsync();
            seedCtx.Rooms.Add(new RoomEntity
            {
                Id = "room-1",
                Name = "Test Room",
                Status = "Active",
                CurrentPhase = "Implementation",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            seedCtx.Tasks.Add(new TaskEntity
            {
                Id = "task-1",
                Title = "Test Task",
                Description = "",
                SuccessCriteria = "",
                Status = "Active",
                Type = "Feature",
                CurrentPhase = "Implementation",
                CurrentPlan = "",
                ValidationStatus = "NotStarted",
                ValidationSummary = "",
                ImplementationStatus = "NotStarted",
                ImplementationSummary = "",
                PreferredRoles = "[]",
                RoomId = "room-1",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                FleetModels = "[]",
                TestsCreated = "[]",
            });
            await seedCtx.SaveChangesAsync();
        }

        var interceptor = new OneTimeSpecLinkUniqueViolationInterceptor();
        await using var ctx = CreateContext(interceptor);

        var catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main",
            Agents: [new AgentDefinition(
                Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
                Summary: "", StartupPrompt: "", Model: null,
                CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true)]);

        var activity = Substitute.For<IActivityPublisher>();
        var deps = Substitute.For<ITaskDependencyService>();

        var service = new TaskLifecycleService(
            ctx,
            NullLogger<TaskLifecycleService>.Instance,
            catalog,
            activity,
            deps);

        // Act — first save throws DbUpdateException; catch block clears the tracker and retries.
        var link = await service.LinkTaskToSpecAsync(
            "task-1", "003-agent-system", "engineer-1", "Hephaestus",
            linkType: "Implements", note: "first try");

        // Assert the interceptor fired exactly once (proves the retry path ran).
        Assert.Equal(1, interceptor.FireCount);

        // Assert the final link was persisted successfully.
        Assert.Equal("task-1", link.TaskId);
        Assert.Equal("003-agent-system", link.SpecSectionId);
        Assert.Equal(SpecLinkType.Implements, link.LinkType);
        Assert.Equal("first try", link.Note);

        // Assert no orphaned tracked entries remain (ChangeTracker.Clear ran
        // between attempts so only the successful insert should be tracked).
        await using var verifyCtx = CreateContext(interceptor: null);
        var persisted = await verifyCtx.SpecTaskLinks
            .Where(l => l.TaskId == "task-1" && l.SpecSectionId == "003-agent-system")
            .ToListAsync();
        Assert.Single(persisted);
        Assert.Equal("first try", persisted[0].Note);
    }

    private AgentAcademyDbContext CreateContext(OneTimeSpecLinkUniqueViolationInterceptor? interceptor)
    {
        var optsBuilder = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection);
        if (interceptor is not null)
            optsBuilder.AddInterceptors(interceptor);
        return new AgentAcademyDbContext(optsBuilder.Options);
    }

    /// <summary>
    /// Simulates a race where a concurrent writer wins the insert and our save
    /// hits the (TaskId, SpecSectionId) unique constraint. Fires exactly once
    /// so the retry succeeds.
    /// </summary>
    private sealed class OneTimeSpecLinkUniqueViolationInterceptor : SaveChangesInterceptor
    {
        public int FireCount { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (FireCount == 0 &&
                eventData.Context is not null &&
                eventData.Context.ChangeTracker.Entries<SpecTaskLinkEntity>()
                    .Any(e => e.State == EntityState.Added))
            {
                FireCount++;
                throw new DbUpdateException(
                    "Simulated unique constraint race on (TaskId, SpecSectionId).",
                    new SqliteException(
                        "UNIQUE constraint failed: SpecTaskLinks.TaskId, SpecTaskLinks.SpecSectionId",
                        /* sqliteErrorCode */ 19));
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
