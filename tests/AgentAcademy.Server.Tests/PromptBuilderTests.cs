using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for PromptBuilder — pure-function prompt construction.
/// Verifies section ordering, truncation, memory bucketing, and edge cases.
/// </summary>
public class PromptBuilderTests
{
    // ── Test Helpers ────────────────────────────────────────────

    private static AgentDefinition MakeAgent(
        string id = "eng-1", string name = "Hephaestus", string role = "SoftwareEngineer") =>
        new(id, name, role, "Test summary", "You are a test agent.",
            null, [], [], true);

    private static RoomSnapshot MakeRoom(
        string name = "Main Room",
        TaskSnapshot? task = null,
        List<ChatEnvelope>? messages = null) =>
        new("room-1", name, null, RoomStatus.Active, CollaborationPhase.Planning,
            task, [], messages ?? [], DateTime.UtcNow, DateTime.UtcNow);

    private static TaskSnapshot MakeTask(
        string title = "Fix login bug",
        string description = "Users can't log in",
        string successCriteria = "") =>
        new(Id: "task-1", Title: title, Description: description,
            SuccessCriteria: successCriteria, Status: Shared.Models.TaskStatus.Active,
            Type: TaskType.Bug, CurrentPhase: CollaborationPhase.Planning,
            CurrentPlan: "", ValidationStatus: WorkstreamStatus.NotStarted,
            ValidationSummary: "", ImplementationStatus: WorkstreamStatus.NotStarted,
            ImplementationSummary: "", PreferredRoles: [],
            CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow,
            WorkspacePath: null, SprintId: null);

    private static BreakoutRoom MakeBreakout(
        string name = "Breakout: Fix login",
        List<TaskItem>? tasks = null,
        List<ChatEnvelope>? messages = null) =>
        new("br-1", name, "room-1", "eng-1", tasks ?? [], RoomStatus.Active,
            messages ?? [], DateTime.UtcNow, DateTime.UtcNow);

    private static ChatEnvelope MakeMessage(string sender, string content, string? role = null) =>
        new("msg-" + Guid.NewGuid().ToString("N")[..6], "room-1", sender, sender,
            role, MessageSenderKind.Agent, MessageKind.Response, content,
            DateTime.UtcNow);

    private static AgentMemory MakeMemory(
        string agentId = "eng-1", string category = "project", string key = "key1",
        string value = "value1", DateTime? createdAt = null, DateTime? expiresAt = null,
        DateTime? lastAccessedAt = null) =>
        new(agentId, category, key, value, createdAt ?? DateTime.UtcNow, null,
            lastAccessedAt, expiresAt);

    private static MessageEntity MakeDm(
        string senderId, string senderName, string recipientId, string content) =>
        new()
        {
            Id = "dm-" + Guid.NewGuid().ToString("N")[..6],
            RoomId = "__dm__",
            SenderId = senderId,
            SenderName = senderName,
            RecipientId = recipientId,
            Content = content,
            SenderKind = "Agent",
            Kind = "DirectMessage",
            SentAt = DateTime.UtcNow
        };

    // ── BuildConversationPrompt ─────────────────────────────────

    [Fact]
    public void ConversationPrompt_ContainsRoomAndAgentIdentity()
    {
        var agent = MakeAgent();
        var room = MakeRoom("War Room Alpha");

        var prompt = PromptBuilder.BuildConversationPrompt(agent, room, null);

        Assert.Contains("Room: War Room Alpha", prompt);
        Assert.Contains("You are Hephaestus (SoftwareEngineer)", prompt);
        Assert.Contains("PASS", prompt);
    }

    [Fact]
    public void ConversationPrompt_DoesNotIncludeStartupPrompt()
    {
        var agent = MakeAgent();
        var room = MakeRoom();

        var prompt = PromptBuilder.BuildConversationPrompt(agent, room, null);

        Assert.DoesNotContain("You are a test agent.", prompt);
    }

    [Fact]
    public void ConversationPrompt_SprintPreambleAppearsFirst()
    {
        var agent = MakeAgent();
        var room = MakeRoom();

        var prompt = PromptBuilder.BuildConversationPrompt(agent, room, null,
            sprintPreamble: "Sprint: Ship v2.0");

        var preambleIdx = prompt.IndexOf("Sprint: Ship v2.0");
        var roomIdx = prompt.IndexOf("=== CURRENT ROOM CONTEXT ===");
        Assert.True(preambleIdx < roomIdx,
            "Sprint preamble should appear before room context");
    }

