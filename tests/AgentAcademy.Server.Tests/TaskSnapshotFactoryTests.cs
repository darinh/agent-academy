using System.Text.Json;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Tests;

public sealed class TaskSnapshotFactoryTests
{
    private static readonly DateTime FixedNow = new(2026, 4, 16, 0, 0, 0, DateTimeKind.Utc);

    // ── BuildTaskSnapshot ──────────────────────────────────────────────────

    #region Happy path

    [Fact]
    public void BuildTaskSnapshot_AllFieldsPopulated_MapsCorrectly()
    {
        var entity = MakeTaskEntity();
        entity.Size = "M";
        entity.StartedAt = FixedNow.AddHours(-2);
        entity.CompletedAt = FixedNow;
        entity.AssignedAgentId = "agent-1";
        entity.AssignedAgentName = "Hephaestus";
        entity.UsedFleet = true;
        entity.FleetModels = """["gpt-4","claude-3"]""";
        entity.BranchName = "feat/task-123";
        entity.PullRequestUrl = "https://github.com/org/repo/pull/42";
        entity.PullRequestNumber = 42;
        entity.PullRequestStatus = "Approved";
        entity.ReviewerAgentId = "agent-2";
        entity.ReviewRounds = 3;
        entity.TestsCreated = """["test1.cs","test2.cs"]""";
        entity.CommitCount = 7;
        entity.MergeCommitSha = "abc123";
        entity.SprintId = "sprint-1";
        entity.Priority = 1; // High
        entity.PreferredRoles = """["Coder","Reviewer"]""";
        entity.WorkspacePath = "/home/user/projects/myapp";

        var snap = TaskSnapshotFactory.BuildTaskSnapshot(
            entity, commentCount: 5,
            dependsOnIds: ["T-10"],
            blockingIds: ["T-20", "T-21"]);

        Assert.Equal("T-1", snap.Id);
        Assert.Equal("Test Task", snap.Title);
        Assert.Equal("A description", snap.Description);
        Assert.Equal("Must pass", snap.SuccessCriteria);
        Assert.Equal(TaskStatus.Active, snap.Status);
        Assert.Equal(TaskType.Bug, snap.Type);
        Assert.Equal(CollaborationPhase.Implementation, snap.CurrentPhase);
        Assert.Equal("The plan", snap.CurrentPlan);
        Assert.Equal(WorkstreamStatus.InProgress, snap.ValidationStatus);
        Assert.Equal("validating", snap.ValidationSummary);
        Assert.Equal(WorkstreamStatus.Ready, snap.ImplementationStatus);
        Assert.Equal("implementing", snap.ImplementationSummary);
        Assert.Equal(["Coder", "Reviewer"], snap.PreferredRoles);
        Assert.Equal(FixedNow, snap.CreatedAt);
        Assert.Equal(FixedNow, snap.UpdatedAt);
        Assert.Equal(TaskSize.M, snap.Size);
        Assert.Equal(FixedNow.AddHours(-2), snap.StartedAt);
        Assert.Equal(FixedNow, snap.CompletedAt);
        Assert.Equal("agent-1", snap.AssignedAgentId);
        Assert.Equal("Hephaestus", snap.AssignedAgentName);
        Assert.True(snap.UsedFleet);
        Assert.Equal(["gpt-4", "claude-3"], snap.FleetModels);
        Assert.Equal("feat/task-123", snap.BranchName);
        Assert.Equal("https://github.com/org/repo/pull/42", snap.PullRequestUrl);
        Assert.Equal(42, snap.PullRequestNumber);
        Assert.Equal(PullRequestStatus.Approved, snap.PullRequestStatus);
        Assert.Equal("agent-2", snap.ReviewerAgentId);
        Assert.Equal(3, snap.ReviewRounds);
        Assert.Equal(["test1.cs", "test2.cs"], snap.TestsCreated);
        Assert.Equal(7, snap.CommitCount);
        Assert.Equal("abc123", snap.MergeCommitSha);
        Assert.Equal(5, snap.CommentCount);
        Assert.Equal("sprint-1", snap.SprintId);
        Assert.Equal(["T-10"], snap.DependsOnTaskIds);
        Assert.Equal(["T-20", "T-21"], snap.BlockingTaskIds);
        Assert.Equal(TaskPriority.High, snap.Priority);
        Assert.Equal("/home/user/projects/myapp", snap.WorkspacePath);
    }

