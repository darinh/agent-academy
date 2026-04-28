using AgentAcademy.Server.Services;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for SprintPreambles — stage preamble generation and roster filtering.
/// </summary>
public class SprintPreamblesTests
{
    // ── BuildPreamble ────────────────────────────────────────────

    [Theory]
    [InlineData("Intake")]
    [InlineData("Planning")]
    [InlineData("Discussion")]
    [InlineData("Validation")]
    [InlineData("Implementation")]
    [InlineData("FinalSynthesis")]
    public void BuildPreamble_ContainsStageHeader(string stage)
    {
        var preamble = SprintPreambles.BuildPreamble(1, stage);

        Assert.Contains("SPRINT #1", preamble);
        Assert.Contains(stage.ToUpperInvariant().Replace("FINALSYNTHESIS", "FINAL SYNTHESIS"), preamble);
    }

    [Fact]
    public void BuildPreamble_IncludesPriorContext()
    {
        var priorContext = new List<(string Stage, string Summary)>
        {
            ("Intake", "Requirements gathered"),
            ("Planning", "Tasks planned"),
        };

        var preamble = SprintPreambles.BuildPreamble(2, "Discussion", priorContext);

        Assert.Contains("PRIOR STAGE CONTEXT", preamble);
        Assert.Contains("--- Intake ---", preamble);
        Assert.Contains("Requirements gathered", preamble);
        Assert.Contains("--- Planning ---", preamble);
        Assert.Contains("Tasks planned", preamble);
    }

    [Fact]
    public void BuildPreamble_NoPriorContextSection_WhenEmpty()
    {
        var preamble = SprintPreambles.BuildPreamble(1, "Intake");

        Assert.DoesNotContain("PRIOR STAGE CONTEXT", preamble);
    }

    [Fact]
    public void BuildPreamble_NoPriorContextSection_WhenNull()
    {
        var preamble = SprintPreambles.BuildPreamble(1, "Intake", null);

        Assert.DoesNotContain("PRIOR STAGE CONTEXT", preamble);
    }

    [Fact]
    public void BuildPreamble_UnknownStage_StillIncludesSprintHeader()
    {
        var preamble = SprintPreambles.BuildPreamble(5, "UnknownStage");

        Assert.Contains("SPRINT #5", preamble);
    }

    // ── IsRoleAllowedInStage ─────────────────────────────────────

    [Theory]
    [InlineData("Planner", "Intake", true)]
    [InlineData("Architect", "Intake", false)]
    [InlineData("SoftwareEngineer", "Intake", false)]
    [InlineData("Reviewer", "Intake", false)]
    [InlineData("TechnicalWriter", "Intake", false)]
    public void IsRoleAllowedInStage_Intake(string role, string stage, bool expected)
    {
        Assert.Equal(expected, SprintPreambles.IsRoleAllowedInStage(role, stage));
    }

    [Theory]
    [InlineData("Planner", "Planning", true)]
    [InlineData("Architect", "Planning", true)]
    [InlineData("SoftwareEngineer", "Planning", false)]
    [InlineData("Reviewer", "Planning", false)]
    public void IsRoleAllowedInStage_Planning(string role, string stage, bool expected)
    {
        Assert.Equal(expected, SprintPreambles.IsRoleAllowedInStage(role, stage));
    }

    [Theory]
    [InlineData("Planner", "Discussion", true)]
    [InlineData("Architect", "Discussion", true)]
    [InlineData("SoftwareEngineer", "Discussion", true)]
    [InlineData("TechnicalWriter", "Discussion", true)]
    [InlineData("Reviewer", "Discussion", false)]
    public void IsRoleAllowedInStage_Discussion(string role, string stage, bool expected)
    {
        Assert.Equal(expected, SprintPreambles.IsRoleAllowedInStage(role, stage));
    }

    [Theory]
    [InlineData("Planner", "Validation", true)]
    [InlineData("Reviewer", "Validation", true)]
    [InlineData("SoftwareEngineer", "Validation", true)]
    public void IsRoleAllowedInStage_Validation_AllAllowed(string role, string stage, bool expected)
    {
        Assert.Equal(expected, SprintPreambles.IsRoleAllowedInStage(role, stage));
    }

    [Theory]
    [InlineData("Planner", "Implementation", true)]
    [InlineData("Reviewer", "Implementation", true)]
    [InlineData("SoftwareEngineer", "Implementation", true)]
    public void IsRoleAllowedInStage_Implementation_AllAllowed(string role, string stage, bool expected)
    {
        Assert.Equal(expected, SprintPreambles.IsRoleAllowedInStage(role, stage));
    }