    [Fact]
    public void ConversationPrompt_SessionSummaryAppearsBeforeRoom()
    {
        var agent = MakeAgent();
        var room = MakeRoom();

        var prompt = PromptBuilder.BuildConversationPrompt(agent, room, null,
            sessionSummary: "Previously discussed auth flow.");

        var summaryIdx = prompt.IndexOf("PREVIOUS CONVERSATION SUMMARY");
        var roomIdx = prompt.IndexOf("=== CURRENT ROOM CONTEXT ===");
        Assert.True(summaryIdx < roomIdx,
            "Session summary should appear before room context");
        Assert.Contains("Previously discussed auth flow.", prompt);
    }

    [Fact]
    public void ConversationPrompt_OwnMemoriesAppearUnderYourMemories()
    {
        var agent = MakeAgent(id: "eng-1");
        var room = MakeRoom();
        var memories = new List<AgentMemory>
        {
            MakeMemory(agentId: "eng-1", category: "project", key: "pattern", value: "Use DI everywhere")
        };

        var prompt = PromptBuilder.BuildConversationPrompt(agent, room, null, memories: memories);

        Assert.Contains("=== YOUR MEMORIES ===", prompt);
        Assert.Contains("[project] pattern: Use DI everywhere", prompt);
    }

    [Fact]
    public void ConversationPrompt_SharedMemoryFromSameAgent_GoesUnderYourMemories()
    {
        var agent = MakeAgent(id: "eng-1");
        var room = MakeRoom();
        var memories = new List<AgentMemory>
        {
            MakeMemory(agentId: "eng-1", category: "shared", key: "convention", value: "Use records")
        };

        var prompt = PromptBuilder.BuildConversationPrompt(agent, room, null, memories: memories);

        Assert.Contains("=== YOUR MEMORIES ===", prompt);
        Assert.Contains("[shared] convention: Use records", prompt);
        Assert.DoesNotContain("=== SHARED KNOWLEDGE ===", prompt);
    }

    [Fact]
    public void ConversationPrompt_SharedMemoryFromOtherAgent_GoesUnderSharedKnowledge()
    {
        var agent = MakeAgent(id: "eng-1");
        var room = MakeRoom();
        var memories = new List<AgentMemory>
        {
            MakeMemory(agentId: "planner-1", category: "shared", key: "arch-decision", value: "Use SQLite")
        };

        var prompt = PromptBuilder.BuildConversationPrompt(agent, room, null, memories: memories);

        Assert.Contains("=== SHARED KNOWLEDGE ===", prompt);
        Assert.Contains("[shared] arch-decision: Use SQLite (from: planner-1)", prompt);
    }

    [Fact]
    public void ConversationPrompt_StaleMemoryGetsTag()
    {
        var agent = MakeAgent(id: "eng-1");
        var room = MakeRoom();
        var memories = new List<AgentMemory>
        {
            MakeMemory(agentId: "eng-1", key: "old-pattern", value: "Obsolete",
                createdAt: DateTime.UtcNow.AddDays(-60))
        };

        var prompt = PromptBuilder.BuildConversationPrompt(agent, room, null, memories: memories);

        Assert.Contains("⚠️STALE", prompt);
    }

    [Fact]
    public void ConversationPrompt_MemoryWithTtl_ShowsExpiryDate()
    {
        var agent = MakeAgent(id: "eng-1");
        var room = MakeRoom();
        var expiryDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var memories = new List<AgentMemory>
        {
            MakeMemory(agentId: "eng-1", key: "temp", value: "expires soon", expiresAt: expiryDate)
        };

        var prompt = PromptBuilder.BuildConversationPrompt(agent, room, null, memories: memories);

        Assert.Contains("[expires 2026-12-31]", prompt);
    }

    [Fact]
    public void ConversationPrompt_TaskContextIncluded()
    {
        var agent = MakeAgent();
        var task = MakeTask("Fix auth bug", "Login fails for OAuth users", "All tests pass");
        var room = MakeRoom(task: task);

        var prompt = PromptBuilder.BuildConversationPrompt(agent, room, null);

        Assert.Contains("=== TASK ===", prompt);
        Assert.Contains("Title: Fix auth bug", prompt);
        Assert.Contains("Description: Login fails for OAuth users", prompt);
        Assert.Contains("Success criteria: All tests pass", prompt);
    }

