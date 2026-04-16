using AgentAcademy.Server.Commands;

namespace AgentAcademy.Server.Tests.Security;

/// <summary>
/// Security tests for the shell command sandboxing system.
/// Validates that ShellCommand.TryParse rejects injection attempts,
/// unsupported operations, and malicious argument values.
/// </summary>
public sealed class ShellCommandSecurityTests
{
    // ── Unsupported operation rejection ─────────────────────────────

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("curl http://evil.com")]
    [InlineData("bash -c 'whoami'")]
    [InlineData("cat /etc/passwd")]
    [InlineData("wget")]
    [InlineData("nc -l 4444")]
    [InlineData("python -c 'import os; os.system(\"id\")'")]
    [InlineData("chmod 777 /")]
    public void TryParse_ArbitraryCommands_Rejected(string operation)
    {
        var args = new Dictionary<string, object?> { ["operation"] = operation };

        var ok = ShellCommand.TryParse(args, out var cmd, out var error);

        Assert.False(ok);
        Assert.Null(cmd);
        Assert.Contains("Unsupported", error!);
    }

    [Fact]
    public void TryParse_EmptyOperation_Rejected()
    {
        var args = new Dictionary<string, object?> { ["operation"] = "" };
        Assert.False(ShellCommand.TryParse(args, out _, out var error));
        Assert.Contains("Missing", error!);
    }

    [Fact]
    public void TryParse_NullOperation_Rejected()
    {
        var args = new Dictionary<string, object?> { ["operation"] = null };
        Assert.False(ShellCommand.TryParse(args, out _, out var error));
        Assert.Contains("Missing", error!);
    }

    [Fact]
    public void TryParse_WhitespaceOperation_Rejected()
    {
        var args = new Dictionary<string, object?> { ["operation"] = "   " };
        Assert.False(ShellCommand.TryParse(args, out _, out var error));
        Assert.Contains("Missing", error!);
    }

    // ── Branch name injection ──────────────────────────────────────

    [Theory]
    [InlineData("--delete")]
    [InlineData("-D")]
    [InlineData("--force")]
    [InlineData("-rf")]
    public void TryParse_GitCheckout_FlagInjection_Rejected(string branch)
    {
        var args = new Dictionary<string, object?>
        {
            ["operation"] = "git-checkout",
            ["branch"] = branch
        };

        var ok = ShellCommand.TryParse(args, out _, out var error);

        Assert.False(ok);
        Assert.Contains("Invalid branch", error!);
    }

    [Theory]
    [InlineData("main; rm -rf /")]
    [InlineData("develop && cat /etc/passwd")]
    [InlineData("feat$(whoami)")]
    [InlineData("test`id`branch")]
    [InlineData("branch|evil")]
    [InlineData("branch\nnewline")]
    public void TryParse_GitCheckout_ShellMetachars_Rejected(string branch)
    {
        var args = new Dictionary<string, object?>
        {
            ["operation"] = "git-checkout",
            ["branch"] = branch
        };

        Assert.False(ShellCommand.TryParse(args, out _, out _));
    }

    [Theory]
    [InlineData("main..develop")]
    [InlineData("../../../etc/passwd")]
    public void TryParse_GitCheckout_DotDotSequence_Rejected(string branch)
    {
        var args = new Dictionary<string, object?>
        {
            ["operation"] = "git-checkout",
            ["branch"] = branch
        };

        Assert.False(ShellCommand.TryParse(args, out _, out var error));
        Assert.Contains("Invalid branch", error!);
    }

    [Theory]
    [InlineData("feat/my-feature")]
    [InlineData("develop")]
    [InlineData("release/1.0.0")]
    [InlineData("fix/bug_123")]
    public void TryParse_GitCheckout_ValidBranches_Accepted(string branch)
    {
        var args = new Dictionary<string, object?>
        {
            ["operation"] = "git-checkout",
            ["branch"] = branch
        };

        Assert.True(ShellCommand.TryParse(args, out var cmd, out _));
        Assert.Equal(branch, cmd!.Branch);
    }

    // ── Commit message injection ───────────────────────────────────

    [Fact]
    public void TryParse_GitCommit_OversizedMessage_Rejected()
    {
        var args = new Dictionary<string, object?>
        {
            ["operation"] = "git-commit",
            ["message"] = new string('x', 5001)
        };

        Assert.False(ShellCommand.TryParse(args, out _, out var error));
        Assert.Contains("5000", error!);
    }

    [Fact]
    public void TryParse_GitCommit_BoundaryMessage_Accepted()
    {
        var args = new Dictionary<string, object?>
        {
            ["operation"] = "git-commit",
            ["message"] = new string('x', 5000)
        };

        Assert.True(ShellCommand.TryParse(args, out var cmd, out _));
        Assert.Equal(5000, cmd!.Message!.Length);
    }

    [Fact]
    public void TryParse_GitCommit_MissingMessage_Rejected()
    {
        var args = new Dictionary<string, object?>
        {
            ["operation"] = "git-commit"
        };

        Assert.False(ShellCommand.TryParse(args, out _, out var error));
        Assert.Contains("message", error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Argument injection (extra args) ────────────────────────────

    [Fact]
    public void TryParse_GitCheckout_ExtraArgs_Rejected()
    {
        var args = new Dictionary<string, object?>
        {
            ["operation"] = "git-checkout",
            ["branch"] = "main",
            ["force"] = "true"      // Not in allowlist
        };

        Assert.False(ShellCommand.TryParse(args, out _, out var error));
        Assert.Contains("Unsupported argument", error!);
    }

    [Fact]
    public void TryParse_DotnetBuild_ExtraArgs_Rejected()
    {
        var args = new Dictionary<string, object?>
        {
            ["operation"] = "dotnet-build",
            ["target"] = "/tmp/evil.proj"  // Not in allowlist
        };

        Assert.False(ShellCommand.TryParse(args, out _, out var error));
        Assert.Contains("Unsupported argument", error!);
    }

    [Fact]
    public void TryParse_DotnetTest_NoExtraArgs_Accepted()
    {
        var args = new Dictionary<string, object?>
        {
            ["operation"] = "dotnet-test"
        };

        Assert.True(ShellCommand.TryParse(args, out var cmd, out _));
        Assert.Equal("dotnet-test", cmd!.Operation);
    }

    // ── Restart server ─────────────────────────────────────────────

    [Fact]
    public void TryParse_RestartServer_OversizedReason_Rejected()
    {
        var args = new Dictionary<string, object?>
        {
            ["operation"] = "restart-server",
            ["reason"] = new string('r', 1001)
        };

        Assert.False(ShellCommand.TryParse(args, out _, out var error));
        Assert.Contains("1000", error!);
    }

    [Fact]
    public void TryParse_RestartServer_MissingReason_Rejected()
    {
        var args = new Dictionary<string, object?>
        {
            ["operation"] = "restart-server"
        };

        Assert.False(ShellCommand.TryParse(args, out _, out var error));
        Assert.Contains("reason", error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Case insensitivity ─────────────────────────────────────────

    [Theory]
    [InlineData("GIT-CHECKOUT")]
    [InlineData("Git-Checkout")]
    [InlineData("DOTNET-BUILD")]
    public void TryParse_CaseInsensitiveOperations_Accepted(string operation)
    {
        var args = new Dictionary<string, object?> { ["operation"] = operation };

        // These require further args (branch/message) so they may fail on validation,
        // but they should NOT fail on "unsupported operation"
        ShellCommand.TryParse(args, out _, out var error);

        if (error is not null)
            Assert.DoesNotContain("Unsupported SHELL operation", error);
    }

    // ── Value key alias ────────────────────────────────────────────

    [Fact]
    public void TryParse_ValueKeyAlias_AcceptedAsOperation()
    {
        var args = new Dictionary<string, object?>
        {
            ["value"] = "dotnet-build"
        };

        Assert.True(ShellCommand.TryParse(args, out var cmd, out _));
        Assert.Equal("dotnet-build", cmd!.Operation);
    }
}
