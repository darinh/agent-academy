using System.Security.Claims;
using System.Text.Encodings.Web;
using AgentAcademy.Server.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.WebEncoders.Testing;

namespace AgentAcademy.Server.Tests;

public sealed class ConsultantKeyAuthTests
{
    private const string ValidSecret = "test-secret-key-12345";

    [Fact]
    public async Task ValidKey_ReturnsSuccess_WithCorrectClaims()
    {
        var handler = await CreateHandlerAsync(ValidSecret, headerValue: ValidSecret);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Principal);

        var name = result.Principal.FindFirst(ClaimTypes.Name)?.Value;
        var nameId = result.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var displayName = result.Principal.FindFirst("urn:github:name")?.Value;
        var role = result.Principal.FindFirst(ClaimTypes.Role)?.Value;

        Assert.Equal("consultant", name);
        Assert.Equal("consultant", nameId);
        Assert.Equal("Consultant", displayName);
        Assert.Equal("Consultant", role);
        Assert.Equal(ConsultantKeyAuthHandler.SchemeName, result.Ticket?.AuthenticationScheme);
    }

    [Fact]
    public async Task InvalidKey_ReturnsFail()
    {
        var handler = await CreateHandlerAsync(ValidSecret, headerValue: "wrong-key");

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
        Assert.Contains("Invalid", result.Failure!.Message);
    }

    [Fact]
    public async Task MissingHeader_ReturnsNoResult()
    {
        var handler = await CreateHandlerAsync(ValidSecret, headerValue: null);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.True(result.None);
    }

    [Fact]
    public async Task EmptyHeader_ReturnsNoResult()
    {
        var handler = await CreateHandlerAsync(ValidSecret, headerValue: "");

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.True(result.None);
    }

    [Fact]
    public async Task EmptyConfiguredSecret_ReturnsNoResult()
    {
        var handler = await CreateHandlerAsync("", headerValue: "some-key");

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.True(result.None);
    }

    [Fact]
    public async Task MissingConfigSection_ReturnsNoResult()
    {
        var handler = await CreateHandlerAsync(configuredSecret: null, headerValue: "some-key");

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.True(result.None);
    }

    private static async Task<ConsultantKeyAuthHandler> CreateHandlerAsync(
        string? configuredSecret, string? headerValue)
    {
        var configData = new Dictionary<string, string?>();
        if (configuredSecret is not null)
            configData["ConsultantApi:SharedSecret"] = configuredSecret;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var options = new OptionsMonitor<AuthenticationSchemeOptions>(
            new OptionsFactory<AuthenticationSchemeOptions>(
                Array.Empty<IConfigureOptions<AuthenticationSchemeOptions>>(),
                Array.Empty<IPostConfigureOptions<AuthenticationSchemeOptions>>()),
            Array.Empty<IOptionsChangeTokenSource<AuthenticationSchemeOptions>>(),
            new OptionsCache<AuthenticationSchemeOptions>());

        var loggerFactory = NullLoggerFactory.Instance;
        var encoder = UrlEncoder.Default;

        var handler = new ConsultantKeyAuthHandler(options, loggerFactory, encoder, configuration);

        var httpContext = new DefaultHttpContext();
        if (headerValue is not null)
            httpContext.Request.Headers[ConsultantKeyAuthHandler.HeaderName] = headerValue;

        var scheme = new AuthenticationScheme(
            ConsultantKeyAuthHandler.SchemeName,
            displayName: null,
            handlerType: typeof(ConsultantKeyAuthHandler));

        await handler.InitializeAsync(scheme, httpContext);

        return handler;
    }

    /// <summary>
    /// Minimal IOptionsMonitor that returns a fixed value for any named option.
    /// </summary>
    private sealed class OptionsMonitor<T> : IOptionsMonitor<T> where T : class, new()
    {
        private readonly IOptionsFactory<T> _factory;

        public OptionsMonitor(
            IOptionsFactory<T> factory,
            IEnumerable<IOptionsChangeTokenSource<T>> sources,
            IOptionsMonitorCache<T> cache)
        {
            _factory = factory;
            CurrentValue = factory.Create(Options.DefaultName);
        }

        public T CurrentValue { get; }
        public T Get(string? name) => _factory.Create(name ?? Options.DefaultName);
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