    [Fact]
    public void BuildTaskSnapshot_MinimalEntity_MapsDefaults()
    {
        var entity = new TaskEntity
        {
            Id = "T-min",
            Title = "Minimal",
            Description = "",
            SuccessCriteria = "",
            CreatedAt = FixedNow,
            UpdatedAt = FixedNow
        };

        var snap = TaskSnapshotFactory.BuildTaskSnapshot(entity);

        Assert.Equal(TaskStatus.Active, snap.Status);
        Assert.Equal(TaskType.Feature, snap.Type);
        Assert.Equal(CollaborationPhase.Planning, snap.CurrentPhase);
        Assert.Equal(WorkstreamStatus.NotStarted, snap.ValidationStatus);
        Assert.Equal(WorkstreamStatus.NotStarted, snap.ImplementationStatus);
        Assert.Empty(snap.PreferredRoles);
        Assert.Null(snap.Size);
        Assert.Null(snap.StartedAt);
        Assert.Null(snap.CompletedAt);
        Assert.Null(snap.AssignedAgentId);
        Assert.False(snap.UsedFleet);
        Assert.Empty(snap.FleetModels!);
        Assert.Null(snap.BranchName);
        Assert.Null(snap.PullRequestUrl);
        Assert.Null(snap.PullRequestNumber);
        Assert.Null(snap.PullRequestStatus);
        Assert.Empty(snap.TestsCreated!);
        Assert.Equal(0, snap.CommitCount);
        Assert.Null(snap.MergeCommitSha);
        Assert.Equal(0, snap.CommentCount);
        Assert.Null(snap.DependsOnTaskIds);
        Assert.Null(snap.BlockingTaskIds);
        Assert.Equal(TaskPriority.Medium, snap.Priority);
    }

    #endregion

    #region JSON deserialization

    [Fact]
    public void BuildTaskSnapshot_EmptyJsonArrays_ReturnsEmptyLists()
    {
        var entity = MakeTaskEntity();
        entity.PreferredRoles = "[]";
        entity.FleetModels = "[]";
        entity.TestsCreated = "[]";

        var snap = TaskSnapshotFactory.BuildTaskSnapshot(entity);

        Assert.Empty(snap.PreferredRoles);
        Assert.Empty(snap.FleetModels!);
        Assert.Empty(snap.TestsCreated!);
    }

    [Fact]
    public void BuildTaskSnapshot_PopulatedJsonArrays_DeserialisesCorrectly()
    {
        var entity = MakeTaskEntity();
        entity.PreferredRoles = """["Alpha","Beta","Gamma"]""";
        entity.FleetModels = """["model-a"]""";
        entity.TestsCreated = """["a.cs","b.cs","c.cs"]""";

        var snap = TaskSnapshotFactory.BuildTaskSnapshot(entity);

        Assert.Equal(3, snap.PreferredRoles.Count);
        Assert.Equal("Alpha", snap.PreferredRoles[0]);
        Assert.Single(snap.FleetModels!);
        Assert.Equal(3, snap.TestsCreated!.Count);
    }

    [Fact]
    public void BuildTaskSnapshot_MalformedJson_ThrowsJsonException()
    {
        var entity = MakeTaskEntity();
        entity.PreferredRoles = "not-json";

        Assert.Throws<JsonException>(() => TaskSnapshotFactory.BuildTaskSnapshot(entity));
    }

    [Fact]
    public void BuildTaskSnapshot_NullJsonDeserializesAsEmpty()
    {
        var entity = MakeTaskEntity();
        entity.PreferredRoles = "null";
        entity.FleetModels = "null";
        entity.TestsCreated = "null";

        var snap = TaskSnapshotFactory.BuildTaskSnapshot(entity);

        Assert.Empty(snap.PreferredRoles);
        Assert.Empty(snap.FleetModels!);
        Assert.Empty(snap.TestsCreated!);
    }

