using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Services;

internal enum CopilotAuthProbeResult
{
    Skipped,
    Healthy,
    AuthFailed,
    TransientFailure
}

/// <summary>
/// Result of a token refresh attempt.
/// </summary>
internal sealed record TokenRefreshResult(
    string AccessToken,
    string? RefreshToken,
    TimeSpan? ExpiresIn,
    TimeSpan? RefreshTokenExpiresIn);

internal interface ICopilotAuthProbe
{
    Task<CopilotAuthProbeResult> ProbeAsync(CancellationToken ct = default);

    /// <summary>
    /// Exchange a refresh token for a new access token via GitHub's OAuth endpoint.
    /// Returns null if the refresh fails (expired refresh token, network error, etc.).
    /// </summary>
    Task<TokenRefreshResult?> RefreshTokenAsync(string refreshToken, CancellationToken ct = default);
}

internal sealed class GitHubCopilotAuthProbe : ICopilotAuthProbe
{
    private readonly HttpClient _httpClient;
    private readonly ICopilotTokenProvider _tokenProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GitHubCopilotAuthProbe> _logger;

    public GitHubCopilotAuthProbe(
        HttpClient httpClient,
        ICopilotTokenProvider tokenProvider,
        IConfiguration configuration,
        ILogger<GitHubCopilotAuthProbe> logger)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<CopilotAuthProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        var token = ResolveToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogDebug("Skipping Copilot auth probe because no GitHub token is available");
            return CopilotAuthProbeResult.Skipped;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            if (response.IsSuccessStatusCode)
            {
                return CopilotAuthProbeResult.Healthy;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return CopilotAuthProbeResult.AuthFailed;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "Copilot auth probe received transient response {StatusCode}: {Body}",
                (int)response.StatusCode,
                string.IsNullOrWhiteSpace(body) ? "<empty>" : body);
            return CopilotAuthProbeResult.TransientFailure;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Copilot auth probe timed out");
            return CopilotAuthProbeResult.TransientFailure;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Copilot auth probe failed with an HTTP transport error");
            return CopilotAuthProbeResult.TransientFailure;
        }
    }

    public async Task<TokenRefreshResult?> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        var clientId = _configuration["GitHub:ClientId"];
        var clientSecret = _configuration["GitHub:ClientSecret"];

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            _logger.LogWarning("Cannot refresh token — GitHub:ClientId or GitHub:ClientSecret not configured");
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
            });

            using var response = await _httpClient.SendAsync(request, ct);

            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Token refresh failed with HTTP {StatusCode}: {Body}",
                    (int)response.StatusCode, json);
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // GitHub returns errors as 200 with an "error" field
            if (root.TryGetProperty("error", out var errorProp))
            {
                var errorDesc = root.TryGetProperty("error_description", out var descProp)
                    ? descProp.GetString() : null;
                _logger.LogWarning("Token refresh returned error: {Error} — {Description}",
                    errorProp.GetString(), errorDesc);
                return null;
            }

            var accessToken = root.GetProperty("access_token").GetString();
            if (string.IsNullOrEmpty(accessToken))
            {
                _logger.LogWarning("Token refresh returned empty access_token");
                return null;
            }

            var newRefreshToken = root.TryGetProperty("refresh_token", out var rtProp)
                ? rtProp.GetString() : null;

            TimeSpan? expiresIn = root.TryGetProperty("expires_in", out var eiProp) && eiProp.TryGetInt64(out var eiVal)
                ? TimeSpan.FromSeconds(eiVal) : null;

            TimeSpan? refreshTokenExpiresIn = root.TryGetProperty("refresh_token_expires_in", out var rteiProp) && rteiProp.TryGetInt64(out var rteiVal)
                ? TimeSpan.FromSeconds(rteiVal) : null;

            _logger.LogInformation("Token refresh succeeded — new token expires in {ExpiresIn}",
                expiresIn?.ToString() ?? "unknown");

            return new TokenRefreshResult(accessToken, newRefreshToken, expiresIn, refreshTokenExpiresIn);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Token refresh failed with an unexpected error");
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Token refresh timed out");
            return null;
        }
    }

    /// <summary>
    /// Resolves the best available GitHub token, including env vars.
    /// Unlike CopilotClientFactory.ResolveToken (which returns null for SDK
    /// fallback), this checks env vars directly because the probe makes raw
    /// HTTP calls without the SDK.
    /// </summary>
    private string? ResolveToken()
    {
        if (!string.IsNullOrWhiteSpace(_tokenProvider.Token))
            return _tokenProvider.Token;

        var configToken = _configuration["Copilot:GitHubToken"];
        if (!string.IsNullOrWhiteSpace(configToken))
            return configToken;

        return Environment.GetEnvironmentVariable("COPILOT_GITHUB_TOKEN")
            ?? Environment.GetEnvironmentVariable("GH_TOKEN")
            ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    }
}