    [Theory]
    [InlineData("Planner", "FinalSynthesis", true)]
    [InlineData("Reviewer", "FinalSynthesis", true)]
    public void IsRoleAllowedInStage_FinalSynthesis_AllAllowed(string role, string stage, bool expected)
    {
        Assert.Equal(expected, SprintPreambles.IsRoleAllowedInStage(role, stage));
    }

    [Fact]
    public void IsRoleAllowedInStage_UnknownStage_AllowsAll()
    {
        Assert.True(SprintPreambles.IsRoleAllowedInStage("Reviewer", "UnknownStage"));
        Assert.True(SprintPreambles.IsRoleAllowedInStage("Planner", "UnknownStage"));
    }

    // ── FilterByStageRoster ──────────────────────────────────────

    private record TestAgent(string Id, string Role);

    [Fact]
    public void FilterByStageRoster_IntakeOnlyAllowsPlanner()
    {
        var agents = new List<TestAgent>
        {
            new("planner-1", "Planner"),
            new("architect-1", "Architect"),
            new("swe-1", "SoftwareEngineer"),
            new("reviewer-1", "Reviewer"),
        };

        var filtered = SprintPreambles.FilterByStageRoster(agents, "Intake", a => a.Role);

        Assert.Single(filtered);
        Assert.Equal("planner-1", filtered[0].Id);
    }

    [Fact]
    public void FilterByStageRoster_DiscussionExcludesReviewer()
    {
        var agents = new List<TestAgent>
        {
            new("planner-1", "Planner"),
            new("architect-1", "Architect"),
            new("swe-1", "SoftwareEngineer"),
            new("reviewer-1", "Reviewer"),
            new("writer-1", "TechnicalWriter"),
        };

        var filtered = SprintPreambles.FilterByStageRoster(agents, "Discussion", a => a.Role);

        Assert.Equal(4, filtered.Count);
        Assert.DoesNotContain(filtered, a => a.Role == "Reviewer");
    }

    [Fact]
    public void FilterByStageRoster_ValidationAllowsAll()
    {
        var agents = new List<TestAgent>
        {
            new("planner-1", "Planner"),
            new("reviewer-1", "Reviewer"),
            new("swe-1", "SoftwareEngineer"),
        };

        var filtered = SprintPreambles.FilterByStageRoster(agents, "Validation", a => a.Role);

        Assert.Equal(3, filtered.Count);
    }

    [Fact]
    public void FilterByStageRoster_EmptyInput_ReturnsEmpty()
    {
        var filtered = SprintPreambles.FilterByStageRoster(
            Array.Empty<TestAgent>(), "Intake", a => a.Role);

        Assert.Empty(filtered);
    }

    // ── Overflow preamble ────────────────────────────────────────

    [Fact]
    public void BuildPreamble_IncludesOverflowContent_AtIntake()
    {
        var overflow = """{"items": ["unfinished feature"]}""";

        var preamble = SprintPreambles.BuildPreamble(2, "Intake", null, overflow);

        Assert.Contains("OVERFLOW FROM PREVIOUS SPRINT", preamble);
        Assert.Contains("unfinished feature", preamble);
    }

    [Fact]
    public void BuildPreamble_IgnoresOverflow_AtOtherStages()
    {
        var overflow = """{"items": ["unfinished feature"]}""";

        var preamble = SprintPreambles.BuildPreamble(2, "Planning", null, overflow);

        Assert.DoesNotContain("OVERFLOW FROM PREVIOUS SPRINT", preamble);
    }

    [Fact]
    public void BuildPreamble_NoOverflowSection_WhenNull()
    {
        var preamble = SprintPreambles.BuildPreamble(1, "Intake", null, null);

        Assert.DoesNotContain("OVERFLOW FROM PREVIOUS SPRINT", preamble);
    }

    // ── Goal card workflow in Implementation ─────────────────────

    [Fact]
    public void BuildPreamble_Implementation_IncludesGoalCardWorkflow()
    {
        var preamble = SprintPreambles.BuildPreamble(1, "Implementation");

        Assert.Contains("CREATE_GOAL_CARD", preamble);
        Assert.Contains("goal card", preamble.ToLowerInvariant());
    }