    #endregion

    #region Enum parsing — TryParse with defaults

    [Fact]
    public void BuildTaskSnapshot_InvalidType_FallsBackToFeature()
    {
        var entity = MakeTaskEntity();
        entity.Type = "NotARealType";

        var snap = TaskSnapshotFactory.BuildTaskSnapshot(entity);

        Assert.Equal(TaskType.Feature, snap.Type);
    }

    [Fact]
    public void BuildTaskSnapshot_NumericTypeString_ParsesAsEnumValue()
    {
        var entity = MakeTaskEntity();
        entity.Type = "1"; // Bug = 1

        var snap = TaskSnapshotFactory.BuildTaskSnapshot(entity);

        Assert.Equal(TaskType.Bug, snap.Type);
    }

    [Fact]
    public void BuildTaskSnapshot_OutOfRangeNumericType_ProducesUndefinedEnumValue()
    {
        var entity = MakeTaskEntity();
        entity.Type = "999";

        var snap = TaskSnapshotFactory.BuildTaskSnapshot(entity);

        // TryParse returns true for out-of-range numeric strings, producing
        // an undefined enum value. The fallback only triggers when TryParse
        // returns false (named strings that don't match). Document this:
        Assert.Equal((TaskType)999, snap.Type);
        Assert.False(Enum.IsDefined(snap.Type));
    }

    [Fact]
    public void BuildTaskSnapshot_EmptyPrStatus_ReturnsNull()
    {
        var entity = MakeTaskEntity();
        entity.PullRequestStatus = "";

        var snap = TaskSnapshotFactory.BuildTaskSnapshot(entity);

        Assert.Null(snap.PullRequestStatus);
    }

    [Fact]
    public void BuildTaskSnapshot_NullPrStatus_ReturnsNull()
    {
        var entity = MakeTaskEntity();
        entity.PullRequestStatus = null;

        var snap = TaskSnapshotFactory.BuildTaskSnapshot(entity);

        Assert.Null(snap.PullRequestStatus);
    }

    [Fact]
    public void BuildTaskSnapshot_ValidPrStatus_ParsesCorrectly()
    {
        var entity = MakeTaskEntity();
        entity.PullRequestStatus = "Merged";

        var snap = TaskSnapshotFactory.BuildTaskSnapshot(entity);

        Assert.Equal(PullRequestStatus.Merged, snap.PullRequestStatus);
    }

    [Fact]
    public void BuildTaskSnapshot_EmptySize_ReturnsNull()
    {
        var entity = MakeTaskEntity();
        entity.Size = "";

        var snap = TaskSnapshotFactory.BuildTaskSnapshot(entity);

        Assert.Null(snap.Size);
    }

    [Fact]
    public void BuildTaskSnapshot_NullSize_ReturnsNull()
    {
        var entity = MakeTaskEntity();
        entity.Size = null;

        var snap = TaskSnapshotFactory.BuildTaskSnapshot(entity);

        Assert.Null(snap.Size);
    }

    [Fact]
    public void BuildTaskSnapshot_ValidSize_ParsesCorrectly()
    {
        var entity = MakeTaskEntity();
        entity.Size = "XL";

        var snap = TaskSnapshotFactory.BuildTaskSnapshot(entity);

        Assert.Equal(TaskSize.XL, snap.Size);
    }

    [Theory]
    [InlineData("XS", TaskSize.XS)]
    [InlineData("S", TaskSize.S)]
    [InlineData("M", TaskSize.M)]
    [InlineData("L", TaskSize.L)]
    [InlineData("XL", TaskSize.XL)]
    public void BuildTaskSnapshot_AllSizeValues_ParseCorrectly(string sizeStr, TaskSize expected)
    {
        var entity = MakeTaskEntity();
        entity.Size = sizeStr;

        var snap = TaskSnapshotFactory.BuildTaskSnapshot(entity);

        Assert.Equal(expected, snap.Size);
    }

