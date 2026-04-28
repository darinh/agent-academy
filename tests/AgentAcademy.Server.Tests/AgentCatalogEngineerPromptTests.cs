using System.Text.Json;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests that lock in the per-agent StartupPrompt language for the
/// SoftwareEngineer role (Hephaestus + Athena) added 2026-04-28 to close
/// the agent-runtime tool-availability hallucination surfaced by Sprint #16.
///
/// Failure mode (Sprint #16, room
/// <c>add-changelog-md-entry-for-pr-192-terminal-stage-ceremony-driver-c747e7de</c>):
/// software-engineer-1 reported <c>"I do not have a bash/shell command tool in
/// this runtime"</c> after a successful CLAIM_TASK provisioned the worktree.
/// PR #174's <c>SessionConfig.ExcludedTools</c> intentionally excludes the SDK
/// builtins; the agent treated that exclusion as a runtime fault rather than a
/// directive to use the structured-command surface (RUN_BUILD / RUN_TESTS /
/// READ_FILE / write_file). 26 rounds of analysis-paralysis followed
/// (cap-tripped 20/20 then 26/20) and the deliverable never landed. Peer agents
/// (Aristotle, Socrates) confirmed the worktree was healthy. Diagnostic
/// hypothesis was filed in the "Agent runtime tool-availability hallucination"
/// Proposed Addition row in <c>specs/100-product-vision/roadmap.md</c> by the
/// Sprint #16 evidence-record PR (PR #196 — pending human merge at the time
/// this fix landed).
///
/// The fix has two surfaces, both tested:
/// <list type="number">
///   <item>Per-agent StartupPrompt (this file): a "Runtime Tool Availability"
///         section in software-engineer-1 + software-engineer-2 prompts that
///         names the excluded SDK builtins, maps each common need to its
///         structured equivalent, and directs MARK_BLOCKED escalation.</item>
///   <item>Sprint Implementation preamble (SprintPreamblesTests.cs): the same
///         guidance injected into every Implementation-stage round.</item>
/// </list>
///
/// Defence in depth: the per-agent prompt covers non-sprint task work; the
/// preamble covers sprint Implementation regardless of agent identity.
///
/// Reviewer findings (2026-04-28, three independent reviewers — gpt-5.3-codex,
/// claude-opus-4.6, gpt-5.5) shaped the test surface:
/// <list type="bullet">
///   <item>Opus / GPT-5.5: the preamble must NOT list <c>write_file</c> as
///         excluded (it shadows the SDK builtin via ResolveExcludedSdkTools)
///         — covered by <c>EngineerPrompt_DoesNotListWriteFileAsExcludedBuiltin</c>.</item>
///   <item>GPT-5.5 / Codex: the inline <c>MARK_BLOCKED: taskId=X reason=...</c>
///         form truncates <c>reason</c> at the first whitespace
///         (<c>CommandParser.ParseInlineArgs</c> uses regex
///         <c>(\w+)=(\S+)</c>) — the prompt must use the block form
///         (<c>EngineerPrompt_MarkBlockedExampleUsesMultiLineForm</c>).</item>
///   <item>Codex: Athena's Verify step must reference frontend commands
///         (<c>RUN_FRONTEND_BUILD</c>, <c>RUN_TYPECHECK</c>) — the original
///         <c>RUN_BUILD</c> only runs <c>dotnet build</c>
///         (<c>AthenaVerifyStep_ReferencesFrontendStructuredCommands</c>).</item>
///   <item>Codex: <c>SHOW_DIFF</c> and <c>GIT_LOG</c> are advertised in the
///         mapping table — assert them so a regression that drops either is
///         caught (<c>EngineerPrompt_MapsCommonNeedsToStructuredCommands</c>).</item>
/// </list>
/// </summary>
public class AgentCatalogEngineerPromptTests
{
    private static JsonElement LoadEngineerPrompt(string agentId)
    {
        var repoRoot = FindRepoRoot();
        var agentsJsonPath = Path.Combine(repoRoot, "src", "AgentAcademy.Server", "Config", "agents.json");
        Assert.True(File.Exists(agentsJsonPath), $"agents.json not found at {agentsJsonPath}");

        using var stream = File.OpenRead(agentsJsonPath);
        var doc = JsonDocument.Parse(stream);
        var agents = doc.RootElement.GetProperty("AgentCatalog").GetProperty("Agents");

        foreach (var agent in agents.EnumerateArray())
        {
            if (agent.GetProperty("Id").GetString() == agentId)
                return agent.Clone();
        }

        throw new InvalidOperationException($"Agent '{agentId}' not found in agents.json");
    }