    [Fact]
    public void BuildPreamble_Implementation_GoalCardBeforePR()
    {
        var preamble = SprintPreambles.BuildPreamble(1, "Implementation");

        var goalCardIndex = preamble.IndexOf("CREATE_GOAL_CARD");
        var createPrIndex = preamble.IndexOf("CREATE_PR");

        Assert.True(goalCardIndex > -1, "Implementation preamble should mention CREATE_GOAL_CARD");
        Assert.True(createPrIndex > goalCardIndex, "Goal card step should come before CREATE_PR step");
    }

    [Fact]
    public void BuildPreamble_Implementation_GoalCardAutoIncludedInPR()
    {
        var preamble = SprintPreambles.BuildPreamble(1, "Implementation");

        Assert.Contains("Goal card content is automatically included in the PR description", preamble);
    }

    // ── P1.4 ceremony lifecycle gap closure ──────────────────────
    // Regression: roadmap line 216 (filed 2026-04-26 from Sprint #2 audit)
    // diagnosed three failure modes that prevented the P1.4 self-evaluation
    // ceremony from ever firing:
    //   (1) UPDATE_TASK status=Completed → VALIDATION (terminal status rejected)
    //   (2) APPROVE_TASK with invented slug ID → NOT_FOUND
    //   (3) MERGE_TASK exit 1 (consequence of (2) — no diff to squash)
    //
    // Both halves of the prescribed fix shipped in PR #157 (commit 9665209,
    // 2026-04-25): the Implementation preamble now spells out the full
    // lifecycle command sequence with explicit warnings, AND
    // TaskWriteToolWrapper.CreateTaskAsync returns "- ID: {GUID}" as the
    // second response line so the LLM can echo it back. Live verification
    // (Sprint #14, 200 records, post-#157) shows zero recurrences of the
    // three failure modes.
    //
    // These tests lock in the closure. If any of them fail, a future
    // preamble edit has silently regressed the fix — re-read PR #157 and
    // roadmap line 216 before changing the assertion.

    [Fact]
    public void BuildPreamble_Implementation_LifecycleClosure_ContainsFullStateDiagram()
    {
        // The full state diagram MUST appear contiguously so agents read
        // it as a single coherent flow, not just see the words scattered
        // across the preamble. This is the contract — if a future edit
        // breaks the diagram into pieces or reorders the states,
        // assertion fails.
        var preamble = SprintPreambles.BuildPreamble(1, "Implementation");

        Assert.Contains(
            "Queued → Active → (InReview ⟷ AwaitingValidation) → Approved → Completed",
            preamble);
    }

    [Fact]
    public void BuildPreamble_Implementation_LifecycleClosure_RejectsCompletedStatusUpdate()
    {
        // Failure mode (1): the preamble must explicitly tell agents that
        // UPDATE_TASK status=Completed is invalid and will be rejected.
        // Anchoring on the full warning sentence (not just three loose
        // substrings that could co-occur in a *positive* example) so a
        // future edit that softens the warning fails this test.
        var preamble = SprintPreambles.BuildPreamble(1, "Implementation");

        Assert.Contains(
            "⚠️ `UPDATE_TASK status=Completed` is **NOT VALID** and will be rejected.",
            preamble);
        // The "do not retry" guidance must follow — this is the line that
        // breaks the planner out of the retry loop observed in Sprint #2.
        Assert.Contains(
            "Do not retry with status=Completed; advance the",
            preamble);
        Assert.Contains(
            "status to `InReview` (or `AwaitingValidation`) and continue to step 4.",
            preamble);
    }

    [Fact]
    public void BuildPreamble_Implementation_LifecycleClosure_ForbidsInventedSlugIds()
    {
        // Failure mode (2): the preamble must explicitly tell the planner
        // to capture the GUID returned by create_task and to NEVER invent
        // slug-style IDs from the title.
        var preamble = SprintPreambles.BuildPreamble(1, "Implementation");

        Assert.Contains("- ID: <GUID>", preamble);
        Assert.Contains("Do NOT invent slug IDs", preamble);
        Assert.Contains("Slug IDs derived from titles will not resolve", preamble);
    }

