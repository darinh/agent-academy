using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Services;

/// <summary>
/// Immutable result from a single agent turn: the resolved agent definition,
/// the raw LLM response, and whether the response was substantive (non-pass).
/// </summary>
public sealed record AgentTurnResult(
    AgentDefinition Agent,
    string Response,
    bool IsNonPass);
