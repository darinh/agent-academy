using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for the agent quota REST endpoints: GET/PUT/DELETE /api/agents/{id}/quota.
/// Uses real DB + real AgentQuotaService to verify end-to-end controller behavior.
/// </summary>
public sealed class AgentQuotaEndpointTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly IServiceScope _controllerScope;
    private readonly AgentCatalogOptions _catalog;
    private readonly AgentQuotaService _quotaService;

    private static readonly AgentDefinition TestAgent = new(
        Id: "coder-1",
        Name: "Coder",
        Role: "Developer",
        Summary: "Writes code",
        StartupPrompt: "You write code.",
        Model: "gpt-5",
        CapabilityTags: ["coding"],
        EnabledTools: ["bash"],
        AutoJoinDefaultRoom: true);

    public AgentQuotaEndpointTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentAcademyDbContext>(opt => opt.UseSqlite(_connection));
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>().Database.EnsureCreated();

        _catalog = new AgentCatalogOptions(
            DefaultRoomId: "main",
            DefaultRoomName: "Main Room",
            Agents: [TestAgent]);

        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var usageTracker = new LlmUsageTracker(scopeFactory, NullLogger<LlmUsageTracker>.Instance);
        _quotaService = new AgentQuotaService(scopeFactory, usageTracker, NullLogger<AgentQuotaService>.Instance);

        _controllerScope = _serviceProvider.CreateScope();
    }

    public void Dispose()
    {
        _controllerScope.Dispose();
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private AgentController CreateController()
    {
        var executor = Substitute.For<IAgentExecutor>();
        var configService = new AgentConfigService(
            _controllerScope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>());

        var controller = new AgentController(
            agentLocationService: null!,  // Not needed for quota endpoints
            breakoutRoomService: null!,   // Not needed for quota endpoints
            executor,
            _catalog,
            _quotaService,
            NullLogger<AgentController>.Instance);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = _serviceProvider
            }
        };

        return controller;
    }

    // ── GET /api/agents/{id}/quota ─────────────────────────

    [Fact]
    public async Task GetQuota_UnknownAgent_ReturnsNotFound()
    {
        var controller = CreateController();
        var result = await controller.GetAgentQuota("nonexistent");
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetQuota_NoQuotaConfigured_ReturnsAllowed()
    {
        var controller = CreateController();
        var result = await controller.GetAgentQuota("coder-1");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var status = Assert.IsType<QuotaStatus>(ok.Value);
        Assert.True(status.IsAllowed);
        Assert.Equal("coder-1", status.AgentId);
        Assert.Null(status.ConfiguredQuota);
    }

    [Fact]
    public async Task GetQuota_WithQuotaConfigured_ReturnsQuotaDetails()
    {
        // Seed quota in DB
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
            db.AgentConfigs.Add(new AgentConfigEntity
            {
                AgentId = "coder-1",
                MaxRequestsPerHour = 100,
                MaxTokensPerHour = 50000,
                MaxCostPerHour = 5.00m,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        _quotaService.InvalidateCache("coder-1");

        var controller = CreateController();
        var result = await controller.GetAgentQuota("coder-1");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var status = Assert.IsType<QuotaStatus>(ok.Value);
        Assert.True(status.IsAllowed);
        Assert.NotNull(status.ConfiguredQuota);
        Assert.Equal(100, status.ConfiguredQuota.MaxRequestsPerHour);
        Assert.Equal(50000, status.ConfiguredQuota.MaxTokensPerHour);
        Assert.Equal(5.00m, status.ConfiguredQuota.MaxCostPerHour);
    }

    [Fact]
    public async Task GetQuota_CaseInsensitiveAgentId()
    {
        var controller = CreateController();
        var result = await controller.GetAgentQuota("CODER-1");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var status = Assert.IsType<QuotaStatus>(ok.Value);
        Assert.Equal("coder-1", status.AgentId);
    }

    // ── PUT /api/agents/{id}/quota ─────────────────────────

    [Fact]
    public async Task UpdateQuota_UnknownAgent_ReturnsNotFound()
    {
        var controller = CreateController();
        var request = new UpdateQuotaRequest(100, null, null);
        var result = await controller.UpdateAgentQuota("nonexistent", request);
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateQuota_ValidLimits_PersistsAndReturnsStatus()
    {
        var controller = CreateController();
        var request = new UpdateQuotaRequest(60, 10000, 2.50m);
        var result = await controller.UpdateAgentQuota("coder-1", request);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var status = Assert.IsType<QuotaStatus>(ok.Value);
        Assert.True(status.IsAllowed);
        Assert.NotNull(status.ConfiguredQuota);
        Assert.Equal(60, status.ConfiguredQuota.MaxRequestsPerHour);
        Assert.Equal(10000, status.ConfiguredQuota.MaxTokensPerHour);
        Assert.Equal(2.50m, status.ConfiguredQuota.MaxCostPerHour);

        // Verify persisted in DB
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var config = await db.AgentConfigs.FindAsync("coder-1");
        Assert.NotNull(config);
        Assert.Equal(60, config.MaxRequestsPerHour);
        Assert.Equal(10000, config.MaxTokensPerHour);
        Assert.Equal(2.50m, config.MaxCostPerHour);
    }

    [Fact]
    public async Task UpdateQuota_NullLimits_SetsUnlimited()
    {
        var controller = CreateController();
        var request = new UpdateQuotaRequest(null, null, null);
        var result = await controller.UpdateAgentQuota("coder-1", request);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var status = Assert.IsType<QuotaStatus>(ok.Value);
        Assert.Null(status.ConfiguredQuota);
    }

    [Fact]
    public async Task UpdateQuota_OverwritesExistingConfig()
    {
        var controller = CreateController();

        // Set initial limits
        await controller.UpdateAgentQuota("coder-1", new UpdateQuotaRequest(100, 50000, 5.00m));

        // Overwrite with new limits
        var result = await controller.UpdateAgentQuota("coder-1", new UpdateQuotaRequest(200, null, 10.00m));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var status = Assert.IsType<QuotaStatus>(ok.Value);
        Assert.NotNull(status.ConfiguredQuota);
        Assert.Equal(200, status.ConfiguredQuota.MaxRequestsPerHour);
        Assert.Null(status.ConfiguredQuota.MaxTokensPerHour);
        Assert.Equal(10.00m, status.ConfiguredQuota.MaxCostPerHour);
    }

    [Fact]
    public async Task UpdateQuota_NegativeRequests_ReturnsBadRequest()
    {
        var controller = CreateController();
        var request = new UpdateQuotaRequest(-1, null, null);
        var result = await controller.UpdateAgentQuota("coder-1", request);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateQuota_NegativeTokens_ReturnsBadRequest()
    {
        var controller = CreateController();
        var request = new UpdateQuotaRequest(null, -100, null);
        var result = await controller.UpdateAgentQuota("coder-1", request);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateQuota_NegativeCost_ReturnsBadRequest()
    {
        var controller = CreateController();
        var request = new UpdateQuotaRequest(null, null, -0.01m);
        var result = await controller.UpdateAgentQuota("coder-1", request);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateQuota_ZeroValues_Allowed()
    {
        var controller = CreateController();
        var request = new UpdateQuotaRequest(0, 0, 0m);
        var result = await controller.UpdateAgentQuota("coder-1", request);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var status = Assert.IsType<QuotaStatus>(ok.Value);
        Assert.NotNull(status.ConfiguredQuota);
        Assert.Equal(0, status.ConfiguredQuota.MaxRequestsPerHour);
    }

    // ── DELETE /api/agents/{id}/quota ───────────────────────

    [Fact]
    public async Task RemoveQuota_UnknownAgent_ReturnsNotFound()
    {
        var controller = CreateController();
        var result = await controller.RemoveAgentQuota("nonexistent");
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task RemoveQuota_ExistingQuota_ClearsLimits()
    {
        var controller = CreateController();

        // Set quota first
        await controller.UpdateAgentQuota("coder-1", new UpdateQuotaRequest(100, 50000, 5.00m));

        // Remove it
        var result = await controller.RemoveAgentQuota("coder-1");
        var ok = Assert.IsType<OkObjectResult>(result);

        // Verify DB is cleared
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentAcademyDbContext>();
        var config = await db.AgentConfigs.FindAsync("coder-1");
        Assert.NotNull(config); // Row still exists
        Assert.Null(config.MaxRequestsPerHour);
        Assert.Null(config.MaxTokensPerHour);
        Assert.Null(config.MaxCostPerHour);
    }

    [Fact]
    public async Task RemoveQuota_NoExistingConfig_ReturnsOk()
    {
        var controller = CreateController();
        var result = await controller.RemoveAgentQuota("coder-1");
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task RemoveQuota_InvalidatesCache_SubsequentGetShowsUnlimited()
    {
        var controller = CreateController();

        // Set then remove
        await controller.UpdateAgentQuota("coder-1", new UpdateQuotaRequest(100, 50000, 5.00m));
        await controller.RemoveAgentQuota("coder-1");

        // Verify GET returns unlimited
        var result = await controller.GetAgentQuota("coder-1");
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var status = Assert.IsType<QuotaStatus>(ok.Value);
        Assert.True(status.IsAllowed);
        Assert.Null(status.ConfiguredQuota);
    }

    // ── Round-trip integration ──────────────────────────────

    [Fact]
    public async Task Quota_FullLifecycle_SetUpdateRemove()
    {
        var controller = CreateController();

        // 1. Initially no quota
        var get1 = await controller.GetAgentQuota("coder-1");
        var status1 = Assert.IsType<QuotaStatus>(Assert.IsType<OkObjectResult>(get1.Result).Value);
        Assert.Null(status1.ConfiguredQuota);

        // 2. Set quota
        await controller.UpdateAgentQuota("coder-1", new UpdateQuotaRequest(50, 20000, 1.00m));
        var get2 = await controller.GetAgentQuota("coder-1");
        var status2 = Assert.IsType<QuotaStatus>(Assert.IsType<OkObjectResult>(get2.Result).Value);
        Assert.Equal(50, status2.ConfiguredQuota!.MaxRequestsPerHour);

        // 3. Update quota
        await controller.UpdateAgentQuota("coder-1", new UpdateQuotaRequest(200, null, 10.00m));
        var get3 = await controller.GetAgentQuota("coder-1");
        var status3 = Assert.IsType<QuotaStatus>(Assert.IsType<OkObjectResult>(get3.Result).Value);
        Assert.Equal(200, status3.ConfiguredQuota!.MaxRequestsPerHour);
        Assert.Null(status3.ConfiguredQuota.MaxTokensPerHour);

        // 4. Remove quota
        await controller.RemoveAgentQuota("coder-1");
        var get4 = await controller.GetAgentQuota("coder-1");
        var status4 = Assert.IsType<QuotaStatus>(Assert.IsType<OkObjectResult>(get4.Result).Value);
        Assert.Null(status4.ConfiguredQuota);
    }
}