    [Fact]
    public void BuildPreamble_Implementation_LifecycleClosure_StepOrderingIsCorrect()
    {
        // Failure mode (3) was a downstream consequence of (2), but the
        // proximate cure is the correct command ordering. The preamble
        // enumerates the lifecycle in six numbered steps; their headers
        // must appear in dependency order:
        //   1. Create (create_task) → 2. Claim (CLAIM_TASK)
        //   → 3. Work the task (UPDATE_TASK + CREATE_PR)
        //   → 4. Review the PR → 5. Approve the task (APPROVE_TASK)
        //   → 6. Merge (MERGE_PR / MERGE_TASK)
        // Anchoring on the numbered step headers (not raw first-occurrence
        // of each verb) avoids false positives from the step-3 warning
        // block, which forward-references MERGE_PR by design.
        var preamble = SprintPreambles.BuildPreamble(1, "Implementation");

        var step1 = preamble.IndexOf("**1. Create", System.StringComparison.Ordinal);
        var step2 = preamble.IndexOf("**2. Claim", System.StringComparison.Ordinal);
        var step3 = preamble.IndexOf("**3. Work the task", System.StringComparison.Ordinal);
        var step4 = preamble.IndexOf("**4. Review the PR", System.StringComparison.Ordinal);
        var step5 = preamble.IndexOf("**5. Approve the task", System.StringComparison.Ordinal);
        var step6 = preamble.IndexOf("**6. Merge", System.StringComparison.Ordinal);

        Assert.True(step1 >= 0, "step 1 (Create) header must be present");
        Assert.True(step2 > step1, "step 2 (Claim) must follow step 1");
        Assert.True(step3 > step2, "step 3 (Work the task) must follow step 2");
        Assert.True(step4 > step3, "step 4 (Review the PR) must follow step 3");
        Assert.True(step5 > step4, "step 5 (Approve the task) must follow step 4");
        Assert.True(step6 > step5, "step 6 (Merge) must follow step 5");

        // The step-1 body must reference create_task; step-5 body must
        // reference APPROVE_TASK; step-6 body must reference MERGE_PR.
        // These verb-in-section assertions guard against a future edit
        // that keeps the headers but rewrites the bodies into a different
        // sequence.
        var step1Body = preamble.Substring(step1, step2 - step1);
        var step5Body = preamble.Substring(step5, step6 - step5);
        var step6Body = preamble.Substring(step6);

        Assert.Contains("create_task", step1Body);
        Assert.Contains("APPROVE_TASK", step5Body);
        Assert.Contains("MERGE_PR", step6Body);
    }

    // ── STORE_ARTIFACT JSON schema visibility ────────────────────
    // Regression: Sprint #14 stalled in Intake because the planner kept
    // submitting markdown for RequirementsDocument / SprintPlan /
    // ValidationReport / SprintReport, which the validator rejects with
    // VALIDATION errors. The Intake/Planning/Validation/FinalSynthesis
    // preambles must surface the actual JSON schema (not free-form
    // `Content=<the document>`) so agents can produce a valid payload
    // on the first attempt.
    //
    // The expected JSON strings below are the EXACT schema fragments
    // returned by SprintArtifactService.GetSchemaHint. If GetSchemaHint
    // changes, update both — the test exists to keep the preamble
    // contract and the validator contract synchronised.

    [Theory]
    [InlineData(
        "Intake",
        "RequirementsDocument",
        """{"Title":"...","Description":"...","InScope":["...","..."],"OutOfScope":["...","..."]}""")]
    [InlineData(
        "Planning",
        "SprintPlan",
        """{"Summary":"...","Phases":[{"Name":"...","Description":"...","Deliverables":["...","..."]}],"OverflowRequirements":["..."]}""")]
    [InlineData(
        "Validation",
        "ValidationReport",
        """{"Verdict":"...","Findings":["...","..."],"RequiredChanges":["..."]}""")]
    [InlineData(
        "FinalSynthesis",
        "SprintReport",
        """{"Summary":"...","Delivered":["...","..."],"Learnings":["...","..."],"OverflowRequirements":["..."]}""")]
    public void BuildPreamble_StoreArtifactStages_ShowExactJsonSchema(
        string stage, string artifactType, string expectedSchema)
    {
        var preamble = SprintPreambles.BuildPreamble(1, stage);

        Assert.Contains($"Type: {artifactType}", preamble);
        Assert.Contains(expectedSchema, preamble);
        Assert.Contains("valid JSON", preamble);
        // Guard against the regression that started this fix: free-form
        // "Content=<the document>" / "Content=<plan ...>" instructions
        // must NOT appear, otherwise agents fall back to markdown.
        Assert.DoesNotContain("Content=<", preamble);
    }

