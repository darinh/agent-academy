using AgentAcademy.Server.Controllers;
using AgentAcademy.Server.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentAcademy.Server.Tests;

public sealed class InstructionTemplateControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentAcademyDbContext _db;
    private readonly InstructionTemplateController _controller;

    public InstructionTemplateControllerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentAcademyDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentAcademyDbContext(options);
        _db.Database.EnsureCreated();

        var configService = new AgentAcademy.Server.Services.AgentConfigService(_db);
        _controller = new InstructionTemplateController(configService, NullLogger<InstructionTemplateController>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── List ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetTemplates_EmptyDb_ReturnsEmptyList()
    {
        var result = await _controller.GetTemplates();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<InstructionTemplateResponse>>(ok.Value);
        Assert.Empty(list);
    }

    [Fact]
    public async Task GetTemplates_ReturnsAllTemplates()
    {
        await _controller.CreateTemplate(new InstructionTemplateRequest("T1", null, "Content1"));
        await _controller.CreateTemplate(new InstructionTemplateRequest("T2", "Desc", "Content2"));

        var result = await _controller.GetTemplates();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<InstructionTemplateResponse>>(ok.Value);
        Assert.Equal(2, list.Count);
    }

    // ── Get ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetTemplate_NotFound_Returns404()
    {
        var result = await _controller.GetTemplate("nonexistent");
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetTemplate_Found_ReturnsTemplate()
    {
        var createResult = await _controller.CreateTemplate(
            new InstructionTemplateRequest("MyTemplate", "Desc", "Content here"));
        var created = Assert.IsType<InstructionTemplateResponse>(
            ((ObjectResult)createResult.Result!).Value);

        var result = await _controller.GetTemplate(created.Id);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var template = Assert.IsType<InstructionTemplateResponse>(ok.Value);
        Assert.Equal("MyTemplate", template.Name);
        Assert.Equal("Desc", template.Description);
        Assert.Equal("Content here", template.Content);
    }

    // ── Create ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTemplate_MissingName_ReturnsBadRequest()
    {
        var result = await _controller.CreateTemplate(
            new InstructionTemplateRequest("", null, "content"));
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateTemplate_MissingContent_ReturnsBadRequest()
    {
        var result = await _controller.CreateTemplate(
            new InstructionTemplateRequest("Name", null, ""));
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateTemplate_Valid_Returns201()
    {
        var result = await _controller.CreateTemplate(
            new InstructionTemplateRequest("Test Template", "A description", "Template content"));

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(201, obj.StatusCode);
        var template = Assert.IsType<InstructionTemplateResponse>(obj.Value);
        Assert.Equal("Test Template", template.Name);
        Assert.Equal("A description", template.Description);
        Assert.False(string.IsNullOrEmpty(template.Id));
    }

    [Fact]
    public async Task CreateTemplate_DuplicateName_ReturnsConflict()
    {
        await _controller.CreateTemplate(
            new InstructionTemplateRequest("Unique", null, "content1"));

        var result = await _controller.CreateTemplate(
            new InstructionTemplateRequest("Unique", null, "content2"));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateTemplate_TrimsNameAndDescription()
    {
        var result = await _controller.CreateTemplate(
            new InstructionTemplateRequest("  Padded Name  ", "  Padded Desc  ", "content"));

        var obj = Assert.IsType<ObjectResult>(result.Result);
        var template = Assert.IsType<InstructionTemplateResponse>(obj.Value);
        Assert.Equal("Padded Name", template.Name);
        Assert.Equal("Padded Desc", template.Description);
    }

    // ── Update ────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateTemplate_NotFound_Returns404()
    {
        var result = await _controller.UpdateTemplate("missing",
            new InstructionTemplateRequest("Name", null, "Content"));
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateTemplate_MissingName_ReturnsBadRequest()
    {
        var result = await _controller.UpdateTemplate("id",
            new InstructionTemplateRequest("", null, "Content"));
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateTemplate_MissingContent_ReturnsBadRequest()
    {
        var result = await _controller.UpdateTemplate("id",
            new InstructionTemplateRequest("Name", null, ""));
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateTemplate_Valid_UpdatesFields()
    {
        var createResult = await _controller.CreateTemplate(
            new InstructionTemplateRequest("Original", "Old desc", "Old content"));
        var created = Assert.IsType<InstructionTemplateResponse>(
            ((ObjectResult)createResult.Result!).Value);

        var updateResult = await _controller.UpdateTemplate(created.Id,
            new InstructionTemplateRequest("Updated", "New desc", "New content"));

        var ok = Assert.IsType<OkObjectResult>(updateResult.Result);
        var updated = Assert.IsType<InstructionTemplateResponse>(ok.Value);
        Assert.Equal("Updated", updated.Name);
        Assert.Equal("New desc", updated.Description);
        Assert.Equal("New content", updated.Content);
        Assert.Equal(created.Id, updated.Id);
    }

    // ── Delete ────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteTemplate_NotFound_Returns404()
    {
        var result = await _controller.DeleteTemplate("missing");
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task DeleteTemplate_Existing_RemovesIt()
    {
        var createResult = await _controller.CreateTemplate(
            new InstructionTemplateRequest("ToDelete", null, "content"));
        var created = Assert.IsType<InstructionTemplateResponse>(
            ((ObjectResult)createResult.Result!).Value);

        var deleteResult = await _controller.DeleteTemplate(created.Id);
        Assert.IsType<OkObjectResult>(deleteResult);

        var getResult = await _controller.GetTemplate(created.Id);
        Assert.IsType<NotFoundObjectResult>(getResult.Result);
    }
}
