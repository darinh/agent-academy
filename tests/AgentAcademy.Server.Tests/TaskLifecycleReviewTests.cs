using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TaskStatus = AgentAcademy.Shared.Models.TaskStatus;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for <see cref="TaskLifecycleService"/> review workflow:
/// approve, request-changes, reject transitions and edge cases.
/// </summary>
public sealed class TaskLifecycleReviewTests : IDisposable
{
    private readonly TestServiceGraph _svc;

    private static readonly AgentDefinition Reviewer = new(
        Id: "reviewer-1", Name: "Athena", Role: "Reviewer",
        Summary: "Reviewer", StartupPrompt: "prompt", Model: null,
        CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true);

    private static readonly AgentDefinition Engineer = new(
        Id: "engineer-1", Name: "Hephaestus", Role: "SoftwareEngineer",
        Summary: "Engineer", StartupPrompt: "prompt", Model: null,
        CapabilityTags: [], EnabledTools: [], AutoJoinDefaultRoom: true);

    public TaskLifecycleReviewTests()
    {
        _svc = new TestServiceGraph(agents: [Reviewer, Engineer]);
    }

    // ──────────────── APPROVE ────────────────

    [Fact]
    public async Task ApproveTask_FromInReview_SetsApprovedStatus()
    {
        var task = await CreateTaskAsync(nameof(TaskStatus.InReview));

        var result = await _svc.TaskLifecycleService.ApproveTaskAsync(task.Id, Reviewer.Id);

        Assert.Equal(TaskStatus.Approved, result.Status);
    }

    [Fact]
    public async Task ApproveTask_FromAwaitingValidation_SetsApprovedStatus()
    {
        var task = await CreateTaskAsync(nameof(TaskStatus.AwaitingValidation));

        var result = await _svc.TaskLifecycleService.ApproveTaskAsync(task.Id, Reviewer.Id);

        Assert.Equal(TaskStatus.Approved, result.Status);
    }

    [Fact]
    public async Task ApproveTask_RecordsReviewerAndIncrementsRounds()
    {
        var task = await CreateTaskAsync(nameof(TaskStatus.InReview));

        await _svc.TaskLifecycleService.ApproveTaskAsync(task.Id, Reviewer.Id);

        var entity = await _svc.Db.Tasks.FindAsync(task.Id);
        Assert.Equal(Reviewer.Id, entity!.ReviewerAgentId);
        Assert.Equal(1, entity.ReviewRounds);
    }

    [Fact]
    public async Task ApproveTask_WithFindings_PostsReviewMessage()
    {
        var task = await CreateTaskAsync(nameof(TaskStatus.InReview), roomId: "room-1");
        await CreateRoomAsync("room-1");

        await _svc.TaskLifecycleService.ApproveTaskAsync(task.Id, Reviewer.Id, "LGTM - clean code");

        var messages = await _svc.Db.Messages
            .Where(m => m.RoomId == "room-1" && m.Kind == nameof(MessageKind.Review))
            .ToListAsync();
        Assert.Single(messages);
        Assert.Contains("Approved", messages[0].Content);
        Assert.Contains("LGTM - clean code", messages[0].Content);
    }

    [Fact]
    public async Task ApproveTask_WithoutFindings_NoReviewMessage()
    {
        var task = await CreateTaskAsync(nameof(TaskStatus.InReview), roomId: "room-1");
        await CreateRoomAsync("room-1");

        await _svc.TaskLifecycleService.ApproveTaskAsync(task.Id, Reviewer.Id);

        var messages = await _svc.Db.Messages
            .Where(m => m.RoomId == "room-1" && m.Kind == nameof(MessageKind.Review))
            .ToListAsync();
        Assert.Empty(messages);
    }

    [Fact]
    public async Task ApproveTask_FromActiveState_Throws()
    {
        var task = await CreateTaskAsync(nameof(TaskStatus.Active));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.TaskLifecycleService.ApproveTaskAsync(task.Id, Reviewer.Id));

