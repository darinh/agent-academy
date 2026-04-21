namespace AgentAcademy.Forge;

/// <summary>
/// Generates ULID-based run IDs prefixed with "R_" per the storage-layout contract.
/// ULIDs are sortable by creation time and URL-safe.
/// </summary>
public static class ForgeId
{
    /// <summary>
    /// Generate a new run ID: R_ + ULID (26 chars, Crockford Base32).
    /// </summary>
    public static string NewRunId() => $"R_{Ulid.NewUlid()}";

    /// <summary>
    /// Parse a ULID from a run ID string (strips "R_" prefix).
    /// </summary>
    public static Ulid ParseRunId(string runId)
    {
        var raw = runId.StartsWith("R_", StringComparison.Ordinal) ? runId[2..] : runId;
        return Ulid.Parse(raw);
    }
}
