using AgentAcademy.Server.Services;

namespace AgentAcademy.Server.Tests;

public sealed class CopilotTokenProviderTests
{
    private readonly CopilotTokenProvider _sut = new();

    #region Initial State

    [Fact]
    public void InitialState_TokenIsNull()
        => Assert.Null(_sut.Token);

    [Fact]
    public void InitialState_RefreshTokenIsNull()
        => Assert.Null(_sut.RefreshToken);

    [Fact]
    public void InitialState_TokenSetAtIsNull()
        => Assert.Null(_sut.TokenSetAt);

    [Fact]
    public void InitialState_HasPendingCookieUpdateIsFalse()
        => Assert.False(_sut.HasPendingCookieUpdate);

    [Fact]
    public void InitialState_IsTokenExpiringSoonIsFalse()
        => Assert.False(_sut.IsTokenExpiringSoon);

    [Fact]
    public void InitialState_CanRefreshIsFalse()
        => Assert.False(_sut.CanRefresh);

    [Fact]
    public void InitialState_ExpiresAtUtcIsNull()
        => Assert.Null(_sut.ExpiresAtUtc);

    [Fact]
    public void InitialState_RefreshTokenExpiresAtUtcIsNull()
        => Assert.Null(_sut.RefreshTokenExpiresAtUtc);

    #endregion

    #region SetToken

    [Fact]
    public void SetToken_SetsTokenProperty()
    {
        _sut.SetToken("abc123");

        Assert.Equal("abc123", _sut.Token);
    }

    [Fact]
    public void SetToken_SetsTokenSetAtToApproximateUtcNow()
    {
        var before = DateTime.UtcNow;
        _sut.SetToken("tok");
        var after = DateTime.UtcNow;

        Assert.NotNull(_sut.TokenSetAt);
        Assert.InRange(_sut.TokenSetAt!.Value, before, after);
    }

    [Fact]
    public void SetToken_FiresTokenChangedEvent()
    {
        var fired = false;
        _sut.TokenChanged += () => fired = true;

        _sut.SetToken("tok");

        Assert.True(fired);
    }

    [Fact]
    public void SetToken_HandlerExceptionDoesNotPropagate()
    {
        _sut.TokenChanged += () => throw new InvalidOperationException("boom");

        var ex = Record.Exception(() => _sut.SetToken("tok"));

        Assert.Null(ex);
        Assert.Equal("tok", _sut.Token);
    }

    #endregion

    #region SetTokens

    [Fact]
    public void SetTokens_SetsAllPropertiesWhenAllProvided()
    {
        var before = DateTime.UtcNow;
        _sut.SetTokens(
            "access",
            refreshToken: "refresh",
            expiresIn: TimeSpan.FromHours(1),
            refreshTokenExpiresIn: TimeSpan.FromHours(8));
        var after = DateTime.UtcNow;

        Assert.Equal("access", _sut.Token);
        Assert.Equal("refresh", _sut.RefreshToken);
        Assert.NotNull(_sut.TokenSetAt);
        Assert.InRange(_sut.TokenSetAt!.Value, before, after);
        Assert.NotNull(_sut.ExpiresAtUtc);
        Assert.InRange(_sut.ExpiresAtUtc!.Value, before.AddHours(1).AddSeconds(-1), after.AddHours(1).AddSeconds(1));
        Assert.NotNull(_sut.RefreshTokenExpiresAtUtc);
        Assert.InRange(_sut.RefreshTokenExpiresAtUtc!.Value, before.AddHours(8).AddSeconds(-1), after.AddHours(8).AddSeconds(1));
    }

    [Fact]
    public void SetTokens_PreservesExistingRefreshTokenWhenNullPassed()
    {
        _sut.SetTokens("a1", refreshToken: "original-refresh");
        _sut.SetTokens("a2", refreshToken: null);

        Assert.Equal("a2", _sut.Token);
        Assert.Equal("original-refresh", _sut.RefreshToken);
    }