    [Theory]
    [InlineData("software-engineer-1")]
    [InlineData("software-engineer-2")]
    public void EngineerPrompt_ContainsRuntimeToolAvailabilitySection(string agentId)
    {
        var prompt = LoadEngineerPrompt(agentId).GetProperty("StartupPrompt").GetString() ?? "";

        // The section header must be present so the engineer reads this as
        // a discrete contract, not a passing remark.
        Assert.Contains("Runtime Tool Availability", prompt);
    }

    [Theory]
    [InlineData("software-engineer-1")]
    [InlineData("software-engineer-2")]
    public void EngineerPrompt_NamesExcludedSdkBuiltins(string agentId)
    {
        // The engineer must recognise the names the SDK reports as missing
        // so it does not mistake their absence for a runtime fault. If a
        // future edit collapses this list to something vague ("some shell
        // tools") the model loses the recognition signal.
        var prompt = LoadEngineerPrompt(agentId).GetProperty("StartupPrompt").GetString() ?? "";

        Assert.Contains("`bash`", prompt);
        Assert.Contains("`shell`", prompt);
        Assert.Contains("`view`", prompt);
        Assert.Contains("`apply_patch`", prompt);
        Assert.Contains("`write_bash`", prompt);
        // Per-tool exclusions added after adversarial review (Opus): the
        // per-agent and preamble lists must stay aligned with the actual
        // ExcludedSdkBuiltinTools array in CopilotExecutor.cs.
        Assert.Contains("`create_file`", prompt);
        Assert.Contains("`str_replace_editor`", prompt);
        Assert.Contains("`task`", prompt);

        // The "by design" framing is load-bearing — without it the model
        // treats absence as a fault.
        Assert.Contains("by design", prompt);
    }

    [Theory]
    [InlineData("software-engineer-1")]
    [InlineData("software-engineer-2")]
    public void EngineerPrompt_DoesNotListWriteFileAsExcludedBuiltin(string agentId)
    {
        // Regression guard: write_file is registered as a custom SDK tool
        // and shadows the excluded builtin via ResolveExcludedSdkTools
        // (CopilotExecutor.cs). Listing write_file as "NOT exposed" would
        // cause the engineer to stall on file writes — the very class of
        // failure this PR closes for shell tools. The prompt must list it
        // ONLY as an available tool, not in the exclusion list.
        var prompt = LoadEngineerPrompt(agentId).GetProperty("StartupPrompt").GetString() ?? "";

        // Locate the excluded-builtins paragraph (the "## Runtime Tool
        // Availability" section's first paragraph) and assert write_file
        // is not in it.
        var sectionStart = prompt.IndexOf("## Runtime Tool Availability", StringComparison.Ordinal);
        Assert.True(sectionStart > -1, "Runtime Tool Availability section missing");

        // The paragraph ends before the first "\n\n" after the section
        // header. We use the literal "Their absence" anchor that follows
        // the excluded-builtins sentence as the paragraph terminator.
        var paragraphEnd = prompt.IndexOf("Their absence is", sectionStart, StringComparison.Ordinal);
        Assert.True(paragraphEnd > sectionStart,
            "Could not isolate the excluded-builtins paragraph");

        var excludedParagraph = prompt.Substring(sectionStart, paragraphEnd - sectionStart);
        Assert.DoesNotContain("`write_file`", excludedParagraph);
    }

    [Theory]
    [InlineData("software-engineer-1")]
    [InlineData("software-engineer-2")]
    public void EngineerPrompt_MapsCommonNeedsToStructuredCommands(string agentId)
    {
        // Each shell-style need must have a positive structured replacement
        // named so the engineer has a concrete next step, not just a list
        // of forbidden tools.
        var prompt = LoadEngineerPrompt(agentId).GetProperty("StartupPrompt").GetString() ?? "";

        Assert.Contains("RUN_BUILD", prompt);
        Assert.Contains("RUN_TESTS", prompt);
        Assert.Contains("read_file", prompt);
        Assert.Contains("write_file", prompt);
        Assert.Contains("COMMIT_CHANGES", prompt);
        Assert.Contains("SEARCH_CODE", prompt);
        // Added after adversarial review (gpt-5.3-codex): SHOW_DIFF and
        // GIT_LOG are advertised in the mapping table — assert them so a
        // future regression that drops either is caught at CI.
        Assert.Contains("SHOW_DIFF", prompt);
        Assert.Contains("GIT_LOG", prompt);
    }

