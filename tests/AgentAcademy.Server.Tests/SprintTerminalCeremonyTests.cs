using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for <see cref="SprintTerminalStageHandler"/> and the
/// terminal-stage ceremony chain: self-eval auto-fire, stage advancement on
/// AllPass, completion on SprintReport, watchdog stalls, sign-off-configured
/// environments, predicate alignment with
/// <c>RoomLifecycleService.TerminalTaskStatuses</c>, and the §6.4 force=true
/// consistency hardening. Covers acceptance criteria 1-9 from
/// <c>specs/100-product-vision/sprint-terminal-stage-handler-design.md §7</c>;
/// criterion 10 is the live supervised acceptance run gate.
/// </summary>
public sealed class SprintTerminalCeremonyTests : IDisposable
{
    private const string TestWorkspace = "/tmp/test-terminal-ceremony";

    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly SprintService _sprintService;
    private readonly SprintStageService _stageService;
    private readonly SprintArtifactService _artifactService;
    private readonly TaskQueryService _taskQueryService;
    private readonly IOrchestratorWakeService _wakeService;
    private readonly FakeTimeProvider _clock;
    private readonly TerminalStageOptions _options;

    public SprintTerminalCeremonyTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var dbOptions = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(dbOptions);
        _db.Database.EnsureCreated();

        var broadcaster = new ActivityBroadcaster();
        var settings = new SystemSettingsService(_db);
        _sprintService = new SprintService(
            _db, broadcaster, settings, NullLogger<SprintService>.Instance);
        _stageService = new SprintStageService(
            _db, broadcaster, NullLogger<SprintStageService>.Instance);
        _artifactService = new SprintArtifactService(
            _db, broadcaster, NullLogger<SprintArtifactService>.Instance);
        var dependencies = Substitute.For<ITaskDependencyService>();
        var catalog = Substitute.For<IAgentCatalog>();
        catalog.Agents.Returns(Array.Empty<AgentDefinition>());
        _taskQueryService = new TaskQueryService(
            _db, NullLogger<TaskQueryService>.Instance, catalog, dependencies);

        _wakeService = Substitute.For<IOrchestratorWakeService>();
        // Anchor the fake clock to system "now" so the few production paths
        // that still use DateTime.UtcNow (SprintStageService.AdvanceStageAsync
        // stamping FinalSynthesisEnteredAt; SprintArtifactService verdict path
        // stamping LastSelfEvalAt) line up with the driver's clock-driven
        // watchdog calculations to within milliseconds. Tests that need to
        // fast-forward use _clock.Advance(...).
        _clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _options = new TerminalStageOptions
        {
            FinalSynthesisStallMinutes = 30,
            SelfEvalStallMinutes = 15,
        };
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ─────────────────────────────────────────────────

    private SprintTerminalStageHandler CreateHandler(
        TerminalStageOptions? options = null,
        SprintStageService? stageService = null) =>
        new(
            _db,
            _sprintService,
            stageService ?? _stageService,
            _artifactService,
            _taskQueryService,
            _wakeService,
            NullLogger<SprintTerminalStageHandler>.Instance,
            Options.Create(options ?? _options),
            _clock);

    private async Task<SprintEntity> SeedImplementationSprintAsync()
    {
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        sprint.CurrentStage = "Implementation";
        await _db.SaveChangesAsync();
        return sprint;
    }

