using System.Text.Json;
using AgentAcademy.Forge.Artifacts;
using AgentAcademy.Forge.Costs;
using AgentAcademy.Forge.Llm;
using AgentAcademy.Forge.Models;
using AgentAcademy.Forge.Prompt;
using AgentAcademy.Forge.Storage;
using AgentAcademy.Forge.Validation;
using Microsoft.Extensions.Logging;

namespace AgentAcademy.Forge.Execution;

/// <summary>
/// Executes a single phase's attempt loop. Implements the Attempt state machine:
/// Pending → Prompting → Generating → Validating → (Accepted | Rejected | Errored).
/// Writes all state to disk after every transition for crash recovery.
/// </summary>
public sealed class PhaseExecutor
{
    private readonly ILlmClient _llm;
    private readonly PromptBuilder _promptBuilder;
    private readonly ValidatorPipeline _validatorPipeline;
    private readonly IArtifactStore _artifactStore;
    private readonly IRunStore _runStore;
    private readonly CostCalculator _costCalculator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PhaseExecutor> _logger;

    public PhaseExecutor(
        ILlmClient llm,
        PromptBuilder promptBuilder,
        ValidatorPipeline validatorPipeline,
        IArtifactStore artifactStore,
        IRunStore runStore,
        CostCalculator costCalculator,
        TimeProvider timeProvider,
        ILogger<PhaseExecutor> logger)
    {
        _llm = llm;
        _promptBuilder = promptBuilder;
        _validatorPipeline = validatorPipeline;
        _artifactStore = artifactStore;
        _runStore = runStore;
        _costCalculator = costCalculator;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Execute a single phase: resolve inputs, run attempt loop, return result.
    /// </summary>
    public async Task<PhaseExecutorResult> ExecuteAsync(
        string runId,
        int phaseIndex,
        PhaseDefinition phase,
        MethodologyDefinition methodology,
        TaskBrief task,
        IReadOnlyDictionary<string, ArtifactEnvelope> inputArtifacts,
        decimal? budgetRemaining = null,
        CancellationToken ct = default)
    {
        var maxAttempts = phase.MaxAttempts ?? methodology.MaxAttemptsDefault;
        if (maxAttempts <= 0)
            throw new ArgumentException($"MaxAttempts must be positive, got {maxAttempts} for phase '{phase.Id}'.");

        var attempts = new List<AttemptTrace>();
        var stateTransitions = new List<StateTransition>();
        var inputHashes = BuildInputHashes(inputArtifacts);
        string? acceptedHash = null;

        // PhaseRun: Pending → Running
        RecordTransition(stateTransitions, null, PhaseRunStatus.Pending);
        RecordTransition(stateTransitions, PhaseRunStatus.Pending, PhaseRunStatus.Running);
        await PersistPhaseRunSnapshot(runId, phaseIndex, phase, stateTransitions, attempts, inputHashes, [], ct);

        IReadOnlyList<AmendmentNote>? amendmentNotes = null;
        var localBudgetRemaining = budgetRemaining;

        // Resolve models once for pricing — use requested model, not response model,
        // since providers may return variant IDs not in the pricing table
        var requestedGenModel = ResolveModel(phase.Model, methodology.ModelDefaults?.Generation, "gpt-4o");
        var requestedJudgeModel = ResolveModel(phase.JudgeModel, methodology.ModelDefaults?.Judge, "gpt-4o-mini");

        for (var attemptNumber = 1; attemptNumber <= maxAttempts; attemptNumber++)
        {
            _logger.LogInformation("Phase {PhaseId}: starting attempt {Attempt}/{Max}",
                phase.Id, attemptNumber, maxAttempts);

            var attemptStart = _timeProvider.GetUtcNow().UtcDateTime;
            var attemptResult = await ExecuteAttemptAsync(
                runId, phaseIndex, phase, methodology, task, inputArtifacts,
                attemptNumber, amendmentNotes, ct);

            // Compute cost for this attempt using requested models (not response model IDs)
            var genCost = _costCalculator.Calculate(requestedGenModel, attemptResult.Tokens);
            var judgeCost = attemptResult.JudgeTokens is not null
                ? _costCalculator.Calculate(requestedJudgeModel, attemptResult.JudgeTokens)
                : 0m;
            var attemptCost = genCost + judgeCost;

            var attemptTrace = new AttemptTrace
            {
                AttemptNumber = attemptNumber,
                Status = attemptResult.Status.ToString().ToLowerInvariant(),
                ArtifactHash = attemptResult.ArtifactHash is not null
                    ? $"sha256:{attemptResult.ArtifactHash}"
                    : null,
                ValidatorResults = attemptResult.ValidatorResults,
                Tokens = attemptResult.Tokens,
                LatencyMs = attemptResult.LatencyMs,
                Model = attemptResult.Model ?? "unknown",
                JudgeTokens = attemptResult.JudgeTokens,
                JudgeModel = attemptResult.JudgeModel,
                Cost = attemptCost,
                StartedAt = attemptStart,
                EndedAt = _timeProvider.GetUtcNow().UtcDateTime
            };

            attempts.Add(attemptTrace);

            // Write attempt files to run store
            await WriteAttemptFiles(runId, phaseIndex, phase.Id, attemptNumber, attemptResult, ct);

            // Track budget
            if (localBudgetRemaining.HasValue)
                localBudgetRemaining -= attemptCost;

            if (attemptResult.Status == AttemptStatus.Accepted)
            {
                acceptedHash = attemptResult.ArtifactHash;
                RecordTransition(stateTransitions, PhaseRunStatus.Running, PhaseRunStatus.Succeeded);
                await PersistPhaseRunSnapshot(runId, phaseIndex, phase, stateTransitions, attempts,
                    inputHashes, acceptedHash is not null ? [$"sha256:{acceptedHash}"] : [], ct);

                _logger.LogInformation("Phase {PhaseId}: accepted on attempt {Attempt}", phase.Id, attemptNumber);
                break;
            }

            // Budget exceeded and artifact not accepted — stop retrying
            if (localBudgetRemaining.HasValue && localBudgetRemaining.Value <= 0)
            {
                _logger.LogWarning("Phase {PhaseId}: budget exhausted after attempt {Attempt}",
                    phase.Id, attemptNumber);
                RecordTransition(stateTransitions, PhaseRunStatus.Running, PhaseRunStatus.Failed);
                await PersistPhaseRunSnapshot(runId, phaseIndex, phase, stateTransitions, attempts, inputHashes, [], ct);
                break;
            }

            // Build amendment notes for next attempt
            amendmentNotes = AmendmentNote.FromValidatorResults(attemptResult.ValidatorResults);
            await PersistPhaseRunSnapshot(runId, phaseIndex, phase, stateTransitions, attempts, inputHashes, [], ct);

            if (attemptNumber == maxAttempts)
            {
                RecordTransition(stateTransitions, PhaseRunStatus.Running, PhaseRunStatus.Failed);
                await PersistPhaseRunSnapshot(runId, phaseIndex, phase, stateTransitions, attempts, inputHashes, [], ct);
                _logger.LogWarning("Phase {PhaseId}: exhausted all {Max} attempts", phase.Id, maxAttempts);
            }
        }

        var finalStatus = acceptedHash is not null ? PhaseRunStatus.Succeeded : PhaseRunStatus.Failed;

        var phaseRunTrace = new PhaseRunTrace
        {
            PhaseId = phase.Id,
            ArtifactType = phase.ArtifactType,
            StateTransitions = stateTransitions,
            Attempts = attempts,
            InputArtifactHashes = inputHashes,
            OutputArtifactHashes = acceptedHash is not null ? [$"sha256:{acceptedHash}"] : []
        };

        return new PhaseExecutorResult
        {
            PhaseRunTrace = phaseRunTrace,
            Status = finalStatus,
            AcceptedArtifactHash = acceptedHash
        };
    }

    private async Task<AttemptResult> ExecuteAttemptAsync(
        string runId,
        int phaseIndex,
        PhaseDefinition phase,
        MethodologyDefinition methodology,
        TaskBrief task,
        IReadOnlyDictionary<string, ArtifactEnvelope> inputArtifacts,
        int attemptNumber,
        IReadOnlyList<AmendmentNote>? amendmentNotes,
        CancellationToken ct)
    {
        // Step 1: Prompting — build the prompt
        var resolvedInputs = ResolveInputs(phase, inputArtifacts);
        var userMessage = _promptBuilder.BuildUserMessage(task, phase, resolvedInputs, amendmentNotes);
        var systemMessage = PromptBuilder.SystemMessage;

        // Resolve configurable models: phase override → methodology default → hardcoded fallback
        var generationModel = ResolveModel(phase.Model, methodology?.ModelDefaults?.Generation, "gpt-4o");
        var judgeModel = ResolveModel(phase.JudgeModel, methodology?.ModelDefaults?.Judge, "gpt-4o-mini");

        // Step 2: Generating — call the LLM
        LlmResponse llmResponse;
        try
        {
            llmResponse = await _llm.GenerateAsync(new LlmRequest
            {
                SystemMessage = systemMessage,
                UserMessage = userMessage,
                Model = generationModel,
                Temperature = 0.2,
                MaxTokens = 8192,
                JsonMode = true
            }, ct);
        }
        catch (LlmClientException ex)
        {
            _logger.LogWarning(ex, "Phase {PhaseId} attempt {Attempt}: LLM call failed ({ErrorKind})",
                phase.Id, attemptNumber, ex.ErrorKind);

            return new AttemptResult
            {
                Status = AttemptStatus.Errored,
                ValidatorResults = [],
                Tokens = new TokenCount(),
                LatencyMs = 0,
                Model = "unknown",
                PromptText = $"{systemMessage}\n---\n{userMessage}",
                ResponseRaw = null,
                ErrorKind = ex.ErrorKind.ToString()
            };
        }

        // Step 3: Parse the response
        var parseResult = AttemptResponseParser.Parse(llmResponse.Content, phase, attemptNumber);

        if (!parseResult.Success)
        {
            // Parse failure → Errored (not Rejected), per state machine
            return new AttemptResult
            {
                Status = AttemptStatus.Errored,
                ValidatorResults = parseResult.Failures,
                Tokens = new TokenCount { In = llmResponse.InputTokens, Out = llmResponse.OutputTokens },
                LatencyMs = llmResponse.LatencyMs,
                Model = llmResponse.Model,
                PromptText = $"{systemMessage}\n---\n{userMessage}",
                ResponseRaw = llmResponse.Content
            };
        }

        var envelope = parseResult.Envelope!;

        // Write artifact even before validation (for audit trail, per trace contract)
        var artifactMeta = new ArtifactMeta
        {
            DerivedFrom = phase.Inputs,
            InputHashes = inputArtifacts.Values
                .Select(a => CanonicalJson.PrefixedHash(a))
                .ToList(),
            ProducedAt = _timeProvider.GetUtcNow().UtcDateTime,
            AttemptNumber = attemptNumber
        };
        var hash = await _artifactStore.WriteAsync(envelope, artifactMeta, ct);

        // Step 4: Validating — run the validator pipeline
        var validationResult = await _validatorPipeline.ValidateAsync(
            envelope, inputArtifacts, attemptNumber, judgeModel, ct);

        if (validationResult.Passed)
        {
            return new AttemptResult
            {
                Status = AttemptStatus.Accepted,
                ArtifactHash = hash,
                ValidatorResults = validationResult.Findings,
                Tokens = new TokenCount { In = llmResponse.InputTokens, Out = llmResponse.OutputTokens },
                LatencyMs = llmResponse.LatencyMs,
                Model = llmResponse.Model,
                JudgeTokens = validationResult.JudgeTokens,
                JudgeModel = judgeModel,
                PromptText = $"{systemMessage}\n---\n{userMessage}",
                ResponseRaw = llmResponse.Content,
                ResponseParsedJson = JsonSerializer.Serialize(envelope)
            };
        }

        return new AttemptResult
        {
            Status = AttemptStatus.Rejected,
            ArtifactHash = hash, // Persist even for rejected attempts
            ValidatorResults = validationResult.Findings,
            Tokens = new TokenCount { In = llmResponse.InputTokens, Out = llmResponse.OutputTokens },
            LatencyMs = llmResponse.LatencyMs,
            Model = llmResponse.Model,
            JudgeTokens = validationResult.JudgeTokens,
            JudgeModel = judgeModel,
            PromptText = $"{systemMessage}\n---\n{userMessage}",
            ResponseRaw = llmResponse.Content,
            ResponseParsedJson = JsonSerializer.Serialize(envelope)
        };
    }

    private static IReadOnlyList<ResolvedInput> ResolveInputs(
        PhaseDefinition phase,
        IReadOnlyDictionary<string, ArtifactEnvelope> inputArtifacts)
    {
        var inputs = new List<ResolvedInput>();
        foreach (var inputPhaseId in phase.Inputs)
        {
            if (inputArtifacts.TryGetValue(inputPhaseId, out var artifact))
            {
                inputs.Add(new ResolvedInput
                {
                    PhaseId = inputPhaseId,
                    SchemaId = $"{artifact.ArtifactType}/v{artifact.SchemaVersion}",
                    BodyJson = artifact.Payload.ValueKind == JsonValueKind.Undefined
                        ? "{}"
                        : JsonSerializer.Serialize(artifact.Payload, IndentedOptions)
                });
            }
        }
        return inputs;
    }

    private static IReadOnlyList<string> BuildInputHashes(
        IReadOnlyDictionary<string, ArtifactEnvelope> inputArtifacts)
    {
        return inputArtifacts.Values
            .Select(a => CanonicalJson.PrefixedHash(a))
            .ToList();
    }

    private void RecordTransition(
        List<StateTransition> transitions,
        PhaseRunStatus? from,
        PhaseRunStatus to)
    {
        transitions.Add(new StateTransition
        {
            From = from?.ToString().ToLowerInvariant(),
            To = to.ToString().ToLowerInvariant(),
            At = _timeProvider.GetUtcNow().UtcDateTime
        });
    }

    private async Task PersistPhaseRunSnapshot(
        string runId,
        int phaseIndex,
        PhaseDefinition phase,
        IReadOnlyList<StateTransition> transitions,
        IReadOnlyList<AttemptTrace> attempts,
        IReadOnlyList<string> inputHashes,
        IReadOnlyList<string> outputHashes,
        CancellationToken ct)
    {
        var snapshot = new PhaseRunTrace
        {
            PhaseId = phase.Id,
            ArtifactType = phase.ArtifactType,
            StateTransitions = transitions.ToList(),
            Attempts = attempts.ToList(),
            InputArtifactHashes = inputHashes,
            OutputArtifactHashes = outputHashes
        };

        await _runStore.WritePhaseRunScratchAsync(runId, phaseIndex, phase.Id, snapshot, ct);
    }

    private async Task WriteAttemptFiles(
        string runId,
        int phaseIndex,
        string phaseId,
        int attemptNumber,
        AttemptResult result,
        CancellationToken ct)
    {
        var validatorReportJson = result.ValidatorResults.Count > 0
            ? JsonSerializer.Serialize(result.ValidatorResults, IndentedOptions)
            : null;

        await _runStore.WriteAttemptFilesAsync(runId, phaseIndex, phaseId, attemptNumber, new AttemptFiles
        {
            PromptText = result.PromptText,
            ResponseRaw = result.ResponseRaw,
            ResponseParsedJson = result.ResponseParsedJson,
            ValidatorReportJson = validatorReportJson
        }, ct);
    }

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    /// <summary>
    /// Resolve a model ID from the three-tier cascade: phase override → methodology default → hardcoded fallback.
    /// Treats null, empty, and whitespace-only strings as "not configured".
    /// </summary>
    internal static string ResolveModel(string? phaseOverride, string? methodologyDefault, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(phaseOverride))
            return phaseOverride;
        if (!string.IsNullOrWhiteSpace(methodologyDefault))
            return methodologyDefault;
        return fallback;
    }
}

/// <summary>
/// Result of executing a single phase.
/// </summary>
public sealed record PhaseExecutorResult
{
    public required PhaseRunTrace PhaseRunTrace { get; init; }
    public required PhaseRunStatus Status { get; init; }

    /// <summary>Raw hex hash of the accepted artifact, or null if phase failed.</summary>
    public string? AcceptedArtifactHash { get; init; }
}

/// <summary>
/// Internal result of a single attempt execution.
/// </summary>
internal sealed record AttemptResult
{
    public required AttemptStatus Status { get; init; }
    public string? ArtifactHash { get; init; }
    public required IReadOnlyList<ValidatorResultTrace> ValidatorResults { get; init; }
    public required TokenCount Tokens { get; init; }
    public required long LatencyMs { get; init; }
    public string? Model { get; init; }
    public TokenCount? JudgeTokens { get; init; }
    public string? JudgeModel { get; init; }
    public string? PromptText { get; init; }
    public string? ResponseRaw { get; init; }
    public string? ResponseParsedJson { get; init; }
    public string? ErrorKind { get; init; }
}
