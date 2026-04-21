using AgentAcademy.Server.Notifications;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for DiscordChannelRebuilder — pure parsing functions that recover
/// agent routing and room mapping info from Discord channel topics.
/// </summary>
public class DiscordChannelRebuilderTests
{
    // ── ParseRoomIdFromTopic ────────────────────────────────────

    [Fact]
    public void ParseRoomIdFromTopic_NewFormat_ExtractsId()
    {
        var topic = "Agent Academy — Main · ID: abc-123-def";
        Assert.Equal("abc-123-def", DiscordChannelRebuilder.ParseRoomIdFromTopic(topic));
    }

    [Fact]
    public void ParseRoomIdFromTopic_OldFormat_ExtractsId()
    {
        var topic = "Agent Academy — Room: General (ID: room-456)";
        Assert.Equal("room-456", DiscordChannelRebuilder.ParseRoomIdFromTopic(topic));
    }

    [Fact]
    public void ParseRoomIdFromTopic_NoMarker_ReturnsNull()
    {
        Assert.Null(DiscordChannelRebuilder.ParseRoomIdFromTopic("Just a random topic"));
    }

    [Fact]
    public void ParseRoomIdFromTopic_EmptyTopic_ReturnsNull()
    {
        Assert.Null(DiscordChannelRebuilder.ParseRoomIdFromTopic(""));
    }

    [Fact]
    public void ParseRoomIdFromTopic_OldFormat_MissingClosingParen_ReturnsNull()
    {
        var topic = "Agent Academy — Room: General (ID: room-456";
        Assert.Null(DiscordChannelRebuilder.ParseRoomIdFromTopic(topic));
    }

    [Fact]
    public void ParseRoomIdFromTopic_NewFormat_WithTrailingWhitespace_Trims()
    {
        var topic = "Main · ID: abc-123  ";
        Assert.Equal("abc-123", DiscordChannelRebuilder.ParseRoomIdFromTopic(topic));
    }

    [Fact]
    public void ParseRoomIdFromTopic_OldFormatPreferred_WhenBothPresent()
    {
        // Old format check runs first in code
        var topic = "Agent Academy — Room: Test (ID: old-id) · ID: new-id";
        var result = DiscordChannelRebuilder.ParseRoomIdFromTopic(topic);
        Assert.Equal("old-id", result);
    }

    [Fact]
    public void ParseRoomIdFromTopic_GuidRoomId()
    {
        var topic = "Sprint Room · ID: 7b3f0a22-c5e1-4d89-a142-deadbeef1234";
        Assert.Equal("7b3f0a22-c5e1-4d89-a142-deadbeef1234", DiscordChannelRebuilder.ParseRoomIdFromTopic(topic));
    }

    [Fact]
    public void ParseRoomIdFromTopic_NewFormat_EmptyId_ReturnsEmptyString()
    {
        // Documents current behavior: empty ID after marker returns "" not null.
        // Callers should guard against this.
        var topic = "Room · ID:   ";
        var result = DiscordChannelRebuilder.ParseRoomIdFromTopic(topic);
        Assert.Equal("", result);
    }

    // ── ParseAgentChannelTopic ──────────────────────────────────

    [Fact]
    public void ParseAgentChannelTopic_NewFormat_ExtractsBothFields()
    {
        var agentName = "default";
        var roomId = "unknown";
        var topic = "Direct messages — Aristotle · Room: main-room-123";

        DiscordChannelRebuilder.ParseAgentChannelTopic(topic, ref agentName, ref roomId);

        Assert.Equal("Aristotle", agentName);
        Assert.Equal("main-room-123", roomId);
    }

    [Fact]
    public void ParseAgentChannelTopic_LegacyFormat_ExtractsBothFields()
    {
        var agentName = "default";
        var roomId = "unknown";
        var topic = "Agent Academy — Socrates questions (Room: room-456)";

        DiscordChannelRebuilder.ParseAgentChannelTopic(topic, ref agentName, ref roomId);

        Assert.Equal("Socrates", agentName);
        Assert.Equal("room-456", roomId);
    }

    [Fact]
    public void ParseAgentChannelTopic_LegacyFormat_MissingRoomMarker_KeepsDefaultRoomId()
    {
        var agentName = "default";
        var roomId = "unknown";
        var topic = "Agent Academy — Thucydides questions";

        DiscordChannelRebuilder.ParseAgentChannelTopic(topic, ref agentName, ref roomId);

        Assert.Equal("Thucydides", agentName);
        Assert.Equal("unknown", roomId);
    }

    [Fact]
    public void ParseAgentChannelTopic_UnrecognizedFormat_KeepsDefaults()
    {
        var agentName = "default-name";
        var roomId = "unknown";
        var topic = "Some random topic text";

        DiscordChannelRebuilder.ParseAgentChannelTopic(topic, ref agentName, ref roomId);

        Assert.Equal("default-name", agentName);
        Assert.Equal("unknown", roomId);
    }

    [Fact]
    public void ParseAgentChannelTopic_EmptyTopic_KeepsDefaults()
    {
        var agentName = "test";
        var roomId = "test-room";

        DiscordChannelRebuilder.ParseAgentChannelTopic("", ref agentName, ref roomId);

        Assert.Equal("test", agentName);
        Assert.Equal("test-room", roomId);
    }

    [Fact]
    public void ParseAgentChannelTopic_NewFormat_MultiWordAgentName()
    {
        var agentName = "default";
        var roomId = "unknown";
        var topic = "Direct messages — Mr Test Agent · Room: room-789";

        DiscordChannelRebuilder.ParseAgentChannelTopic(topic, ref agentName, ref roomId);

        Assert.Equal("Mr Test Agent", agentName);
        Assert.Equal("room-789", roomId);
    }

    [Fact]
    public void ParseAgentChannelTopic_NewFormat_RoomIdWithSpaces()
    {
        var agentName = "default";
        var roomId = "unknown";
        var topic = "Direct messages — Agent · Room: room with spaces";

        DiscordChannelRebuilder.ParseAgentChannelTopic(topic, ref agentName, ref roomId);

        Assert.Equal("Agent", agentName);
        Assert.Equal("room with spaces", roomId);
    }

    [Fact]
    public void ParseAgentChannelTopic_LegacyFormat_MissingClosingParen_KeepsDefaultRoomId()
    {
        var agentName = "default";
        var roomId = "unknown";
        var topic = "Agent Academy — Plato questions (Room: room-no-close";

        DiscordChannelRebuilder.ParseAgentChannelTopic(topic, ref agentName, ref roomId);

        Assert.Equal("Plato", agentName);
        Assert.Equal("unknown", roomId);
    }

    [Fact]
    public void ParseAgentChannelTopic_NewFormat_PrefersOverLegacy_GreedyRoomCapture()
    {
        var agentName = "default";
        var roomId = "unknown";
        // New format runs first. Room ID capture is greedy (takes everything after "· Room: ").
        // This documents the current behavior — parser has no delimiter after room ID.
        var topic = "Direct messages — NewAgent · Room: new-room Agent Academy — OldAgent questions (Room: old-room)";

        DiscordChannelRebuilder.ParseAgentChannelTopic(topic, ref agentName, ref roomId);

        Assert.Equal("NewAgent", agentName);
        Assert.Equal("new-room Agent Academy — OldAgent questions (Room: old-room)", roomId);
    }
}