    [Fact]
    public void ConversationPrompt_EmptySuccessCriteria_Omitted()
    {
        var agent = MakeAgent();
        var task = MakeTask("Fix bug", "Description", "");
        var room = MakeRoom(task: task);

        var prompt = PromptBuilder.BuildConversationPrompt(agent, room, null);

        Assert.DoesNotContain("Success criteria:", prompt);
    }

    [Fact]
    public void ConversationPrompt_TaskItemsIncluded()
    {
        var agent = MakeAgent();
        var room = MakeRoom();
        var items = new List<TaskItem>
        {
            new("item-1", "Write tests", "Add unit tests", TaskItemStatus.Active,
                "eng-1", "room-1", null, null, null, DateTime.UtcNow, DateTime.UtcNow),
            new("item-2", "Update docs", "Fix API docs", TaskItemStatus.Active,
                "writer-1", "room-1", "br-1", null, null, DateTime.UtcNow, DateTime.UtcNow)
        };

        var prompt = PromptBuilder.BuildConversationPrompt(agent, room, null, activeTaskItems: items);

        Assert.Contains("=== IN-FLIGHT WORK ITEMS ===", prompt);
        Assert.Contains("\"Write tests\" → assigned to eng-1", prompt);
        Assert.Contains("[in workspace]", prompt);
    }

    [Fact]
    public void ConversationPrompt_SpecContextIncluded()
    {
        var agent = MakeAgent();
        var room = MakeRoom();

        var prompt = PromptBuilder.BuildConversationPrompt(agent, room,
            specContext: "## 003 Agent System\n## 005 Workspace Runtime");

        Assert.Contains("=== PROJECT SPECIFICATION ===", prompt);
        Assert.Contains("## 003 Agent System", prompt);
    }

    [Fact]
    public void ConversationPrompt_TruncatesTo20Messages()
    {
        var agent = MakeAgent();
        var messages = Enumerable.Range(1, 30)
            .Select(i => MakeMessage("agent-1", $"Message {i}", "Planner"))
            .ToList();
        var room = MakeRoom(messages: messages);

        var prompt = PromptBuilder.BuildConversationPrompt(agent, room, null);

        Assert.DoesNotContain("Message 1]", prompt);
        Assert.DoesNotContain("Message 10]", prompt);
        Assert.Contains("Message 11", prompt);
        Assert.Contains("Message 30", prompt);
    }

    [Fact]
    public void ConversationPrompt_DirectMessagesIncluded()
    {
        var agent = MakeAgent(id: "eng-1");
        var room = MakeRoom();
        var dms = new List<MessageEntity>
        {
            MakeDm("planner-1", "Aristotle", "eng-1", "Focus on the auth module"),
            MakeDm("eng-1", "Hephaestus", "planner-1", "Got it, on it now")
        };

        var prompt = PromptBuilder.BuildConversationPrompt(agent, room, null, directMessages: dms);

        Assert.Contains("=== DIRECT MESSAGES ===", prompt);
        Assert.Contains("[DM from Aristotle]: Focus on the auth module", prompt);
        Assert.Contains("[DM to planner-1]: Got it, on it now", prompt);
    }

    [Fact]
    public void ConversationPrompt_NoOptionalSections_CleanOutput()
    {
        var agent = MakeAgent();
        var room = MakeRoom(messages: []);

        var prompt = PromptBuilder.BuildConversationPrompt(agent, room, null);

        Assert.DoesNotContain("=== YOUR MEMORIES ===", prompt);
        Assert.DoesNotContain("=== SHARED KNOWLEDGE ===", prompt);
        Assert.DoesNotContain("=== TASK ===", prompt);
        Assert.DoesNotContain("=== IN-FLIGHT WORK ITEMS ===", prompt);
        Assert.DoesNotContain("=== PROJECT SPECIFICATION ===", prompt);
        Assert.DoesNotContain("=== RECENT CONVERSATION ===", prompt);
        Assert.DoesNotContain("=== DIRECT MESSAGES ===", prompt);
        Assert.DoesNotContain("=== PREVIOUS CONVERSATION SUMMARY ===", prompt);
        Assert.Contains("=== CURRENT ROOM CONTEXT ===", prompt);
        Assert.Contains("=== YOUR TURN ===", prompt);
    }

