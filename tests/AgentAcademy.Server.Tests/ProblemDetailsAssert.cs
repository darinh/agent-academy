using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Assertion helpers for ProblemDetails error responses.
/// </summary>
public static class ProblemDetailsAssert
{
    /// <summary>
    /// Asserts that an ObjectResult value is ProblemDetails containing the expected error code.
    /// </summary>
    public static ProblemDetails HasCode(object? value, string expectedCode)
    {
        Assert.NotNull(value);
        var pd = Assert.IsType<ProblemDetails>(value);
        Assert.True(
            pd.Extensions.TryGetValue("code", out var code) && code?.ToString() == expectedCode,
            $"Expected ProblemDetails with code '{expectedCode}', got code '{code}'. Detail: '{pd.Detail}'");
        return pd;
    }

    /// <summary>
    /// Asserts that an ObjectResult value is ProblemDetails containing the expected substring in Detail.
    /// </summary>
    public static ProblemDetails HasDetail(object? value, string expectedSubstring)
    {
        Assert.NotNull(value);
        var pd = Assert.IsType<ProblemDetails>(value);
        Assert.Contains(expectedSubstring, pd.Detail);
        return pd;
    }
}