    private async Task SeedTaskAsync(string sprintId, Shared.Models.TaskStatus status, string title = "Task")
    {
        _db.Tasks.Add(new TaskEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = title,
            Description = "test",
            SuccessCriteria = "test",
            Status = status.ToString(),
            SprintId = sprintId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    private async Task SeedSelfEvalReportAsync(
        string sprintId, SelfEvaluationOverallVerdict verdict, DateTime? createdAt = null)
    {
        // Bypasses validation (which would require a matching non-cancelled
        // task set). The terminal-stage driver's predicates only inspect
        // OverallVerdict via the latest-artifact query, so a verdict-only
        // payload is sufficient for state classification tests.
        var content = $$"""{"OverallVerdict":"{{verdict}}"}""";
        _db.SprintArtifacts.Add(new SprintArtifactEntity
        {
            SprintId = sprintId,
            Stage = "Implementation",
            Type = nameof(ArtifactType.SelfEvaluationReport),
            Content = content,
            CreatedByAgentId = "test-agent",
            CreatedAt = createdAt ?? _clock.GetUtcNow().UtcDateTime,
        });
        await _db.SaveChangesAsync();
    }

    private async Task SeedSprintReportAsync(string sprintId)
    {
        _db.SprintArtifacts.Add(new SprintArtifactEntity
        {
            SprintId = sprintId,
            Stage = "FinalSynthesis",
            Type = nameof(ArtifactType.SprintReport),
            Content = "{}",
            CreatedByAgentId = "test-agent",
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        });
        await _db.SaveChangesAsync();
    }

    // ── Test 1: Happy path — auto-fires self-eval ───────────────

    [Fact]
    public async Task SelfEvalAutoFires_OnAllTasksTerminal()
    {
        var sprint = await SeedImplementationSprintAsync();
        await SeedTaskAsync(sprint.Id, Shared.Models.TaskStatus.Completed, "A");
        await SeedTaskAsync(sprint.Id, Shared.Models.TaskStatus.Completed, "B");

        var action = await CreateHandler().AdvanceIfReadyAsync(sprint.Id);

        Assert.Equal(TerminalStageAction.StartedSelfEval, action);

        _db.ChangeTracker.Clear();
        var refreshed = await _db.Sprints.FindAsync(sprint.Id);
        Assert.True(refreshed!.SelfEvaluationInFlight,
            "Driver must flip the in-flight flag to open the self-eval window.");
        Assert.NotNull(refreshed.SelfEvalStartedAt);

        await _wakeService.Received(1).WakeWorkspaceRoomsForSprintAsync(
            sprint.Id, Arg.Any<CancellationToken>());
    }

    // ── Test 2: AnyFail loop, then AllPass advances ─────────────

    [Fact]
    public async Task AdvancesToFinalSynthesis_OnAllPass()
    {
        var sprint = await SeedImplementationSprintAsync();
        await SeedTaskAsync(sprint.Id, Shared.Models.TaskStatus.Completed, "A");

        var handler = CreateHandler();

        // Step 1: driver fires self-eval.
        Assert.Equal(TerminalStageAction.StartedSelfEval,
            await handler.AdvanceIfReadyAsync(sprint.Id));

        // Step 2: AnyFail report stored. Simulate the verdict path's effect:
        // attempts++, in-flight=false, latest-verdict=AnyFail. Driver should
        // NoOp (it sees in-flight=false, no AllPass; it would re-fire, but
        // a real verdict path persists across the round, so for this test
        // we just confirm the AnyFail branch doesn't accidentally advance).
        _db.ChangeTracker.Clear();
        var s = await _db.Sprints.FindAsync(sprint.Id);
        s!.SelfEvaluationInFlight = false;
        s.SelfEvalAttempts = 1;
        s.LastSelfEvalVerdict = "AnyFail";
        await _db.SaveChangesAsync();
        await SeedSelfEvalReportAsync(sprint.Id, SelfEvaluationOverallVerdict.AnyFail);

        var anyFailAction = await handler.AdvanceIfReadyAsync(sprint.Id);
        // Driver sees AnyFail latest verdict, in-flight=false → re-fires
        // self-eval (open another attempt). This matches design intent: the
        // P1.4 verdict path re-opens implementation; driver auto-retries.
        Assert.Equal(TerminalStageAction.StartedSelfEval, anyFailAction);

        // Step 3: now seed an AllPass report (newer createdAt).
        _clock.Advance(TimeSpan.FromMinutes(1));
        await SeedSelfEvalReportAsync(sprint.Id, SelfEvaluationOverallVerdict.AllPass);

        var allPassAction = await handler.AdvanceIfReadyAsync(sprint.Id);
        Assert.Equal(TerminalStageAction.AdvancedToFinalSynthesis, allPassAction);

        _db.ChangeTracker.Clear();
        var advanced = await _db.Sprints.FindAsync(sprint.Id);
        Assert.Equal("FinalSynthesis", advanced!.CurrentStage);
        Assert.NotNull(advanced.FinalSynthesisEnteredAt);
    }

    // ── Test 3: Auto-completion ─────────────────────────────────

    [Fact]
    public async Task AutoCompletes_OnSprintReport()
    {
        var sprint = await SeedImplementationSprintAsync();
        await SeedTaskAsync(sprint.Id, Shared.Models.TaskStatus.Completed, "A");
        await SeedSelfEvalReportAsync(sprint.Id, SelfEvaluationOverallVerdict.AllPass);

        var handler = CreateHandler();

        // Advance to FinalSynthesis.
        Assert.Equal(TerminalStageAction.AdvancedToFinalSynthesis,
            await handler.AdvanceIfReadyAsync(sprint.Id));

        // No SprintReport yet — driver should steer (wake) without changing state.
        Assert.Equal(TerminalStageAction.SteeredToFinalSynthesis,
            await handler.AdvanceIfReadyAsync(sprint.Id));

        // Wake should have been called for both StartedAdvance + Steer.
        await _wakeService.Received(2).WakeWorkspaceRoomsForSprintAsync(
            sprint.Id, Arg.Any<CancellationToken>());

        // Now seed the report and the next invocation should complete.
        await SeedSprintReportAsync(sprint.Id);
        Assert.Equal(TerminalStageAction.CompletedSprint,
            await handler.AdvanceIfReadyAsync(sprint.Id));

        _db.ChangeTracker.Clear();
        var completed = await _db.Sprints.FindAsync(sprint.Id);
        Assert.Equal("Completed", completed!.Status);
        Assert.Equal("FinalSynthesis", completed.CurrentStage);
    }

    // ── Test 4: Cap-tripped sprint → NoOp, then resumes after unblock ──

    [Fact]
    public async Task DriverDefersTo_BlockedSprint_ThenResumes()
    {
        var sprint = await SeedImplementationSprintAsync();
        await SeedTaskAsync(sprint.Id, Shared.Models.TaskStatus.Completed, "A");

        // Simulate a cap-tripped sprint: BlockedAt set by SelfDriveDecisionService.
        _db.ChangeTracker.Clear();
        var s = await _db.Sprints.FindAsync(sprint.Id);
        s!.BlockedAt = _clock.GetUtcNow().UtcDateTime;
        s.BlockReason = "Stage round cap reached for Implementation: 20/20";
        await _db.SaveChangesAsync();

        var handler = CreateHandler();
        Assert.Equal(TerminalStageAction.NoOp, await handler.AdvanceIfReadyAsync(sprint.Id));

        // Operator unblocks: BlockedAt cleared.
        await _sprintService.UnblockSprintAsync(sprint.Id);

        // Now the driver should fire StartedSelfEval.
        Assert.Equal(TerminalStageAction.StartedSelfEval, await handler.AdvanceIfReadyAsync(sprint.Id));
    }

    // ── Test 5: FinalSynthesis stall watchdog ───────────────────

    [Fact]
    public async Task AutoBlocks_OnFinalSynthesisStall()
    {
        var sprint = await SeedImplementationSprintAsync();
        await SeedTaskAsync(sprint.Id, Shared.Models.TaskStatus.Completed, "A");
        await SeedSelfEvalReportAsync(sprint.Id, SelfEvaluationOverallVerdict.AllPass);

        var handler = CreateHandler();
        // Advance to FinalSynthesis (stamps FinalSynthesisEnteredAt).
        Assert.Equal(TerminalStageAction.AdvancedToFinalSynthesis,
            await handler.AdvanceIfReadyAsync(sprint.Id));

        // Fast-forward past the 30-min stall window.
        _clock.Advance(TimeSpan.FromMinutes(31));

        var stalled = await handler.AdvanceIfReadyAsync(sprint.Id);
        Assert.Equal(TerminalStageAction.Blocked, stalled);

        _db.ChangeTracker.Clear();
        var blocked = await _db.Sprints.FindAsync(sprint.Id);
        Assert.NotNull(blocked!.BlockedAt);
        Assert.Contains("SprintReport not produced within 30 minutes", blocked.BlockReason);
    }

    // ── Test 6: Self-eval stall watchdog ────────────────────────

    [Fact]
    public async Task AutoBlocks_OnSelfEvalStall()
    {
        var sprint = await SeedImplementationSprintAsync();
        await SeedTaskAsync(sprint.Id, Shared.Models.TaskStatus.Completed, "A");

        var handler = CreateHandler();
        // Fire self-eval.
        Assert.Equal(TerminalStageAction.StartedSelfEval,
            await handler.AdvanceIfReadyAsync(sprint.Id));

        // Fast-forward 16 minutes past the 15-minute window. SelfEvalStartedAt
        // is the baseline; LastSelfEvalAt is null (no report yet).
        _clock.Advance(TimeSpan.FromMinutes(16));

        var stalled = await handler.AdvanceIfReadyAsync(sprint.Id);
        Assert.Equal(TerminalStageAction.Blocked, stalled);

        _db.ChangeTracker.Clear();
        var blocked = await _db.Sprints.FindAsync(sprint.Id);
        Assert.NotNull(blocked!.BlockedAt);
        Assert.Contains("SelfEvaluationReport not produced within 15 minutes", blocked.BlockReason);
    }

    // ── Test 7: §6.4 force=true consistency hardening ──────────

    [Fact]
    public async Task ForceComplete_AdvancesCurrentStage_ToFinalSynthesis()
    {
        // Sprint at non-terminal stage (Intake). Force-complete via API.
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        Assert.Equal("Intake", sprint.CurrentStage);

        var completed = await _sprintService.CompleteSprintAsync(sprint.Id, force: true);

        Assert.Equal("Completed", completed.Status);
        Assert.Equal("FinalSynthesis", completed.CurrentStage);
        Assert.NotNull(completed.FinalSynthesisEnteredAt);
        Assert.Equal(completed.CompletedAt, completed.FinalSynthesisEnteredAt);
    }

    [Fact]
    public async Task ForceComplete_AtFinalSynthesis_PreservesExistingEnteredAt()
    {
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        sprint.CurrentStage = "FinalSynthesis";
        sprint.FinalSynthesisEnteredAt = _clock.GetUtcNow().UtcDateTime.AddHours(-2);
        await _db.SaveChangesAsync();

        var originalEnteredAt = sprint.FinalSynthesisEnteredAt;

        var completed = await _sprintService.CompleteSprintAsync(sprint.Id, force: true);

        Assert.Equal("FinalSynthesis", completed.CurrentStage);
        // Set-once invariant: existing value must be preserved.
        Assert.Equal(originalEnteredAt, completed.FinalSynthesisEnteredAt);
    }

    // ── Test 8: Approved is non-terminal (predicate alignment) ─

    [Fact]
    public async Task DoesNotFireSelfEval_OnApprovedNonTerminal()
    {
        var sprint = await SeedImplementationSprintAsync();
        await SeedTaskAsync(sprint.Id, Shared.Models.TaskStatus.Approved, "A");
        await SeedTaskAsync(sprint.Id, Shared.Models.TaskStatus.Approved, "B");

        var action = await CreateHandler().AdvanceIfReadyAsync(sprint.Id);

        // Locks the predicate to RoomLifecycleService.TerminalTaskStatuses
        // ({Completed, Cancelled} only). If a future change incorrectly
        // included Approved, this test would fail and surface the regression
        // before it reached a real sprint with PR-merge work in flight.
        Assert.Equal(TerminalStageAction.NoOp, action);
        await _wakeService.DidNotReceive().WakeWorkspaceRoomsForSprintAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Test 9: All-cancelled sprint → NoOp ──────────────────

    [Fact]
    public async Task DoesNotFireSelfEval_OnAllCancelled()
    {
        var sprint = await SeedImplementationSprintAsync();
        await SeedTaskAsync(sprint.Id, Shared.Models.TaskStatus.Cancelled, "A");
        await SeedTaskAsync(sprint.Id, Shared.Models.TaskStatus.Cancelled, "B");

        var action = await CreateHandler().AdvanceIfReadyAsync(sprint.Id);

        // NonCancelledCount == 0 — operator must explicitly force-complete.
        Assert.Equal(TerminalStageAction.NoOp, action);

        _db.ChangeTracker.Clear();
        var refreshed = await _db.Sprints.FindAsync(sprint.Id);
        Assert.False(refreshed!.SelfEvaluationInFlight);
    }

    // ── Test 10: Stale-state classifier ─────────────────────────

    [Fact]
    public async Task StaleStateRace_ReturnsNoOp_NotBlocked()
    {
        // Simulate the loser-of-the-race scenario: the driver invokes
        // AdvanceStageAsync but a concurrent invocation already moved the
        // sprint to FinalSynthesis. AdvanceStageAsync throws "already at the
        // final stage" InvalidOperationException — the stale-state classifier
        // must convert this to NoOp, NOT Blocked.
        var sprint = await SeedImplementationSprintAsync();
        await SeedTaskAsync(sprint.Id, Shared.Models.TaskStatus.Completed, "A");
        await SeedSelfEvalReportAsync(sprint.Id, SelfEvaluationOverallVerdict.AllPass);

        // Pre-advance to FinalSynthesis simulating the winner of the race.
        await _stageService.AdvanceStageAsync(sprint.Id, force: false);

        // Re-stamp the sprint back to Implementation in our local view, but
        // leave the DB at FinalSynthesis. Then call the driver — it loads
        // fresh state from DB so it sees FinalSynthesis. To force the stale
        // path, we instead test the classifier directly with a synthetic
        // exception that matches the stale-state predicate.
        var staleExceptions = new[]
        {
            new InvalidOperationException("Sprint X is already at the final stage."),
            new InvalidOperationException("Cannot advance — status is Completed."),
            new InvalidOperationException("Sprint Y is awaiting user sign-off."),
            new InvalidOperationException("Sprint Z is already Completed."),
        };
        foreach (var ex in staleExceptions)
        {
            Assert.True(SprintTerminalStageHandler.IsStaleStateException(ex),
                $"Expected stale-state classification for: {ex.Message}");
        }

        // Non-stale exceptions must NOT be classified as stale (would otherwise
        // mask real structural failures as silent NoOps).
        var nonStale = new[]
        {
            new InvalidOperationException("required artifact 'SelfEvaluationReport' has not been stored."),
            new InvalidOperationException("Sprint not found."),
        };
        foreach (var ex in nonStale)
        {
            Assert.False(SprintTerminalStageHandler.IsStaleStateException(ex),
                $"Expected NON-stale classification for: {ex.Message}");
        }
    }

    // ── Test 11: Sign-off-configured environment ────────────────

    [Fact]
    public async Task RequestsSignOff_WhenImplementationSignOffConfigured()
    {
        var sprint = await SeedImplementationSprintAsync();
        await SeedTaskAsync(sprint.Id, Shared.Models.TaskStatus.Completed, "A");
        await SeedSelfEvalReportAsync(sprint.Id, SelfEvaluationOverallVerdict.AllPass);

        // Build a stage service configured to require sign-off at Implementation.
        var stageWithSignOff = new SprintStageService(
            _db,
            new ActivityBroadcaster(),
            NullLogger<SprintStageService>.Instance,
            options: Options.Create(new SprintStageOptions
            {
                SignOffRequiredStages = new[] { "Implementation" },
            }));
        var handler = CreateHandler(stageService: stageWithSignOff);

        var action = await handler.AdvanceIfReadyAsync(sprint.Id);
        Assert.Equal(TerminalStageAction.RequestedSignOff, action);

        _db.ChangeTracker.Clear();
        var awaiting = await _db.Sprints.FindAsync(sprint.Id);
        Assert.True(awaiting!.AwaitingSignOff);
        Assert.Equal("Implementation", awaiting.CurrentStage);  // hasn't advanced yet
        Assert.Equal("FinalSynthesis", awaiting.PendingStage);

        // Subsequent invocation NoOps (NotApplicable predicate covers AwaitingSignOff).
        Assert.Equal(TerminalStageAction.NoOp, await handler.AdvanceIfReadyAsync(sprint.Id));
    }

    // ── Test 12: NotApplicable for non-Active sprints ───────────

    [Fact]
    public async Task NoOp_ForCompletedOrCancelledSprint()
    {
        var sprint = await _sprintService.CreateSprintAsync(TestWorkspace);
        sprint.CurrentStage = "FinalSynthesis";
        sprint.Status = "Completed";
        await _db.SaveChangesAsync();

        Assert.Equal(TerminalStageAction.NoOp,
            await CreateHandler().AdvanceIfReadyAsync(sprint.Id));

        await _wakeService.DidNotReceive().WakeWorkspaceRoomsForSprintAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Bonus: empty sprintId / unknown sprint → NoOp (fail-open) ──

    [Fact]
    public async Task NoOp_ForUnknownOrEmptySprint()
    {
        Assert.Equal(TerminalStageAction.NoOp,
            await CreateHandler().AdvanceIfReadyAsync(""));
        Assert.Equal(TerminalStageAction.NoOp,
            await CreateHandler().AdvanceIfReadyAsync("does-not-exist"));
    }

    // ── Bonus: SelfEvalStartedAt cleared on AllPass verdict path ─

    [Fact]
    public async Task SelfEvalStartedAt_ClearedOnAllPassViaVerdictPath()
    {
        var sprint = await SeedImplementationSprintAsync();
        await SeedTaskAsync(sprint.Id, Shared.Models.TaskStatus.Completed, "A");

        // Driver opens window — stamps SelfEvalStartedAt.
        Assert.Equal(TerminalStageAction.StartedSelfEval,
            await CreateHandler().AdvanceIfReadyAsync(sprint.Id));

        _db.ChangeTracker.Clear();
        var afterStart = await _db.Sprints.FindAsync(sprint.Id);
        Assert.NotNull(afterStart!.SelfEvalStartedAt);

        // Submit an AllPass report through the verdict path. The task IDs in
        // the report must match the sprint's non-cancelled tasks for
        // validation to pass. Re-load the task to copy its id + criteria.
        _db.ChangeTracker.Clear();
        var task = await _db.Tasks.FirstAsync(t => t.SprintId == sprint.Id);
        var report = $$"""
        {
          "Attempt": 1,
          "OverallVerdict": "AllPass",
          "Items": [
            {
              "TaskId": "{{task.Id}}",
              "SuccessCriteria": "{{task.SuccessCriteria}}",
              "Verdict": "PASS",
              "Evidence": "test"
            }
          ]
        }
        """;

        await _artifactService.StoreArtifactAsync(
            sprint.Id, "Implementation", nameof(ArtifactType.SelfEvaluationReport), report, "test-agent");

        _db.ChangeTracker.Clear();
        var afterVerdict = await _db.Sprints.FindAsync(sprint.Id);
        // SelfEvalStartedAt MUST be cleared on AllPass (driver stops watchdog
        // for the now-quiescent in-flight window — design §6.2).
        Assert.Null(afterVerdict!.SelfEvalStartedAt);
        // SelfEvaluationInFlight stays TRUE on AllPass — only ADVANCE_STAGE
        // out of Implementation clears it (SprintStageService.cs:229-233).
        // What changes between StartedSelfEval and AllPass is the verdict
        // gate becoming satisfied, not the in-flight flag.
        Assert.True(afterVerdict.SelfEvaluationInFlight);
        Assert.Equal("AllPass", afterVerdict.LastSelfEvalVerdict);
    }

    // ── Criterion 5: P1.4 cap deference (review-driven addition) ─

    [Fact]
    public async Task DriverDefersTo_P14_OnSelfEvalCapExceeded()
    {
        // Drives the full P1.4 cap flow through StoreArtifactAsync (3 AnyFail
        // attempts → P1.4 auto-blocks the sprint). Then asserts the driver
        // returns NoOp on the next invocation AND does NOT overwrite
        // BlockReason with the driver's "Terminal-stage ceremony failed: …"
        // message. Locks design §7 criterion 5: the driver must NEVER race
        // or override the P1.4 verdict path's cap-block.
        var sprint = await SeedImplementationSprintAsync();
        await SeedTaskAsync(sprint.Id, Shared.Models.TaskStatus.Completed, "A");

        var task = await _db.Tasks.FirstAsync(t => t.SprintId == sprint.Id);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            // Re-open the in-flight window (P1.4 verdict path clears it on
            // each AnyFail unless the cap blocks). On attempt 1 the driver-or-
            // operator path opens it; on attempts 2-3 we open via the handler-
            // equivalent direct flag flip so we don't depend on driver behaviour.
            _db.ChangeTracker.Clear();
            var s = await _db.Sprints.FindAsync(sprint.Id);
            if (!s!.SelfEvaluationInFlight)
            {
                s.SelfEvaluationInFlight = true;
                s.SelfEvalStartedAt = _clock.GetUtcNow().UtcDateTime;
                await _db.SaveChangesAsync();
            }
            _db.ChangeTracker.Clear();

            var report = $$"""
            {
              "Attempt": {{attempt}},
              "OverallVerdict": "AnyFail",
              "Items": [
                {
                  "TaskId": "{{task.Id}}",
                  "SuccessCriteria": "{{task.SuccessCriteria}}",
                  "Verdict": "FAIL",
                  "Evidence": "fail",
                  "FixPlan": "retry"
                }
              ]
            }
            """;
            await _artifactService.StoreArtifactAsync(
                sprint.Id, "Implementation", nameof(ArtifactType.SelfEvaluationReport),
                report, "test-agent");
        }

        _db.ChangeTracker.Clear();
        var blocked = await _db.Sprints.FindAsync(sprint.Id);
        Assert.NotNull(blocked!.BlockedAt);
        Assert.Equal(3, blocked.SelfEvalAttempts);
        Assert.Contains("Self-eval failed", blocked.BlockReason);
        var preDriverReason = blocked.BlockReason;

        // Driver must defer: NoOp, no overwrite of BlockReason, no double-block.
        Assert.Equal(TerminalStageAction.NoOp,
            await CreateHandler().AdvanceIfReadyAsync(sprint.Id));

        _db.ChangeTracker.Clear();
        var afterDriver = await _db.Sprints.FindAsync(sprint.Id);
        Assert.Equal(preDriverReason, afterDriver!.BlockReason);
        Assert.DoesNotContain("Terminal-stage ceremony failed", afterDriver.BlockReason);
    }

    // ── Criterion 9: SelfDriveDecisionService skipped when driver acts ─

    [Fact]
    public async Task SelfDriveDecisionSkipped_WhenDriverActed_StartedSelfEval()
    {
        var sprint = await SeedImplementationSprintAsync();
        await SeedTaskAsync(sprint.Id, Shared.Models.TaskStatus.Completed, "A");

        // Construct a real ConversationRoundRunner with substituted dependencies
        // so the conditional self-drive skip in the wiring (§4.4) is exercised
        // end-to-end. The substituted ISprintTerminalStageHandler returns
        // StartedSelfEval, and the substituted ISelfDriveDecisionService must
        // receive zero calls.
        await using var harness = await BuildRunnerHarnessAsync(returns: TerminalStageAction.StartedSelfEval);

        await harness.Runner.RunRoundsAsync(harness.RoomId);

        await harness.SelfDrive.DidNotReceive().DecideAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<RoundRunOutcome>(), Arg.Any<CancellationToken>());
        await harness.Handler.Received(1).AdvanceIfReadyAsync(
            sprint.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SelfDriveDecisionRuns_WhenDriverNoOp()
    {
        var sprint = await SeedImplementationSprintAsync();
        await SeedTaskAsync(sprint.Id, Shared.Models.TaskStatus.Active, "A");  // not terminal — handler returns NoOp

        await using var harness = await BuildRunnerHarnessAsync(returns: TerminalStageAction.NoOp);

        await harness.Runner.RunRoundsAsync(harness.RoomId);

        // Self-drive must run when the driver took no action — preserves
        // the existing per-round behaviour.
        await harness.SelfDrive.Received(1).DecideAsync(
            Arg.Any<string>(), sprint.Id, Arg.Any<RoundRunOutcome>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SelfDriveDecisionSkipped_WhenDriverActed_Blocked()
    {
        // Even on Block — design §4.4: "if the driver took ANY action
        // (including Block), self-drive is skipped". Skipping after Block is
        // safety: a self-drive enqueue on a now-blocked sprint would no-op
        // anyway, but skipping makes the intent explicit.
        var sprint = await SeedImplementationSprintAsync();
        await SeedTaskAsync(sprint.Id, Shared.Models.TaskStatus.Completed, "A");

        await using var harness = await BuildRunnerHarnessAsync(returns: TerminalStageAction.Blocked);

        await harness.Runner.RunRoundsAsync(harness.RoomId);

        await harness.SelfDrive.DidNotReceive().DecideAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<RoundRunOutcome>(), Arg.Any<CancellationToken>());
    }

    // ── Test harness for ConversationRoundRunner skip wiring ────

    private sealed record RunnerHarness(
        ConversationRoundRunner Runner,
        ISprintTerminalStageHandler Handler,
        ISelfDriveDecisionService SelfDrive,
        string RoomId,
        Microsoft.Extensions.DependencyInjection.ServiceProvider Provider) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }

    private async Task<RunnerHarness> BuildRunnerHarnessAsync(TerminalStageAction returns)
    {
        // Build a minimal DI container that wires:
        //   - the real ConversationRoundRunner
        //   - a stub IAgentTurnRunner that does nothing (round count = 0 for
        //     handler-no-action paths, but >= 1 when a planner is present —
        //     we only care about the wiring branch, not agent dispatch).
        //   - a substituted ISprintTerminalStageHandler that returns the
        //     requested TerminalStageAction
        //   - a substituted ISelfDriveDecisionService whose DecideAsync we
        //     assert against.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));

        var catalog = Substitute.For<IAgentCatalog>();
        catalog.Agents.Returns(new[]
        {
            new AgentDefinition(
                Id: "planner-1", Name: "Planner", Role: "Planner",
                Summary: "P", StartupPrompt: "P", Model: null,
                CapabilityTags: new List<string> { "planning" }, EnabledTools: new List<string> { "chat" },
                AutoJoinDefaultRoom: true)
        });
        services.AddSingleton(catalog);

        var turnRunner = Substitute.For<IAgentTurnRunner>();
        turnRunner.RunAgentTurnAsync(
            Arg.Any<AgentDefinition>(), Arg.Any<IServiceScope>(),
            Arg.Any<IMessageService>(), Arg.Any<IAgentConfigService>(),
            Arg.Any<IActivityPublisher>(), Arg.Any<RoomSnapshot>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<TaskItem>?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => new AgentTurnResult(call.Arg<AgentDefinition>(), "PASS", IsNonPass: false));

        var selfDrive = Substitute.For<ISelfDriveDecisionService>();

        // Domain services registers the real SprintTerminalStageHandler and
        // ISelfDriveDecisionService — we MUST register substitutes AFTER so
        // our registrations win.
        services.AddDomainServices();
        services.AddSingleton<IActivityBroadcaster>(new ActivityBroadcaster());
        services.AddSingleton<IMessageBroadcaster>(new MessageBroadcaster());
        var executor = Substitute.For<IAgentExecutor>();
        executor.IsFullyOperational.Returns(true);
        services.AddSingleton(executor);
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddSingleton(new SpecManager(
            Path.Combine(Path.GetTempPath(), $"runner-{Guid.NewGuid()}"),
            NullLogger<SpecManager>.Instance));
        services.AddSingleton<ISpecManager>(sp => sp.GetRequiredService<SpecManager>());
        services.AddSingleton<AgentAcademy.Server.Services.AgentWatchdog.IWatchdogAgentRunner>(
            sp => new TestDoubles.NoOpWatchdogAgentRunner(sp.GetRequiredService<IAgentExecutor>()));

        // Override-substitutes — registered last so they win against AddDomainServices.
        var handler = Substitute.For<ISprintTerminalStageHandler>();
        handler.AdvanceIfReadyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(returns);
        services.AddScoped(_ => handler);
        services.AddSingleton(selfDrive);

        var provider = services.BuildServiceProvider();

        // Seed a room linked to the workspace so the runner can drive rounds.
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            var roomId = "ceremony-test-room";
            if (!db.Rooms.Any(r => r.Id == roomId))
            {
                db.Rooms.Add(new RoomEntity
                {
                    Id = roomId,
                    Name = "Main",
                    WorkspacePath = TestWorkspace,
                    Status = nameof(RoomStatus.Active),
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }
        }

        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var runner = new ConversationRoundRunner(
            scopeFactory,
            new AgentCatalogOptions(
                DefaultRoomId: "ceremony-test-room",
                DefaultRoomName: "Main",
                Agents: catalog.Agents.ToList()),
            turnRunner,
            NullLogger<ConversationRoundRunner>.Instance,
            selfDrive);

        return new RunnerHarness(runner, handler, selfDrive, "ceremony-test-room", provider);
    }
}
