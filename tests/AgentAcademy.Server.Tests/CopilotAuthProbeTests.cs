using System.Net;
using System.Net.Http.Headers;
using AgentAcademy.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

#region GitHubCopilotAuthProbe Tests

public class GitHubCopilotAuthProbeTests : IDisposable
{
    private readonly MockHandler _handler = new();
    private readonly CopilotTokenProvider _tokenProvider = new();
    private readonly ConfigurationBuilder _configBuilder = new();
    private HttpClient? _httpClient;

    public void Dispose()
    {
        _httpClient?.Dispose();
        _handler.Dispose();
    }

    private GitHubCopilotAuthProbe CreateSut(
        Dictionary<string, string?>? configOverrides = null)
    {
        var config = _configBuilder
            .AddInMemoryCollection(configOverrides ?? new Dictionary<string, string?>())
            .Build();

        _httpClient = new HttpClient(_handler)
        {
            BaseAddress = new Uri("https://api.github.com/")
        };

        return new GitHubCopilotAuthProbe(
            _httpClient,
            _tokenProvider,
            config,
            NullLogger<GitHubCopilotAuthProbe>.Instance);
    }

    // ── ProbeAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task ProbeAsync_NoToken_ReturnsSkipped()
    {
        var sut = CreateSut();

        var result = await sut.ProbeAsync();

        Assert.Equal(CopilotAuthProbeResult.Skipped, result);
    }

    [Fact]
    public async Task ProbeAsync_EmptyToken_ReturnsSkipped()
    {
        _tokenProvider.SetToken("   ");
        var sut = CreateSut(new Dictionary<string, string?>
        {
            ["Copilot:GitHubToken"] = ""
        });

        var result = await sut.ProbeAsync();

        Assert.Equal(CopilotAuthProbeResult.Skipped, result);
    }

    [Fact]
    public async Task ProbeAsync_HealthyResponse_ReturnsHealthy()
    {
        _tokenProvider.SetToken("ghp_valid_token");
        _handler.StatusCode = HttpStatusCode.OK;
        var sut = CreateSut();

        var result = await sut.ProbeAsync();

        Assert.Equal(CopilotAuthProbeResult.Healthy, result);
    }

    [Fact]
    public async Task ProbeAsync_Unauthorized_ReturnsAuthFailed()
    {
        _tokenProvider.SetToken("ghp_bad_token");
        _handler.StatusCode = HttpStatusCode.Unauthorized;
        var sut = CreateSut();

        var result = await sut.ProbeAsync();

        Assert.Equal(CopilotAuthProbeResult.AuthFailed, result);
    }

    [Fact]
    public async Task ProbeAsync_Forbidden_ReturnsAuthFailed()
    {
        _tokenProvider.SetToken("ghp_forbidden_token");
        _handler.StatusCode = HttpStatusCode.Forbidden;
        var sut = CreateSut();

        var result = await sut.ProbeAsync();

        Assert.Equal(CopilotAuthProbeResult.AuthFailed, result);
    }

    [Fact]
    public async Task ProbeAsync_ServerError_ReturnsTransientFailure()
    {
        _tokenProvider.SetToken("ghp_valid");
        _handler.StatusCode = HttpStatusCode.InternalServerError;
        var sut = CreateSut();

        var result = await sut.ProbeAsync();

        Assert.Equal(CopilotAuthProbeResult.TransientFailure, result);
    }

    [Fact]
    public async Task ProbeAsync_ServiceUnavailable_ReturnsTransientFailure()
    {
        _tokenProvider.SetToken("ghp_valid");
        _handler.StatusCode = HttpStatusCode.ServiceUnavailable;
        var sut = CreateSut();

        var result = await sut.ProbeAsync();

        Assert.Equal(CopilotAuthProbeResult.TransientFailure, result);
    }

    [Fact]
    public async Task ProbeAsync_HttpRequestException_ReturnsTransientFailure()
    {
        _tokenProvider.SetToken("ghp_valid");
        _handler.ThrowOnSend = true;
        var sut = CreateSut();

        var result = await sut.ProbeAsync();

        Assert.Equal(CopilotAuthProbeResult.TransientFailure, result);
    }