        Assert.Contains("must be InReview or AwaitingValidation", ex.Message);
    }

    [Fact]
    public async Task ApproveTask_NonExistentTask_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.TaskLifecycleService.ApproveTaskAsync("non-existent", Reviewer.Id));

        Assert.Contains("not found", ex.Message);
    }

    // ──────────────── REQUEST CHANGES ────────────────

    [Fact]
    public async Task RequestChanges_FromInReview_SetsChangesRequestedStatus()
    {
        var task = await CreateTaskAsync(nameof(TaskStatus.InReview));

        var result = await _svc.TaskLifecycleService.RequestChangesAsync(
            task.Id, Reviewer.Id, "Fix the null check");

        Assert.Equal(TaskStatus.ChangesRequested, result.Status);
    }

    [Fact]
    public async Task RequestChanges_PostsFindingsMessage()
    {
        var task = await CreateTaskAsync(nameof(TaskStatus.InReview), roomId: "room-2");
        await CreateRoomAsync("room-2");

        await _svc.TaskLifecycleService.RequestChangesAsync(
            task.Id, Reviewer.Id, "Missing error handling");

        var messages = await _svc.Db.Messages
            .Where(m => m.RoomId == "room-2" && m.Kind == nameof(MessageKind.Review))
            .ToListAsync();
        Assert.Single(messages);
        Assert.Contains("Changes Requested", messages[0].Content);
        Assert.Contains("Missing error handling", messages[0].Content);
    }

    [Fact]
    public async Task RequestChanges_AtMaxReviewRounds_Throws()
    {
        var task = await CreateTaskAsync(nameof(TaskStatus.InReview), reviewRounds: 5);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.TaskLifecycleService.RequestChangesAsync(
                task.Id, Reviewer.Id, "More changes"));

        Assert.Contains("maximum", ex.Message);
    }

    [Fact]
    public async Task RequestChanges_FromActiveState_Throws()
    {
        var task = await CreateTaskAsync(nameof(TaskStatus.Active));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.TaskLifecycleService.RequestChangesAsync(
                task.Id, Reviewer.Id, "Changes needed"));
    }

    // ──────────────── REJECT (core) ────────────────

    [Fact]
    public async Task RejectTaskCore_FromApproved_SetsChangesRequested()
    {
        var task = await CreateTaskAsync(nameof(TaskStatus.Approved));

        var result = await _svc.TaskLifecycleService.RejectTaskCoreAsync(
            task.Id, Reviewer.Id, "Found a regression");

        Assert.Equal(TaskStatus.ChangesRequested, result.Snapshot.Status);
        Assert.Equal(task.Id, result.TaskId);
    }

    [Fact]
    public async Task RejectTaskCore_FromCompleted_ClearsMergeInfo()
    {
        var task = await CreateTaskAsync(nameof(TaskStatus.Completed), mergeCommitSha: "abc123");

        var result = await _svc.TaskLifecycleService.RejectTaskCoreAsync(
            task.Id, Reviewer.Id, "Production issue");

        // NOTE: RejectTaskCore does NOT call SaveChangesAsync —
        // the caller is responsible. We check the entity directly.
        var entity = await _svc.Db.Tasks.FindAsync(task.Id);
        Assert.Null(entity!.MergeCommitSha);
        Assert.Null(entity.CompletedAt);
    }

    [Fact]
    public async Task RejectTaskCore_FromInReview_Throws()
    {
        var task = await CreateTaskAsync(nameof(TaskStatus.InReview));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.TaskLifecycleService.RejectTaskCoreAsync(
                task.Id, Reviewer.Id, "Reject attempt"));
    }

    [Fact]
    public async Task RejectTaskCore_AtMaxReviewRounds_Throws()
    {
        var task = await CreateTaskAsync(nameof(TaskStatus.Approved), reviewRounds: 5);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.TaskLifecycleService.RejectTaskCoreAsync(
                task.Id, Reviewer.Id, "Reject"));

        Assert.Contains("maximum", ex.Message);
    }

    [Fact]
    public async Task RejectTaskCore_PostsRejectionMessage()
    {
        var task = await CreateTaskAsync(nameof(TaskStatus.Approved), roomId: "room-3");
        await CreateRoomAsync("room-3");

        await _svc.TaskLifecycleService.RejectTaskCoreAsync(
            task.Id, Reviewer.Id, "Missing tests");

        // RejectTaskCoreAsync does NOT save — caller is responsible
        await _svc.Db.SaveChangesAsync();

        var messages = await _svc.Db.Messages
            .Where(m => m.RoomId == "room-3" && m.Kind == nameof(MessageKind.Review))
            .ToListAsync();
        Assert.Single(messages);
        Assert.Contains("Rejected", messages[0].Content);
    }

    [Fact]
    public async Task RejectTaskCore_WithRevertSha_IncludesRevertNote()
    {
        var task = await CreateTaskAsync(nameof(TaskStatus.Completed), roomId: "room-4");
        await CreateRoomAsync("room-4");

        await _svc.TaskLifecycleService.RejectTaskCoreAsync(
            task.Id, Reviewer.Id, "Bug found", revertCommitSha: "abc123");

        // RejectTaskCoreAsync does NOT save — caller is responsible
        await _svc.Db.SaveChangesAsync();

        var messages = await _svc.Db.Messages
            .Where(m => m.RoomId == "room-4" && m.Kind == nameof(MessageKind.Review))
            .ToListAsync();
        Assert.Single(messages);
        Assert.Contains("merge reverted", messages[0].Content);
    }

    // ──────────────── REVIEW ROUND INCREMENTING ────────────────

    [Fact]
    public async Task MultipleReviewCycles_IncrementRoundsCorrectly()
    {
        var task = await CreateTaskAsync(nameof(TaskStatus.InReview));

        // Round 1: request changes
        await _svc.TaskLifecycleService.RequestChangesAsync(task.Id, Reviewer.Id, "Fix A");

        // Simulate agent fixing and resubmitting
        var entity = await _svc.Db.Tasks.FindAsync(task.Id);
        entity!.Status = nameof(TaskStatus.InReview);
        await _svc.Db.SaveChangesAsync();

        // Round 2: approve
        await _svc.TaskLifecycleService.ApproveTaskAsync(task.Id, Reviewer.Id, "Fixed");

        entity = await _svc.Db.Tasks.FindAsync(task.Id);
        Assert.Equal(2, entity!.ReviewRounds);
    }

    // ──────────────── REVIEWER NAME RESOLUTION ────────────────

    [Fact]
    public async Task ApproveTask_ResolvesReviewerNameFromCatalog()
    {
        var task = await CreateTaskAsync(nameof(TaskStatus.InReview), roomId: "room-5");
        await CreateRoomAsync("room-5");

        await _svc.TaskLifecycleService.ApproveTaskAsync(task.Id, Reviewer.Id, "Good work");

        var messages = await _svc.Db.Messages
            .Where(m => m.RoomId == "room-5" && m.Kind == nameof(MessageKind.Review))
            .ToListAsync();
        Assert.Contains("Athena", messages[0].Content);
    }

    [Fact]
    public async Task ApproveTask_UnknownReviewerId_FallsBackToId()
    {
        var task = await CreateTaskAsync(nameof(TaskStatus.InReview), roomId: "room-6");
        await CreateRoomAsync("room-6");

        await _svc.TaskLifecycleService.ApproveTaskAsync(task.Id, "unknown-agent", "OK");

        var messages = await _svc.Db.Messages
            .Where(m => m.RoomId == "room-6" && m.Kind == nameof(MessageKind.Review))
            .ToListAsync();
        Assert.Contains("unknown-agent", messages[0].Content);
    }

    // ──────────────── HELPERS ────────────────

    private async Task<TaskEntity> CreateTaskAsync(
        string status,
        string roomId = "main",
        int reviewRounds = 0,
        string? mergeCommitSha = null)
    {
        await EnsureRoomAsync(roomId);

        var entity = new TaskEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "Test Task",
            Description = "A test task",
            Status = status,
            RoomId = roomId,
            ReviewRounds = reviewRounds,
            MergeCommitSha = mergeCommitSha,
            CompletedAt = mergeCommitSha is not null ? DateTime.UtcNow : null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _svc.Db.Tasks.Add(entity);
        await _svc.Db.SaveChangesAsync();
        return entity;
    }

    private async Task EnsureRoomAsync(string roomId)
    {
        if (!await _svc.Db.Rooms.AnyAsync(r => r.Id == roomId))
        {
            _svc.Db.Rooms.Add(new RoomEntity
            {
                Id = roomId,
                Name = roomId,
                Status = "Active",
                CreatedAt = DateTime.UtcNow
            });
            await _svc.Db.SaveChangesAsync();
        }
    }

    private async Task CreateRoomAsync(string roomId) => await EnsureRoomAsync(roomId);

    public void Dispose() => _svc.Dispose();
}
