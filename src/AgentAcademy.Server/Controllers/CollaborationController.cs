using System.ComponentModel.DataAnnotations;
using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentAcademy.Server.Controllers;

/// <summary>
/// Task submission, messaging, human input, phase transitions, and session compaction.
/// </summary>
[ApiController]
public class CollaborationController : ControllerBase
{
    private readonly TaskOrchestrationService _taskOrchestration;
    private readonly TaskQueryService _taskQueries;
    private readonly TaskDependencyService _taskDependencies;
    private readonly MessageService _messageService;
    private readonly RoomService _roomService;
    private readonly AgentCatalogOptions _catalog;
    private readonly AgentOrchestrator _orchestrator;
    private readonly IAgentExecutor _executor;
    private readonly SpecManager _specManager;
    private readonly ILogger<CollaborationController> _logger;

    public CollaborationController(
        TaskOrchestrationService taskOrchestration,
        TaskQueryService taskQueries,
        TaskDependencyService taskDependencies,
        MessageService messageService,
        RoomService roomService,
        AgentCatalogOptions catalog,
        AgentOrchestrator orchestrator,
        IAgentExecutor executor,
        SpecManager specManager,
        ILogger<CollaborationController> logger)
    {
        _taskOrchestration = taskOrchestration;
        _taskQueries = taskQueries;
        _taskDependencies = taskDependencies;
        _messageService = messageService;
        _roomService = roomService;
        _catalog = catalog;
        _orchestrator = orchestrator;
        _executor = executor;
        _specManager = specManager;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/tasks — submit a new task.
    /// Creates the task and kicks off orchestration.
    /// </summary>
    [HttpPost("api/tasks")]
    public async Task<ActionResult<TaskAssignmentResult>> SubmitTask(
        [FromBody] TaskAssignmentRequest request)
    {
        if (request is null)
            return BadRequest(new { code = "invalid_task_request", message = "Task payload is required." });

        try
        {
            var result = await _taskOrchestration.CreateTaskAsync(request);
            _orchestrator.HandleHumanMessage(result.Room.Id);
            return StatusCode(201, result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { code = "invalid_task_request", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit task");
            return Problem("Failed to submit task.");
        }
    }

    /// <summary>
    /// GET /api/tasks/{taskId}/comments — get all comments for a task.
    /// </summary>
    [HttpGet("api/tasks/{taskId}/comments")]
    public async Task<ActionResult<List<TaskComment>>> GetTaskComments(string taskId)
    {
        try
        {
            var comments = await _taskQueries.GetTaskCommentsAsync(taskId);
            return Ok(comments);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/tasks/{taskId}/specs — get spec links for a task.
    /// </summary>
    [HttpGet("api/tasks/{taskId}/specs")]
    public async Task<ActionResult<List<SpecTaskLink>>> GetTaskSpecLinks(string taskId)
    {
        try
        {
            var links = await _taskQueries.GetSpecLinksForTaskAsync(taskId);
            return Ok(links);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/specs/{sectionId}/tasks — get tasks linked to a spec section.
    /// </summary>
    [HttpGet("api/specs/{sectionId}/tasks")]
    public async Task<ActionResult<List<SpecTaskLink>>> GetSpecTaskLinks(string sectionId)
    {
        var links = await _taskQueries.GetTasksForSpecAsync(sectionId);
        return Ok(links);
    }

    /// <summary>
    /// GET /api/specs/version — get current spec corpus version, content hash, and section count.
    /// </summary>
    [HttpGet("api/specs/version")]
    public async Task<ActionResult> GetSpecVersion()
    {
        var version = await _specManager.GetSpecVersionAsync();
        if (version is null)
            return NotFound(new { code = "no_specs", message = "No specification directory found." });
        return Ok(version);
    }

    /// <summary>
    /// POST /api/rooms/{roomId}/messages — post a message (agent-to-agent or system).
    /// </summary>
    [HttpPost("api/rooms/{roomId}/messages")]
    public async Task<ActionResult<ChatEnvelope>> PostMessage(
        string roomId,
        [FromBody] PostMessageRequest request)
    {
        if (request is null)
            return BadRequest(new { code = "invalid_message", message = "Message payload is required." });

        try
        {
            // Override roomId from path, matching v1 behavior
            var adjusted = request with { RoomId = roomId };
            var envelope = await _messageService.PostMessageAsync(adjusted);
            return Ok(envelope);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { code = "invalid_message", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post message to room '{RoomId}'", roomId);
            return Problem("Failed to post message.");
        }
    }

    /// <summary>
    /// POST /api/rooms/{roomId}/human — post a human message.
    /// Triggers orchestration after posting. Rate limiting is enforced
    /// via middleware or a future rate-limit service.
    /// </summary>
    [HttpPost("api/rooms/{roomId}/human")]
    public async Task<ActionResult<ChatEnvelope>> PostHumanMessage(
        string roomId,
        [FromBody] HumanMessageRequest request)
    {
        try
        {
            // Extract identity from authenticated user claims
            string? userId = null;
            string? userName = null;
            string? userRole = null;
            if (User.Identity?.IsAuthenticated == true)
            {
                userId = User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value;
                userName = User.FindFirst("urn:github:name")?.Value ?? userId;
                userRole = User.IsInRole("Consultant") ? "Consultant" : "Human";
            }

            var envelope = await _messageService.PostHumanMessageAsync(roomId, request.Content, userId, userName, userRole);

            // System status + orchestration are best-effort — don't fail the request
            try
            {
                await _messageService.PostSystemStatusAsync(roomId, "Human message received — notifying agents.");
                _orchestrator.HandleHumanMessage(roomId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Best-effort post-message actions failed for room '{RoomId}'", roomId);
            }

            return Ok(envelope);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { code = "invalid_message", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post human message to room '{RoomId}'", roomId);
            return Problem("Failed to post message.");
        }
    }

    /// <summary>
    /// POST /api/rooms/{roomId}/phase — transition room phase.
    /// </summary>
    [HttpPost("api/rooms/{roomId}/phase")]
    public async Task<ActionResult<RoomSnapshot>> TransitionPhase(
        string roomId,
        [FromBody] PhaseTransitionRequest request)
    {
        if (request is null)
            return BadRequest(new { code = "invalid_phase_request", message = "Phase transition payload is required." });

        try
        {
            var snapshot = await _roomService.TransitionPhaseAsync(
                roomId, request.TargetPhase, request.Reason);
            return Ok(snapshot);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { code = "invalid_phase_request", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to transition phase for room '{RoomId}'", roomId);
            return Problem("Failed to transition phase.");
        }
    }

    /// <summary>
    /// POST /api/rooms/{roomId}/compact — reset agent sessions for a room.
    /// Invalidates cached CLI sessions to free context window space.
    /// Note: The exact count of compacted sessions is not returned by the executor;
    /// we report the agent count as the upper bound, matching v1 behavior.
    /// </summary>
    [HttpPost("api/rooms/{roomId}/compact")]
    public async Task<IActionResult> CompactRoom(string roomId)
    {
        try
        {
            var totalAgents = _catalog.Agents.Count;

            if (_executor.IsFullyOperational)
            {
                await _executor.InvalidateRoomSessionsAsync(roomId);
                return Ok(new { compactedSessions = totalAgents, totalAgents });
            }

            return Ok(new
            {
                compactedSessions = 0,
                totalAgents,
                note = "Executor is not fully operational; no sessions to compact."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compact sessions for room '{RoomId}'", roomId);
            return Problem("Failed to compact room sessions.");
        }
    }
    /// <summary>
    /// GET /api/tasks — list all tasks. Optionally filter by sprint.
    /// </summary>
    [HttpGet("api/tasks")]
    public async Task<ActionResult<List<TaskSnapshot>>> ListTasks([FromQuery] string? sprintId = null)
    {
        var tasks = await _taskQueries.GetTasksAsync(sprintId);
        return Ok(tasks);
    }

    /// <summary>
    /// GET /api/tasks/{taskId} — get a single task.
    /// </summary>
    [HttpGet("api/tasks/{taskId}")]
    public async Task<ActionResult<TaskSnapshot>> GetTask(string taskId)
    {
        var task = await _taskQueries.GetTaskAsync(taskId);
        return task is null ? NotFound() : Ok(task);
    }

    /// <summary>
    /// PUT /api/tasks/{taskId}/assign — assign an agent to a task.
    /// </summary>
    [HttpPut("api/tasks/{taskId}/assign")]
    public async Task<ActionResult<TaskSnapshot>> AssignTask(
        string taskId, [FromBody] AssignTaskRequest request)
    {
        try
        {
            var task = await _taskQueries.AssignTaskAsync(taskId, request.AgentId, request.AgentName);
            return Ok(task);
        }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
    }

    /// <summary>
    /// PUT /api/tasks/{taskId}/status — update task status.
    /// </summary>
    [HttpPut("api/tasks/{taskId}/status")]
    public async Task<ActionResult<TaskSnapshot>> UpdateTaskStatus(
        string taskId, [FromBody] UpdateTaskStatusRequest request)
    {
        try
        {
            var task = await _taskQueries.UpdateTaskStatusAsync(taskId, request.Status);
            return Ok(task);
        }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
    }

    /// <summary>
    /// PUT /api/tasks/{taskId}/branch — record branch name on task.
    /// </summary>
    [HttpPut("api/tasks/{taskId}/branch")]
    public async Task<ActionResult<TaskSnapshot>> UpdateTaskBranch(
        string taskId, [FromBody] UpdateTaskBranchRequest request)
    {
        try
        {
            var task = await _taskQueries.UpdateTaskBranchAsync(taskId, request.BranchName);
            return Ok(task);
        }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
    }

    /// <summary>
    /// PUT /api/tasks/{taskId}/pr — record PR information on task.
    /// </summary>
    [HttpPut("api/tasks/{taskId}/pr")]
    public async Task<ActionResult<TaskSnapshot>> UpdateTaskPr(
        string taskId, [FromBody] UpdateTaskPrRequest request)
    {
        try
        {
            var task = await _taskQueries.UpdateTaskPrAsync(taskId, request.Url, request.Number, request.Status);
            return Ok(task);
        }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
    }

    /// <summary>
    /// PUT /api/tasks/{taskId}/complete — mark task complete with final metadata.
    /// </summary>
    [HttpPut("api/tasks/{taskId}/complete")]
    public async Task<ActionResult<TaskSnapshot>> CompleteTask(
        string taskId, [FromBody] CompleteTaskRequest request)
    {
        try
        {
            var task = await _taskOrchestration.CompleteTaskAsync(taskId, request.CommitCount, request.TestsCreated);
            return Ok(task);
        }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
    }

    // ── Task Dependencies ───────────────────────────────────────

    /// <summary>
    /// POST /api/tasks/{taskId}/dependencies — add a dependency.
    /// </summary>
    [HttpPost("api/tasks/{taskId}/dependencies")]
    public async Task<ActionResult<TaskDependencyInfo>> AddDependency(
        string taskId, [FromBody] AddDependencyRequest request)
    {
        try
        {
            var info = await _taskDependencies.AddDependencyAsync(taskId, request.DependsOnTaskId);
            return StatusCode(201, info);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>
    /// DELETE /api/tasks/{taskId}/dependencies/{dependsOnTaskId} — remove a dependency.
    /// </summary>
    [HttpDelete("api/tasks/{taskId}/dependencies/{dependsOnTaskId}")]
    public async Task<ActionResult<TaskDependencyInfo>> RemoveDependency(
        string taskId, string dependsOnTaskId)
    {
        try
        {
            var info = await _taskDependencies.RemoveDependencyAsync(taskId, dependsOnTaskId);
            return Ok(info);
        }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
    }

    /// <summary>
    /// GET /api/tasks/{taskId}/dependencies — get full dependency info.
    /// </summary>
    [HttpGet("api/tasks/{taskId}/dependencies")]
    public async Task<ActionResult<TaskDependencyInfo>> GetDependencies(string taskId)
    {
        var task = await _taskQueries.GetTaskAsync(taskId);
        if (task is null) return NotFound(new { error = $"Task '{taskId}' not found" });
        var info = await _taskDependencies.GetDependencyInfoAsync(taskId);
        return Ok(info);
    }
}

/// <summary>
/// Request body for human message endpoint.
/// </summary>
public record HumanMessageRequest([property: Required, MinLength(1), StringLength(50_000)] string Content);

public record AssignTaskRequest(
    [property: Required, StringLength(100)] string AgentId,
    [property: Required, StringLength(200)] string AgentName);
public record UpdateTaskStatusRequest([property: EnumDataType(typeof(Shared.Models.TaskStatus))] Shared.Models.TaskStatus Status);
public record UpdateTaskBranchRequest([property: Required, StringLength(300)] string BranchName);
public record UpdateTaskPrRequest(
    [property: Required, Url, StringLength(2000)] string Url,
    [property: Range(1, int.MaxValue)] int Number,
    [property: EnumDataType(typeof(Shared.Models.PullRequestStatus))] Shared.Models.PullRequestStatus Status);
public record CompleteTaskRequest(
    [property: Range(0, 100_000)] int CommitCount,
    List<string>? TestsCreated = null);
public record AddDependencyRequest(
    [property: Required, StringLength(200)] string DependsOnTaskId);
