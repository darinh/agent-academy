namespace AgentAcademy.Forge.Tests;

public sealed class ForgeIdentifiersTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("invalid", false)]
    [InlineData("../../etc/passwd", false)]
    [InlineData("R_short", false)]
    public void IsValidRunId_RejectsInvalid(string? runId, bool expected)
    {
        Assert.Equal(expected, ForgeIdentifiers.IsValidRunId(runId));
    }

    [Fact]
    public void IsValidRunId_AcceptsValidUlid()
    {
        var runId = "R_" + Ulid.NewUlid().ToString();
        Assert.True(ForgeIdentifiers.IsValidRunId(runId));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("not-hex", null)]
    [InlineData("abc", null)]
    [InlineData("../../traversal", null)]
    public void NormalizeArtifactHash_RejectsInvalid(string? hash, string? expected)
    {
        Assert.Equal(expected, ForgeIdentifiers.NormalizeArtifactHash(hash));
    }

    [Fact]
    public void NormalizeArtifactHash_AcceptsRawHex()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, ForgeIdentifiers.NormalizeArtifactHash(hash));
    }

    [Fact]
    public void NormalizeArtifactHash_StripsSha256Prefix()
    {
        var rawHash = new string('b', 64);
        var prefixed = "sha256:" + rawHash;
        Assert.Equal(rawHash, ForgeIdentifiers.NormalizeArtifactHash(prefixed));
    }
}
