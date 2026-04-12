using AgentAcademy.Server.Services;

namespace AgentAcademy.Server.Tests;

public class BreakoutLifecycleServiceTests
{
    // ── Stuck Detection Constants ────────────────────────────────

    [Fact]
    public void MaxConsecutiveIdleRounds_IsReasonableDefault()
    {
        Assert.True(BreakoutLifecycleService.MaxConsecutiveIdleRounds >= 3,
            "MaxConsecutiveIdleRounds should be at least 3 to allow for planning rounds");
        Assert.True(BreakoutLifecycleService.MaxConsecutiveIdleRounds <= 20,
            "MaxConsecutiveIdleRounds should not be so high that stuck agents waste resources");
    }

    [Fact]
    public void MaxBreakoutRounds_IsReasonableDefault()
    {
        Assert.True(BreakoutLifecycleService.MaxBreakoutRounds >= 50,
            "MaxBreakoutRounds should allow complex multi-step tasks");
        Assert.True(BreakoutLifecycleService.MaxBreakoutRounds <= 500,
            "MaxBreakoutRounds should prevent truly unbounded loops");
    }

    [Fact]
    public void MaxBreakoutRounds_ExceedsMaxConsecutiveIdleRounds()
    {
        Assert.True(BreakoutLifecycleService.MaxBreakoutRounds > BreakoutLifecycleService.MaxConsecutiveIdleRounds,
            "MaxBreakoutRounds must exceed MaxConsecutiveIdleRounds");
    }
}
