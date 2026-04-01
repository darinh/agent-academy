using System.Net;
using System.Net.Http.Headers;

namespace AgentAcademy.Server.Services;

internal enum CopilotAuthProbeResult
{
    Skipped,
    Healthy,
    AuthFailed,
    TransientFailure
}

internal interface ICopilotAuthProbe
{
    Task<CopilotAuthProbeResult> ProbeAsync(CancellationToken ct = default);
}

internal sealed class GitHubCopilotAuthProbe : ICopilotAuthProbe
{
    private readonly HttpClient _httpClient;
    private readonly CopilotTokenProvider _tokenProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GitHubCopilotAuthProbe> _logger;

    public GitHubCopilotAuthProbe(
        HttpClient httpClient,
        CopilotTokenProvider tokenProvider,
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
