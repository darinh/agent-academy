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
            // Validation through FinalSynthesis: all roles
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
                """,

            ["Planning"] = """
                === SPRINT STAGE: PLANNING ===
                You are planning the sprint. Using the RequirementsDocument from Intake,
                produce a SprintPlan artifact that breaks work into concrete tasks with
                assignments, dependencies, and risk assessments. Consider architecture
                implications and identify unknowns that need resolution.
                """,

            ["Discussion"] = """
                === SPRINT STAGE: DISCUSSION ===
                You are in open discussion about the sprint plan. Debate trade-offs,
                challenge assumptions, identify risks, and refine the approach. All
                perspectives matter — raise concerns now before implementation begins.
                No code review happens at this stage.
                """,

            ["Validation"] = """
                === SPRINT STAGE: VALIDATION ===
                You are validating the sprint plan before implementation. Review the plan
                for completeness, feasibility, and alignment with requirements. Produce
                a ValidationReport artifact summarizing findings. Flag any blockers.
                This is the last gate before code is written.
                """,

            ["Implementation"] = """
                === SPRINT STAGE: IMPLEMENTATION ===
                You are implementing the sprint plan. Work through tasks systematically.
                The planner coordinates, software engineers write code in their worktrees,
                and the reviewer performs code review on completed work. Follow the plan —
                deviations should be discussed before acting on them.
                """,

            ["FinalSynthesis"] = """
                === SPRINT STAGE: FINAL SYNTHESIS ===
                The sprint is wrapping up. Review what was accomplished against the
                original requirements. Produce a SprintReport artifact summarizing
                outcomes, lessons learned, and any overflow items that should carry
                to the next sprint. If work remains, create an OverflowRequirements
                artifact.
                """,
        };

    /// <summary>
    /// Returns the preamble text for a sprint stage, including sprint number
    /// and any prior-stage context summaries.
    /// </summary>
    public static string BuildPreamble(
        int sprintNumber,
        string stage,
        IReadOnlyList<(string Stage, string Summary)>? priorStageContext = null)
    {
        var lines = new List<string>
        {
            $"=== SPRINT #{sprintNumber} ===",
        };

        if (StagePreambles.TryGetValue(stage, out var preamble))
            lines.Add(preamble.Trim());

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
