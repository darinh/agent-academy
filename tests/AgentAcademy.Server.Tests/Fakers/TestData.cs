using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Shared.Models;
using Bogus;

namespace AgentAcademy.Server.Tests.Fakers;

/// <summary>
/// Shared test data generators powered by Bogus.
/// Each property exposes a <see cref="Faker{T}"/> that produces valid, minimal
/// instances with sensible defaults. Clone a faker before adding custom rules.
/// <code>
/// // Simple usage:
/// var room = TestData.Room.Generate();
/// var rooms = TestData.Room.Generate(5);
///
/// // Custom overrides (always clone first):
/// var custom = TestData.Room.Clone()
///     .RuleFor(r => r.Name, "War Room")
///     .Generate();
/// </code>
/// </summary>
public static class TestData
{
    private static readonly string[] AgentNames =
        ["Hephaestus", "Athena", "Hermes", "Apollo", "Artemis", "Ares"];

    private static readonly string[] AgentRoles =
        ["SoftwareEngineer", "Architect", "QAEngineer", "TechLead", "DevOps", "ProductManager"];

    private static readonly string[] RoomStatuses = ["Idle", "Active", "Paused", "Completed"];

    private static readonly string[] Phases =
        ["Intake", "Planning", "Implementation", "Review", "Completed"];

    private static readonly string[] SprintStages =
        ["Intake", "Planning", "Discussion", "Validation", "Implementation", "FinalSynthesis"];

    private static readonly string[] GoalCardVerdicts = ["Proceed", "ProceedWithCaveat", "Challenge"];

    private static readonly string[] GoalCardStatuses = ["Active", "Completed", "Challenged", "Abandoned"];

    private static readonly string[] TaskStatuses =
        ["Active", "Completed", "Blocked", "Cancelled"];

    private static readonly string[] TaskTypes =
        ["Feature", "Bug", "Refactor", "Documentation", "Test"];

    private static readonly string[] WorkstreamStatuses =
        ["NotStarted", "InProgress", "Completed", "Failed"];

    private static readonly string[] MemoryCategories =
        ["project", "codebase", "preference", "history", "context"];

    // ── Entity Fakers ──────────────────────────────────────────────

    public static Faker<RoomEntity> Room { get; } = new Faker<RoomEntity>()
        .RuleFor(r => r.Id, f => f.Random.Guid().ToString())
        .RuleFor(r => r.Name, f => f.Company.CompanyName() + " Room")
        .RuleFor(r => r.Topic, f => f.Lorem.Sentence())
        .RuleFor(r => r.Status, f => f.PickRandom(RoomStatuses))
        .RuleFor(r => r.CurrentPhase, f => f.PickRandom(Phases))
        .RuleFor(r => r.CreatedAt, f => f.Date.Recent(30))
        .RuleFor(r => r.UpdatedAt, (f, r) => r.CreatedAt.AddMinutes(f.Random.Int(0, 1440)));

    public static Faker<TaskEntity> Task { get; } = new Faker<TaskEntity>()
        .RuleFor(t => t.Id, f => f.Random.Guid().ToString())
        .RuleFor(t => t.Title, f => f.Lorem.Sentence(4))
        .RuleFor(t => t.Description, f => f.Lorem.Paragraph())
        .RuleFor(t => t.SuccessCriteria, f => f.Lorem.Sentence())
        .RuleFor(t => t.Status, f => f.PickRandom(TaskStatuses))
        .RuleFor(t => t.Type, f => f.PickRandom(TaskTypes))
        .RuleFor(t => t.CurrentPhase, f => f.PickRandom(Phases))
        .RuleFor(t => t.Priority, f => f.Random.Int(1, 5))
        .RuleFor(t => t.CreatedAt, f => f.Date.Recent(30))
        .RuleFor(t => t.UpdatedAt, (f, t) => t.CreatedAt.AddMinutes(f.Random.Int(0, 1440)));

    public static Faker<MessageEntity> Message { get; } = new Faker<MessageEntity>()
        .RuleFor(m => m.Id, f => f.Random.Guid().ToString())
        .RuleFor(m => m.RoomId, f => f.Random.Guid().ToString())
        .RuleFor(m => m.SenderId, f => f.Random.AlphaNumeric(8))
        .RuleFor(m => m.SenderName, f => f.PickRandom(AgentNames))
        .RuleFor(m => m.SenderRole, f => f.PickRandom(AgentRoles))
        .RuleFor(m => m.SenderKind, f => f.PickRandom("Agent", "Human", "System"))
        .RuleFor(m => m.Kind, f => f.PickRandom("Response", "Command", "System"))
        .RuleFor(m => m.Content, f => f.Lorem.Paragraph())
        .RuleFor(m => m.SentAt, f => f.Date.Recent(7));

    public static Faker<AgentMemoryEntity> AgentMemory { get; } = new Faker<AgentMemoryEntity>()
        .RuleFor(m => m.AgentId, f => f.Random.AlphaNumeric(8))
        .RuleFor(m => m.Key, f => f.Lorem.Word())
        .RuleFor(m => m.Category, f => f.PickRandom(MemoryCategories))
        .RuleFor(m => m.Value, f => f.Lorem.Sentence())
        .RuleFor(m => m.CreatedAt, f => f.Date.Recent(30));

