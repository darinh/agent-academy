using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Factory for consistent ProblemDetails error responses.
/// Use with the built-in result helpers (BadRequest, NotFound, Conflict, etc.)
/// to preserve typed result types for tests while returning RFC 7807 bodies.
/// </summary>
public static class ApiProblem
{
    public static ProblemDetails BadRequest(string detail, string? code = null)
        => Create(StatusCodes.Status400BadRequest, "Bad Request", detail, code);

    public static ProblemDetails NotFound(string detail, string? code = null)
        => Create(StatusCodes.Status404NotFound, "Not Found", detail, code);

    public static ProblemDetails Conflict(string detail, string? code = null)
        => Create(StatusCodes.Status409Conflict, "Conflict", detail, code);

    public static ProblemDetails Unauthorized(string detail, string? code = null)
        => Create(StatusCodes.Status401Unauthorized, "Unauthorized", detail, code);

    public static ProblemDetails Forbidden(string detail, string? code = null)
        => Create(StatusCodes.Status403Forbidden, "Forbidden", detail, code);

    public static ProblemDetails ServerError(string detail, string? code = null)
        => Create(StatusCodes.Status500InternalServerError, "Internal Server Error", detail, code);

    private static ProblemDetails Create(int statusCode, string title, string detail, string? code)
    {
        var pd = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
        };
        if (code is not null)
            pd.Extensions["code"] = code;
        return pd;
    }
}
