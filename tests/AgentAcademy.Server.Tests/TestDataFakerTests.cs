using AgentAcademy.Server.Tests.Fakers;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Verifies that all <see cref="TestData"/> fakers generate valid,
/// non-null instances without throwing.
/// </summary>
public class TestDataFakerTests
{
    [Fact]
    public void Room_GeneratesValidInstance()
    {
        var room = TestData.Room.Generate();

        Assert.NotEmpty(room.Id);
        Assert.NotEmpty(room.Name);
        Assert.NotEqual(default, room.CreatedAt);
        Assert.True(room.UpdatedAt >= room.CreatedAt);
    }

    [Fact]
    public void Room_GeneratesBatchWithUniqueIds()
    {
        var rooms = TestData.Room.Generate(10);

        Assert.Equal(10, rooms.Count);
        Assert.Equal(10, rooms.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void Task_GeneratesValidInstance()
    {
        var task = TestData.Task.Generate();

        Assert.NotEmpty(task.Id);
        Assert.NotEmpty(task.Title);
        Assert.NotEmpty(task.Description);
        Assert.InRange(task.Priority, 1, 5);
        Assert.NotEqual(default, task.CreatedAt);
    }

    [Fact]
    public void Message_GeneratesValidInstance()
    {
        var msg = TestData.Message.Generate();

        Assert.NotEmpty(msg.Id);
        Assert.NotEmpty(msg.RoomId);
        Assert.NotEmpty(msg.SenderId);
        Assert.NotEmpty(msg.SenderName);
        Assert.NotEmpty(msg.Content);
        Assert.NotEqual(default, msg.SentAt);
    }

    [Fact]
    public void AgentMemory_GeneratesValidInstance()
    {
        var mem = TestData.AgentMemory.Generate();

        Assert.NotEmpty(mem.AgentId);
        Assert.NotEmpty(mem.Key);
        Assert.NotEmpty(mem.Category);
        Assert.NotEmpty(mem.Value);
        Assert.NotEqual(default, mem.CreatedAt);
    }

    [Fact]
    public void ConversationSession_GeneratesValidInstance()
    {
        var session = TestData.ConversationSession.Generate();

        Assert.NotEmpty(session.Id);
        Assert.NotEmpty(session.RoomId);
        Assert.True(session.SequenceNumber >= 1);
        Assert.True(session.MessageCount >= 0);
    }

    [Fact]
    public void Workspace_GeneratesValidInstance()
    {
        var ws = TestData.Workspace.Generate();

        Assert.NotEmpty(ws.Path);
        Assert.NotNull(ws.ProjectName);
        Assert.NotNull(ws.RepositoryUrl);
        Assert.NotEqual(default, ws.CreatedAt);
    }

    [Fact]
    public void Sprint_GeneratesValidInstance()
    {
        var sprint = TestData.Sprint.Generate();

        Assert.NotEmpty(sprint.Id);
        Assert.True(sprint.Number >= 1);
        Assert.NotEmpty(sprint.WorkspacePath);
        Assert.NotEqual(default, sprint.CreatedAt);
    }

    [Fact]
    public void BreakoutRoom_GeneratesValidInstance()
    {
        var br = TestData.BreakoutRoom.Generate();

        Assert.NotEmpty(br.Id);
        Assert.NotEmpty(br.Name);
        Assert.NotEmpty(br.ParentRoomId);
        Assert.NotEmpty(br.AssignedAgentId);
        Assert.True(br.UpdatedAt >= br.CreatedAt);
    }

    [Fact]
    public void GoalCard_GeneratesValidInstance()
    {
        var gc = TestData.GoalCard.Generate();

        Assert.NotEmpty(gc.Id);
        Assert.NotEmpty(gc.AgentId);
        Assert.NotEmpty(gc.AgentName);
        Assert.NotEmpty(gc.RoomId);
        Assert.NotEmpty(gc.Intent);
        Assert.NotEmpty(gc.Steelman);
        Assert.NotEmpty(gc.Strawman);
        Assert.True(gc.UpdatedAt >= gc.CreatedAt);
    }

    [Fact]
    public void Agent_GeneratesValidInstance()
    {
        var agent = TestData.Agent.Generate();

        Assert.NotEmpty(agent.Id);
        Assert.NotEmpty(agent.Name);
        Assert.NotEmpty(agent.Role);
        Assert.NotEmpty(agent.Summary);
        Assert.NotEmpty(agent.StartupPrompt);
        Assert.NotEmpty(agent.CapabilityTags);
        Assert.NotEmpty(agent.EnabledTools);
    }

    [Fact]
    public void Permissions_GeneratesValidInstance()
    {
        var perms = TestData.Permissions.Generate();

        Assert.NotEmpty(perms.Allowed);
        Assert.NotNull(perms.Denied);
    }

    [Fact]
    public void Clone_AllowsCustomOverrides()
    {
        var customRoom = TestData.Room.Clone()
            .RuleFor(r => r.Name, "War Room Alpha")
            .RuleFor(r => r.Status, "Active")
            .Generate();

        Assert.Equal("War Room Alpha", customRoom.Name);
        Assert.Equal("Active", customRoom.Status);
        Assert.NotEmpty(customRoom.Id);
    }
}
