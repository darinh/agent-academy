using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AgentAcademy.Server.Auth;

/// <summary>
/// Authenticates requests bearing an X-Consultant-Key header by comparing
/// the value against a pre-shared secret (constant-time, length-independent).
/// </summary>
public sealed class ConsultantKeyAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ConsultantKey";
    public const string HeaderName = "X-Consultant-Key";

    private readonly string _expectedSecret;

    public ConsultantKeyAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _expectedSecret = configuration["ConsultantApi:SharedSecret"] ?? "";
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var headerValue))
            return Task.FromResult(AuthenticateResult.NoResult());

        var providedKey = headerValue.ToString();

        if (string.IsNullOrEmpty(providedKey) || string.IsNullOrEmpty(_expectedSecret))
            return Task.FromResult(AuthenticateResult.NoResult());

        // Hash both values to fixed-length digests before comparing.
        // FixedTimeEquals short-circuits on length mismatch, so comparing
        // raw strings would leak the secret's byte length via timing.
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(_expectedSecret));
        var provided = SHA256.HashData(Encoding.UTF8.GetBytes(providedKey));

        if (!CryptographicOperations.FixedTimeEquals(expected, provided))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid consultant key."));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "consultant"),
            new Claim(ClaimTypes.Name, "consultant"),
            new Claim("urn:github:name", "Consultant"),
            new Claim(ClaimTypes.Role, "Consultant"),
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
