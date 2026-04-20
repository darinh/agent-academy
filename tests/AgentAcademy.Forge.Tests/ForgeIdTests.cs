using AgentAcademy.Forge.Models;

namespace AgentAcademy.Forge.Tests;

public sealed class ForgeIdTests
{
    [Fact]
    public void NewRunId_StartsWithR_Prefix()
    {
        var id = ForgeId.NewRunId();
        Assert.StartsWith("R_", id);
    }

    [Fact]
    public void NewRunId_HasCorrectLength()
    {
        var id = ForgeId.NewRunId();
        // "R_" (2) + ULID (26) = 28
        Assert.Equal(28, id.Length);
    }

    [Fact]
    public void NewRunId_IsUnique()
    {
        var ids = Enumerable.Range(0, 100).Select(_ => ForgeId.NewRunId()).ToHashSet();
        Assert.Equal(100, ids.Count);
    }

    [Fact]
    public void NewRunId_IsSortable()
    {
        var id1 = ForgeId.NewRunId();
        Thread.Sleep(2); // Ensure different ULID timestamp
        var id2 = ForgeId.NewRunId();

        Assert.True(string.Compare(id1, id2, StringComparison.Ordinal) < 0);
    }

    [Fact]
    public void ParseRunId_RoundTrips()
    {
        var id = ForgeId.NewRunId();
        var ulid = ForgeId.ParseRunId(id);
        Assert.Equal(id, $"R_{ulid}");
    }

    [Fact]
    public void ParseRunId_StripsPrefix()
    {
        var ulid = Ulid.NewUlid();
        var parsed = ForgeId.ParseRunId($"R_{ulid}");
        Assert.Equal(ulid, parsed);
    }
}