    #endregion

    #region Enum parsing — Parse (throws on invalid)

    [Fact]
    public void BuildTaskSnapshot_InvalidStatus_Throws()
    {
        var entity = MakeTaskEntity();
        entity.Status = "NotAStatus";

        Assert.ThrowsAny<ArgumentException>(
            () => TaskSnapshotFactory.BuildTaskSnapshot(entity));
    }

    [Fact]
    public void BuildTaskSnapshot_InvalidCurrentPhase_Throws()
    {
        var entity = MakeTaskEntity();
        entity.CurrentPhase = "BadPhase";

        Assert.ThrowsAny<ArgumentException>(
            () => TaskSnapshotFactory.BuildTaskSnapshot(entity));
    }

    [Fact]
    public void BuildTaskSnapshot_InvalidValidationStatus_Throws()
    {
        var entity = MakeTaskEntity();
        entity.ValidationStatus = "Whatever";

        Assert.ThrowsAny<ArgumentException>(
            () => TaskSnapshotFactory.BuildTaskSnapshot(entity));
    }

    [Fact]
    public void BuildTaskSnapshot_InvalidImplementationStatus_Throws()
    {
        var entity = MakeTaskEntity();
        entity.ImplementationStatus = "Nope";

        Assert.ThrowsAny<ArgumentException>(
            () => TaskSnapshotFactory.BuildTaskSnapshot(entity));
    }

    [Theory]
    [InlineData("Active", TaskStatus.Active)]
    [InlineData("Completed", TaskStatus.Completed)]
    [InlineData("Blocked", TaskStatus.Blocked)]
    [InlineData("InReview", TaskStatus.InReview)]
    [InlineData("Queued", TaskStatus.Queued)]
    [InlineData("Cancelled", TaskStatus.Cancelled)]
    [InlineData("AwaitingValidation", TaskStatus.AwaitingValidation)]
    [InlineData("ChangesRequested", TaskStatus.ChangesRequested)]
    [InlineData("Approved", TaskStatus.Approved)]
    [InlineData("Merging", TaskStatus.Merging)]
    public void BuildTaskSnapshot_AllStatusValues_ParseCorrectly(string statusStr, TaskStatus expected)
    {
        var entity = MakeTaskEntity();
        entity.Status = statusStr;

        var snap = TaskSnapshotFactory.BuildTaskSnapshot(entity);

        Assert.Equal(expected, snap.Status);
    }

    #endregion

    #region Priority mapping

    [Theory]
    [InlineData(0, TaskPriority.Critical)]
    [InlineData(1, TaskPriority.High)]
    [InlineData(2, TaskPriority.Medium)]
    [InlineData(3, TaskPriority.Low)]
    public void BuildTaskSnapshot_DefinedPriorities_MapCorrectly(int value, TaskPriority expected)
    {
        var entity = MakeTaskEntity();
        entity.Priority = value;

        var snap = TaskSnapshotFactory.BuildTaskSnapshot(entity);

        Assert.Equal(expected, snap.Priority);
    }

    [Fact]
    public void BuildTaskSnapshot_UndefinedPriorityValue_FallsBackToMedium()
    {
        var entity = MakeTaskEntity();
        entity.Priority = 99;

        var snap = TaskSnapshotFactory.BuildTaskSnapshot(entity);

        Assert.Equal(TaskPriority.Medium, snap.Priority);
    }

    [Fact]
    public void BuildTaskSnapshot_NegativePriority_FallsBackToMedium()
    {
        var entity = MakeTaskEntity();
        entity.Priority = -1;

        var snap = TaskSnapshotFactory.BuildTaskSnapshot(entity);

        Assert.Equal(TaskPriority.Medium, snap.Priority);
    }

    #endregion

    #region Dependency pass-through

    [Fact]
    public void BuildTaskSnapshot_NullDependencies_PassedThrough()
    {
        var entity = MakeTaskEntity();

        var snap = TaskSnapshotFactory.BuildTaskSnapshot(entity, dependsOnIds: null, blockingIds: null);

        Assert.Null(snap.DependsOnTaskIds);
        Assert.Null(snap.BlockingTaskIds);
    }

