namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Shared limits for agent memory values. The 500-character cap is part of the
/// agent memory contract (spec 008-agent-memory §Storage Model — "Max 500 characters
/// per value"). All write paths — REMEMBER (command + SDK tool) and IMPORT_MEMORIES —
/// must enforce the same limit so agents see consistent behaviour.
/// </summary>
internal static class MemoryValueLimits
{
    /// <summary>Maximum number of characters allowed in a memory value.</summary>
    internal const int MaxValueChars = 500;
}
