using BenchmarkDotNet.Attributes;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Benchmarks;

/// <summary>
/// Benchmarks for <see cref="PromptBuilder"/> — the pure-function prompt composition
/// used in every agent turn (conversation rounds, breakout rooms, reviews).
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
public class PromptBuilderBenchmarks
{
    private AgentDefinition _agent = default!;
    private RoomSnapshot _smallRoom = default!;
    private RoomSnapshot _largeRoom = default!;
    private BreakoutRoom _breakoutRoom = default!;
    private string _specContext = default!;
    private string _largeSpecContext = default!;
    private List<TaskItem> _taskItems = default!;
    private List<AgentMemory> _memories = default!;
    private List<MessageEntity> _directMessages = default!;

    [GlobalSetup]
    public void Setup()
    {
        _agent = new AgentDefinition(
            "agent-alpha", "Alpha", "Engineer",
            "Senior software engineer specializing in backend services",
            "You are Alpha, a senior backend engineer. Follow clean architecture principles.",
            "gpt-4", ["backend", "testing"], ["read_file", "write_file"], true,
            Permissions: new CommandPermissionSet(["*"], []));

        _smallRoom = CreateRoom("room-1", "Main Room", 5);
        _largeRoom = CreateRoom("room-2", "Large Room", 50);

        _breakoutRoom = new BreakoutRoom(
            "br-1", "Breakout: Alpha", "room-1", "agent-alpha",
            [new TaskItem("task-1", "Implement auth module",
                "Build JWT-based authentication with refresh tokens",
                TaskItemStatus.Active, "agent-alpha", "room-1", "br-1",
                null, null, DateTime.UtcNow.AddHours(-2), DateTime.UtcNow)],
            RoomStatus.Active,
            CreateMessages(8),
            DateTime.UtcNow.AddHours(-2), DateTime.UtcNow);

        _specContext = string.Join("\n", Enumerable.Range(0, 5).Select(i =>
            $"- specs/{i:D3}-section-{i}/spec.md: Section {i} — Brief description of section {i}"));

        _largeSpecContext = string.Join("\n", Enumerable.Range(0, 20).Select(i =>
            $"- [{(i < 3 ? "★" : " ")}] specs/{i:D3}-section-{i}/spec.md: Section {i} Title — " +
            $"Detailed purpose text that describes what this section covers including technical details"));

        _taskItems = Enumerable.Range(0, 5).Select(i => new TaskItem(
            $"item-{i}", $"Task item {i}: do something", "Description of work",
            i < 2 ? TaskItemStatus.Active : TaskItemStatus.Pending,
            "agent-alpha", "room-1", i < 2 ? "br-1" : null,
            null, null, DateTime.UtcNow, DateTime.UtcNow)).ToList();

        _memories = Enumerable.Range(0, 10).Select(i => new AgentMemory(
            "agent-alpha",
            i % 3 == 0 ? "shared" : i % 2 == 0 ? "lesson" : "pattern",
            $"key-{i}",
            $"This is memory value {i} with some content about patterns and learnings discovered during development",
            DateTime.UtcNow.AddDays(-i), DateTime.UtcNow.AddDays(-i), null, null)).ToList();

        _directMessages = Enumerable.Range(0, 3).Select(i => new MessageEntity
        {
            Id = $"dm-{i}", RoomId = "dm", SenderId = $"agent-{(char)('a' + i)}",
            SenderName = $"Agent-{(char)('A' + i)}", SenderRole = "Engineer",
            SenderKind = "Agent", Kind = "DirectMessage", RecipientId = "agent-alpha",
            Content = $"Hey Alpha, I noticed an issue with the auth module on line {100 + i}. " +
                      "Can you check if the token expiration is handled correctly?",
            SentAt = DateTime.UtcNow.AddMinutes(-10 + i)
        }).ToList();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Conversation")]
    public string ConversationMinimal() =>
        PromptBuilder.BuildConversationPrompt(_agent, _smallRoom, null);

    [Benchmark]
    [BenchmarkCategory("Conversation")]
    public string ConversationFull() =>
        PromptBuilder.BuildConversationPrompt(
            _agent, _largeRoom, _largeSpecContext,
            _taskItems, _memories, _directMessages,
            "Previous session: worked on auth module, got token refresh working",
            "Sprint 5: Focus on security hardening",
            "1.2.3");

    [Benchmark]
    [BenchmarkCategory("Breakout")]
    public string BreakoutMinimal() =>
        PromptBuilder.BuildBreakoutPrompt(_agent, _breakoutRoom, 1);

    [Benchmark]
    [BenchmarkCategory("Breakout")]
    public string BreakoutFull() =>
        PromptBuilder.BuildBreakoutPrompt(
            _agent, _breakoutRoom, 3, _memories, _directMessages,
            "Previous work: set up JWT middleware",
            _largeSpecContext, "1.2.3");

    [Benchmark]
    [BenchmarkCategory("Review")]
    public string Review() =>
        PromptBuilder.BuildReviewPrompt(
            _agent, "Beta",
            "WORK REPORT:\nStatus: COMPLETE\nFiles: src/Auth/TokenProvider.cs, src/Auth/JwtMiddleware.cs\n" +
            "Evidence: Implemented JWT authentication with refresh token support. All 15 tests pass.",
            _specContext, "1.2.3");

    private static RoomSnapshot CreateRoom(string id, string name, int messageCount) =>
        new(id, name, "Discussion topic", RoomStatus.Active,
            CollaborationPhase.Implementation,
            new TaskSnapshot(
                "task-1", "Implement authentication",
                "Build JWT auth with refresh tokens",
                "All tests pass, no security warnings",
                Shared.Models.TaskStatus.Active, TaskType.Feature,
                CollaborationPhase.Implementation,
                "Current plan text",
                WorkstreamStatus.InProgress, "",
                WorkstreamStatus.InProgress, "",
                ["Engineer"],
                DateTime.UtcNow.AddHours(-4), DateTime.UtcNow),
            [new AgentPresence("agent-alpha", "Alpha", "Engineer",
                AgentAvailability.Active, false, DateTime.UtcNow, ["backend"])],
            CreateMessages(messageCount),
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

    private static List<ChatEnvelope> CreateMessages(int count) =>
        Enumerable.Range(0, count).Select(i => new ChatEnvelope(
            $"msg-{i}", "room-1", $"agent-{(char)('a' + i % 3)}",
            $"Agent-{(char)('A' + i % 3)}", "Engineer",
            MessageSenderKind.Agent, MessageKind.Response,
            $"Message {i}: This is a typical agent response discussing the work being done. " +
            $"It includes technical details about implementation approach #{i}.",
            DateTime.UtcNow.AddMinutes(-count + i))).ToList();
}