    [Theory]
    [InlineData("software-engineer-1")]
    [InlineData("software-engineer-2")]
    public void EngineerPrompt_DirectsMarkBlockedRatherThanRuntimeBrokenReport(string agentId)
    {
        // The escalation directive — when a structured equivalent really is
        // missing, surface it as a specific MARK_BLOCKED with a precise
        // request, not "the runtime is broken".
        var prompt = LoadEngineerPrompt(agentId).GetProperty("StartupPrompt").GetString() ?? "";

        Assert.Contains("MARK_BLOCKED", prompt);
        Assert.Contains("platform gap", prompt);

        // The exact anti-pattern phrasing the agent produced in Sprint #16
        // is named so the model recognises it as forbidden, not just as
        // generally undesirable.
        Assert.Contains(
            "I do not have a bash/shell command tool in this runtime",
            prompt);
    }

    [Theory]
    [InlineData("software-engineer-1")]
    [InlineData("software-engineer-2")]
    public void EngineerPrompt_MarkBlockedExampleUsesMultiLineForm(string agentId)
    {
        // Regression guard against parser truncation surfaced by adversarial
        // review (gpt-5.5 + gpt-5.3-codex). The inline-args parser
        // (CommandParser.ParseInlineArgs, regex `(\w+)=(\S+)`) captures
        // values only up to the first whitespace. The inline form
        //   MARK_BLOCKED: taskId=X reason=<long sentence>
        // would parse `reason="<long"` and silently lose the actionable
        // text the engineer is supposed to provide. The prompt's example
        // MUST therefore use the block form so the parser captures the
        // entire reason line.
        var prompt = LoadEngineerPrompt(agentId).GetProperty("StartupPrompt").GetString() ?? "";

        Assert.Contains("MARK_BLOCKED:\n", prompt);
        Assert.Contains("taskId: <your-task-GUID>", prompt);
        Assert.Contains("reason: <", prompt);

        // The inline `MARK_BLOCKED: taskId=...` form must NOT appear as the
        // recommended example.
        Assert.DoesNotContain("MARK_BLOCKED: taskId=", prompt);
    }

    [Fact]
    public void HephaestusVerifyStep_ReferencesBackendStructuredCommands()
    {
        // Hephaestus is the backend engineer — Verify should call out
        // RUN_BUILD (dotnet build) and RUN_TESTS.
        var prompt = LoadEngineerPrompt("software-engineer-1").GetProperty("StartupPrompt").GetString() ?? "";

        var verifyIdx = prompt.IndexOf("**Verify**", StringComparison.Ordinal);
        Assert.True(verifyIdx > -1, "software-engineer-1 prompt missing Verify step");

        var verifyBlock = prompt.Substring(verifyIdx, Math.Min(500, prompt.Length - verifyIdx));
        Assert.Contains("RUN_BUILD", verifyBlock);
        Assert.Contains("RUN_TESTS", verifyBlock);
    }

    [Fact]
    public void AthenaVerifyStep_ReferencesFrontendStructuredCommands()
    {
        // Athena is the frontend engineer — Verify must call out
        // RUN_FRONTEND_BUILD (Vite client build) and RUN_TYPECHECK
        // (TypeScript), NOT just RUN_BUILD which only runs `dotnet build`
        // (RunBuildHandler.cs). This was a real bug surfaced by
        // adversarial review (gpt-5.3-codex): the original Athena Verify
        // line referenced RUN_BUILD only, so frontend compile/type errors
        // would silently slip through.
        var prompt = LoadEngineerPrompt("software-engineer-2").GetProperty("StartupPrompt").GetString() ?? "";

        var verifyIdx = prompt.IndexOf("**Verify**", StringComparison.Ordinal);
        Assert.True(verifyIdx > -1, "software-engineer-2 prompt missing Verify step");

        var verifyBlock = prompt.Substring(verifyIdx, Math.Min(500, prompt.Length - verifyIdx));
        Assert.Contains("RUN_FRONTEND_BUILD", verifyBlock);
        Assert.Contains("RUN_TYPECHECK", verifyBlock);
        Assert.Contains("RUN_TESTS", verifyBlock);
    }

    [Theory]
    [InlineData("software-engineer-1")]
    [InlineData("software-engineer-2")]
    public void EngineerPrompt_VerifyStepDoesNotInstructStartingTheServer(string agentId)
    {
        // Regression guard against the original Hephaestus phrasing
        // "Start the server and smoke test the endpoints." Engineers run
        // inside breakout SDK sessions — they cannot start the live server.
        // The platform owns server lifecycle; the engineer's smoke-test
        // surface is structured commands like CALL_ENDPOINT (when granted)
        // or escalation via MARK_BLOCKED.
        var prompt = LoadEngineerPrompt(agentId).GetProperty("StartupPrompt").GetString() ?? "";

        Assert.DoesNotContain(
            "Start the server and smoke test",
            prompt);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "AgentAcademy.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("Could not find repo root (AgentAcademy.sln)");
    }
}
