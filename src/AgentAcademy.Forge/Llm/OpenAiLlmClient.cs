using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AgentAcademy.Forge.Llm;

/// <summary>
/// ILlmClient implementation calling the OpenAI Chat Completions API.
/// Reads API key from OPENAI_API_KEY environment variable.
/// Uses raw HttpClient — no third-party SDK dependencies.
/// </summary>
public sealed class OpenAiLlmClient : ILlmClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Create a client using the OPENAI_API_KEY environment variable.
    /// </summary>
    /// <param name="baseUrl">API base URL (default: https://api.openai.com/v1).</param>
    /// <param name="httpClient">Optional pre-configured HttpClient (for testing).</param>
    public OpenAiLlmClient(string? baseUrl = null, HttpClient? httpClient = null)
    {
        _baseUrl = (baseUrl ?? "https://api.openai.com/v1").TrimEnd('/');

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey) && httpClient is null)
            throw new InvalidOperationException(
                "OPENAI_API_KEY environment variable is not set. Set it before creating OpenAiLlmClient.");

        _http = httpClient ?? new HttpClient();

        if (apiKey is not null)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        // Create a linked CTS for request-level timeout, separate from caller cancellation
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));

        var requestBody = BuildRequestBody(request);
        var jsonContent = JsonSerializer.Serialize(requestBody, JsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
        {
            Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
        };

        var sw = Stopwatch.StartNew();

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await _http.SendAsync(httpRequest, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout (not caller cancellation)
            throw new LlmClientException(LlmErrorKind.Timeout,
                $"Request timed out after {request.TimeoutSeconds}s");
        }
        catch (OperationCanceledException)
        {
            throw; // Caller cancellation — propagate as-is
        }
        catch (HttpRequestException ex)
        {
            throw new LlmClientException(LlmErrorKind.Transient,
                $"HTTP request failed: {ex.Message}", ex);
        }

        sw.Stop();

        using (httpResponse)
        {
            var responseBody = await httpResponse.Content.ReadAsStringAsync(ct);

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorKind = ClassifyHttpError(httpResponse.StatusCode);
                throw new LlmClientException(errorKind,
                    $"OpenAI API returned {(int)httpResponse.StatusCode}: {Truncate(responseBody, 500)}");
            }

            return ParseResponse(responseBody, sw.ElapsedMilliseconds);
        }
    }

    private static object BuildRequestBody(LlmRequest request)
    {
        var messages = new List<object>
        {
            new { role = "system", content = request.SystemMessage },
            new { role = "user", content = request.UserMessage }
        };

        var body = new Dictionary<string, object>
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxTokens
        };

        if (request.JsonMode)
        {
            body["response_format"] = new { type = "json_object" };
        }

        return body;
    }

    private static LlmResponse ParseResponse(string responseBody, long latencyMs)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        var choices = root.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
            throw new LlmClientException(LlmErrorKind.MalformedResponse,
                "OpenAI response contained no choices");

        var message = choices[0].GetProperty("message");
        var content = message.GetProperty("content").GetString()
            ?? throw new LlmClientException(LlmErrorKind.MalformedResponse,
                "OpenAI response message content was null");

        var model = root.GetProperty("model").GetString() ?? "unknown";

        var inputTokens = 0;
        var outputTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            inputTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
            outputTokens = usage.TryGetProperty("completion_tokens", out var ct2) ? ct2.GetInt32() : 0;
        }

        return new LlmResponse
        {
            Content = content,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Model = model,
            LatencyMs = latencyMs
        };
    }

    private static LlmErrorKind ClassifyHttpError(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.TooManyRequests => LlmErrorKind.Transient,
        HttpStatusCode.ServiceUnavailable => LlmErrorKind.Transient,
        HttpStatusCode.GatewayTimeout => LlmErrorKind.Timeout,
        HttpStatusCode.Unauthorized => LlmErrorKind.Authentication,
        HttpStatusCode.Forbidden => LlmErrorKind.Authentication,
        HttpStatusCode.BadRequest => LlmErrorKind.BadRequest,
        _ when (int)statusCode >= 500 => LlmErrorKind.Transient,
        _ => LlmErrorKind.Unknown
    };

    private static string Truncate(string s, int maxLength) =>
        s.Length > maxLength ? s[..maxLength] + "..." : s;

    public void Dispose()
    {
        _http.Dispose();
    }
}
