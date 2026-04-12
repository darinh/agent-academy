using System.Text.RegularExpressions;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Pure parsing logic for structured agent response formats: work reports,
/// review verdicts, task assignments, agent tags, and message classification.
/// All methods are static and side-effect-free.
/// </summary>
internal static class AgentResponseParser
{
    /// <summary>Cap on the number of agents that can be tagged in one round.</summary>
    internal const int MaxTaggedAgents = 6;

    // ── WORK REPORTS ────────────────────────────────────────────

    /// <summary>
    /// Parses a WORK REPORT: block from agent response text.
    /// Returns null if no work report block is found.
    /// </summary>
    internal static ParsedWorkReport? ParseWorkReport(string content)
    {
        var match = Regex.Match(content, @"WORK REPORT:([\s\S]*?)(?=$|\nTASK ASSIGNMENT:)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var block = match.Groups[1].Value;
        var statusMatch = Regex.Match(block, @"Status:\s*(.+)", RegexOptions.IgnoreCase);
        var filesMatch = Regex.Match(block, @"Files?:\s*([\s\S]*?)(?=Evidence:|$)", RegexOptions.IgnoreCase);
        var evidenceMatch = Regex.Match(block, @"Evidence:\s*([\s\S]*?)$", RegexOptions.IgnoreCase);

        var files = new List<string>();
        if (filesMatch.Success)
        {
            foreach (var line in filesMatch.Groups[1].Value.Split('\n'))
            {
                var trimmed = Regex.Replace(line, @"^[-*]\s*", "").Trim();
                if (!string.IsNullOrEmpty(trimmed)) files.Add(trimmed);
            }
        }

        return new ParsedWorkReport(
            Status: statusMatch.Success ? statusMatch.Groups[1].Value.Trim() : "unknown",
            Files: files,
            Evidence: evidenceMatch.Success ? evidenceMatch.Groups[1].Value.Trim() : "");
    }

    // ── REVIEW VERDICTS ─────────────────────────────────────────

    /// <summary>
    /// Parses a REVIEW: block from reviewer response text.
    /// Returns null if no review block is found.
    /// </summary>
    internal static ParsedReviewVerdict? ParseReviewVerdict(string content)
    {
        var match = Regex.Match(content, @"REVIEW:([\s\S]*?)$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var block = match.Groups[1].Value;
        var verdictMatch = Regex.Match(block, @"(?:Verdict|Status|Decision):\s*(.+)", RegexOptions.IgnoreCase);
        var findingsMatch = Regex.Match(block, @"(?:Findings?|Issues?|Comments?):\s*([\s\S]*?)$", RegexOptions.IgnoreCase);

        var findings = new List<string>();
        if (findingsMatch.Success)
        {
            foreach (var line in findingsMatch.Groups[1].Value.Split('\n'))
            {
                var trimmed = Regex.Replace(line, @"^[-*]\s*", "").Trim();
                if (!string.IsNullOrEmpty(trimmed)) findings.Add(trimmed);
            }
        }

        return new ParsedReviewVerdict(
            Verdict: verdictMatch.Success
                ? verdictMatch.Groups[1].Value.Trim()
                : block.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "",
            Findings: findings);
    }

    // ── TASK ASSIGNMENTS ────────────────────────────────────────

    /// <summary>
    /// Parses all TASK ASSIGNMENT: blocks from planner response text.
    /// Returns an empty list if no valid assignments are found.
    /// </summary>
    internal static List<ParsedTaskAssignment> ParseTaskAssignments(string content)
    {
        var assignments = new List<ParsedTaskAssignment>();

        var blocks = Regex.Split(content, @"TASK ASSIGNMENT:", RegexOptions.IgnoreCase);
        foreach (var block in blocks.Skip(1))
        {
            var agentMatch = Regex.Match(block, @"Agent:\s*@?(\S+)", RegexOptions.IgnoreCase);
            var titleMatch = Regex.Match(block, @"Title:\s*(.+)", RegexOptions.IgnoreCase);
            var descMatch = Regex.Match(block, @"Description:\s*([\s\S]*?)(?=Acceptance Criteria:|Type:|TASK ASSIGNMENT:|$)", RegexOptions.IgnoreCase);
            var criteriaMatch = Regex.Match(block, @"Acceptance Criteria:\s*([\s\S]*?)(?=Type:|TASK ASSIGNMENT:|$)", RegexOptions.IgnoreCase);
            var typeMatch = Regex.Match(block, @"Type:\s*(\S+)", RegexOptions.IgnoreCase);

            if (!agentMatch.Success || !titleMatch.Success) continue;

            var criteria = new List<string>();
            if (criteriaMatch.Success)
            {
                foreach (var line in criteriaMatch.Groups[1].Value.Split('\n'))
                {
                    var trimmed = Regex.Replace(line, @"^[-*]\s*", "").Trim();
                    if (!string.IsNullOrEmpty(trimmed)) criteria.Add(trimmed);
                }
            }

            var taskType = TaskType.Feature;
            if (typeMatch.Success)
                Enum.TryParse(typeMatch.Groups[1].Value.Trim(), ignoreCase: true, out taskType);

            assignments.Add(new ParsedTaskAssignment(
                Agent: agentMatch.Groups[1].Value.Trim(),
                Title: titleMatch.Groups[1].Value.Trim(),
                Description: descMatch.Success ? descMatch.Groups[1].Value.Trim() : titleMatch.Groups[1].Value.Trim(),
                Criteria: criteria,
                Type: taskType));
        }

        return assignments;
    }

    // ── AGENT TAGGING ───────────────────────────────────────────

    /// <summary>
    /// Scans response text for @mentions of known agents by name or ID.
    /// Returns up to <see cref="MaxTaggedAgents"/> matched agents.
    /// </summary>
    internal static List<AgentDefinition> ParseTaggedAgents(
        IReadOnlyList<AgentDefinition> allAgents, string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return [];

        var tagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AgentDefinition>();

        foreach (var agent in allAgents)
        {
            if (tagged.Contains(agent.Id)) continue;

            var namePattern = $@"@?{Regex.Escape(agent.Name)}\b";
            var idPattern = $@"@?{Regex.Escape(agent.Id)}\b";

            if (Regex.IsMatch(response, namePattern, RegexOptions.IgnoreCase) ||
                Regex.IsMatch(response, idPattern, RegexOptions.IgnoreCase))
            {
                tagged.Add(agent.Id);
                result.Add(agent);
            }
        }

        return result.Take(MaxTaggedAgents).ToList();
    }

    // ── RESPONSE CLASSIFICATION ─────────────────────────────────

    /// <summary>
    /// Detects "pass" responses — short responses that indicate the agent
    /// has nothing meaningful to contribute.
    /// </summary>
    internal static bool IsPassResponse(string response)
    {
        var trimmed = response.Trim();
        return trimmed.Length < 30 &&
               (Regex.IsMatch(trimmed, @"PASS", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(trimmed, @"^N/A$", RegexOptions.IgnoreCase) ||
                trimmed == "No comment." ||
                trimmed == "Nothing to add.");
    }

    /// <summary>
    /// Detects StubExecutor offline responses. When the Copilot SDK is not
    /// connected, retrying will produce the same result — abort early instead
    /// of burning through all breakout/review rounds.
    /// </summary>
    internal static bool IsStubOfflineResponse(string response) =>
        response.Contains("is offline — the Copilot SDK is not connected", StringComparison.Ordinal);

    /// <summary>
    /// Maps an agent's role to the appropriate <see cref="MessageKind"/>.
    /// </summary>
    internal static MessageKind InferMessageKind(string role) => role switch
    {
        "Planner" => MessageKind.Coordination,
        "Architect" => MessageKind.Decision,
        "SoftwareEngineer" => MessageKind.Response,
        "Reviewer" => MessageKind.Review,
        "Validator" => MessageKind.Validation,
        "TechnicalWriter" => MessageKind.SpecChangeProposal,
        _ => MessageKind.Response,
    };
}

// ── Data Transfer Records ───────────────────────────────────────

/// <summary>A parsed TASK ASSIGNMENT: block.</summary>
internal record ParsedTaskAssignment(
    string Agent,
    string Title,
    string Description,
    List<string> Criteria,
    TaskType Type = TaskType.Feature);

/// <summary>A parsed WORK REPORT: block.</summary>
internal record ParsedWorkReport(
    string Status,
    List<string> Files,
    string Evidence);

/// <summary>A parsed REVIEW: verdict block.</summary>
internal record ParsedReviewVerdict(
    string Verdict,
    List<string> Findings);
