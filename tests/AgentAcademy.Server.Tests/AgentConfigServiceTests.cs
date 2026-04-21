using AgentAcademy.Server.Data;
using AgentAcademy.Server.Data.Entities;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Tests for AgentConfigService: merge logic, layering, edge cases.
/// </summary>
public class AgentConfigServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly AgentConfigService _service;

    private static readonly AgentDefinition CatalogAgent = new(
        Id: "test-agent-1",
        Name: "TestAgent",
        Role: "Tester",
        Summary: "A test agent",
        StartupPrompt: "You are a test agent. Be helpful.",
        Model: "gpt-5",
        CapabilityTags: ["testing"],
        EnabledTools: ["bash"],
        AutoJoinDefaultRoom: true
    );

    public AgentConfigServiceTests()
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

    // ── No Override ──────────────────────────────────────────

    [Fact]
    public async Task GetEffectiveAgent_NoOverride_ReturnsCatalogUnchanged()
    {
        var result = await _service.GetEffectiveAgentAsync(CatalogAgent);

        Assert.Equal(CatalogAgent.StartupPrompt, result.StartupPrompt);
        Assert.Equal(CatalogAgent.Model, result.Model);
        Assert.Equal(CatalogAgent.Id, result.Id);
        Assert.Equal(CatalogAgent.Name, result.Name);
    }

    // ── Model Override ───────────────────────────────────────

    [Fact]
    public async Task GetEffectiveAgent_ModelOverride_ReplacesModel()
    {
        _db.AgentConfigs.Add(new AgentConfigEntity
        {
            AgentId = CatalogAgent.Id,
            ModelOverride = "claude-opus-4.7",
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var result = await _service.GetEffectiveAgentAsync(CatalogAgent);

        Assert.Equal("claude-opus-4.7", result.Model);
        Assert.Equal(CatalogAgent.StartupPrompt, result.StartupPrompt);
    }

    // ── Startup Prompt Override ──────────────────────────────

    [Fact]
    public async Task GetEffectiveAgent_StartupPromptOverride_ReplacesPrompt()
    {
        _db.AgentConfigs.Add(new AgentConfigEntity
        {
            AgentId = CatalogAgent.Id,
            StartupPromptOverride = "You are a custom agent. Be precise.",
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var result = await _service.GetEffectiveAgentAsync(CatalogAgent);

        Assert.Equal("You are a custom agent. Be precise.", result.StartupPrompt);
        Assert.Equal(CatalogAgent.Model, result.Model);
    }

    // ── Custom Instructions Append ───────────────────────────

    [Fact]
    public async Task GetEffectiveAgent_CustomInstructions_AppendedToPrompt()
    {
        _db.AgentConfigs.Add(new AgentConfigEntity
        {
            AgentId = CatalogAgent.Id,
            CustomInstructions = "Always verify your work.",
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var result = await _service.GetEffectiveAgentAsync(CatalogAgent);

        Assert.Contains(CatalogAgent.StartupPrompt, result.StartupPrompt);
        Assert.Contains("Always verify your work.", result.StartupPrompt);
        Assert.EndsWith("Always verify your work.", result.StartupPrompt);
    }

    // ── Instruction Template ─────────────────────────────────

    [Fact]
    public async Task GetEffectiveAgent_InstructionTemplate_AppendedBetweenPromptAndCustom()
    {
        var template = new InstructionTemplateEntity
        {
            Id = "tmpl-1",
            Name = "Verification-First",
            Content = "Always verify before presenting.",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.InstructionTemplates.Add(template);

        _db.AgentConfigs.Add(new AgentConfigEntity
        {
            AgentId = CatalogAgent.Id,
            InstructionTemplateId = "tmpl-1",
            CustomInstructions = "Be concise.",
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var result = await _service.GetEffectiveAgentAsync(CatalogAgent);

        // Layering: catalog prompt + template + custom instructions
        var prompt = result.StartupPrompt;
        var promptIdx = prompt.IndexOf(CatalogAgent.StartupPrompt);
        var templateIdx = prompt.IndexOf("Always verify before presenting.");
        var customIdx = prompt.IndexOf("Be concise.");

        Assert.True(promptIdx >= 0, "Catalog prompt missing");
        Assert.True(templateIdx > promptIdx, "Template should come after catalog prompt");
        Assert.True(customIdx > templateIdx, "Custom instructions should come after template");
    }

    // ── Full Layering ────────────────────────────────────────

    [Fact]
    public async Task GetEffectiveAgent_FullLayering_OverridePromptPlusTemplatePlusCustom()
    {
        var template = new InstructionTemplateEntity
        {
            Id = "tmpl-2",
            Name = "Pushback-Enabled",
            Content = "Push back on bad ideas.",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.InstructionTemplates.Add(template);

        _db.AgentConfigs.Add(new AgentConfigEntity
        {
            AgentId = CatalogAgent.Id,
            StartupPromptOverride = "You are a senior reviewer.",
            ModelOverride = "claude-sonnet-4.5",
            InstructionTemplateId = "tmpl-2",
            CustomInstructions = "Focus on security.",
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var result = await _service.GetEffectiveAgentAsync(CatalogAgent);

        Assert.Equal("claude-sonnet-4.5", result.Model);
        Assert.DoesNotContain(CatalogAgent.StartupPrompt, result.StartupPrompt);
        Assert.Contains("You are a senior reviewer.", result.StartupPrompt);
        Assert.Contains("Push back on bad ideas.", result.StartupPrompt);
        Assert.Contains("Focus on security.", result.StartupPrompt);

        // Identity preserved
        Assert.Equal(CatalogAgent.Id, result.Id);
        Assert.Equal(CatalogAgent.Name, result.Name);
        Assert.Equal(CatalogAgent.Role, result.Role);
    }

    // ── Whitespace/Empty Handling ────────────────────────────

    [Fact]
    public async Task GetEffectiveAgent_EmptyOverrides_TreatedAsNoOverride()
    {
        _db.AgentConfigs.Add(new AgentConfigEntity
        {
            AgentId = CatalogAgent.Id,
            StartupPromptOverride = "   ",
            ModelOverride = "",
            CustomInstructions = "  \n  ",
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var result = await _service.GetEffectiveAgentAsync(CatalogAgent);

        Assert.Equal(CatalogAgent.StartupPrompt, result.StartupPrompt);
        Assert.Equal(CatalogAgent.Model, result.Model);
    }

    // ── Bulk Query ───────────────────────────────────────────

    [Fact]
    public async Task GetEffectiveAgents_MixedOverrides_AppliedCorrectly()
    {
        var agent2 = CatalogAgent with { Id = "test-agent-2", Name = "Agent2" };
        var agent3 = CatalogAgent with { Id = "test-agent-3", Name = "Agent3" };

        _db.AgentConfigs.Add(new AgentConfigEntity
        {
            AgentId = "test-agent-1",
            ModelOverride = "custom-model-1",
            UpdatedAt = DateTime.UtcNow
        });
        _db.AgentConfigs.Add(new AgentConfigEntity
        {
            AgentId = "test-agent-3",
            CustomInstructions = "Extra instructions for agent 3",
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var results = await _service.GetEffectiveAgentsAsync([CatalogAgent, agent2, agent3]);

        Assert.Equal(3, results.Count);
        Assert.Equal("custom-model-1", results[0].Model);          // agent1: overridden
        Assert.Equal(CatalogAgent.Model, results[1].Model);        // agent2: unchanged
        Assert.Contains("Extra instructions", results[2].StartupPrompt); // agent3: custom
    }

    // ── Static BuildEffectivePrompt ──────────────────────────

    [Fact]
    public void BuildEffectivePrompt_NoOverrides_ReturnsCatalogPrompt()
    {
        var result = AgentConfigService.BuildEffectivePrompt(
            "base prompt", null, null, null);

        Assert.Equal("base prompt", result);
    }

    [Fact]
    public void BuildEffectivePrompt_AllLayers_DoubleNewlineSeparated()
    {
        var result = AgentConfigService.BuildEffectivePrompt(
            "catalog", "override", "template", "custom");

        Assert.Equal("override\n\ntemplate\n\ncustom", result);
    }

    [Fact]
    public void BuildEffectivePrompt_OnlyTemplate_AppendedToCatalog()
    {
        var result = AgentConfigService.BuildEffectivePrompt(
            "catalog prompt", null, "template content", null);

        Assert.Equal("catalog prompt\n\ntemplate content", result);
    }

    // ── Identity Preservation ────────────────────────────────

    [Fact]
    public async Task GetEffectiveAgent_PreservesAllIdentityFields()
    {
        var agent = new AgentDefinition(
            Id: "full-agent",
            Name: "FullAgent",
            Role: "Architect",
            Summary: "Full test",
            StartupPrompt: "base",
            Model: "gpt-5",
            CapabilityTags: ["design", "review"],
            EnabledTools: ["bash", "git"],
            AutoJoinDefaultRoom: false,
            GitIdentity: new AgentGitIdentity("Agent", "agent@test.com"),
            Permissions: new CommandPermissionSet(["LIST_*"], ["DELETE_*"])
        );

        _db.AgentConfigs.Add(new AgentConfigEntity
        {
            AgentId = "full-agent",
            ModelOverride = "new-model",
            StartupPromptOverride = "new prompt",
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var result = await _service.GetEffectiveAgentAsync(agent);

        Assert.Equal("new-model", result.Model);
        Assert.Equal("new prompt", result.StartupPrompt);
        // Identity preserved
        Assert.Equal("full-agent", result.Id);
        Assert.Equal("FullAgent", result.Name);
        Assert.Equal("Architect", result.Role);
        Assert.Equal("Full test", result.Summary);
        Assert.Equal(["design", "review"], result.CapabilityTags);
        Assert.Equal(["bash", "git"], result.EnabledTools);
        Assert.False(result.AutoJoinDefaultRoom);
        Assert.NotNull(result.GitIdentity);
        Assert.Equal("Agent", result.GitIdentity!.AuthorName);
        Assert.NotNull(result.Permissions);
        Assert.Contains("LIST_*", result.Permissions!.Allowed);
    }

    // ── DB Schema ────────────────────────────────────────────

    [Fact]
    public async Task AgentConfig_InstructionTemplateFk_SetNullOnDelete()
    {
        var template = new InstructionTemplateEntity
        {
            Id = "delete-me",
            Name = "ToDelete",
            Content = "content",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.InstructionTemplates.Add(template);
        _db.AgentConfigs.Add(new AgentConfigEntity
        {
            AgentId = "orphan-agent",
            InstructionTemplateId = "delete-me",
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        _db.InstructionTemplates.Remove(template);
        await _db.SaveChangesAsync();

        var config = await _db.AgentConfigs.FindAsync("orphan-agent");
        Assert.NotNull(config);
        Assert.Null(config!.InstructionTemplateId);
    }

    [Fact]
    public async Task InstructionTemplate_NameIsUnique()
    {
        _db.InstructionTemplates.Add(new InstructionTemplateEntity
        {
            Id = "t1", Name = "UniqueTemplate", Content = "c1",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        _db.InstructionTemplates.Add(new InstructionTemplateEntity
        {
            Id = "t2", Name = "UniqueTemplate", Content = "c2",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }
}