    [Fact]
    public void ConversationPrompt_SectionOrdering()
    {
        var agent = MakeAgent(id: "eng-1");
        var task = MakeTask();
        var room = MakeRoom(task: task, messages: [MakeMessage("planner", "Hello")]);
        var memories = new List<AgentMemory> { MakeMemory(agentId: "eng-1") };

        var prompt = PromptBuilder.BuildConversationPrompt(agent, room,
            specContext: "spec content",
            memories: memories,
            sessionSummary: "Previous session",
            sprintPreamble: "Sprint goal");

        var indices = new[]
        {
            ("Sprint preamble", prompt.IndexOf("Sprint goal")),
            ("Session summary", prompt.IndexOf("PREVIOUS CONVERSATION SUMMARY")),
            ("Memories", prompt.IndexOf("YOUR MEMORIES")),
            ("Room context", prompt.IndexOf("CURRENT ROOM CONTEXT")),
            ("Task", prompt.IndexOf("=== TASK ===")),
            ("Spec", prompt.IndexOf("PROJECT SPECIFICATION")),
            ("Messages", prompt.IndexOf("RECENT CONVERSATION")),
            ("Your turn", prompt.IndexOf("YOUR TURN")),
        };

        for (int i = 1; i < indices.Length; i++)
        {
            Assert.True(indices[i - 1].Item2 < indices[i].Item2,
                $"Expected '{indices[i - 1].Item1}' before '{indices[i].Item1}'");
        }
    }

    // ── BuildBreakoutPrompt ─────────────────────────────────────

    [Fact]
    public void BreakoutPrompt_ContainsRoomNameAndRound()
    {
        var agent = MakeAgent();
        var br = MakeBreakout("Breakout: Auth Fix");

        var prompt = PromptBuilder.BuildBreakoutPrompt(agent, br, 5);

        Assert.Contains("=== BREAKOUT ROOM: Breakout: Auth Fix ===", prompt);
        Assert.Contains("Round: 5", prompt);
    }

    [Fact]
    public void BreakoutPrompt_DoesNotIncludeStartupPrompt()
    {
        var agent = MakeAgent();
        var br = MakeBreakout();

        var prompt = PromptBuilder.BuildBreakoutPrompt(agent, br, 1);

        Assert.DoesNotContain("You are a test agent.", prompt);
    }

    [Fact]
    public void BreakoutPrompt_IncludesWorkReportInstructions()
    {
        var agent = MakeAgent();
        var br = MakeBreakout();

        var prompt = PromptBuilder.BuildBreakoutPrompt(agent, br, 1);

        Assert.Contains("WORK REPORT:", prompt);
        Assert.Contains("Status: COMPLETE", prompt);
    }

    [Fact]
    public void BreakoutPrompt_SpecContextWithStarNote()
    {
        var agent = MakeAgent();
        var br = MakeBreakout();

        var prompt = PromptBuilder.BuildBreakoutPrompt(agent, br, 1,
            specContext: "★ 003 Agent System");

        Assert.Contains("=== PROJECT SPECIFICATIONS ===", prompt);
        Assert.Contains("Sections marked with ★ are linked to your current task.", prompt);
        Assert.Contains("★ 003 Agent System", prompt);
    }

    [Fact]
    public void BreakoutPrompt_TasksIncluded()
    {
        var agent = MakeAgent();
        var tasks = new List<TaskItem>
        {
            new("item-1", "Implement endpoint", "Add GET /api/health", TaskItemStatus.Active,
                "eng-1", "room-1", "br-1", null, null, DateTime.UtcNow, DateTime.UtcNow)
        };
        var br = MakeBreakout(tasks: tasks);

        var prompt = PromptBuilder.BuildBreakoutPrompt(agent, br, 1);

        Assert.Contains("=== ASSIGNED TASKS ===", prompt);
        Assert.Contains("Task: Implement endpoint", prompt);
    }

    [Fact]
    public void BreakoutPrompt_TruncatesTo10WorkLogMessages()
    {
        var agent = MakeAgent();
        var messages = Enumerable.Range(1, 15)
            .Select(i => MakeMessage("eng-1", $"Work log {i}"))
            .ToList();
        var br = MakeBreakout(messages: messages);

        var prompt = PromptBuilder.BuildBreakoutPrompt(agent, br, 3);

        Assert.DoesNotContain("Work log 1]", prompt);
        Assert.DoesNotContain("Work log 5]", prompt);
        Assert.Contains("Work log 6", prompt);
        Assert.Contains("Work log 15", prompt);
    }

