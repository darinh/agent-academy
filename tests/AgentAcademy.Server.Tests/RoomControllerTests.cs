using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public sealed class RoomControllerTests : IDisposable
{
    private static readonly AgentDefinition TestAgent = new(
        Id: "engineer-1",
        Name: "Engineer",
        Role: "Engineer",
        Summary: "Writes code",
        StartupPrompt: "You are an engineer.",
        Model: null,
        CapabilityTags: [],
        EnabledTools: [],
        AutoJoinDefaultRoom: true);

    private readonly TestServiceGraph _svc;
    private readonly RoomController _controller;

    public RoomControllerTests()
    {
        _svc = new TestServiceGraph([TestAgent]);
        _controller = new RoomController(
            _svc.RoomService, _svc.AgentLocationService, _svc.MessageService,
            new MessageBroadcaster(), _svc.Catalog, _svc.UsageTracker, _svc.ErrorTracker,
            _svc.ArtifactTracker,
            NullLogger<RoomController>.Instance);
    }

    public void Dispose() => _svc.Dispose();

    // ── GetRooms ──────────────────────────────────────────────────

    [Fact]
    public async Task GetRooms_EmptyDb_ReturnsEmptyList()
    {
        var result = await _controller.GetRooms();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var rooms = Assert.IsType<List<RoomSnapshot>>(ok.Value);
        Assert.Empty(rooms);
    }

    [Fact]
    public async Task GetRooms_WithRoom_ReturnsList()
    {
        await _svc.RoomService.CreateRoomAsync("Test Room");

        var result = await _controller.GetRooms();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var rooms = Assert.IsType<List<RoomSnapshot>>(ok.Value);
        Assert.Single(rooms);
        Assert.Equal("Test Room", rooms[0].Name);
    }

    // ── GetRoom ──────────────────────────────────────────────────

    [Fact]
    public async Task GetRoom_NotFound_Returns404()
    {
        var result = await _controller.GetRoom("nonexistent");
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetRoom_Found_ReturnsSnapshot()
    {
        var created = await _svc.RoomService.CreateRoomAsync("My Room");

        var result = await _controller.GetRoom(created.Id);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var room = Assert.IsType<RoomSnapshot>(ok.Value);
        Assert.Equal("My Room", room.Name);
    }

    // ── GetRoomArtifacts ─────────────────────────────────────────

    [Fact]
    public async Task GetRoomArtifacts_ReturnsEmptyList()
    {
        var result = await _controller.GetRoomArtifacts("any-room");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var artifacts = Assert.IsType<List<ArtifactRecord>>(ok.Value);
        Assert.Empty(artifacts);
    }

    [Fact]
    public async Task GetRoomArtifacts_ReturnsRecordedArtifacts()
    {
        var roomId = "test-room";
        _svc.Db.RoomArtifacts.Add(new AgentAcademy.Server.Data.Entities.RoomArtifactEntity
        {
            RoomId = roomId,
            AgentId = "engineer-1",
            FilePath = "src/Models/User.cs",
            Operation = "Created",
            Timestamp = DateTime.UtcNow,
        });
        await _svc.Db.SaveChangesAsync();

        var result = await _controller.GetRoomArtifacts(roomId);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var artifacts = Assert.IsType<List<ArtifactRecord>>(ok.Value);
        Assert.Single(artifacts);
        Assert.Equal("src/Models/User.cs", artifacts[0].FilePath);
        Assert.Equal("Created", artifacts[0].Operation);
        Assert.Equal("engineer-1", artifacts[0].AgentId);
    }

    [Fact]
    public async Task GetRoomArtifacts_RespectsLimit()
    {
        var roomId = "limit-room";
        for (int i = 0; i < 5; i++)
        {
            _svc.Db.RoomArtifacts.Add(new AgentAcademy.Server.Data.Entities.RoomArtifactEntity
            {
                RoomId = roomId,
                AgentId = "eng",
                FilePath = $"src/File{i}.cs",
                Operation = "Created",
                Timestamp = DateTime.UtcNow.AddMinutes(-i),
            });
        }
        await _svc.Db.SaveChangesAsync();

        var result = await _controller.GetRoomArtifacts(roomId, limit: 3);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var artifacts = Assert.IsType<List<ArtifactRecord>>(ok.Value);
        Assert.Equal(3, artifacts.Count);
    }

    [Fact]
    public async Task GetRoomArtifacts_IsolatedByRoom()
    {
        _svc.Db.RoomArtifacts.Add(new AgentAcademy.Server.Data.Entities.RoomArtifactEntity
        {
            RoomId = "room-a", AgentId = "eng", FilePath = "a.cs", Operation = "Created", Timestamp = DateTime.UtcNow,
        });
        _svc.Db.RoomArtifacts.Add(new AgentAcademy.Server.Data.Entities.RoomArtifactEntity
        {
            RoomId = "room-b", AgentId = "eng", FilePath = "b.cs", Operation = "Created", Timestamp = DateTime.UtcNow,
        });
        await _svc.Db.SaveChangesAsync();

        var result = await _controller.GetRoomArtifacts("room-a");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var artifacts = Assert.IsType<List<ArtifactRecord>>(ok.Value);
        Assert.Single(artifacts);
        Assert.Equal("a.cs", artifacts[0].FilePath);
    }

    // ── GetRoomEvaluations ───────────────────────────────────────

    [Fact]
    public void GetRoomEvaluations_ReturnsEmptyResult()
    {
        var result = _controller.GetRoomEvaluations("any-room");
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    // ── RenameRoom ───────────────────────────────────────────────

    [Fact]
    public async Task RenameRoom_EmptyName_ReturnsBadRequest()
    {
        var result = await _controller.RenameRoom("room-1", new RenameRoomRequest(""));
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task RenameRoom_NotFound_Returns404()
    {
        var result = await _controller.RenameRoom("nonexistent", new RenameRoomRequest("New Name"));
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task RenameRoom_Valid_RenamesAndReturnsSnapshot()
    {
        var created = await _svc.RoomService.CreateRoomAsync("Old Name");

        var result = await _controller.RenameRoom(created.Id, new RenameRoomRequest("New Name"));
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var room = Assert.IsType<RoomSnapshot>(ok.Value);
        Assert.Equal("New Name", room.Name);
    }

    // ── CreateRoom ───────────────────────────────────────────────

    [Fact]
    public async Task CreateRoom_EmptyName_ReturnsBadRequest()
    {
        var result = await _controller.CreateRoom(new CreateRoomRequest(""));
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateRoom_Valid_Returns201()
    {
        var result = await _controller.CreateRoom(new CreateRoomRequest("New Room", "A test room"));
        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var room = Assert.IsType<RoomSnapshot>(created.Value);
        Assert.Equal("New Room", room.Name);
    }

    [Fact]
    public async Task CreateRoom_TrimsName()
    {
        var result = await _controller.CreateRoom(new CreateRoomRequest("  Padded  "));
        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var room = Assert.IsType<RoomSnapshot>(created.Value);
        Assert.Equal("Padded", room.Name);
    }

    // ── CleanupStaleRooms ────────────────────────────────────────

    [Fact]
    public async Task CleanupStaleRooms_ReturnsOk()
    {
        var result = await _controller.CleanupStaleRooms(_svc.RoomLifecycleService);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("archivedCount", json);
    }
}