    [Fact]
    public async Task ProbeAsync_Timeout_ReturnsTransientFailure()
    {
        _tokenProvider.SetToken("ghp_valid");
        _handler.ThrowTimeoutOnSend = true;
        var sut = CreateSut();

        var result = await sut.ProbeAsync();

        Assert.Equal(CopilotAuthProbeResult.TransientFailure, result);
    }

    [Fact]
    public async Task ProbeAsync_UsesTokenFromProvider()
    {
        _tokenProvider.SetToken("ghp_provider_token");
        _handler.StatusCode = HttpStatusCode.OK;
        _handler.CaptureRequest = true;
        var sut = CreateSut(new Dictionary<string, string?>
        {
            ["Copilot:GitHubToken"] = "ghp_config_token"
        });

        await sut.ProbeAsync();

        Assert.NotNull(_handler.LastRequest);
        Assert.Equal("Bearer", _handler.LastRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("ghp_provider_token", _handler.LastRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task ProbeAsync_FallsBackToConfigToken()
    {
        // Provider token is null — should fall back to config
        _handler.StatusCode = HttpStatusCode.OK;
        _handler.CaptureRequest = true;
        var sut = CreateSut(new Dictionary<string, string?>
        {
            ["Copilot:GitHubToken"] = "ghp_from_config"
        });

        var result = await sut.ProbeAsync();

        Assert.Equal(CopilotAuthProbeResult.Healthy, result);
        Assert.NotNull(_handler.LastRequest);
        Assert.Equal("ghp_from_config", _handler.LastRequest!.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task ProbeAsync_SendsGetToUserEndpoint()
    {
        _tokenProvider.SetToken("ghp_valid");
        _handler.StatusCode = HttpStatusCode.OK;
        _handler.CaptureRequest = true;
        var sut = CreateSut();

        await sut.ProbeAsync();

        Assert.NotNull(_handler.LastRequest);
        Assert.Equal(HttpMethod.Get, _handler.LastRequest!.Method);
        Assert.Equal("https://api.github.com/user", _handler.LastRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task ProbeAsync_SetsAcceptJsonHeader()
    {
        _tokenProvider.SetToken("ghp_valid");
        _handler.StatusCode = HttpStatusCode.OK;
        _handler.CaptureRequest = true;
        var sut = CreateSut();

        await sut.ProbeAsync();

        Assert.NotNull(_handler.LastRequest);
        Assert.Contains(
            new MediaTypeWithQualityHeaderValue("application/json"),
            _handler.LastRequest!.Headers.Accept);
    }

    [Fact]
    public async Task ProbeAsync_WhenHandlerThrowsTimeout_ReturnsTransientFailure()
    {
        _tokenProvider.SetToken("ghp_valid");
        _handler.ThrowTimeoutOnSend = true;
        var sut = CreateSut();

        var result = await sut.ProbeAsync();

        Assert.Equal(CopilotAuthProbeResult.TransientFailure, result);
    }

    // ── RefreshTokenAsync ───────────────────────────────────────────────

    [Fact]
    public async Task RefreshTokenAsync_MissingClientId_ReturnsNull()
    {
        var sut = CreateSut(new Dictionary<string, string?>
        {
            ["GitHub:ClientSecret"] = "secret123"
        });

        var result = await sut.RefreshTokenAsync("refresh_tok");

        Assert.Null(result);
    }

    [Fact]
    public async Task RefreshTokenAsync_MissingClientSecret_ReturnsNull()
    {
        var sut = CreateSut(new Dictionary<string, string?>
        {
            ["GitHub:ClientId"] = "client123"
        });

        var result = await sut.RefreshTokenAsync("refresh_tok");

        Assert.Null(result);
    }

    [Fact]
    public async Task RefreshTokenAsync_EmptyClientId_ReturnsNull()
    {
        var sut = CreateSut(new Dictionary<string, string?>
        {
            ["GitHub:ClientId"] = "  ",
            ["GitHub:ClientSecret"] = "secret123"
        });

        var result = await sut.RefreshTokenAsync("refresh_tok");

        Assert.Null(result);
    }

    [Fact]
    public async Task RefreshTokenAsync_HttpError_ReturnsNull()
    {
        _handler.StatusCode = HttpStatusCode.BadRequest;
        _handler.ResponseBody = """{"error":"bad_request"}""";
        var sut = CreateSut(new Dictionary<string, string?>
        {
            ["GitHub:ClientId"] = "client123",
            ["GitHub:ClientSecret"] = "secret123"
        });

        var result = await sut.RefreshTokenAsync("refresh_tok");

        Assert.Null(result);
    }

    [Fact]
    public async Task RefreshTokenAsync_ErrorFieldInResponse_ReturnsNull()
    {
        _handler.StatusCode = HttpStatusCode.OK;
        _handler.ResponseBody = """{"error":"bad_refresh_token","error_description":"Token expired"}""";
        var sut = CreateSut(new Dictionary<string, string?>
        {
            ["GitHub:ClientId"] = "client123",
            ["GitHub:ClientSecret"] = "secret123"
        });

        var result = await sut.RefreshTokenAsync("refresh_tok");

        Assert.Null(result);
    }

    [Fact]
    public async Task RefreshTokenAsync_EmptyAccessToken_ReturnsNull()
    {
        _handler.StatusCode = HttpStatusCode.OK;
        _handler.ResponseBody = """{"access_token":"","token_type":"bearer"}""";
        var sut = CreateSut(new Dictionary<string, string?>
        {
            ["GitHub:ClientId"] = "client123",
            ["GitHub:ClientSecret"] = "secret123"
        });

        var result = await sut.RefreshTokenAsync("refresh_tok");

        Assert.Null(result);
    }

    [Fact]
    public async Task RefreshTokenAsync_Success_ReturnsTokenRefreshResult()
    {
        _handler.StatusCode = HttpStatusCode.OK;
        _handler.ResponseBody = """
        {
            "access_token": "ghu_new_access",
            "token_type": "bearer",
            "refresh_token": "ghr_new_refresh",
            "expires_in": 28800,
            "refresh_token_expires_in": 15897600
        }
        """;
        var sut = CreateSut(new Dictionary<string, string?>
        {
            ["GitHub:ClientId"] = "client123",
            ["GitHub:ClientSecret"] = "secret123"
        });

        var result = await sut.RefreshTokenAsync("ghr_old_refresh");

        Assert.NotNull(result);
        Assert.Equal("ghu_new_access", result.AccessToken);
        Assert.Equal("ghr_new_refresh", result.RefreshToken);
        Assert.Equal(TimeSpan.FromSeconds(28800), result.ExpiresIn);
        Assert.Equal(TimeSpan.FromSeconds(15897600), result.RefreshTokenExpiresIn);
    }

    [Fact]
    public async Task RefreshTokenAsync_NoRefreshTokenInResponse_ReturnsNullRefreshToken()
    {
        _handler.StatusCode = HttpStatusCode.OK;
        _handler.ResponseBody = """
        {
            "access_token": "ghu_new_access",
            "token_type": "bearer",
            "expires_in": 28800
        }
        """;
        var sut = CreateSut(new Dictionary<string, string?>
        {
            ["GitHub:ClientId"] = "client123",
            ["GitHub:ClientSecret"] = "secret123"
        });

        var result = await sut.RefreshTokenAsync("ghr_old_refresh");

        Assert.NotNull(result);
        Assert.Equal("ghu_new_access", result.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Equal(TimeSpan.FromSeconds(28800), result.ExpiresIn);
        Assert.Null(result.RefreshTokenExpiresIn);
    }

    [Fact]
    public async Task RefreshTokenAsync_MinimalSuccessResponse_ParsesCorrectly()
    {
        _handler.StatusCode = HttpStatusCode.OK;
        _handler.ResponseBody = """{"access_token":"ghu_min","token_type":"bearer"}""";
        var sut = CreateSut(new Dictionary<string, string?>
        {
            ["GitHub:ClientId"] = "client123",
            ["GitHub:ClientSecret"] = "secret123"
        });

        var result = await sut.RefreshTokenAsync("ghr_old");

        Assert.NotNull(result);
        Assert.Equal("ghu_min", result.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Null(result.ExpiresIn);
        Assert.Null(result.RefreshTokenExpiresIn);
    }

    [Fact]
    public async Task RefreshTokenAsync_HttpRequestException_ReturnsNull()
    {
        _handler.ThrowOnSend = true;
        var sut = CreateSut(new Dictionary<string, string?>
        {
            ["GitHub:ClientId"] = "client123",
            ["GitHub:ClientSecret"] = "secret123"
        });

        var result = await sut.RefreshTokenAsync("ghr_refresh");

        Assert.Null(result);
    }

    [Fact]
    public async Task RefreshTokenAsync_Timeout_ReturnsNull()
    {
        _handler.ThrowTimeoutOnSend = true;
        var sut = CreateSut(new Dictionary<string, string?>
        {
            ["GitHub:ClientId"] = "client123",
            ["GitHub:ClientSecret"] = "secret123"
        });

        var result = await sut.RefreshTokenAsync("ghr_refresh");

        Assert.Null(result);
    }

    [Fact]
    public async Task RefreshTokenAsync_PostsToCorrectUrl()
    {
        _handler.StatusCode = HttpStatusCode.OK;
        _handler.ResponseBody = """{"access_token":"ghu_new","token_type":"bearer"}""";
        _handler.CaptureRequest = true;
        var sut = CreateSut(new Dictionary<string, string?>
        {
            ["GitHub:ClientId"] = "client123",
            ["GitHub:ClientSecret"] = "secret123"
        });

        await sut.RefreshTokenAsync("ghr_old");

        Assert.NotNull(_handler.LastRequest);
        Assert.Equal(HttpMethod.Post, _handler.LastRequest!.Method);
        Assert.Equal("https://github.com/login/oauth/access_token",
            _handler.LastRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task RefreshTokenAsync_SendsFormEncodedBody()
    {
        _handler.StatusCode = HttpStatusCode.OK;
        _handler.ResponseBody = """{"access_token":"ghu_new","token_type":"bearer"}""";
        _handler.CaptureRequest = true;
        _handler.CaptureRequestBody = true;
        var sut = CreateSut(new Dictionary<string, string?>
        {
            ["GitHub:ClientId"] = "cid_test",
            ["GitHub:ClientSecret"] = "csec_test"
        });

        await sut.RefreshTokenAsync("ghr_my_refresh");

        Assert.NotNull(_handler.LastRequestBody);
        Assert.Contains("grant_type=refresh_token", _handler.LastRequestBody);
        Assert.Contains("refresh_token=ghr_my_refresh", _handler.LastRequestBody);
        Assert.Contains("client_id=cid_test", _handler.LastRequestBody);
        Assert.Contains("client_secret=csec_test", _handler.LastRequestBody);
    }

    // ── Mock handler ────────────────────────────────────────────────────

    private sealed class MockHandler : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public string ResponseBody { get; set; } = "{}";
        public bool ThrowOnSend { get; set; }
        public bool ThrowTimeoutOnSend { get; set; }
        public bool CaptureRequest { get; set; }
        public bool CaptureRequestBody { get; set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (ThrowOnSend)
                throw new HttpRequestException("Simulated network error");

            if (ThrowTimeoutOnSend)
                throw new OperationCanceledException("Simulated timeout",
                    new TimeoutException(), CancellationToken.None);

            if (CaptureRequest)
                LastRequest = request;

            if (CaptureRequestBody && request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(ct);

            return new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(ResponseBody)
            };
        }
    }
}

#endregion

#region CopilotSdkSender.ClassifyError Tests

public class CopilotSdkSenderClassifyErrorTests
{
    [Fact]
    public void ClassifyError_Authentication_ReturnsCopilotAuthException()
    {
        var err = CreateSessionErrorEvent("authentication", "Token expired");

        var ex = CopilotSdkSender.ClassifyError(err);

        var authEx = Assert.IsType<CopilotAuthException>(ex);
        Assert.Equal("authentication", authEx.ErrorType);
    }

    [Fact]
    public void ClassifyError_Authorization_ReturnsCopilotAuthorizationException()
    {
        var err = CreateSessionErrorEvent("authorization", "Insufficient scope");

        var ex = CopilotSdkSender.ClassifyError(err);

        var authzEx = Assert.IsType<CopilotAuthorizationException>(ex);
        Assert.Equal("authorization", authzEx.ErrorType);
    }

    [Fact]
    public void ClassifyError_Quota_ReturnsCopilotQuotaException()
    {
        var err = CreateSessionErrorEvent("quota", "Quota exceeded");

        var ex = CopilotSdkSender.ClassifyError(err);

        var quotaEx = Assert.IsType<CopilotQuotaException>(ex);
        Assert.Equal("quota", quotaEx.ErrorType);
    }

    [Fact]
    public void ClassifyError_RateLimit_ReturnsCopilotQuotaException()
    {
        var err = CreateSessionErrorEvent("rate_limit", "Too many requests");

        var ex = CopilotSdkSender.ClassifyError(err);

        var quotaEx = Assert.IsType<CopilotQuotaException>(ex);
        Assert.Equal("rate_limit", quotaEx.ErrorType);
    }

    [Fact]
    public void ClassifyError_NullErrorType_ReturnsCopilotTransientException()
    {
        var err = CreateSessionErrorEvent(null, "Unknown error");

        var ex = CopilotSdkSender.ClassifyError(err);

        Assert.IsType<CopilotTransientException>(ex);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("something_unknown")]
    [InlineData("")]
    public void ClassifyError_UnknownErrorType_ReturnsCopilotTransientException(string errorType)
    {
        var err = CreateSessionErrorEvent(errorType, "Something went wrong");

        var ex = CopilotSdkSender.ClassifyError(err);

        Assert.IsType<CopilotTransientException>(ex);
    }

    [Fact]
    public void ClassifyError_PreservesErrorMessage()
    {
        var err = CreateSessionErrorEvent("authentication", "Your token is expired, please re-authenticate");

        var ex = CopilotSdkSender.ClassifyError(err);

        Assert.Equal("Your token is expired, please re-authenticate", ex.Message);
    }

    [Fact]
    public void ClassifyError_NullMessage_UsesDefaultMessage()
    {
        var err = CreateSessionErrorEvent("authentication", null);

        var ex = CopilotSdkSender.ClassifyError(err);

        Assert.Equal("Unknown Copilot session error", ex.Message);
    }

    [Fact]
    public void ClassifyError_CaseInsensitive_UpperCase()
    {
        var err = CreateSessionErrorEvent("AUTHENTICATION", "Token expired");

        var ex = CopilotSdkSender.ClassifyError(err);

        Assert.IsType<CopilotAuthException>(ex);
    }

    [Fact]
    public void ClassifyError_CaseInsensitive_MixedCase()
    {
        var err = CreateSessionErrorEvent("Rate_Limit", "Throttled");

        var ex = CopilotSdkSender.ClassifyError(err);

        Assert.IsType<CopilotQuotaException>(ex);
    }

    [Fact]
    public void ClassifyError_QuotaException_PreservesOriginalErrorType()
    {
        var rateLimitErr = CreateSessionErrorEvent("rate_limit", "Rate limited");
        var quotaErr = CreateSessionErrorEvent("quota", "Quota exceeded");

        var rateLimitEx = (CopilotQuotaException)CopilotSdkSender.ClassifyError(rateLimitErr);
        var quotaEx = (CopilotQuotaException)CopilotSdkSender.ClassifyError(quotaErr);

        Assert.Equal("rate_limit", rateLimitEx.ErrorType);
        Assert.Equal("quota", quotaEx.ErrorType);
    }

    /// <summary>
    /// Creates a SessionErrorEvent via reflection. The SDK type does not
    /// expose a public constructor suitable for unit tests.
    /// </summary>
    private static GitHub.Copilot.SDK.SessionErrorEvent CreateSessionErrorEvent(
        string? errorType, string? message)
    {
        var dataType = typeof(GitHub.Copilot.SDK.SessionErrorEvent)
            .GetProperty("Data")!.PropertyType;

        var data = Activator.CreateInstance(dataType)!;
        dataType.GetProperty("ErrorType")?.SetValue(data, errorType);
        dataType.GetProperty("Message")?.SetValue(data, message);

        var evt = Activator.CreateInstance(typeof(GitHub.Copilot.SDK.SessionErrorEvent))!;
        typeof(GitHub.Copilot.SDK.SessionErrorEvent)
            .GetProperty("Data")!.SetValue(evt, data);

        return (GitHub.Copilot.SDK.SessionErrorEvent)evt;
    }
}

#endregion
