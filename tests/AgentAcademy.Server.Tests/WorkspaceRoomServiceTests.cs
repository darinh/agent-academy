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
    public async Task EnsureDefault_SelfHeals_PoisonedCompletedMainRoom()
    {
        // B1 self-heal: a main collaboration room left in Status=Completed by
        // the pre-fix sprint-freeze path must be reset to Idle on the next
        // EnsureDefault invocation, so it's adopted as the workspace default
        // (instead of being skipped, forcing creation of a fresh main with a
        // Guid suffix and stranding the user's history).
        // The test catalog (TestServiceGraph) uses DefaultRoomName="Main Room";
        // the self-heal predicate is strict on _catalog.DefaultRoomName, so the
        // seeded room must use that exact name. (In production the catalog name
        // is "Main Collaboration Room".)
        SeedRoom("ws-main", "Main Room", "/home/user/project",
            status: nameof(RoomStatus.Completed),
            phase: nameof(CollaborationPhase.FinalSynthesis));

        var roomId = await Sut.EnsureDefaultRoomForWorkspaceAsync("/home/user/project");

        Assert.Equal("ws-main", roomId);
        var healed = await Db.Rooms.FindAsync("ws-main");
        Assert.NotNull(healed);
        // Status reset → writable; Phase preserved (next stage sync overwrites it).
        Assert.Equal(nameof(RoomStatus.Idle), healed!.Status);
        Assert.Equal(nameof(CollaborationPhase.FinalSynthesis), healed.CurrentPhase);
    }

    [Fact]
    public async Task EnsureDefault_SelfHeal_HealsOnlyCanonicalMain_LeavesHistoricalDuplicatesCompleted()
    {
        // Reviewer's high-severity edge case: a workspace can accumulate multiple
        // historical Completed mains (same name, different ids) from prior
        // sprints. Self-heal must reset only the canonical one (deterministic
        // ORDER BY) so historical duplicates stay terminal and don't get
        // resurrected as live rooms.
        var older = SeedRoom("ws-main-old", "Main Room", "/home/user/project",
            status: nameof(RoomStatus.Completed));
        // Force the older row to have an earlier UpdatedAt so any incidental
        // recency check would prefer it; the actual ordering is by Name match
        // → DefaultRoomId match → Id alphabetical.
        older.UpdatedAt = DateTime.UtcNow.AddDays(-7);
        Db.SaveChanges();
        SeedRoom("ws-main-new", "Main Room", "/home/user/project",
            status: nameof(RoomStatus.Completed));

        await Sut.EnsureDefaultRoomForWorkspaceAsync("/home/user/project");

        var oldRoom = await Db.Rooms.FindAsync("ws-main-old");
        var newRoom = await Db.Rooms.FindAsync("ws-main-new");
        // Exactly one is healed (the alphabetically-first id since neither is
        // _catalog.DefaultRoomId): "ws-main-new" > "ws-main-old", so old wins.
        var healed = new[] { oldRoom!, newRoom! }.Count(r => r.Status == nameof(RoomStatus.Idle));
        var stillCompleted = new[] { oldRoom!, newRoom! }.Count(r => r.Status == nameof(RoomStatus.Completed));
        Assert.Equal(1, healed);
        Assert.Equal(1, stillCompleted);
    }

    [Fact]
    public async Task EnsureDefault_SelfHeal_LeavesCompletedNonMainRoomsAlone()
    {
        // The self-heal must only target main rooms — a Completed breakout/task
        // room is legitimately frozen by sprint completion and must stay that
        // way. Strict criterion: exact DefaultRoomName OR catalog DefaultRoomId
        // — a user-named "Security Collaboration Room" must NOT be revived.
        SeedRoom("breakout-x", "Breakout X", "/home/user/project",
            status: nameof(RoomStatus.Completed));
        SeedRoom("user-collab", "Security Collaboration Room", "/home/user/project",
            status: nameof(RoomStatus.Completed));

        await Sut.EnsureDefaultRoomForWorkspaceAsync("/home/user/project");

        Assert.Equal(nameof(RoomStatus.Completed),
            (await Db.Rooms.FindAsync("breakout-x"))!.Status);
        Assert.Equal(nameof(RoomStatus.Completed),
            (await Db.Rooms.FindAsync("user-collab"))!.Status);
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
    public async Task ResolveStartup_AdoptsOrphanedLegacyRoom()
    {
        SeedRoom(Catalog.DefaultRoomId, "Legacy", workspacePath: null);

        var result = await Sut.ResolveStartupMainRoomIdAsync("/some/workspace");

        Assert.Equal(Catalog.DefaultRoomId, result);
        var room = await Db.Rooms.FindAsync(Catalog.DefaultRoomId);
        Assert.Equal("/some/workspace", room!.WorkspacePath);
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

    // ═══════════════════════════════════════════════════════════════════
    //  TryAdoptLegacyRoomAsync
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TryAdopt_AdoptsOrphanedLegacyRoom()
    {
        SeedRoom(Catalog.DefaultRoomId, "Old Name", workspacePath: null);

        var result = await Sut.TryAdoptLegacyRoomAsync("/home/user/project");

        Assert.Equal(Catalog.DefaultRoomId, result);
        var room = await Db.Rooms.FindAsync(Catalog.DefaultRoomId);
        Assert.Equal("/home/user/project", room!.WorkspacePath);
        Assert.Equal(Catalog.DefaultRoomName, room.Name);
    }

    [Fact]
    public async Task TryAdopt_ReturnsNull_WhenLegacyRoomAlreadyOwned()
    {
        SeedRoom(Catalog.DefaultRoomId, "Main Room", workspacePath: "/other/workspace");

        var result = await Sut.TryAdoptLegacyRoomAsync("/home/user/project");

        Assert.Null(result);
    }

    [Fact]
    public async Task TryAdopt_ReturnsNull_WhenLegacyRoomArchived()
    {
        SeedRoom(Catalog.DefaultRoomId, "Main Room", workspacePath: null, status: "Archived");

        var result = await Sut.TryAdoptLegacyRoomAsync("/home/user/project");

        Assert.Null(result);
    }

    [Fact]
    public async Task TryAdopt_ReturnsNull_WhenNoLegacyRoom()
    {
        var result = await Sut.TryAdoptLegacyRoomAsync("/home/user/project");

        Assert.Null(result);
    }

    [Fact]
    public async Task TryAdopt_ArchivesDuplicateWorkspaceRoom()
    {
        SeedRoom(Catalog.DefaultRoomId, "Main Room", workspacePath: null);
        SeedRoom("project-main", "Main Collaboration Room", "/home/user/project");

        var result = await Sut.TryAdoptLegacyRoomAsync("/home/user/project");

        Assert.Equal(Catalog.DefaultRoomId, result);
        var duplicate = await Db.Rooms.FindAsync("project-main");
        Assert.Equal(nameof(RoomStatus.Archived), duplicate!.Status);
        // WorkspacePath preserved so workspace-scoped queries still find its data
        Assert.Equal("/home/user/project", duplicate.WorkspacePath);
    }

    [Fact]
    public async Task TryAdopt_SecondCallIsIdempotent()
    {
        SeedRoom(Catalog.DefaultRoomId, "Main Room", workspacePath: null);

        var first = await Sut.TryAdoptLegacyRoomAsync("/home/user/project");
        var second = await Sut.TryAdoptLegacyRoomAsync("/home/user/project");

        Assert.Equal(Catalog.DefaultRoomId, first);
        Assert.Null(second); // Already owned, no re-adoption
    }

    [Fact]
    public async Task EnsureDefault_AdoptsLegacyRoom_InsteadOfCreatingNew()
    {
        SeedRoom(Catalog.DefaultRoomId, "Old Room", workspacePath: null);

        var result = await Sut.EnsureDefaultRoomForWorkspaceAsync("/home/user/project");

        Assert.Equal(Catalog.DefaultRoomId, result);
        var room = await Db.Rooms.FindAsync(Catalog.DefaultRoomId);
        Assert.Equal("/home/user/project", room!.WorkspacePath);
        // No duplicate room should exist
        var allRooms = Db.Rooms.ToList();
        Assert.Single(allRooms);
    }

    [Fact]
    public async Task EnsureDefault_AdoptedRoomSurvivesSecondCall()
    {
        SeedRoom(Catalog.DefaultRoomId, "Old Room", workspacePath: null);

        var first = await Sut.EnsureDefaultRoomForWorkspaceAsync("/home/user/project");
        var second = await Sut.EnsureDefaultRoomForWorkspaceAsync("/home/user/project");

        Assert.Equal(first, second);
        Assert.Equal(Catalog.DefaultRoomId, first);
        // Still only one room
        var rooms = Db.Rooms.Where(r => r.Status != nameof(RoomStatus.Archived)).ToList();
        Assert.Single(rooms);
    }

    [Fact]
    public async Task EnsureDefault_RepairsDuplicateRoomState()
    {
        // Simulate the bug: legacy room orphaned + duplicate created
        SeedRoom(Catalog.DefaultRoomId, "Main Room", workspacePath: null);
        SeedRoom("project-main", "Main Collaboration Room", "/home/user/project");

        var result = await Sut.EnsureDefaultRoomForWorkspaceAsync("/home/user/project");

        // Should adopt legacy and archive duplicate
        Assert.Equal(Catalog.DefaultRoomId, result);
        var duplicate = await Db.Rooms.FindAsync("project-main");
        Assert.Equal(nameof(RoomStatus.Archived), duplicate!.Status);
    }

    [Fact]
    public async Task TasksInAdoptedRoom_VisibleViaWorkspaceQuery()
    {
        // Simulate: legacy room with tasks, then workspace switch
        SeedRoom(Catalog.DefaultRoomId, "Main Room", workspacePath: null);
        Db.Tasks.Add(new TaskEntity
        {
            Id = "task-1", Title = "Test Task", Status = "Active",
            RoomId = Catalog.DefaultRoomId,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        Db.SaveChanges();

        await Sut.EnsureDefaultRoomForWorkspaceAsync("/home/user/project");

        // Task should now be findable by workspace query
        var room = await Db.Rooms.FindAsync(Catalog.DefaultRoomId);
        Assert.Equal("/home/user/project", room!.WorkspacePath);
        // The task's RoomId points to "main" which now has the workspace path
        var task = await Db.Tasks.FindAsync("task-1");
        Assert.Equal(Catalog.DefaultRoomId, task!.RoomId);
    }
}