    [Fact]
    public void SetTokens_PreservesExistingExpiryWhenNullPassed()
    {
        _sut.SetTokens("a1", expiresIn: TimeSpan.FromHours(2));
        var originalExpiry = _sut.ExpiresAtUtc;

        _sut.SetTokens("a2", expiresIn: null);

        Assert.Equal(originalExpiry, _sut.ExpiresAtUtc);
    }

    [Fact]
    public void SetTokens_PreservesExistingRefreshTokenExpiryWhenNullPassed()
    {
        _sut.SetTokens("a1", refreshTokenExpiresIn: TimeSpan.FromHours(4));
        var originalExpiry = _sut.RefreshTokenExpiresAtUtc;

        _sut.SetTokens("a2", refreshTokenExpiresIn: null);

        Assert.Equal(originalExpiry, _sut.RefreshTokenExpiresAtUtc);
    }

    [Fact]
    public void SetTokens_UpdatesExpiresAtUtcFromExpiresIn()
    {
        var before = DateTime.UtcNow;
        _sut.SetTokens("tok", expiresIn: TimeSpan.FromMinutes(45));
        var after = DateTime.UtcNow;

        Assert.NotNull(_sut.ExpiresAtUtc);
        Assert.InRange(
            _sut.ExpiresAtUtc!.Value,
            before.AddMinutes(45).AddSeconds(-1),
            after.AddMinutes(45).AddSeconds(1));
    }

    [Fact]
    public void SetTokens_UpdatesRefreshTokenExpiresAtUtcFromRefreshTokenExpiresIn()
    {
        var before = DateTime.UtcNow;
        _sut.SetTokens("tok", refreshTokenExpiresIn: TimeSpan.FromHours(12));
        var after = DateTime.UtcNow;

        Assert.NotNull(_sut.RefreshTokenExpiresAtUtc);
        Assert.InRange(
            _sut.RefreshTokenExpiresAtUtc!.Value,
            before.AddHours(12).AddSeconds(-1),
            after.AddHours(12).AddSeconds(1));
    }

    [Fact]
    public void SetTokens_FiresTokenChangedEvent()
    {
        var fired = false;
        _sut.TokenChanged += () => fired = true;

        _sut.SetTokens("tok");

        Assert.True(fired);
    }

    [Fact]
    public void SetTokens_HandlerExceptionDoesNotPropagate()
    {
        _sut.TokenChanged += () => throw new InvalidOperationException("boom");

        var ex = Record.Exception(() => _sut.SetTokens("tok", "refresh"));

        Assert.Null(ex);
        Assert.Equal("tok", _sut.Token);
    }

    [Fact]
    public void SetTokens_AccessTokenOnlyLeavesRefreshTokenNull()
    {
        _sut.SetTokens("access-only");

        Assert.Equal("access-only", _sut.Token);
        Assert.Null(_sut.RefreshToken);
    }

    #endregion

    #region IsTokenExpiringSoon

    [Fact]
    public void IsTokenExpiringSoon_ReturnsFalseWhenNoExpirySet()
    {
        _sut.SetToken("tok");

        Assert.False(_sut.IsTokenExpiringSoon);
    }

    [Fact]
    public void IsTokenExpiringSoon_ReturnsFalseWhenExpiryFarInFuture()
    {
        _sut.SetTokens("tok", expiresIn: TimeSpan.FromHours(2));

        Assert.False(_sut.IsTokenExpiringSoon);
    }

    [Fact]
    public void IsTokenExpiringSoon_ReturnsTrueWhenExpiryWithin30Minutes()
    {
        _sut.SetTokens("tok", expiresIn: TimeSpan.FromMinutes(15));

        Assert.True(_sut.IsTokenExpiringSoon);
    }

    [Fact]
    public void IsTokenExpiringSoon_ReturnsTrueWhenTokenAlreadyExpired()
    {
        _sut.SetTokens("tok", expiresIn: TimeSpan.FromSeconds(-1));

        Assert.True(_sut.IsTokenExpiringSoon);
    }