    [Fact]
    public void BreakoutPrompt_SessionSummaryAppearsFirst()
    {
        var agent = MakeAgent();
        var br = MakeBreakout();

        var prompt = PromptBuilder.BuildBreakoutPrompt(agent, br, 1,
            sessionSummary: "Previously fixed auth module.");

        var summaryIdx = prompt.IndexOf("PREVIOUS WORK SUMMARY");
        var roomIdx = prompt.IndexOf("BREAKOUT ROOM:");
        Assert.True(summaryIdx >= 0, "Should contain session summary");
        Assert.True(summaryIdx < roomIdx, "Summary should appear before breakout room header");
    }

    [Fact]
    public void BreakoutPrompt_NoOptionalSections_CleanOutput()
    {
        var agent = MakeAgent();
        var br = MakeBreakout(tasks: [], messages: []);

        var prompt = PromptBuilder.BuildBreakoutPrompt(agent, br, 1);

        Assert.DoesNotContain("=== YOUR MEMORIES ===", prompt);
        Assert.DoesNotContain("=== ASSIGNED TASKS ===", prompt);
        Assert.DoesNotContain("=== WORK LOG ===", prompt);
        Assert.DoesNotContain("=== DIRECT MESSAGES ===", prompt);
        Assert.DoesNotContain("=== PROJECT SPECIFICATIONS ===", prompt);
        Assert.Contains("=== BREAKOUT ROOM:", prompt);
        Assert.Contains("=== YOUR TURN ===", prompt);
    }

    // ── BuildReviewPrompt ───────────────────────────────────────

    [Fact]
    public void ReviewPrompt_IncludesStartupPrompt()
    {
        var reviewer = MakeAgent(id: "rev-1", name: "Socrates", role: "Reviewer");

        var prompt = PromptBuilder.BuildReviewPrompt(reviewer, "Hephaestus",
            "Status: COMPLETE\nFiles: auth.cs", null);

        Assert.Contains("You are a test agent.", prompt);
    }

    [Fact]
    public void ReviewPrompt_ContainsReviewerIdentityAndInstructions()
    {
        var reviewer = MakeAgent(id: "rev-1", name: "Socrates", role: "Reviewer");

        var prompt = PromptBuilder.BuildReviewPrompt(reviewer, "Hephaestus",
            "Status: COMPLETE", null);

        Assert.Contains("You are Socrates (Reviewer)", prompt);
        Assert.Contains("Verdict: APPROVED | NEEDS FIX", prompt);
    }

    [Fact]
    public void ReviewPrompt_ContainsWorkReport()
    {
        var reviewer = MakeAgent(id: "rev-1", name: "Socrates", role: "Reviewer");

        var prompt = PromptBuilder.BuildReviewPrompt(reviewer, "Hephaestus",
            "Status: COMPLETE\nFiles: auth.cs\nEvidence: Tests pass", null);

        Assert.Contains("=== WORK REPORT ===", prompt);
        Assert.Contains("Files: auth.cs", prompt);
    }

    [Fact]
    public void ReviewPrompt_WithSpecContext_IncludesSpecAccuracy()
    {
        var reviewer = MakeAgent(id: "rev-1", name: "Socrates", role: "Reviewer");

        var prompt = PromptBuilder.BuildReviewPrompt(reviewer, "Hephaestus",
            "Status: COMPLETE", "## 003 Agent System");

        Assert.Contains("=== SPEC SECTIONS (verify accuracy against delivered work) ===", prompt);
        Assert.Contains("## 003 Agent System", prompt);
        Assert.Contains("Spec Accuracy:", prompt);
    }

    [Fact]
    public void ReviewPrompt_WithoutSpecContext_NoSpecAccuracy()
    {
        var reviewer = MakeAgent(id: "rev-1", name: "Socrates", role: "Reviewer");

        var prompt = PromptBuilder.BuildReviewPrompt(reviewer, "Hephaestus",
            "Status: COMPLETE", null);

        Assert.DoesNotContain("SPEC SECTIONS", prompt);
        Assert.DoesNotContain("Spec Accuracy:", prompt);
    }

