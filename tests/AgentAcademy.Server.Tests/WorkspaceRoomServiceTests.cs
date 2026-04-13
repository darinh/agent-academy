using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Tests;

public sealed class WorkspaceRoomServiceTests : IDisposable
{
    private static readonly List<AgentDefinition> TestAgents =
    [
        new("agent-1", "Agent One", "Engineer", "Test agent 1", "", null, [], [], true),
        new("agent-2", "Agent Two", "Reviewer", "Test agent 2", "", null, [], [], true),
    ];

    private readonly TestServiceGraph _graph = new(TestAgents);
    private WorkspaceRoomService Sut => _graph.WorkspaceRoomService;
    private AgentAcademyDbContext Db => _graph.Db;
    private AgentCatalogOptions Catalog => _graph.Catalog;

    public void Dispose() => _graph.Dispose();

    // ── Helpers ──

    private RoomEntity SeedRoom(string id, string name, string? workspacePath = null,
        string status = "Idle", string phase = "Intake")
    {
        var now = DateTime.UtcNow;
        var room = new RoomEntity
        {
            Id = id, Name = name,
            Status = status, CurrentPhase = phase,
            WorkspacePath = workspacePath,
            CreatedAt = now, UpdatedAt = now
        };
        Db.Rooms.Add(room);
        Db.SaveChanges();
        return room;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  EnsureDefaultRoomForWorkspaceAsync
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EnsureDefault_CreatesNewRoom_WhenNoneExists()
    {
        var roomId = await Sut.EnsureDefaultRoomForWorkspaceAsync("/home/user/my-project");

        Assert.EndsWith("-main", roomId);
        var room = await Db.Rooms.FindAsync(roomId);
        Assert.NotNull(room);
        Assert.Equal("/home/user/my-project", room.WorkspacePath);
    }

    [Fact]
    public async Task EnsureDefault_ReturnsExistingRoomId()
    {
        SeedRoom("existing-room", "Main Room", "/home/user/project");

        var result = await Sut.EnsureDefaultRoomForWorkspaceAsync("/home/user/project");

        Assert.Equal("existing-room", result);
    }

    [Fact]
    public async Task EnsureDefault_UpdatesRoomName_WhenDifferent()
    {
        SeedRoom("room-old-name", "Collaboration Room", "/home/user/project");

        await Sut.EnsureDefaultRoomForWorkspaceAsync("/home/user/project");

        var room = await Db.Rooms.FindAsync("room-old-name");
        Assert.Equal(Catalog.DefaultRoomName, room!.Name);
    }

    [Fact]
    public async Task EnsureDefault_GeneratesSlugMain()
    {
        var roomId = await Sut.EnsureDefaultRoomForWorkspaceAsync("/home/user/my-project");

        Assert.Equal("my-project-main", roomId);
    }

    [Fact]
    public async Task EnsureDefault_AppendsHash_OnSlugCollision()
    {
        // Create a room with the same slug but different workspace
        SeedRoom("my-project-main", "Other Room", "/other/my-project");

        var roomId = await Sut.EnsureDefaultRoomForWorkspaceAsync("/home/user/my-project");

        Assert.StartsWith("my-project-", roomId);
        Assert.EndsWith("-main", roomId);
        Assert.NotEqual("my-project-main", roomId);
        // Format: slug-{8 hex chars}-main
        Assert.Matches(@"^my-project-[a-f0-9]{8}-main$", roomId);
    }

    [Fact]
    public async Task EnsureDefault_CreatesWelcomeMessage()
    {
        var roomId = await Sut.EnsureDefaultRoomForWorkspaceAsync("/home/user/project");

        var messages = Db.Messages.Where(m => m.RoomId == roomId).ToList();
        Assert.Single(messages);
        Assert.Contains("ready", messages[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(nameof(MessageSenderKind.System), messages[0].SenderKind);
    }

    [Fact]
    public async Task EnsureDefault_SetsCorrectRoomProperties()
    {
        var roomId = await Sut.EnsureDefaultRoomForWorkspaceAsync("/home/user/project");

        var room = await Db.Rooms.FindAsync(roomId);
        Assert.NotNull(room);
        Assert.Equal(nameof(RoomStatus.Idle), room.Status);
        Assert.Equal(nameof(CollaborationPhase.Intake), room.CurrentPhase);
        Assert.Equal("/home/user/project", room.WorkspacePath);
    }

    [Fact]
    public async Task EnsureDefault_MovesAllAgentsToRoom()
    {
        var roomId = await Sut.EnsureDefaultRoomForWorkspaceAsync("/home/user/project");

        var locations = Db.AgentLocations.Where(l => l.RoomId == roomId).ToList();
        Assert.Equal(2, locations.Count);
        Assert.All(locations, l =>
        {
            Assert.Equal(nameof(AgentState.Idle), l.State);
            Assert.Null(l.BreakoutRoomId);
        });
    }

    [Fact]
    public async Task EnsureDefault_RetiresLegacyRoom_WithSameWorkspace()
    {
        // Seed the legacy default room with same workspace
        SeedRoom(Catalog.DefaultRoomId, "Legacy Room", "/home/user/project");

        var roomId = await Sut.EnsureDefaultRoomForWorkspaceAsync("/home/user/project");

        Assert.NotEqual(Catalog.DefaultRoomId, roomId);
        var legacy = await Db.Rooms.FindAsync(Catalog.DefaultRoomId);
        Assert.NotNull(legacy);
        Assert.Equal(nameof(RoomStatus.Archived), legacy.Status);
        Assert.Null(legacy.WorkspacePath);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  RetireLegacyDefaultRoomAsync
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RetireLegacy_ArchivesRoom_WhenWorkspaceMatches()
    {
        SeedRoom(Catalog.DefaultRoomId, "Legacy", "/workspace/path");

        await Sut.RetireLegacyDefaultRoomAsync("/workspace/path", "other-room");

        var legacy = await Db.Rooms.FindAsync(Catalog.DefaultRoomId);
        Assert.Equal(nameof(RoomStatus.Archived), legacy!.Status);
        Assert.Null(legacy.WorkspacePath);
    }

    [Fact]
    public async Task RetireLegacy_ClearsWorkspacePath()
    {
        SeedRoom(Catalog.DefaultRoomId, "Legacy", "/workspace/path");

        await Sut.RetireLegacyDefaultRoomAsync("/workspace/path", "other-room");

        var legacy = await Db.Rooms.FindAsync(Catalog.DefaultRoomId);
        Assert.Null(legacy!.WorkspacePath);
    }

    [Fact]
    public async Task RetireLegacy_DoesNothing_WhenDifferentWorkspace()
    {
        SeedRoom(Catalog.DefaultRoomId, "Legacy", "/other/workspace");

        await Sut.RetireLegacyDefaultRoomAsync("/workspace/path", "other-room");

        var legacy = await Db.Rooms.FindAsync(Catalog.DefaultRoomId);
        Assert.Equal("Idle", legacy!.Status);
        Assert.Equal("/other/workspace", legacy.WorkspacePath);
    }

    [Fact]
    public async Task RetireLegacy_DoesNothing_WhenLegacyIsWorkspaceDefault()
    {
        SeedRoom(Catalog.DefaultRoomId, "Legacy", "/workspace/path");

        await Sut.RetireLegacyDefaultRoomAsync("/workspace/path", Catalog.DefaultRoomId);

        var legacy = await Db.Rooms.FindAsync(Catalog.DefaultRoomId);
        Assert.Equal("Idle", legacy!.Status);
        Assert.Equal("/workspace/path", legacy.WorkspacePath);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MoveAllAgentsToRoomAsync
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MoveAllAgents_CreatesLocations_WhenNoneExist()
    {
        SeedRoom("target", "Target Room");

        await Sut.MoveAllAgentsToRoomAsync("target");

        var locs = Db.AgentLocations.ToList();
        Assert.Equal(2, locs.Count);
        Assert.All(locs, l =>
        {
            Assert.Equal("target", l.RoomId);
            Assert.Equal(nameof(AgentState.Idle), l.State);
        });
    }

    [Fact]
    public async Task MoveAllAgents_UpdatesExistingLocations()
    {
        SeedRoom("old-room", "Old Room");
        SeedRoom("new-room", "New Room");
        Db.AgentLocations.Add(new AgentLocationEntity
        {
            AgentId = "agent-1", RoomId = "old-room",
            State = "Working", BreakoutRoomId = "br-1",
            UpdatedAt = DateTime.UtcNow.AddHours(-1)
        });
        Db.SaveChanges();

        await Sut.MoveAllAgentsToRoomAsync("new-room");

        var loc = await Db.AgentLocations.FindAsync("agent-1");
        Assert.Equal("new-room", loc!.RoomId);
        Assert.Equal(nameof(AgentState.Idle), loc.State);
        Assert.Null(loc.BreakoutRoomId);
    }

    [Fact]
    public async Task MoveAllAgents_SetsIdleAndClearsBreakout()
    {
        SeedRoom("target", "Target");
        Db.AgentLocations.Add(new AgentLocationEntity
        {
            AgentId = "agent-2", RoomId = "other", State = "Working",
            BreakoutRoomId = "br-1", UpdatedAt = DateTime.UtcNow
        });
        Db.SaveChanges();

        await Sut.MoveAllAgentsToRoomAsync("target");

        var loc = await Db.AgentLocations.FindAsync("agent-2");
        Assert.Equal(nameof(AgentState.Idle), loc!.State);
        Assert.Null(loc.BreakoutRoomId);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ResolveStartupMainRoomIdAsync
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveStartup_NullWorkspace_ReturnsCatalogDefault()
    {
        var result = await Sut.ResolveStartupMainRoomIdAsync(null);

        Assert.Equal(Catalog.DefaultRoomId, result);
    }

    [Fact]
    public async Task ResolveStartup_ReturnsWorkspaceRoom_WhenExists()
    {
        SeedRoom("ws-room", "Main Room", "/home/user/project");

        var result = await Sut.ResolveStartupMainRoomIdAsync("/home/user/project");

        Assert.Equal("ws-room", result);
    }

    [Fact]
    public async Task ResolveStartup_FallsBackToLegacy_WhenNoWorkspaceRoom()
    {
        SeedRoom(Catalog.DefaultRoomId, "Legacy", workspacePath: null);

        var result = await Sut.ResolveStartupMainRoomIdAsync("/some/workspace");

        Assert.Equal(Catalog.DefaultRoomId, result);
    }

    [Fact]
    public async Task ResolveStartup_CreatesRoom_WhenNothingExists()
    {
        var result = await Sut.ResolveStartupMainRoomIdAsync("/home/user/new-project");

        Assert.EndsWith("-main", result);
        var room = await Db.Rooms.FindAsync(result);
        Assert.NotNull(room);
        Assert.Equal("/home/user/new-project", room.WorkspacePath);
    }
}