    [Fact]
    public void BuildTaskSnapshot_EmptyDependencies_PassedThrough()
    {
        var entity = MakeTaskEntity();

        var snap = TaskSnapshotFactory.BuildTaskSnapshot(entity,
            dependsOnIds: [], blockingIds: []);

        Assert.Empty(snap.DependsOnTaskIds!);
        Assert.Empty(snap.BlockingTaskIds!);
    }

    [Fact]
    public void BuildTaskSnapshot_CommentCount_PassedThrough()
    {
        var entity = MakeTaskEntity();

        var snap = TaskSnapshotFactory.BuildTaskSnapshot(entity, commentCount: 42);

        Assert.Equal(42, snap.CommentCount);
    }

    #endregion

    // ── BuildTaskComment ───────────────────────────────────────────────────

    #region TaskComment

    [Fact]
    public void BuildTaskComment_AllFieldsPopulated_MapsCorrectly()
    {
        var entity = new TaskCommentEntity
        {
            Id = "C-1",
            TaskId = "T-1",
            AgentId = "agent-1",
            AgentName = "Archimedes",
            CommentType = "Finding",
            Content = "Found a bug",
            CreatedAt = FixedNow
        };

        var comment = TaskSnapshotFactory.BuildTaskComment(entity);

        Assert.Equal("C-1", comment.Id);
        Assert.Equal("T-1", comment.TaskId);
        Assert.Equal("agent-1", comment.AgentId);
        Assert.Equal("Archimedes", comment.AgentName);
        Assert.Equal(TaskCommentType.Finding, comment.CommentType);
        Assert.Equal("Found a bug", comment.Content);
        Assert.Equal(FixedNow, comment.CreatedAt);
    }

    [Fact]
    public void BuildTaskComment_InvalidCommentType_FallsBackToComment()
    {
        var entity = new TaskCommentEntity
        {
            Id = "C-2",
            TaskId = "T-1",
            AgentId = "agent-1",
            AgentName = "Test",
            CommentType = "InvalidType",
            Content = "text",
            CreatedAt = FixedNow
        };

        var comment = TaskSnapshotFactory.BuildTaskComment(entity);

        Assert.Equal(TaskCommentType.Comment, comment.CommentType);
    }

    [Theory]
    [InlineData("Comment", TaskCommentType.Comment)]
    [InlineData("Finding", TaskCommentType.Finding)]
    [InlineData("Evidence", TaskCommentType.Evidence)]
    [InlineData("Blocker", TaskCommentType.Blocker)]
    [InlineData("Retrospective", TaskCommentType.Retrospective)]
    public void BuildTaskComment_AllCommentTypes_ParseCorrectly(string typeStr, TaskCommentType expected)
    {
        var entity = new TaskCommentEntity
        {
            Id = "C-3", TaskId = "T-1", AgentId = "a", AgentName = "A",
            CommentType = typeStr, Content = "x", CreatedAt = FixedNow
        };

        var comment = TaskSnapshotFactory.BuildTaskComment(entity);

        Assert.Equal(expected, comment.CommentType);
    }

    [Fact]
    public void BuildTaskComment_NumericCommentType_ProducesUndefinedEnumValue()
    {
        var entity = new TaskCommentEntity
        {
            Id = "C-4", TaskId = "T-1", AgentId = "a", AgentName = "A",
            CommentType = "999", Content = "x", CreatedAt = FixedNow
        };

        var comment = TaskSnapshotFactory.BuildTaskComment(entity);

        // TryParse succeeds for numeric strings even when undefined
        Assert.Equal((TaskCommentType)999, comment.CommentType);
    }

    #endregion

    // ── BuildTaskEvidence ──────────────────────────────────────────────────

    #region TaskEvidence