    [Fact]
    public void ReviewPrompt_SectionOrdering()
    {
        var reviewer = MakeAgent(id: "rev-1", name: "Socrates", role: "Reviewer");

        var prompt = PromptBuilder.BuildReviewPrompt(reviewer, "Hephaestus",
            "Status: COMPLETE", "spec content");

        var startupIdx = prompt.IndexOf("You are a test agent.");
        var reviewIdx = prompt.IndexOf("=== REVIEW REQUEST ===");
        var reportIdx = prompt.IndexOf("=== WORK REPORT ===");
        var specIdx = prompt.IndexOf("=== SPEC SECTIONS");
        var turnIdx = prompt.IndexOf("=== YOUR TURN ===");

        Assert.True(startupIdx < reviewIdx, "Startup prompt before review request");
        Assert.True(reviewIdx < reportIdx, "Review request before work report");
        Assert.True(reportIdx < specIdx, "Work report before spec sections");
        Assert.True(specIdx < turnIdx, "Spec sections before your turn");
    }

    // ── BuildAssignmentPlanContent ──────────────────────────────

    [Fact]
    public void AssignmentPlan_IncludesObjectiveAndCriteria()
    {
        var assignment = new ParsedTaskAssignment(
            Agent: "Hephaestus",
            Title: "Add plan seeding",
            Description: "Persist plan content for breakout rooms",
            Criteria: ["Plan tab shows content", "No API regressions"],
            Type: TaskType.Feature);

        var content = PromptBuilder.BuildAssignmentPlanContent(assignment);

        Assert.Contains("# Add plan seeding", content);
        Assert.Contains("## Objective", content);
        Assert.Contains("Persist plan content for breakout rooms", content);
        Assert.Contains("## Acceptance Criteria", content);
        Assert.Contains("- Plan tab shows content", content);
    }

    [Fact]
    public void AssignmentPlan_BlankDescription_FallsBackToTitle()
    {
        var assignment = new ParsedTaskAssignment(
            Agent: "Hephaestus",
            Title: "Quick fix",
            Description: "  ",
            Criteria: []);

        var content = PromptBuilder.BuildAssignmentPlanContent(assignment);

        Assert.Contains("## Objective", content);
        Assert.Contains("Quick fix", content);
        Assert.DoesNotContain("## Acceptance Criteria", content);
    }

    [Fact]
    public void AssignmentPlan_NoCriteria_OmitsCriteriaSection()
    {
        var assignment = new ParsedTaskAssignment(
            Agent: "Hephaestus",
            Title: "Spike",
            Description: "Research options",
            Criteria: []);

        var content = PromptBuilder.BuildAssignmentPlanContent(assignment);

        Assert.DoesNotContain("## Acceptance Criteria", content);
    }

    // ── BuildTaskBrief ──────────────────────────────────────────

    [Fact]
    public void TaskBrief_IncludesAgentNameAndTasks()
    {
        var agent = MakeAgent(name: "Athena");
        var tasks = new List<TaskItem>
        {
            new("item-1", "Write tests", "Add unit tests", TaskItemStatus.Active,
                "eng-2", "room-1", "br-1", null, null, DateTime.UtcNow, DateTime.UtcNow)
        };

        var brief = PromptBuilder.BuildTaskBrief(agent, tasks);

        Assert.Contains("Task Brief for Athena", brief);
        Assert.Contains("Task: Write tests", brief);
        Assert.Contains("Description: Add unit tests", brief);
    }

    [Fact]
    public void TaskBrief_WithBranch_IncludesBranchName()
    {
        var agent = MakeAgent();
        var tasks = new List<TaskItem>
        {
            new("item-1", "Fix bug", "Fix it", TaskItemStatus.Active,
                "eng-1", "room-1", null, null, null, DateTime.UtcNow, DateTime.UtcNow)
        };

        var brief = PromptBuilder.BuildTaskBrief(agent, tasks, "task/fix-login-a1b2c3");

        Assert.Contains("Branch: task/fix-login-a1b2c3", brief);
    }

    [Fact]
    public void TaskBrief_WithoutBranch_NoBranchLine()
    {
        var agent = MakeAgent();

        var brief = PromptBuilder.BuildTaskBrief(agent, []);

        Assert.DoesNotContain("Branch:", brief);
    }

    // ── IsMemoryStale ───────────────────────────────────────────

    [Fact]
    public void IsMemoryStale_FreshMemory_NotStale()
    {
        var memory = MakeMemory(createdAt: DateTime.UtcNow.AddDays(-5));

        Assert.False(PromptBuilder.IsMemoryStale(memory));
    }