    [Fact]
    public void BuildPreamble_FinalSynthesis_OverflowRequirementsRemainsFreeForm()
    {
        // OverflowRequirements is the one artifact the validator does NOT
        // schema-check (SprintArtifactService.ValidateArtifactContent
        // returns early). The preamble must say so, otherwise the planner
        // wastes rounds trying to JSON-encode plain prose.
        var preamble = SprintPreambles.BuildPreamble(1, "FinalSynthesis");

        Assert.Contains("Type: OverflowRequirements", preamble);
        Assert.Contains("free-form", preamble);
    }

    // ── Runtime tool availability (Sprint #16 agent-runtime hallucination
    // closure, 2026-04-28) ───────────────────────────────────────────────
    //
    // Failure mode: software-engineer-1 (Hephaestus) claimed the runtime
    // did not expose bash/shell after a successful CLAIM_TASK, despite
    // PR #174's SDK-builtin exclusion being intentional. The agent never
    // attempted any structured equivalent (RUN_BUILD, RUN_TESTS,
    // write_file SDK tool) and never escalated the gap as a specific
    // blocker; instead it stalled. 26 rounds of analysis-paralysis
    // followed (cap-tripped 20/20 then 26/20). See the "Agent runtime
    // tool-availability hallucination" Proposed Addition row in
    // specs/100-product-vision/roadmap.md (PR #196).
    //
    // The Implementation preamble now includes a RUNTIME TOOL AVAILABILITY
    // section that (a) names the excluded SDK builtins so the agent
    // recognises their absence is by design, (b) maps each common need
    // to its structured equivalent, and (c) directs the agent to use
    // MARK_BLOCKED with a specific structured-command request rather than
    // declaring the runtime broken. These tests lock in the closure.

    [Fact]
    public void BuildPreamble_Implementation_RuntimeToolAvailability_NamesSdkExclusionAsByDesign()
    {
        var preamble = SprintPreambles.BuildPreamble(1, "Implementation");

        // The header must be present so the agent reads this as a discrete
        // section before the lifecycle.
        Assert.Contains("RUNTIME TOOL AVAILABILITY", preamble);

        // The excluded builtins must be named explicitly. If a future edit
        // softens this list, the agent loses the recognition signal that
        // their absence is expected.
        Assert.Contains("`bash`", preamble);
        Assert.Contains("`shell`", preamble);
        Assert.Contains("`view`", preamble);
        Assert.Contains("`apply_patch`", preamble);
        // Per-tool exclusions added after adversarial review (gpt-5.3-codex,
        // claude-opus-4.6, gpt-5.5) — the per-agent list and the preamble
        // list MUST stay aligned, otherwise an engineer that knows
        // create_file/str_replace_editor exist will treat their absence as
        // a runtime fault.
        Assert.Contains("`create_file`", preamble);
        Assert.Contains("`str_replace_editor`", preamble);
        Assert.Contains("`task`", preamble);

        // The "by design" framing is the load-bearing phrase — without it
        // the agent treats absence as a runtime fault.
        Assert.Contains("by design", preamble);
    }

    [Fact]
    public void BuildPreamble_Implementation_RuntimeToolAvailability_DoesNotListWriteFileAsExcluded()
    {
        // Regression guard against the day-zero contradiction surfaced by
        // adversarial review (Opus + GPT-5.5): the preamble previously
        // listed `write_file` as a NOT-exposed SDK builtin AND told the
        // engineer to use the registered `write_file` SDK tool to write
        // files. The model would treat the first statement as authoritative
        // and stall on file writes. The registered custom `write_file` tool
        // shadows the SDK builtin via ResolveExcludedSdkTools (see
        // CopilotExecutor.cs), so write_file IS available to engineers.
        var preamble = SprintPreambles.BuildPreamble(1, "Implementation");

        // Isolate ONLY the "The SDK does NOT expose ..." sentence (the
        // excluded-builtins enumeration). Subsequent paragraphs may
        // legitimately reference write_file as the available registered
        // tool — that's the whole point of the disambiguation.
        var notExposedStart = preamble.IndexOf("The SDK does NOT expose", StringComparison.Ordinal);
        var notExposedEnd = preamble.IndexOf("by design", notExposedStart, StringComparison.Ordinal);
        Assert.True(notExposedStart > -1 && notExposedEnd > notExposedStart,
            "Could not isolate the 'NOT expose ... by design' sentence");

        var excludedSentence = preamble.Substring(notExposedStart, notExposedEnd - notExposedStart);
        Assert.DoesNotContain("`write_file`", excludedSentence);

        // But write_file MUST still appear elsewhere (mapping table or
        // the explanatory note) as the registered tool engineers should
        // use for file writes.
        var afterExcludedSentence = preamble.Substring(notExposedEnd);
        Assert.Contains("`write_file`", afterExcludedSentence);
    }

