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

    // ── Operation/value precedence (null-coalesce direction) ───────

    [Fact]
    public void TryParse_BothOperationAndValue_OperationTakesPrecedence()
    {
        var args = new Dictionary<string, object?>
        {
            ["operation"] = "dotnet-build",
            ["value"] = "dotnet-test"
        };

        Assert.True(ShellCommand.TryParse(args, out var cmd, out _));
        Assert.Equal("dotnet-build", cmd!.Operation);
    }

    // ── Unsupported operation error content & ordering ─────────────

    [Fact]
    public void TryParse_UnsupportedOperation_ErrorHasExactFormat()
    {
        var args = new Dictionary<string, object?> { ["operation"] = "bogus" };

        Assert.False(ShellCommand.TryParse(args, out _, out var error));
        Assert.Equal(
            "Unsupported SHELL operation 'bogus'. Supported operations: dotnet-build, dotnet-test, git-checkout, git-commit, git-stash-pop, restart-server.",
            error);
    }

    // ── Per-operation argument allowlist ───────────────────────────
    // Each of the 6 operations lists its allowed optional args. Extra args must be rejected,
    // allowed args must be accepted (when required ones are provided).

    [Theory]
    // git-stash-pop allows: operation, value, branch — rejects message, reason
    [InlineData("git-stash-pop", "message", "text", "branch", "feat/ok")]
    [InlineData("git-stash-pop", "reason", "text", "branch", "feat/ok")]
    // git-checkout allows: operation, value, branch — rejects message, reason
    [InlineData("git-checkout", "message", "text", "branch", "feat/ok")]
    [InlineData("git-checkout", "reason", "text", "branch", "feat/ok")]
    // git-commit allows: operation, value, message — rejects branch, reason
    [InlineData("git-commit", "branch", "feat/ok", "message", "msg")]
    [InlineData("git-commit", "reason", "text", "message", "msg")]
    // restart-server allows: operation, value, reason — rejects branch, message
    [InlineData("restart-server", "branch", "feat/ok", "reason", "why")]
    [InlineData("restart-server", "message", "text", "reason", "why")]
    // dotnet-build allows only: operation, value
    [InlineData("dotnet-build", "branch", "feat/ok", null, null)]
    [InlineData("dotnet-build", "message", "text", null, null)]
    [InlineData("dotnet-build", "reason", "text", null, null)]
    // dotnet-test allows only: operation, value
    [InlineData("dotnet-test", "branch", "feat/ok", null, null)]
    [InlineData("dotnet-test", "message", "text", null, null)]
    [InlineData("dotnet-test", "reason", "text", null, null)]
    public void TryParse_DisallowedArgForOperation_Rejected(
        string operation, string badKey, string badValue, string? requiredKey, string? requiredValue)
    {
        var args = new Dictionary<string, object?>
        {
            ["operation"] = operation,
            [badKey] = badValue
        };
        if (requiredKey is not null)
            args[requiredKey] = requiredValue;

        Assert.False(ShellCommand.TryParse(args, out _, out var error));
        Assert.Contains("Unsupported argument(s) for operation", error!);
        Assert.Contains(badKey, error!);
    }

    [Theory]
    // Each operation with its allowed optional arg (operation key only, value key, plus op-specific)
    [InlineData("git-stash-pop", "branch", "feat/ok")]
    [InlineData("git-checkout", "branch", "feat/ok")]
    [InlineData("git-commit", "message", "msg")]
    [InlineData("restart-server", "reason", "why")]
    public void TryParse_AllowedArgForOperation_Accepted(string operation, string key, string value)
    {
        var args = new Dictionary<string, object?>
        {
            ["operation"] = operation,
            [key] = value
        };

        Assert.True(ShellCommand.TryParse(args, out var cmd, out _));
        Assert.Equal(operation, cmd!.Operation);
    }

    [Theory]
    // The "value" alias is in the allowlist for every operation (carrying the op name).
    [InlineData("git-stash-pop", "branch", "feat/ok")]
    [InlineData("git-checkout", "branch", "feat/ok")]
    [InlineData("git-commit", "message", "msg")]
    [InlineData("restart-server", "reason", "why")]
    [InlineData("dotnet-build", null, null)]
    [InlineData("dotnet-test", null, null)]
    public void TryParse_ValueKeyAlias_AllowedAlongsideRequiredArgs(string operation, string? reqKey, string? reqValue)
    {
        var args = new Dictionary<string, object?>
        {
            ["value"] = operation
        };
        if (reqKey is not null)
            args[reqKey] = reqValue;

        Assert.True(ShellCommand.TryParse(args, out var cmd, out _));
        Assert.Equal(operation, cmd!.Operation);
    }

    // ── Unexpected-argument error: alphabetical ordering ───────────

    [Fact]
    public void TryParse_MultipleUnexpectedArgs_SortedAlphabetically()
    {
        var args = new Dictionary<string, object?>
        {
            ["operation"] = "git-checkout",
            ["branch"] = "main",
            ["zzz"] = "x",
            ["aaa"] = "y",
            ["mmm"] = "z"
        };

        Assert.False(ShellCommand.TryParse(args, out _, out var error));
        Assert.Equal(
            "Unsupported argument(s) for operation 'git-checkout': aaa, mmm, zzz",
            error);
    }

    // ── Boundary for restart-server reason length (> vs >=) ────────

    [Fact]
    public void TryParse_RestartServer_BoundaryReason_Accepted()
    {
        var args = new Dictionary<string, object?>
        {
            ["operation"] = "restart-server",
            ["reason"] = new string('r', 1000)
        };

        Assert.True(ShellCommand.TryParse(args, out var cmd, out _));
        Assert.Equal(1000, cmd!.Reason!.Length);
    }
}
