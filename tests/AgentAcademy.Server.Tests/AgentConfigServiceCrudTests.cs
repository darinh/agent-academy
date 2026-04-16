using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for AgentConfigService CRUD operations: config upsert/delete, template CRUD.
/// </summary>
public class AgentConfigServiceCrudTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly AgentConfigService _service;

    private static readonly AgentDefinition CatalogAgent = new(
        Id: "crud-agent-1",
        Name: "CrudAgent",
        Role: "Tester",
        Summary: "A test agent for CRUD",
        StartupPrompt: "You are a CRUD test agent.",
        Model: "gpt-5",
        CapabilityTags: ["testing"],
        EnabledTools: ["bash"],
        AutoJoinDefaultRoom: true
    );

    public AgentConfigServiceCrudTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();
        _service = new AgentConfigService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── GetConfigOverrideAsync ──────────────────────────────

    [Fact]
    public async Task GetConfigOverride_NoOverride_ReturnsNull()
    {
        var result = await _service.GetConfigOverrideAsync("nonexistent-agent");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetConfigOverride_WithOverride_ReturnsEntity()
    {
        _db.AgentConfigs.Add(new AgentConfigEntity
        {
            AgentId = "crud-agent-1",
            ModelOverride = "claude-opus-4.7",
            CustomInstructions = "Be thorough.",
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var result = await _service.GetConfigOverrideAsync("crud-agent-1");

        Assert.NotNull(result);
        Assert.Equal("claude-opus-4.7", result!.ModelOverride);
        Assert.Equal("Be thorough.", result.CustomInstructions);
    }

    [Fact]
    public async Task GetConfigOverride_IncludesTemplateNavigation()
    {
        _db.InstructionTemplates.Add(new InstructionTemplateEntity
        {
            Id = "tmpl-nav", Name = "NavTest", Content = "content",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        _db.AgentConfigs.Add(new AgentConfigEntity
        {
            AgentId = "crud-agent-1",
            InstructionTemplateId = "tmpl-nav",
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var result = await _service.GetConfigOverrideAsync("crud-agent-1");

        Assert.NotNull(result);
        Assert.NotNull(result!.InstructionTemplate);
        Assert.Equal("NavTest", result.InstructionTemplate!.Name);
    }

    // ── UpsertConfigAsync ──────────────────────────────────

    [Fact]
    public async Task UpsertConfig_CreatesNew_WhenNoOverrideExists()
    {
        var result = await _service.UpsertConfigAsync(
            "crud-agent-1", "custom prompt", "new-model", "instructions", null);

        Assert.Equal("crud-agent-1", result.AgentId);
        Assert.Equal("custom prompt", result.StartupPromptOverride);
        Assert.Equal("new-model", result.ModelOverride);
        Assert.Equal("instructions", result.CustomInstructions);

        var dbCheck = await _db.AgentConfigs.FindAsync("crud-agent-1");
        Assert.NotNull(dbCheck);
    }

    [Fact]
    public async Task UpsertConfig_UpdatesExisting_WhenOverrideExists()
    {
        await _service.UpsertConfigAsync("crud-agent-1", "v1", null, null, null);
        var result = await _service.UpsertConfigAsync("crud-agent-1", "v2", "model-2", "new-custom", null);

        Assert.Equal("v2", result.StartupPromptOverride);
        Assert.Equal("model-2", result.ModelOverride);
        Assert.Equal("new-custom", result.CustomInstructions);

        // Only one row in DB
        var count = await _db.AgentConfigs.CountAsync(c => c.AgentId == "crud-agent-1");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task UpsertConfig_NullFields_ClearOverride()
    {
        await _service.UpsertConfigAsync("crud-agent-1", "prompt", "model", "custom", null);
        var result = await _service.UpsertConfigAsync("crud-agent-1", null, null, null, null);

        Assert.Null(result.StartupPromptOverride);
        Assert.Null(result.ModelOverride);
        Assert.Null(result.CustomInstructions);
    }

    [Fact]
    public async Task UpsertConfig_WithValidTemplate_SetsFK()
    {
        _db.InstructionTemplates.Add(new InstructionTemplateEntity
        {
            Id = "tmpl-upsert", Name = "UpsertTest", Content = "template content",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var result = await _service.UpsertConfigAsync(
            "crud-agent-1", null, null, null, "tmpl-upsert");

        Assert.Equal("tmpl-upsert", result.InstructionTemplateId);
        Assert.NotNull(result.InstructionTemplate);
        Assert.Equal("UpsertTest", result.InstructionTemplate!.Name);
    }

    [Fact]
    public async Task UpsertConfig_WithInvalidTemplate_ThrowsArgumentException()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.UpsertConfigAsync("crud-agent-1", null, null, null, "nonexistent-template"));

        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task UpsertConfig_SetsUpdatedAt()
    {
        var before = DateTime.UtcNow;
        var result = await _service.UpsertConfigAsync("crud-agent-1", "prompt", null, null, null);
        var after = DateTime.UtcNow;

        Assert.True(result.UpdatedAt >= before && result.UpdatedAt <= after);
    }

    // ── DeleteConfigAsync ──────────────────────────────────

    [Fact]
    public async Task DeleteConfig_ExistingOverride_ReturnsTrueAndRemoves()
    {
        await _service.UpsertConfigAsync("crud-agent-1", "prompt", null, null, null);

        var deleted = await _service.DeleteConfigAsync("crud-agent-1");

        Assert.True(deleted);
        var dbCheck = await _db.AgentConfigs.FindAsync("crud-agent-1");
        Assert.Null(dbCheck);
    }

    [Fact]
    public async Task DeleteConfig_NoOverride_ReturnsFalse()
    {
        var deleted = await _service.DeleteConfigAsync("nonexistent-agent");
        Assert.False(deleted);
    }

    // ── Template CRUD ──────────────────────────────────────

    [Fact]
    public async Task CreateTemplate_ReturnsNewTemplate()
    {
        var result = await _service.CreateTemplateAsync("Test Template", "A description", "template content");

        Assert.NotNull(result.Id);
        Assert.Equal("Test Template", result.Name);
        Assert.Equal("A description", result.Description);
        Assert.Equal("template content", result.Content);
        Assert.True(result.CreatedAt > DateTime.MinValue);
    }

    [Fact]
    public async Task CreateTemplate_DuplicateName_ThrowsInvalidOperationException()
    {
        await _service.CreateTemplateAsync("Duplicate", null, "content1");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.CreateTemplateAsync("Duplicate", null, "content2"));

        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public async Task GetAllTemplates_ReturnsOrderedByName()
    {
        await _service.CreateTemplateAsync("Zeta Template", null, "z");
        await _service.CreateTemplateAsync("Alpha Template", null, "a");
        await _service.CreateTemplateAsync("Mid Template", null, "m");

        var results = await _service.GetAllTemplatesAsync();

        Assert.Equal(3, results.Count);
        Assert.Equal("Alpha Template", results[0].Name);
        Assert.Equal("Mid Template", results[1].Name);
        Assert.Equal("Zeta Template", results[2].Name);
    }

    [Fact]
    public async Task GetAllTemplates_Empty_ReturnsEmptyList()
    {
        var results = await _service.GetAllTemplatesAsync();
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetTemplate_Exists_ReturnsTemplate()
    {
        var created = await _service.CreateTemplateAsync("FindMe", "desc", "content");
        var result = await _service.GetTemplateAsync(created.Id);

        Assert.NotNull(result);
        Assert.Equal("FindMe", result!.Name);
    }

    [Fact]
    public async Task GetTemplate_NotFound_ReturnsNull()
    {
        var result = await _service.GetTemplateAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateTemplate_Exists_UpdatesFields()
    {
        var created = await _service.CreateTemplateAsync("Original", "old desc", "old content");

        var result = await _service.UpdateTemplateAsync(
            created.Id, "Updated", "new desc", "new content");

        Assert.NotNull(result);
        Assert.Equal("Updated", result!.Name);
        Assert.Equal("new desc", result.Description);
        Assert.Equal("new content", result.Content);
        Assert.True(result.UpdatedAt >= result.CreatedAt);
    }

    [Fact]
    public async Task UpdateTemplate_NotFound_ReturnsNull()
    {
        var result = await _service.UpdateTemplateAsync("nonexistent", "N", null, "C");
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateTemplate_DuplicateNameWithOtherTemplate_ThrowsInvalidOperationException()
    {
        await _service.CreateTemplateAsync("Existing", null, "content1");
        var second = await _service.CreateTemplateAsync("Second", null, "content2");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UpdateTemplateAsync(second.Id, "Existing", null, "updated"));

        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public async Task UpdateTemplate_SameNameOnSameTemplate_Succeeds()
    {
        var created = await _service.CreateTemplateAsync("KeepName", null, "content");

        var result = await _service.UpdateTemplateAsync(created.Id, "KeepName", "new desc", "new content");

        Assert.NotNull(result);
        Assert.Equal("KeepName", result!.Name);
        Assert.Equal("new content", result.Content);
    }

    [Fact]
    public async Task DeleteTemplate_Exists_ReturnsTrueAndRemoves()
    {
        var created = await _service.CreateTemplateAsync("DeleteMe", null, "content");

        var deleted = await _service.DeleteTemplateAsync(created.Id);

        Assert.True(deleted);
        var dbCheck = await _service.GetTemplateAsync(created.Id);
        Assert.Null(dbCheck);
    }

    [Fact]
    public async Task DeleteTemplate_NotFound_ReturnsFalse()
    {
        var deleted = await _service.DeleteTemplateAsync("nonexistent");
        Assert.False(deleted);
    }

    [Fact]
    public async Task DeleteTemplate_NullifiesFkOnAgentConfigs()
    {
        var template = await _service.CreateTemplateAsync("FkTest", null, "content");
        await _service.UpsertConfigAsync("crud-agent-1", null, null, null, template.Id);

        await _service.DeleteTemplateAsync(template.Id);

        var config = await _service.GetConfigOverrideAsync("crud-agent-1");
        Assert.NotNull(config);
        Assert.Null(config!.InstructionTemplateId);
    }
}
