using AgentAcademy.Forge.Models;

namespace AgentAcademy.Forge.Tests;

public sealed class DriftCodeTests
{
    [Theory]
    [InlineData(DriftCode.OMITTED_CONSTRAINT, true)]
    [InlineData(DriftCode.CONSTRAINT_WEAKENED, true)]
    [InlineData(DriftCode.INVENTED_REQUIREMENT, false)]
    [InlineData(DriftCode.SCOPE_BROADENED, false)]
    [InlineData(DriftCode.SCOPE_NARROWED, false)]
    public void IsBlocking_ClassifiesCorrectly(DriftCode code, bool expectedBlocking)
    {
        Assert.Equal(expectedBlocking, DriftSeverity.IsBlocking(code));
    }

    [Fact]
    public void Taxonomy_HasExactly5Codes()
    {
        var allCodes = Enum.GetValues<DriftCode>();
        Assert.Equal(5, allCodes.Length);
    }

    [Fact]
    public void BlockingAndAdvisory_CoverAllCodes()
    {
        var allCodes = Enum.GetValues<DriftCode>();
        foreach (var code in allCodes)
        {
            Assert.True(
                DriftSeverity.Blocking.Contains(code) || DriftSeverity.Advisory.Contains(code),
                $"DriftCode.{code} is not classified as blocking or advisory");
        }
    }

    [Fact]
    public void BlockingAndAdvisory_AreDisjoint()
    {
        var overlap = DriftSeverity.Blocking.Intersect(DriftSeverity.Advisory);
        Assert.Empty(overlap);
    }

    [Fact]
    public void Blocking_Contains2Codes()
    {
        Assert.Equal(2, DriftSeverity.Blocking.Count);
    }

    [Fact]
    public void Advisory_Contains3Codes()
    {
        Assert.Equal(3, DriftSeverity.Advisory.Count);
    }
}
