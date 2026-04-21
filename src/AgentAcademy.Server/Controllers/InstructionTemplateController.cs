using System.ComponentModel.DataAnnotations;
using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// CRUD endpoints for reusable instruction templates.
/// Templates can be assigned to agent configurations to provide common instruction patterns.
/// </summary>
[ApiController]
[Route("api/instruction-templates")]
public class InstructionTemplateController : ControllerBase
{
    private readonly IAgentConfigService _configService;
    private readonly ILogger<InstructionTemplateController> _logger;

    public InstructionTemplateController(
        IAgentConfigService configService,
        ILogger<InstructionTemplateController> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/instruction-templates — list all templates.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<InstructionTemplateResponse>>> GetTemplates()
    {
        try
        {
            var templates = await _configService.GetAllTemplatesAsync();
            var response = templates.Select(t => new InstructionTemplateResponse(
                t.Id, t.Name, t.Description, t.Content, t.CreatedAt, t.UpdatedAt)).ToList();
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list instruction templates");
            return Problem("Failed to retrieve instruction templates.");
        }
    }

    /// <summary>
    /// GET /api/instruction-templates/{id} — get a single template.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<InstructionTemplateResponse>> GetTemplate(string id)
    {
        try
        {
            var template = await _configService.GetTemplateAsync(id);
            if (template is null)
                return NotFound(ApiProblem.NotFound($"Template '{id}' not found", "template_not_found"));

            return Ok(new InstructionTemplateResponse(
                template.Id, template.Name, template.Description,
                template.Content, template.CreatedAt, template.UpdatedAt));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get instruction template '{TemplateId}'", id);
            return Problem("Failed to retrieve instruction template.");
        }
    }

    /// <summary>
    /// POST /api/instruction-templates — create a new template.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<InstructionTemplateResponse>> CreateTemplate(
        [FromBody] InstructionTemplateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(ApiProblem.BadRequest("Template name is required", "invalid_template"));

        if (string.IsNullOrWhiteSpace(request.Content))
            return BadRequest(ApiProblem.BadRequest("Template content is required", "invalid_template"));

        try
        {
            var template = await _configService.CreateTemplateAsync(
                request.Name.Trim(), request.Description?.Trim(), request.Content);

            return StatusCode(201, new InstructionTemplateResponse(
                template.Id, template.Name, template.Description,
                template.Content, template.CreatedAt, template.UpdatedAt));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ApiProblem.Conflict(ex.Message, "duplicate_name"));
        }
        catch (DbUpdateException)
        {
            return Conflict(ApiProblem.Conflict("A template with this name already exists.", "duplicate_name"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create instruction template");
            return Problem("Failed to create instruction template.");
        }
    }

    /// <summary>
    /// PUT /api/instruction-templates/{id} — update an existing template.
    /// </summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<InstructionTemplateResponse>> UpdateTemplate(
        string id, [FromBody] InstructionTemplateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(ApiProblem.BadRequest("Template name is required", "invalid_template"));

        if (string.IsNullOrWhiteSpace(request.Content))
            return BadRequest(ApiProblem.BadRequest("Template content is required", "invalid_template"));

        try
        {
            var template = await _configService.UpdateTemplateAsync(
                id, request.Name.Trim(), request.Description?.Trim(), request.Content);

            if (template is null)
                return NotFound(ApiProblem.NotFound($"Template '{id}' not found", "template_not_found"));

            return Ok(new InstructionTemplateResponse(
                template.Id, template.Name, template.Description,
                template.Content, template.CreatedAt, template.UpdatedAt));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ApiProblem.Conflict(ex.Message, "duplicate_name"));
        }
        catch (DbUpdateException)
        {
            return Conflict(ApiProblem.Conflict("A template with this name already exists.", "duplicate_name"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update instruction template '{TemplateId}'", id);
            return Problem("Failed to update instruction template.");
        }
    }

    /// <summary>
    /// DELETE /api/instruction-templates/{id} — delete a template.
    /// Agent configs referencing this template will have their FK set to null.
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteTemplate(string id)
    {
        try
        {
            var deleted = await _configService.DeleteTemplateAsync(id);
            if (!deleted)
                return NotFound(ApiProblem.NotFound($"Template '{id}' not found", "template_not_found"));

            return Ok(new { status = "deleted", id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete instruction template '{TemplateId}'", id);
            return Problem("Failed to delete instruction template.");
        }
    }
}

/// <summary>
/// Request body for creating or updating an instruction template.
/// </summary>
public record InstructionTemplateRequest(
    [Required, StringLength(200)] string Name,
    [StringLength(1000)] string? Description,
    [Required, MinLength(1), StringLength(100_000)] string Content
);

/// <summary>
/// Response containing instruction template details.
/// </summary>
public record InstructionTemplateResponse(
    string Id,
    string Name,
    string? Description,
    string Content,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