    [Fact]
    public void BuildTaskEvidence_AllFieldsPopulated_MapsCorrectly()
    {
        var entity = new TaskEvidenceEntity
        {
            Id = "E-1",
            TaskId = "T-1",
            Phase = "Baseline",
            CheckName = "build",
            Tool = "bash",
            Command = "dotnet build",
            ExitCode = 0,
            OutputSnippet = "Build succeeded",
            Passed = true,
            AgentId = "agent-1",
            AgentName = "Hephaestus",
            CreatedAt = FixedNow
        };

        var evidence = TaskSnapshotFactory.BuildTaskEvidence(entity);

        Assert.Equal("E-1", evidence.Id);
        Assert.Equal("T-1", evidence.TaskId);
        Assert.Equal(EvidencePhase.Baseline, evidence.Phase);
        Assert.Equal("build", evidence.CheckName);
        Assert.Equal("bash", evidence.Tool);
        Assert.Equal("dotnet build", evidence.Command);
        Assert.Equal(0, evidence.ExitCode);
        Assert.Equal("Build succeeded", evidence.OutputSnippet);
        Assert.True(evidence.Passed);
        Assert.Equal("agent-1", evidence.AgentId);
        Assert.Equal("Hephaestus", evidence.AgentName);
        Assert.Equal(FixedNow, evidence.CreatedAt);
    }

    [Fact]
    public void BuildTaskEvidence_NullableFieldsAreNull_MapsNulls()
    {
        var entity = new TaskEvidenceEntity
        {
            Id = "E-2", TaskId = "T-1", Phase = "After",
            CheckName = "review", Tool = "manual",
            Command = null, ExitCode = null, OutputSnippet = null,
            Passed = false, AgentId = "a", AgentName = "A", CreatedAt = FixedNow
        };

        var evidence = TaskSnapshotFactory.BuildTaskEvidence(entity);

        Assert.Null(evidence.Command);
        Assert.Null(evidence.ExitCode);
        Assert.Null(evidence.OutputSnippet);
        Assert.False(evidence.Passed);
    }

    [Fact]
    public void BuildTaskEvidence_InvalidPhase_FallsBackToAfter()
    {
        var entity = new TaskEvidenceEntity
        {
            Id = "E-3", TaskId = "T-1", Phase = "NotAPhase",
            CheckName = "x", Tool = "y",
            Passed = true, AgentId = "a", AgentName = "A", CreatedAt = FixedNow
        };

        var evidence = TaskSnapshotFactory.BuildTaskEvidence(entity);

        Assert.Equal(EvidencePhase.After, evidence.Phase);
    }

    [Theory]
    [InlineData("Baseline", EvidencePhase.Baseline)]
    [InlineData("After", EvidencePhase.After)]
    [InlineData("Review", EvidencePhase.Review)]
    public void BuildTaskEvidence_AllPhaseValues_ParseCorrectly(string phaseStr, EvidencePhase expected)
    {
        var entity = new TaskEvidenceEntity
        {
            Id = "E-4", TaskId = "T-1", Phase = phaseStr,
            CheckName = "x", Tool = "y",
            Passed = true, AgentId = "a", AgentName = "A", CreatedAt = FixedNow
        };

        var evidence = TaskSnapshotFactory.BuildTaskEvidence(entity);

        Assert.Equal(expected, evidence.Phase);
    }

    [Fact]
    public void BuildTaskEvidence_NumericPhase_ProducesUndefinedEnumValue()
    {
        var entity = new TaskEvidenceEntity
        {
            Id = "E-5", TaskId = "T-1", Phase = "999",
            CheckName = "x", Tool = "y",
            Passed = true, AgentId = "a", AgentName = "A", CreatedAt = FixedNow
        };

        var evidence = TaskSnapshotFactory.BuildTaskEvidence(entity);

        // TryParse succeeds for numeric strings even when undefined
        Assert.Equal((EvidencePhase)999, evidence.Phase);
    }

    #endregion

    // ── BuildSpecTaskLink ─────────────────────────────────────────────────

    #region SpecTaskLink