    [Fact]
    public void IsMemoryStale_OldMemory_Stale()
    {
        var memory = MakeMemory(createdAt: DateTime.UtcNow.AddDays(-45));

        Assert.True(PromptBuilder.IsMemoryStale(memory));
    }

    [Fact]
    public void IsMemoryStale_OldMemoryWithTtl_NotStale()
    {
        var memory = MakeMemory(
            createdAt: DateTime.UtcNow.AddDays(-45),
            expiresAt: DateTime.UtcNow.AddDays(10));

        Assert.False(PromptBuilder.IsMemoryStale(memory));
    }

    [Fact]
    public void IsMemoryStale_RecentlyAccessed_NotStale()
    {
        var memory = MakeMemory(
            createdAt: DateTime.UtcNow.AddDays(-60),
            lastAccessedAt: DateTime.UtcNow.AddDays(-5));

        Assert.False(PromptBuilder.IsMemoryStale(memory));
    }

    [Fact]
    public void IsMemoryStale_AccessedLongAgo_Stale()
    {
        var memory = MakeMemory(
            createdAt: DateTime.UtcNow.AddDays(-90),
            lastAccessedAt: DateTime.UtcNow.AddDays(-35));

        Assert.True(PromptBuilder.IsMemoryStale(memory));
    }

    // ── Spec Version in Headers ────────────────────────────────

    [Fact]
    public void BuildConversationPrompt_IncludesSpecVersion_WhenProvided()
    {
        var agent = MakeAgent();
        var room = MakeRoom();
        var prompt = PromptBuilder.BuildConversationPrompt(agent, room,
            "- specs/000-overview/spec.md: Overview", specVersion: "2.1.0");

        Assert.Contains("=== PROJECT SPECIFICATION (v2.1.0) ===", prompt);
    }

    [Fact]
    public void BuildConversationPrompt_OmitsVersionTag_WhenNull()
    {
        var agent = MakeAgent();
        var room = MakeRoom();
        var prompt = PromptBuilder.BuildConversationPrompt(agent, room,
            "- specs/000-overview/spec.md: Overview");

        Assert.Contains("=== PROJECT SPECIFICATION ===", prompt);
        Assert.DoesNotContain("(v", prompt);
    }

    [Fact]
    public void BuildBreakoutPrompt_IncludesSpecVersion_WhenProvided()
    {
        var agent = MakeAgent();
        var br = MakeBreakout();

        var prompt = PromptBuilder.BuildBreakoutPrompt(agent, br, 1,
            specContext: "- specs/000/spec.md: Test", specVersion: "3.0.0");

        Assert.Contains("=== PROJECT SPECIFICATIONS (v3.0.0) ===", prompt);
    }

    [Fact]
    public void BuildBreakoutPrompt_OmitsVersionTag_WhenNull()
    {
        var agent = MakeAgent();
        var br = MakeBreakout();

        var prompt = PromptBuilder.BuildBreakoutPrompt(agent, br, 1,
            specContext: "- specs/000/spec.md: Test");

        Assert.Contains("=== PROJECT SPECIFICATIONS ===", prompt);
        Assert.DoesNotContain("(v", prompt);
    }

    [Fact]
    public void BuildReviewPrompt_IncludesSpecVersion_WhenProvided()
    {
        var reviewer = MakeAgent(id: "reviewer-1", name: "Socrates", role: "Reviewer");

        var prompt = PromptBuilder.BuildReviewPrompt(reviewer, "Agent1",
            "Work done.", "- specs/000/spec.md: Test", specVersion: "1.2.3");

        Assert.Contains("=== SPEC SECTIONS (v1.2.3) (verify accuracy against delivered work) ===", prompt);
    }

    [Fact]
    public void BuildReviewPrompt_OmitsVersionTag_WhenNull()
    {
        var reviewer = MakeAgent(id: "reviewer-1", name: "Socrates", role: "Reviewer");

        var prompt = PromptBuilder.BuildReviewPrompt(reviewer, "Agent1",
            "Work done.", "- specs/000/spec.md: Test");

        Assert.Contains("=== SPEC SECTIONS (verify accuracy against delivered work) ===", prompt);
        // Should not contain a version tag like (v1.2.3) — but (verify...) is expected
        Assert.DoesNotMatch(@"\(v\d+\.\d+\.\d+\)", prompt);
    }
}
