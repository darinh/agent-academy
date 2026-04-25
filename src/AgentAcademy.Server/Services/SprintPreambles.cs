using System.Collections.ObjectModel;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Provides sprint stage preambles (injected into agent prompts) and
/// agent roster rules (which roles participate in each stage).
/// </summary>
public static class SprintPreambles
{
    /// <summary>
    /// Roles allowed per sprint stage. Agents whose Role is not in the set
    /// for the current stage are excluded from conversation rounds.
    /// A null value means all roles are allowed.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> StageRosters =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["Intake"] = new HashSet<string>(StringComparer.Ordinal) { "Planner" },
            ["Planning"] = new HashSet<string>(StringComparer.Ordinal) { "Planner", "Architect" },
            // Discussion: all except Reviewer (Reviewer joins at Validation)
            ["Discussion"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "Planner", "Architect", "SoftwareEngineer", "TechnicalWriter"
            },
            // Validation, Implementation, FinalSynthesis: all roles (permissive default)
        };

    /// <summary>
    /// Stage-specific instruction preambles injected into agent prompts.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> StagePreambles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Intake"] = """
                === SPRINT STAGE: INTAKE ===
                You are gathering requirements for this sprint. Your goal is to produce a
                RequirementsDocument artifact that clearly defines what needs to be built,
                success criteria, and constraints. Ask clarifying questions. Do not propose
                solutions yet — focus on understanding the problem space.

                **HOW TO ADVANCE (Planner only — Aristotle):**
                1. When the human has provided enough information that requirements are clear
                   (or explicitly says to proceed), synthesize them into a RequirementsDocument.
                2. Run: `STORE_ARTIFACT: Type=RequirementsDocument Content=<the document>`
                3. Then run: `ADVANCE_STAGE:` — this requires human sign-off; wait for approval.
                Do not ask permission to draft the requirements — drafting IS your job. Only the
                ADVANCE_STAGE call needs the artifact gate satisfied first.
                """,

            ["Planning"] = """
                === SPRINT STAGE: PLANNING ===
                You are planning the sprint. Using the RequirementsDocument from Intake,
                produce a SprintPlan artifact that breaks work into concrete tasks with
                assignments, dependencies, and risk assessments. Consider architecture
                implications and identify unknowns that need resolution.

                **HOW TO ADVANCE (Planner only — Aristotle):**
                1. Collaborate with the team on task breakdown, then synthesize the plan.
                2. Run: `STORE_ARTIFACT: Type=SprintPlan Content=<plan including task list,
                   assignments, deps, risks>`
                3. Then run: `ADVANCE_STAGE:` — this requires human sign-off.
                """,

            ["Discussion"] = """
                === SPRINT STAGE: DISCUSSION ===
                You are in open discussion about the sprint plan. Debate trade-offs,
                challenge assumptions, identify risks, and refine the approach. All
                perspectives matter — raise concerns now before implementation begins.
                No code review happens at this stage.

                **HOW TO ADVANCE (Planner only — Aristotle):**
                When discussion has converged (no new objections in the last round, or all
                raised risks have an owner / mitigation in the plan), run: `ADVANCE_STAGE:`.
                No artifact is required to leave Discussion; no human sign-off is required.
                """,

            ["Validation"] = """
                === SPRINT STAGE: VALIDATION ===
                You are validating the sprint plan before implementation. Review the plan
                for completeness, feasibility, and alignment with requirements. Produce
                a ValidationReport artifact summarizing findings. Flag any blockers.
                This is the last gate before code is written.

                **HOW TO ADVANCE (Planner only — Aristotle):**
                1. Once reviewers have weighed in, synthesize the findings.
                2. Run: `STORE_ARTIFACT: Type=ValidationReport Content=<findings, blockers,
                   go/no-go recommendation>`
                3. Then run: `ADVANCE_STAGE:` to enter Implementation. No human sign-off.
                """,

            ["Implementation"] = """
                === SPRINT STAGE: IMPLEMENTATION ===
                You are implementing the sprint plan. Work through tasks systematically.

                **Workflow:**
                1. The Planner creates tasks using CREATE_TASK for each planned work item.
                2. SoftwareEngineers work in task branches (created automatically).
                3. Before starting significant work, create a goal card (CREATE_GOAL_CARD)
                   to capture your intent, approach, and any divergence from the task description.
                4. When a task is code-complete, the SWE runs CREATE_PR to push and open a PR.
                   Goal card content is automatically included in the PR description.
                5. The Reviewer uses POST_PR_REVIEW (APPROVE / REQUEST_CHANGES) to review PRs.
                6. On approval, use MERGE_PR to merge, then mark the task complete.

                **PR review cycle:**
                - If REQUEST_CHANGES, the SWE addresses feedback and requests re-review.
                - Maximum 5 review rounds per task before escalation.
                - The Reviewer should use GET_PR_REVIEWS to track review history.

                **Rules:**
                - Follow the validated SprintPlan — deviations must be discussed first.
                - Each task should have tests. Use CHECK_GATES to verify evidence.
                - The Planner coordinates priorities and unblocks dependencies.
                - All code goes through PR review before merging.

                **HOW TO ADVANCE (Planner only — Aristotle):**
                When all planned tasks are merged (or explicitly deferred to overflow), run
                `ADVANCE_STAGE:` to enter FinalSynthesis. No artifact gate; no human sign-off.
                """,

            ["FinalSynthesis"] = """
                === SPRINT STAGE: FINAL SYNTHESIS ===
                The sprint is wrapping up. Review what was accomplished against the
                original requirements. Produce a SprintReport artifact summarizing
                outcomes, lessons learned, and any overflow items that should carry
                to the next sprint. If work remains, create an OverflowRequirements
                artifact.

                **HOW TO COMPLETE (Planner only — Aristotle):**
                1. If incomplete work exists, run:
                   `STORE_ARTIFACT: Type=OverflowRequirements Content=<remaining items>`
                2. Run: `STORE_ARTIFACT: Type=SprintReport Content=<outcomes, lessons,
                   overflow summary>`
                3. Then run: `COMPLETE_SPRINT:` to finalize. The next sprint will auto-start
                   if scheduling is enabled and will inherit any overflow you stored.
                """,

            ["ImplementationSelfEval"] = """
                === SPRINT STAGE: IMPLEMENTATION — SELF-EVALUATION ===
                A self-evaluation has been opened (RUN_SELF_EVAL). Implementation work is
                paused until you submit a SelfEvaluationReport.

                **What you must do (Planner only — Aristotle):**
                1. Enumerate every non-cancelled task in this sprint (LIST_TASKS).
                2. For EACH task, write one Items[] entry verifying the task's success
                   criterion against concrete evidence:
                   - `TaskId` — the task ID
                   - `SuccessCriteria` — copied verbatim from the task (case-sensitive,
                     whitespace-significant; reworded criteria are rejected)
                   - `Verdict` — PASS (concrete evidence the criterion is met) /
                     FAIL (criterion not met) / UNVERIFIED (plausibly met, no evidence)
                   - `Evidence` — the proof: PR #s, test names, file:line references,
                     command output, sample data. NO claims without artifacts.
                   - `FixPlan` — required when Verdict ≠ PASS. What's missing and how to fix it.
                3. Compute `OverallVerdict` as the rollup:
                   AllPass (every PASS) | AnyFail (any FAIL) | Unverified (no FAIL but ≥1 UNVERIFIED).
                4. Set `Attempt` = (sprint.SelfEvalAttempts + 1) — must monotonically increment.
                5. Submit:
                   ```
                   STORE_ARTIFACT:
                     Type: SelfEvaluationReport
                     Content: { "Attempt": N, "Items": [...], "OverallVerdict": "...", "Notes": "..." }
                   ```

                **What happens next:**
                - AllPass → you may run `ADVANCE_STAGE:` to enter FinalSynthesis.
                - AnyFail / Unverified, attempts < {MaxSelfEvalAttempts} → window re-opens; fix the gaps,
                  resume Implementation, then run `RUN_SELF_EVAL` again when ready.
                - AnyFail / Unverified at attempt {MaxSelfEvalAttempts} → sprint is auto-blocked for
                  human input.

                Be honest. UNVERIFIED with a clear FixPlan beats PASS without evidence — humans review
                this and aspirational PASS verdicts are a P0 trust violation.
                """,
        };

    /// <summary>
    /// Returns the preamble text for a sprint stage, including sprint number
    /// and any prior-stage context summaries.
    /// </summary>
    /// <param name="selfEvaluationInFlight">
    /// When true and <paramref name="stage"/> is <c>Implementation</c>, the
    /// self-evaluation preamble is selected instead of the regular
    /// Implementation preamble. Default false for backward compatibility with
    /// test fixtures that build preambles directly.
    /// </param>
    /// <param name="maxSelfEvalAttempts">
    /// Configured cap (<c>Orchestrator:SelfEval:MaxSelfEvalAttempts</c>).
    /// Token-interpolated into the self-eval preamble so the agent sees the
    /// exact attempt budget.
    /// </param>
    public static string BuildPreamble(
        int sprintNumber,
        string stage,
        IReadOnlyList<(string Stage, string Summary)>? priorStageContext = null,
        string? overflowContent = null,
        bool selfEvaluationInFlight = false,
        int maxSelfEvalAttempts = 3)
    {
        var lines = new List<string>
        {
            $"=== SPRINT #{sprintNumber} ===",
        };

        // Self-eval window flips Implementation into a different ceremony.
        var preambleKey =
            (selfEvaluationInFlight && string.Equals(stage, "Implementation", StringComparison.Ordinal))
                ? "ImplementationSelfEval"
                : stage;

        if (StagePreambles.TryGetValue(preambleKey, out var preamble))
        {
            var rendered = preamble.Trim();
            if (preambleKey == "ImplementationSelfEval")
                rendered = rendered.Replace("{MaxSelfEvalAttempts}", maxSelfEvalAttempts.ToString());
            lines.Add(rendered);
        }

        if (overflowContent is not null && stage == "Intake")
        {
            lines.Add("");
            lines.Add("=== OVERFLOW FROM PREVIOUS SPRINT ===");
            lines.Add("The following requirements were not completed in the previous sprint");
            lines.Add("and must be addressed in this sprint's intake:");
            lines.Add(overflowContent);
        }

        if (priorStageContext is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("=== PRIOR STAGE CONTEXT ===");
            foreach (var (priorStage, summary) in priorStageContext)
            {
                lines.Add($"--- {priorStage} ---");
                lines.Add(summary);
            }
        }

        lines.Add("");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Returns true if the given agent role is allowed to participate
    /// in the specified sprint stage. Returns true for unknown stages
    /// (permissive default).
    /// </summary>
    public static bool IsRoleAllowedInStage(string role, string stage)
    {
        if (!StageRosters.TryGetValue(stage, out var allowedRoles))
            return true; // Validation, Implementation, FinalSynthesis: all roles
        return allowedRoles.Contains(role);
    }

    /// <summary>
    /// Filters a list of agents to only those allowed in the given stage.
    /// </summary>
    public static List<T> FilterByStageRoster<T>(
        IEnumerable<T> agents, string stage, Func<T, string> roleSelector)
    {
        return agents.Where(a => IsRoleAllowedInStage(roleSelector(a), stage)).ToList();
    }
}