    [Fact]
    public void IsTokenExpiringSoon_ReturnsTrueAtExactly30MinuteBoundary()
    {
        // 30 minutes means UtcNow >= expiry - 30min, i.e. expiry <= UtcNow + 30min
        // Setting expiresIn to exactly 30 minutes: expiry = UtcNow + 30min
        // Check: UtcNow >= (UtcNow + 30min) - 30min => UtcNow >= UtcNow => true
        _sut.SetTokens("tok", expiresIn: TimeSpan.FromMinutes(30));

        Assert.True(_sut.IsTokenExpiringSoon);
    }

    #endregion

    #region CanRefresh

    [Fact]
    public void CanRefresh_ReturnsFalseWhenNoRefreshToken()
    {
        _sut.SetTokens("tok");

        Assert.False(_sut.CanRefresh);
    }

    [Fact]
    public void CanRefresh_ReturnsFalseWhenRefreshTokenIsWhitespace()
    {
        _sut.SetTokens("tok", refreshToken: "   ");

        Assert.False(_sut.CanRefresh);
    }

    [Fact]
    public void CanRefresh_ReturnsFalseWhenRefreshTokenIsEmpty()
    {
        _sut.SetTokens("tok", refreshToken: "");

        Assert.False(_sut.CanRefresh);
    }

    [Fact]
    public void CanRefresh_ReturnsTrueWhenRefreshTokenSetWithNoExpiry()
    {
        _sut.SetTokens("tok", refreshToken: "valid-refresh");

        Assert.True(_sut.CanRefresh);
    }

    [Fact]
    public void CanRefresh_ReturnsTrueWhenRefreshTokenSetWithFutureExpiry()
    {
        _sut.SetTokens("tok", refreshToken: "valid-refresh", refreshTokenExpiresIn: TimeSpan.FromHours(8));

        Assert.True(_sut.CanRefresh);
    }

    [Fact]
    public void CanRefresh_ReturnsFalseWhenRefreshTokenExpired()
    {
        _sut.SetTokens("tok", refreshToken: "expired-refresh", refreshTokenExpiresIn: TimeSpan.FromSeconds(-1));

        Assert.False(_sut.CanRefresh);
    }

    #endregion

    #region ClearToken

    [Fact]
    public void ClearToken_ClearsAllProperties()
    {
        _sut.SetTokens("tok", "refresh", TimeSpan.FromHours(1), TimeSpan.FromHours(8));

        _sut.ClearToken();

        Assert.Null(_sut.Token);
        Assert.Null(_sut.RefreshToken);
        Assert.Null(_sut.TokenSetAt);
        Assert.Null(_sut.ExpiresAtUtc);
        Assert.Null(_sut.RefreshTokenExpiresAtUtc);
    }

    [Fact]
    public void ClearToken_ClearsPendingCookieUpdateFlag()
    {
        _sut.SetToken("tok");
        _sut.MarkCookieUpdatePending();
        Assert.True(_sut.HasPendingCookieUpdate);

        _sut.ClearToken();

        Assert.False(_sut.HasPendingCookieUpdate);
    }

    #endregion

    #region MarkCookieUpdatePending / ClearCookieUpdatePending

    [Fact]
    public void MarkCookieUpdatePending_SetsFlagToTrue()
    {
        _sut.MarkCookieUpdatePending();

        Assert.True(_sut.HasPendingCookieUpdate);
    }

    [Fact]
    public void ClearCookieUpdatePending_SetsFlagToFalse()
    {
        _sut.MarkCookieUpdatePending();
        _sut.ClearCookieUpdatePending();

        Assert.False(_sut.HasPendingCookieUpdate);
    }

    [Fact]
    public void ClearCookieUpdatePending_NoOpWhenAlreadyFalse()
    {
        _sut.ClearCookieUpdatePending();

        Assert.False(_sut.HasPendingCookieUpdate);
    }

    #endregion
}