    [Fact]
    public void BuildPreamble_Implementation_RuntimeToolAvailability_MapsNeedsToStructuredCommands()
    {
        var preamble = SprintPreambles.BuildPreamble(1, "Implementation");

        // Each common need must have its structured equivalent named so
        // the agent has a positive path forward, not just a list of
        // forbidden tools.
        Assert.Contains("RUN_BUILD", preamble);
        Assert.Contains("RUN_TESTS", preamble);
        Assert.Contains("read_file", preamble);
        Assert.Contains("write_file", preamble);
        Assert.Contains("COMMIT_CHANGES", preamble);
        Assert.Contains("SEARCH_CODE", preamble);
        // Added after adversarial review (gpt-5.3-codex): the prompt
        // advertises SHOW_DIFF and GIT_LOG in its mapping table — assert
        // them so a future regression that drops either is caught.
        Assert.Contains("SHOW_DIFF", preamble);
        Assert.Contains("GIT_LOG", preamble);
        // Frontend-specific commands are also in the mapping table after
        // the codex review — they must remain so frontend engineers
        // (Athena) have a structured path that doesn't fall back to bash.
        Assert.Contains("RUN_FRONTEND_BUILD", preamble);
        Assert.Contains("RUN_TYPECHECK", preamble);
    }

    [Fact]
    public void BuildPreamble_Implementation_RuntimeToolAvailability_DirectsAgentToMarkBlockedNotStall()
    {
        var preamble = SprintPreambles.BuildPreamble(1, "Implementation");

        // The escalation directive — an agent that needs a capability not
        // covered by the structured surface must surface MARK_BLOCKED
        // with a specific request, not declare the runtime broken.
        Assert.Contains("MARK_BLOCKED", preamble);
        Assert.Contains("platform gap", preamble);

        // Anti-pattern explicitly forbidden so the model recognises the
        // exact phrasing it produced in Sprint #16 as wrong.
        Assert.Contains("I do not have a bash/shell command tool in this runtime", preamble);
    }

    [Fact]
    public void BuildPreamble_Implementation_RuntimeToolAvailability_MarkBlockedExampleUsesMultiLineForm()
    {
        // Regression guard against a parser-truncation bug surfaced by
        // adversarial review (gpt-5.5 + gpt-5.3-codex). The inline command
        // parser uses regex `(\w+)=(\S+)` which captures values only up to
        // the first whitespace — so the inline form
        //   `MARK_BLOCKED: taskId=X reason=<long sentence>`
        // would parse `reason="<long"` and silently drop the actionable
        // text. The prompt must therefore use the block form.
        var preamble = SprintPreambles.BuildPreamble(1, "Implementation");

        // The block form: `MARK_BLOCKED:\n  taskId: <...>\n  reason: <...>`
        // must appear contiguously so the agent copies it as a unit.
        Assert.Contains("MARK_BLOCKED:\n", preamble);
        Assert.Contains("taskId: <your-task-GUID>", preamble);
        Assert.Contains("reason: <", preamble);

        // The inline form with `reason=` (key=value, where the parser would
        // truncate) must NOT appear as the recommended example.
        Assert.DoesNotContain("MARK_BLOCKED: taskId=", preamble);
    }

    [Fact]
    public void BuildPreamble_Implementation_RuntimeToolAvailability_PrecedesTaskLifecycle()
    {
        // Ordering matters: the agent must read RUNTIME TOOL AVAILABILITY
        // before TASK LIFECYCLE, otherwise a CLAIM_TASK -> "no bash"
        // stall can fire before the runtime guidance is in context.
        var preamble = SprintPreambles.BuildPreamble(1, "Implementation");

        var runtimeIdx = preamble.IndexOf("RUNTIME TOOL AVAILABILITY", StringComparison.Ordinal);
        var lifecycleIdx = preamble.IndexOf("TASK LIFECYCLE", StringComparison.Ordinal);

        Assert.True(runtimeIdx > -1, "Implementation preamble must contain RUNTIME TOOL AVAILABILITY");
        Assert.True(lifecycleIdx > -1, "Implementation preamble must contain TASK LIFECYCLE");
        Assert.True(runtimeIdx < lifecycleIdx,
            $"RUNTIME TOOL AVAILABILITY (at {runtimeIdx}) must precede TASK LIFECYCLE (at {lifecycleIdx})");
    }
}