    public static Faker<ConversationSessionEntity> ConversationSession { get; } =
        new Faker<ConversationSessionEntity>()
            .RuleFor(s => s.Id, f => f.Random.Guid().ToString())
            .RuleFor(s => s.RoomId, f => f.Random.Guid().ToString())
            .RuleFor(s => s.RoomType, f => f.PickRandom("Main", "Breakout"))
            .RuleFor(s => s.SequenceNumber, f => f.Random.Int(1, 10))
            .RuleFor(s => s.Status, f => f.PickRandom("Active", "Archived"))
            .RuleFor(s => s.MessageCount, f => f.Random.Int(0, 100))
            .RuleFor(s => s.CreatedAt, f => f.Date.Recent(14));

    public static Faker<WorkspaceEntity> Workspace { get; } = new Faker<WorkspaceEntity>()
        .RuleFor(w => w.Path, f => $"/tmp/workspaces/{f.Random.AlphaNumeric(8)}")
        .RuleFor(w => w.ProjectName, f => f.Company.CompanyName())
        .RuleFor(w => w.IsActive, f => f.Random.Bool())
        .RuleFor(w => w.RepositoryUrl, f => $"https://github.com/{f.Internet.UserName()}/{f.Lorem.Word()}")
        .RuleFor(w => w.DefaultBranch, f => f.PickRandom("main", "develop", "master"))
        .RuleFor(w => w.CreatedAt, f => f.Date.Recent(60));

    public static Faker<SprintEntity> Sprint { get; } = new Faker<SprintEntity>()
        .RuleFor(s => s.Id, f => f.Random.Guid().ToString())
        .RuleFor(s => s.Number, f => f.Random.Int(1, 50))
        .RuleFor(s => s.WorkspacePath, f => $"/tmp/workspaces/{f.Random.AlphaNumeric(8)}")
        .RuleFor(s => s.Status, f => f.PickRandom("Active", "Completed", "Cancelled"))
        .RuleFor(s => s.CurrentStage, f => f.PickRandom(SprintStages))
        .RuleFor(s => s.AwaitingSignOff, false)
        .RuleFor(s => s.CreatedAt, f => f.Date.Recent(30));

    public static Faker<BreakoutRoomEntity> BreakoutRoom { get; } = new Faker<BreakoutRoomEntity>()
        .RuleFor(b => b.Id, f => f.Random.Guid().ToString())
        .RuleFor(b => b.Name, f => $"Breakout: {f.Lorem.Sentence(3)}")
        .RuleFor(b => b.ParentRoomId, f => f.Random.Guid().ToString())
        .RuleFor(b => b.AssignedAgentId, f => f.Random.AlphaNumeric(8))
        .RuleFor(b => b.Status, f => f.PickRandom("Active", "Completed", "Recalled"))
        .RuleFor(b => b.CreatedAt, f => f.Date.Recent(14))
        .RuleFor(b => b.UpdatedAt, (f, b) => b.CreatedAt.AddMinutes(f.Random.Int(0, 720)));

    public static Faker<GoalCardEntity> GoalCard { get; } = new Faker<GoalCardEntity>()
        .RuleFor(g => g.Id, f => f.Random.Guid().ToString())
        .RuleFor(g => g.AgentId, f => f.Random.AlphaNumeric(8))
        .RuleFor(g => g.AgentName, f => f.PickRandom(AgentNames))
        .RuleFor(g => g.RoomId, f => f.Random.Guid().ToString())
        .RuleFor(g => g.TaskDescription, f => f.Lorem.Sentence())
        .RuleFor(g => g.Intent, f => f.Lorem.Sentence())
        .RuleFor(g => g.Divergence, f => f.Lorem.Sentence())
        .RuleFor(g => g.Steelman, f => f.Lorem.Sentence())
        .RuleFor(g => g.Strawman, f => f.Lorem.Sentence())
        .RuleFor(g => g.Verdict, f => f.PickRandom(GoalCardVerdicts))
        .RuleFor(g => g.FreshEyes1, f => f.Lorem.Sentence())
        .RuleFor(g => g.FreshEyes2, f => f.Lorem.Sentence())
        .RuleFor(g => g.FreshEyes3, f => f.Lorem.Sentence())
        .RuleFor(g => g.Status, f => f.PickRandom(GoalCardStatuses))
        .RuleFor(g => g.CreatedAt, f => f.Date.Recent(7))
        .RuleFor(g => g.UpdatedAt, (f, g) => g.CreatedAt.AddMinutes(f.Random.Int(0, 360)));

    // ── Shared Model Fakers ────────────────────────────────────────

    public static Faker<AgentDefinition> Agent { get; } = new Faker<AgentDefinition>()
        .CustomInstantiator(f =>
        {
            var name = f.PickRandom(AgentNames);
            return new AgentDefinition(
                Id: name.ToLowerInvariant() + "-" + f.Random.Int(1, 99),
                Name: name,
                Role: f.PickRandom(AgentRoles),
                Summary: f.Lorem.Sentence(),
                StartupPrompt: f.Lorem.Paragraph(),
                Model: null,
                CapabilityTags: [f.PickRandom("code", "review", "test", "docs")],
                EnabledTools: [f.PickRandom("code-write", "read-file", "shell")],
                AutoJoinDefaultRoom: true);
        });

    public static Faker<CommandPermissionSet> Permissions { get; } =
        new Faker<CommandPermissionSet>()
            .CustomInstantiator(f => new CommandPermissionSet(
                Allowed: [f.PickRandom("RUN_BUILD", "RUN_TESTS", "LIST_TASKS", "SHOW_DIFF")],
                Denied: []));
}