    [Fact]
    public void BuildSpecTaskLink_AllFieldsPopulated_MapsCorrectly()
    {
        var entity = new SpecTaskLinkEntity
        {
            Id = "L-1",
            TaskId = "T-1",
            SpecSectionId = "007",
            LinkType = "Modifies",
            LinkedByAgentId = "agent-1",
            LinkedByAgentName = "Athena",
            Note = "Updated command list",
            CreatedAt = FixedNow
        };

        var link = TaskSnapshotFactory.BuildSpecTaskLink(entity);

        Assert.Equal("L-1", link.Id);
        Assert.Equal("T-1", link.TaskId);
        Assert.Equal("007", link.SpecSectionId);
        Assert.Equal(SpecLinkType.Modifies, link.LinkType);
        Assert.Equal("agent-1", link.LinkedByAgentId);
        Assert.Equal("Athena", link.LinkedByAgentName);
        Assert.Equal("Updated command list", link.Note);
        Assert.Equal(FixedNow, link.CreatedAt);
    }

    [Fact]
    public void BuildSpecTaskLink_NullNote_MapsNull()
    {
        var entity = new SpecTaskLinkEntity
        {
            Id = "L-2", TaskId = "T-1", SpecSectionId = "001",
            LinkType = "Implements", LinkedByAgentId = "a", LinkedByAgentName = "A",
            Note = null, CreatedAt = FixedNow
        };

        var link = TaskSnapshotFactory.BuildSpecTaskLink(entity);

        Assert.Null(link.Note);
    }

    [Fact]
    public void BuildSpecTaskLink_InvalidLinkType_FallsBackToImplements()
    {
        var entity = new SpecTaskLinkEntity
        {
            Id = "L-3", TaskId = "T-1", SpecSectionId = "001",
            LinkType = "InvalidLink", LinkedByAgentId = "a", LinkedByAgentName = "A",
            CreatedAt = FixedNow
        };

        var link = TaskSnapshotFactory.BuildSpecTaskLink(entity);

        Assert.Equal(SpecLinkType.Implements, link.LinkType);
    }

    [Theory]
    [InlineData("Implements", SpecLinkType.Implements)]
    [InlineData("Modifies", SpecLinkType.Modifies)]
    [InlineData("Fixes", SpecLinkType.Fixes)]
    [InlineData("References", SpecLinkType.References)]
    public void BuildSpecTaskLink_AllLinkTypes_ParseCorrectly(string typeStr, SpecLinkType expected)
    {
        var entity = new SpecTaskLinkEntity
        {
            Id = "L-4", TaskId = "T-1", SpecSectionId = "001",
            LinkType = typeStr, LinkedByAgentId = "a", LinkedByAgentName = "A",
            CreatedAt = FixedNow
        };

        var link = TaskSnapshotFactory.BuildSpecTaskLink(entity);

        Assert.Equal(expected, link.LinkType);
    }

    [Fact]
    public void BuildSpecTaskLink_NumericLinkType_ProducesUndefinedEnumValue()
    {
        var entity = new SpecTaskLinkEntity
        {
            Id = "L-5", TaskId = "T-1", SpecSectionId = "001",
            LinkType = "999", LinkedByAgentId = "a", LinkedByAgentName = "A",
            CreatedAt = FixedNow
        };

        var link = TaskSnapshotFactory.BuildSpecTaskLink(entity);

        // TryParse succeeds for numeric strings even when undefined
        Assert.Equal((SpecLinkType)999, link.LinkType);
    }

    #endregion

    // ── Helpers ────────────────────────────────────────────────────────────

    private static TaskEntity MakeTaskEntity() => new()
    {
        Id = "T-1",
        Title = "Test Task",
        Description = "A description",
        SuccessCriteria = "Must pass",
        Status = "Active",
        Type = "Bug",
        CurrentPhase = "Implementation",
        CurrentPlan = "The plan",
        ValidationStatus = "InProgress",
        ValidationSummary = "validating",
        ImplementationStatus = "Ready",
        ImplementationSummary = "implementing",
        PreferredRoles = "[]",
        CreatedAt = FixedNow,
        UpdatedAt = FixedNow
    };
}
