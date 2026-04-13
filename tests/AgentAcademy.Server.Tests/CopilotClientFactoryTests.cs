using AgentAcademy.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public class CopilotClientFactoryTests : IAsyncLifetime
{
    // Bogus CLI path guarantees CreateClientAsync fails without hitting the network
    private const string BadCliPath = "/nonexistent/copilot-cli-does-not-exist";

    private readonly CopilotTokenProvider _tokenProvider;
    private CopilotClientFactory _factory;

    public CopilotClientFactoryTests()
    {
        _tokenProvider = new CopilotTokenProvider();
        _factory = CreateFactory();
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>
    /// Creates a factory wired with an in-memory config.
    /// By default uses <see cref="BadCliPath"/> so client creation
    /// always fails — pass <c>cliPath: null</c> to use the real SDK.
    /// </summary>
    private CopilotClientFactory CreateFactory(
        string? configToken = null, string? cliPath = BadCliPath)
    {
        var configDict = new Dictionary<string, string?>();
        if (configToken is not null)
            configDict["Copilot:GitHubToken"] = configToken;
        if (cliPath is not null)
            configDict["Copilot:CliPath"] = cliPath;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict)
            .Build();

        return new CopilotClientFactory(
            NullLogger<CopilotClientFactory>.Instance,
            config,
            _tokenProvider);
    }

    // ── ResolveToken ────────────────────────────────────────────

    [Fact]
    public void ResolveToken_NoTokenAnywhere_ReturnsNull()
    {
        Assert.Null(_factory.ResolveToken());
    }

    [Fact]
    public void ResolveToken_ConfigTokenOnly_ReturnsConfigToken()
    {
        var factory = CreateFactory(configToken: "cfg-token-123");
        Assert.Equal("cfg-token-123", factory.ResolveToken());
    }

    [Fact]
    public void ResolveToken_UserOAuthTokenOnly_ReturnsUserToken()
    {
        _tokenProvider.SetToken("user-oauth-xyz");
        Assert.Equal("user-oauth-xyz", _factory.ResolveToken());
    }

    [Fact]
    public void ResolveToken_BothTokens_UserTokenTakesPriority()
    {
        _tokenProvider.SetToken("user-token");
        var factory = CreateFactory(configToken: "config-token");
        Assert.Equal("user-token", factory.ResolveToken());
    }

    [Fact]
    public void ResolveToken_UserTokenCleared_FallsBackToConfigToken()
    {
        _tokenProvider.SetToken("user-token");
        var factory = CreateFactory(configToken: "config-token");

        Assert.Equal("user-token", factory.ResolveToken());

        _tokenProvider.ClearToken();
        Assert.Equal("config-token", factory.ResolveToken());
    }

    [Fact]
    public void ResolveToken_WhitespaceUserToken_TreatedAsAbsent()
    {
        _tokenProvider.SetToken("   ");
        var factory = CreateFactory(configToken: "config-token");
        Assert.Equal("config-token", factory.ResolveToken());
    }

    [Fact]
    public void ResolveToken_EmptyConfigToken_TreatedAsAbsent()
    {
        var configDict = new Dictionary<string, string?> { ["Copilot:GitHubToken"] = "" };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict)
            .Build();
        var factory = new CopilotClientFactory(
            NullLogger<CopilotClientFactory>.Instance, config, _tokenProvider);

        Assert.Null(factory.ResolveToken());
    }

    [Fact]
    public void ResolveToken_AfterSetTokens_ReturnsAccessToken()
    {
        _tokenProvider.SetTokens(
            accessToken: "access-tok",
            refreshToken: "refresh-tok",
            expiresIn: TimeSpan.FromHours(8));

        Assert.Equal("access-tok", _factory.ResolveToken());
    }

    // ── DescribeTokenSource ─────────────────────────────────────

    [Fact]
    public void DescribeTokenSource_Null_ReturnsEnvCliLogin()
    {
        Assert.Equal("env/CLI login", _factory.DescribeTokenSource(null));
    }

    [Fact]
    public void DescribeTokenSource_MatchesUserToken_ReturnsUserOAuth()
    {
        _tokenProvider.SetToken("my-token");
        Assert.Equal("user OAuth", _factory.DescribeTokenSource("my-token"));
    }

    [Fact]
    public void DescribeTokenSource_NonNullNonUserToken_ReturnsConfig()
    {
        Assert.Equal("config", _factory.DescribeTokenSource("some-config-token"));
    }

    [Fact]
    public void DescribeTokenSource_ConfigTokenSet_UserTokenNull_ReturnsConfig()
    {
        var factory = CreateFactory(configToken: "cfg-123");
        Assert.Equal("config", factory.DescribeTokenSource("cfg-123"));
    }

    [Fact]
    public void DescribeTokenSource_UserTokenChanged_ReflectsNewValue()
    {
        _tokenProvider.SetToken("token-v1");
        Assert.Equal("user OAuth", _factory.DescribeTokenSource("token-v1"));

        _tokenProvider.SetToken("token-v2");
        Assert.Equal("config", _factory.DescribeTokenSource("token-v1"));
        Assert.Equal("user OAuth", _factory.DescribeTokenSource("token-v2"));
    }

    [Fact]
    public void DescribeTokenSource_AfterSetTokens_ReturnsUserOAuth()
    {
        _tokenProvider.SetTokens(accessToken: "at-123");
        Assert.Equal("user OAuth", _factory.DescribeTokenSource("at-123"));
    }

    // ── IsDefaultClientOperational ──────────────────────────────

    [Fact]
    public void IsDefaultClientOperational_InitialState_IsFalse()
    {
        Assert.False(_factory.IsDefaultClientOperational);
    }

    [Fact]
    public async Task IsDefaultClientOperational_AfterFailedCreation_IsFalse()
    {
        await _factory.GetClientAsync(CancellationToken.None);
        Assert.False(_factory.IsDefaultClientOperational);
    }

    // ── GetClientAsync — failure paths (bad CLI path) ───────────

    [Fact]
    public async Task GetClientAsync_BadCliPath_NoToken_ReturnsNullClient()
    {
        var result = await _factory.GetClientAsync(CancellationToken.None);

        Assert.Null(result.Client);
        Assert.False(result.WasRecreated);
        Assert.False(_factory.IsDefaultClientOperational);
    }

    [Fact]
    public async Task GetClientAsync_BadCliPath_WithConfigToken_ReturnsNullClient()
    {
        var factory = CreateFactory(configToken: "ghp_test123");
        var result = await factory.GetClientAsync(CancellationToken.None);

        Assert.Null(result.Client);
        Assert.False(result.WasRecreated);
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task GetClientAsync_SameTokenAfterFailure_DoesNotRetry()
    {
        _tokenProvider.SetToken("static-token");

        var first = await _factory.GetClientAsync(CancellationToken.None);
        Assert.Null(first.Client);

        // Second call with same token — should return immediately (no retry)
        var second = await _factory.GetClientAsync(CancellationToken.None);
        Assert.Null(second.Client);
        Assert.False(second.WasRecreated);
    }

    [Fact]
    public async Task GetClientAsync_TokenChangeAfterFailure_ResetsFailureState()
    {
        _tokenProvider.SetToken("token-A");
        var first = await _factory.GetClientAsync(CancellationToken.None);
        Assert.Null(first.Client);

        // Change token — factory should try again (still fails due to bad CLI path)
        _tokenProvider.SetToken("token-B");
        var second = await _factory.GetClientAsync(CancellationToken.None);
        Assert.Null(second.Client);
    }

    [Fact]
    public async Task GetClientAsync_MultipleCalls_SameToken_ConsistentFailure()
    {
        var factory = CreateFactory(configToken: "persistent-token");

        var r1 = await factory.GetClientAsync(CancellationToken.None);
        var r2 = await factory.GetClientAsync(CancellationToken.None);
        var r3 = await factory.GetClientAsync(CancellationToken.None);

        Assert.Null(r1.Client);
        Assert.Null(r2.Client);
        Assert.Null(r3.Client);
        Assert.False(r2.WasRecreated);
        Assert.False(r3.WasRecreated);

        await factory.DisposeAsync();
    }

    [Fact]
    public async Task GetClientAsync_CancelledToken_ThrowsOperationCancelled()
    {
        var cancelled = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _factory.GetClientAsync(cancelled));
    }

    // ── GetClientAsync — token rotation (bad CLI path) ──────────

    [Fact]
    public async Task GetClientAsync_TokenRotation_MultipleRotations()
    {
        for (int i = 0; i < 5; i++)
        {
            _tokenProvider.SetToken($"token-{i}");
            var result = await _factory.GetClientAsync(CancellationToken.None);
            Assert.Null(result.Client);
        }
    }

    [Fact]
    public async Task GetClientAsync_TokenClearedToNull_AttemptsCreation()
    {
        _tokenProvider.SetToken("token-A");
        await _factory.GetClientAsync(CancellationToken.None);

        _tokenProvider.ClearToken();
        var result = await _factory.GetClientAsync(CancellationToken.None);
        Assert.Null(result.Client);
    }

    // ── GetClientAsync — success paths (real CLI) ───────────────

    [Fact]
    public async Task GetClientAsync_RealCli_CreatesClient()
    {
        var factory = CreateFactory(cliPath: null);
        try
        {
            var result = await factory.GetClientAsync(CancellationToken.None);
            Assert.NotNull(result.Client);
            Assert.False(result.WasRecreated);
            Assert.True(factory.IsDefaultClientOperational);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetClientAsync_RealCli_SameToken_ReusesClient()
    {
        var factory = CreateFactory(cliPath: null);
        try
        {
            var first = await factory.GetClientAsync(CancellationToken.None);
            var second = await factory.GetClientAsync(CancellationToken.None);

            Assert.NotNull(first.Client);
            Assert.Same(first.Client, second.Client);
            Assert.False(second.WasRecreated);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetClientAsync_RealCli_TokenRotation_RecreatesToNewClient()
    {
        _tokenProvider.SetToken("token-A");
        var factory = CreateFactory(cliPath: null);
        try
        {
            var first = await factory.GetClientAsync(CancellationToken.None);
            Assert.NotNull(first.Client);

            _tokenProvider.SetToken("token-B");
            var second = await factory.GetClientAsync(CancellationToken.None);

            Assert.NotNull(second.Client);
            Assert.True(second.WasRecreated);
            Assert.NotSame(first.Client, second.Client);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // ── GetWorktreeClientAsync — failure paths ──────────────────

    [Fact]
    public async Task GetWorktreeClientAsync_BadCliPath_ReturnsNullClient()
    {
        var result = await _factory.GetWorktreeClientAsync(
            "/some/worktree/path", CancellationToken.None);

        Assert.Null(result.Client);
        Assert.False(result.WasRecreated);
    }

    [Fact]
    public async Task GetWorktreeClientAsync_BadCliPath_WithToken_ReturnsNull()
    {
        _tokenProvider.SetToken("wt-token");
        var result = await _factory.GetWorktreeClientAsync(
            "/some/worktree", CancellationToken.None);

        Assert.Null(result.Client);
    }

    [Fact]
    public async Task GetWorktreeClientAsync_CancelledToken_Throws()
    {
        var cancelled = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _factory.GetWorktreeClientAsync("/path", cancelled));
    }

    [Fact]
    public async Task GetWorktreeClientAsync_TokenRotation_SetsWasRecreated()
    {
        _tokenProvider.SetToken("wt-token-A");
        await _factory.GetWorktreeClientAsync("/tree1", CancellationToken.None);

        _tokenProvider.SetToken("wt-token-B");
        var result = await _factory.GetWorktreeClientAsync(
            "/tree1", CancellationToken.None);

        Assert.True(result.WasRecreated);
        Assert.Null(result.Client);
    }

    [Fact]
    public async Task GetWorktreeClientAsync_DifferentPaths_IndependentEntries()
    {
        _tokenProvider.SetToken("shared-token");

        var r1 = await _factory.GetWorktreeClientAsync(
            "/path/one", CancellationToken.None);
        var r2 = await _factory.GetWorktreeClientAsync(
            "/path/two", CancellationToken.None);

        // Both fail (bad CLI path) but they're independent lookups
        Assert.Null(r1.Client);
        Assert.Null(r2.Client);
    }

    // ── DisposeWorktreeClientAsync ──────────────────────────────

    [Fact]
    public async Task DisposeWorktreeClientAsync_NeverAdded_ReturnsNull()
    {
        var result = await _factory.DisposeWorktreeClientAsync("/nonexistent/path");
        Assert.Null(result);
    }

    [Fact]
    public async Task DisposeWorktreeClientAsync_UnknownPath_ReturnsNull()
    {
        _tokenProvider.SetToken("tok");
        await _factory.GetWorktreeClientAsync("/known/path", CancellationToken.None);

        var result = await _factory.DisposeWorktreeClientAsync("/unknown/path");
        Assert.Null(result);
    }

    [Fact]
    public async Task DisposeWorktreeClientAsync_RealCli_ReturnsSessionKeyPrefix()
    {
        _tokenProvider.SetToken("tok");
        var factory = CreateFactory(cliPath: null);
        // Use a real directory so the SDK can start the client
        var testPath = Directory.GetCurrentDirectory();
        try
        {
            var acquired = await factory.GetWorktreeClientAsync(testPath, CancellationToken.None);
            Assert.NotNull(acquired.Client);

            var normalizedPath = Path.GetFullPath(testPath);
            var result = await factory.DisposeWorktreeClientAsync(testPath);
            Assert.Equal($"wt:{normalizedPath}:", result);

            // Disposing again returns null (already removed)
            var second = await factory.DisposeWorktreeClientAsync(testPath);
            Assert.Null(second);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // ── DisposeAsync ────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_Idempotent_DoesNotThrow()
    {
        var factory = CreateFactory();

        await factory.DisposeAsync();
        await factory.DisposeAsync();
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_AfterFailedClientCreation_DoesNotThrow()
    {
        var factory = CreateFactory(configToken: "token");
        await factory.GetClientAsync(CancellationToken.None);
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_AfterWorktreeAttempt_DoesNotThrow()
    {
        _tokenProvider.SetToken("tok");
        await _factory.GetWorktreeClientAsync("/wt", CancellationToken.None);
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_RealCli_AfterSuccessfulCreation_DoesNotThrow()
    {
        var factory = CreateFactory(cliPath: null);
        await factory.GetClientAsync(CancellationToken.None);
        await factory.DisposeAsync();
        await factory.DisposeAsync(); // idempotent
    }

    // ── Integration: mixed default + worktree flows ─────────────

    [Fact]
    public async Task MixedFlow_BadCliPath_BothReturnNull()
    {
        _tokenProvider.SetToken("shared");

        var defaultResult = await _factory.GetClientAsync(CancellationToken.None);
        var wtResult = await _factory.GetWorktreeClientAsync(
            "/workspace", CancellationToken.None);

        Assert.Null(defaultResult.Client);
        Assert.Null(wtResult.Client);
        Assert.False(_factory.IsDefaultClientOperational);
    }

    [Fact]
    public async Task MixedFlow_TokenRotation_AffectsBothPools()
    {
        _tokenProvider.SetToken("token-1");
        await _factory.GetClientAsync(CancellationToken.None);
        await _factory.GetWorktreeClientAsync("/wt1", CancellationToken.None);

        _tokenProvider.SetToken("token-2");

        var wtResult = await _factory.GetWorktreeClientAsync(
            "/wt1", CancellationToken.None);
        Assert.True(wtResult.WasRecreated);
    }

    [Fact]
    public async Task MixedFlow_RealCli_DefaultAndWorktreeCoexist()
    {
        var factory = CreateFactory(cliPath: null);
        try
        {
            var defaultResult = await factory.GetClientAsync(CancellationToken.None);
            var wtResult = await factory.GetWorktreeClientAsync(
                "/home", CancellationToken.None);

            Assert.NotNull(defaultResult.Client);
            Assert.NotNull(wtResult.Client);
            Assert.NotSame(defaultResult.Client, wtResult.Client);
            Assert.True(factory.IsDefaultClientOperational);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // ── Constructor / config edge cases ─────────────────────────

    [Fact]
    public void Constructor_NullConfigSection_DoesNotThrow()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var factory = new CopilotClientFactory(
            NullLogger<CopilotClientFactory>.Instance,
            config,
            _tokenProvider);

        Assert.False(factory.IsDefaultClientOperational);
    }
}
