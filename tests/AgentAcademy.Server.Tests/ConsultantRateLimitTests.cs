using System.Security.Claims;
using System.Threading.RateLimiting;
using AgentAcademy.Server.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace AgentAcademy.Server.Tests;

public sealed class ConsultantRateLimitTests : IDisposable
{
    private PartitionedRateLimiter<HttpContext>? _limiter;

    public void Dispose() => _limiter?.Dispose();

    // --- Settings defaults ---

    [Fact]
    public void DefaultSettings_HaveExpectedValues()
    {
        var settings = new ConsultantRateLimitSettings();

        Assert.True(settings.Enabled);
        Assert.Equal(20, settings.WritePermitLimit);
        Assert.Equal(60, settings.ReadPermitLimit);
        Assert.Equal(60, settings.WindowSeconds);
        Assert.Equal(6, settings.SegmentsPerWindow);
    }

    [Fact]
    public void Settings_BindFromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConsultantApi:RateLimiting:Enabled"] = "true",
                ["ConsultantApi:RateLimiting:WritePermitLimit"] = "10",
                ["ConsultantApi:RateLimiting:ReadPermitLimit"] = "30",
                ["ConsultantApi:RateLimiting:WindowSeconds"] = "120",
                ["ConsultantApi:RateLimiting:SegmentsPerWindow"] = "4",
            })
            .Build();

        var settings = config
            .GetSection(ConsultantRateLimitSettings.SectionName)
            .Get<ConsultantRateLimitSettings>()!;

        Assert.Equal(10, settings.WritePermitLimit);
        Assert.Equal(30, settings.ReadPermitLimit);
        Assert.Equal(120, settings.WindowSeconds);
        Assert.Equal(4, settings.SegmentsPerWindow);
    }

    // --- Non-consultant requests pass through ---

    [Fact]
    public async Task NonConsultantRequest_IsNeverRateLimited()
    {
        _limiter = CreateLimiter(writeLimit: 1);

        // Even with a limit of 1, non-consultant requests should pass unlimited
        for (var i = 0; i < 50; i++)
        {
            using var lease = await _limiter.AcquireAsync(
                CreateContext("POST", isConsultant: false));
            Assert.True(lease.IsAcquired, $"Request {i + 1} should not be rate-limited");
        }
    }

    [Fact]
    public async Task AnonymousRequest_IsNeverRateLimited()
    {
        _limiter = CreateLimiter(writeLimit: 1);

        for (var i = 0; i < 10; i++)
        {
            using var lease = await _limiter.AcquireAsync(
                CreateContext("GET", isConsultant: false, isAnonymous: true));
            Assert.True(lease.IsAcquired);
        }
    }

    // --- Consultant write limits ---

    [Fact]
    public async Task ConsultantWriteRequest_IsRateLimited_AfterExceedingLimit()
    {
        const int limit = 3;
        _limiter = CreateLimiter(writeLimit: limit);

        // First N requests should succeed
        for (var i = 0; i < limit; i++)
        {
            using var lease = await _limiter.AcquireAsync(
                CreateContext("POST", isConsultant: true));
            Assert.True(lease.IsAcquired, $"Write request {i + 1} should succeed");
        }

        // Next request should be rejected
        using var rejected = await _limiter.AcquireAsync(
            CreateContext("POST", isConsultant: true));
        Assert.False(rejected.IsAcquired, "Exceeding write limit should be rejected");
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task AllWriteMethods_ShareSameLimit(string method)
    {
        _limiter = CreateLimiter(writeLimit: 2);

        using var first = await _limiter.AcquireAsync(
            CreateContext("POST", isConsultant: true));
        Assert.True(first.IsAcquired);

        using var second = await _limiter.AcquireAsync(
            CreateContext(method, isConsultant: true));
        Assert.True(second.IsAcquired);

        // Third write request should be rejected — shared bucket
        using var third = await _limiter.AcquireAsync(
            CreateContext("POST", isConsultant: true));
        Assert.False(third.IsAcquired);
    }

    // --- Consultant read limits ---

    [Fact]
    public async Task ConsultantReadRequest_IsRateLimited_AfterExceedingLimit()
    {
        const int limit = 5;
        _limiter = CreateLimiter(readLimit: limit);

        for (var i = 0; i < limit; i++)
        {
            using var lease = await _limiter.AcquireAsync(
                CreateContext("GET", isConsultant: true));
            Assert.True(lease.IsAcquired);
        }

        using var rejected = await _limiter.AcquireAsync(
            CreateContext("GET", isConsultant: true));
        Assert.False(rejected.IsAcquired, "Exceeding read limit should be rejected");
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task AllReadMethods_ShareSameLimit(string method)
    {
        _limiter = CreateLimiter(readLimit: 2);

        using var first = await _limiter.AcquireAsync(
            CreateContext("GET", isConsultant: true));
        Assert.True(first.IsAcquired);

        using var second = await _limiter.AcquireAsync(
            CreateContext(method, isConsultant: true));
        Assert.True(second.IsAcquired);

        using var third = await _limiter.AcquireAsync(
            CreateContext("GET", isConsultant: true));
        Assert.False(third.IsAcquired);
    }

    // --- Separate buckets ---

    [Fact]
    public async Task WriteAndRead_UseSeparateBuckets()
    {
        _limiter = CreateLimiter(writeLimit: 2, readLimit: 2);

        // Exhaust write limit
        using var w1 = await _limiter.AcquireAsync(CreateContext("POST", isConsultant: true));
        using var w2 = await _limiter.AcquireAsync(CreateContext("POST", isConsultant: true));
        Assert.True(w1.IsAcquired);
        Assert.True(w2.IsAcquired);

        // Write should be rejected
        using var w3 = await _limiter.AcquireAsync(CreateContext("POST", isConsultant: true));
        Assert.False(w3.IsAcquired);

        // Read should still work — separate bucket
        using var r1 = await _limiter.AcquireAsync(CreateContext("GET", isConsultant: true));
        Assert.True(r1.IsAcquired, "Read requests should not be affected by write limit exhaustion");
    }

    [Fact]
    public async Task ExhaustingReadLimit_DoesNotAffectWrites()
    {
        _limiter = CreateLimiter(writeLimit: 2, readLimit: 2);

        // Exhaust read limit
        using var r1 = await _limiter.AcquireAsync(CreateContext("GET", isConsultant: true));
        using var r2 = await _limiter.AcquireAsync(CreateContext("GET", isConsultant: true));
        Assert.True(r1.IsAcquired);
        Assert.True(r2.IsAcquired);

        using var r3 = await _limiter.AcquireAsync(CreateContext("GET", isConsultant: true));
        Assert.False(r3.IsAcquired);

        // Write should still work
        using var w1 = await _limiter.AcquireAsync(CreateContext("POST", isConsultant: true));
        Assert.True(w1.IsAcquired, "Write requests should not be affected by read limit exhaustion");
    }

    // --- Rejected lease metadata ---

    [Fact]
    public async Task RejectedLease_IsNotAcquired()
    {
        _limiter = CreateLimiter(writeLimit: 1);

        using var first = await _limiter.AcquireAsync(CreateContext("POST", isConsultant: true));
        Assert.True(first.IsAcquired);

        using var rejected = await _limiter.AcquireAsync(CreateContext("POST", isConsultant: true));
        Assert.False(rejected.IsAcquired);

        // RetryAfter metadata may or may not be present depending on the
        // underlying SlidingWindowRateLimiter implementation. The OnRejected
        // handler in AuthenticationExtensions has a 10-second fallback for when it's absent.
        if (rejected.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            Assert.True(retryAfter > TimeSpan.Zero);
        }
    }

    // --- Settings section name ---

    [Fact]
    public void SectionName_IsConsultantApiRateLimiting()
    {
        Assert.Equal("ConsultantApi:RateLimiting", ConsultantRateLimitSettings.SectionName);
    }

    // --- Helpers ---

    private static PartitionedRateLimiter<HttpContext> CreateLimiter(
        int writeLimit = 20,
        int readLimit = 60)
    {
        var settings = new ConsultantRateLimitSettings
        {
            WritePermitLimit = writeLimit,
            ReadPermitLimit = readLimit,
            WindowSeconds = 60,
            SegmentsPerWindow = 1,
        };

        return ConsultantRateLimitExtensions.CreateConsultantRateLimiter(settings);
    }

    private static HttpContext CreateContext(
        string method,
        bool isConsultant,
        bool isAnonymous = false)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;

        if (!isAnonymous && isConsultant)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "consultant"),
                new Claim(ClaimTypes.Name, "consultant"),
                new Claim(ClaimTypes.Role, "Consultant"),
            };
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "ConsultantKey"));
        }
        else if (!isAnonymous)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "regular-user"),
                new Claim(ClaimTypes.Name, "user"),
            };
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"));
        }

        return context;
    }
}
