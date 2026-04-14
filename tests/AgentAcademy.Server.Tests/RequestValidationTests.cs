using System.ComponentModel.DataAnnotations;
using System.Reflection;
using AgentAcademy.Server.Controllers;
using AgentAcademy.Shared.Models;

namespace AgentAcademy.Server.Tests;

/// <summary>
/// Validates that DataAnnotations on API request types enforce constraints.
/// For record types, validation attributes live on constructor parameters
/// (matching ASP.NET Core 8 behavior). For class types, uses the standard
/// <see cref="Validator.TryValidateObject"/> property-based validation.
/// </summary>
public sealed class RequestValidationTests
{
    private static List<ValidationResult> Validate(object model)
    {
        var results = new List<ValidationResult>();
        var type = model.GetType();

        // Record types with positional parameters: validation attributes live on
        // constructor parameters (matching ASP.NET Core 8 behavior for records).
        // Validator.TryValidateObject only checks properties, so we validate manually.
        var ctor = type.GetConstructors().FirstOrDefault();
        if (ctor is not null && type.GetMethod("<Clone>$") is not null)
        {
            var paramsWithAttrs = ctor.GetParameters()
                .Where(p => p.GetCustomAttributes<ValidationAttribute>(true).Any())
                .ToList();

            if (paramsWithAttrs.Count > 0)
            {
                foreach (var param in paramsWithAttrs)
                {
                    var attrs = param.GetCustomAttributes<ValidationAttribute>(true).ToList();
                    var prop = type.GetProperty(param.Name!, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    var value = prop?.GetValue(model);
                    var memberName = prop?.Name ?? param.Name!;

                    foreach (var attr in attrs)
                    {
                        var ctx = new ValidationContext(model) { MemberName = memberName };
                        var result = attr.GetValidationResult(value, ctx);
                        if (result != ValidationResult.Success && result is not null)
                            results.Add(result);
                    }
                }
                return results;
            }
        }

        // Fallback: records with init properties or regular classes
        var context = new ValidationContext(model);
        Validator.TryValidateObject(model, context, results, validateAllProperties: true);
        return results;
    }

    // ── PostMessageRequest ──────────────────────────────────────

    [Fact]
    public void PostMessageRequest_valid_passes()
    {
        var request = new PostMessageRequest("room-1", "agent-1", "Hello world");
        Assert.Empty(Validate(request));
    }

    [Theory]
    [InlineData("", "sender", "content")]
    [InlineData("room", "", "content")]
    [InlineData("room", "sender", "")]
    public void PostMessageRequest_empty_strings_fail(string roomId, string senderId, string content)
    {
        var request = new PostMessageRequest(roomId, senderId, content);
        Assert.NotEmpty(Validate(request));
    }

    [Fact]
    public void PostMessageRequest_content_exceeds_max_length_fails()
    {
        var request = new PostMessageRequest("room-1", "agent-1", new string('x', 50_001));
        Assert.NotEmpty(Validate(request));
    }

    // ── TaskAssignmentRequest ───────────────────────────────────

    [Fact]
    public void TaskAssignmentRequest_valid_passes()
    {
        var request = new TaskAssignmentRequest("Title", "Description", "Success criteria", null, []);
        Assert.Empty(Validate(request));
    }

    [Fact]
    public void TaskAssignmentRequest_title_exceeds_max_length_fails()
    {
        var request = new TaskAssignmentRequest(new string('x', 201), "desc", "criteria", null, []);
        Assert.NotEmpty(Validate(request));
    }

    [Fact]
    public void TaskAssignmentRequest_empty_title_fails()
    {
        var request = new TaskAssignmentRequest("", "desc", "criteria", null, []);
        Assert.NotEmpty(Validate(request));
    }

    // ── HumanMessageRequest ─────────────────────────────────────

    [Fact]
    public void HumanMessageRequest_valid_passes()
    {
        var request = new HumanMessageRequest("Hello");
        Assert.Empty(Validate(request));
    }

    [Fact]
    public void HumanMessageRequest_empty_content_fails()
    {
        var request = new HumanMessageRequest("");
        Assert.NotEmpty(Validate(request));
    }

    // ── CreateCustomAgentRequest ────────────────────────────────

    [Fact]
    public void CreateCustomAgentRequest_valid_passes()
    {
        var request = new CreateCustomAgentRequest("Agent", "You are an agent");
        Assert.Empty(Validate(request));
    }

    [Fact]
    public void CreateCustomAgentRequest_name_exceeds_max_length_fails()
    {
        var request = new CreateCustomAgentRequest(new string('x', 101), "prompt");
        Assert.NotEmpty(Validate(request));
    }

    [Fact]
    public void CreateCustomAgentRequest_empty_prompt_fails()
    {
        var request = new CreateCustomAgentRequest("Agent", "");
        Assert.NotEmpty(Validate(request));
    }

    // ── CreateRoomRequest ───────────────────────────────────────

    [Fact]
    public void CreateRoomRequest_valid_passes()
    {
        var request = new CreateRoomRequest("Room 1");
        Assert.Empty(Validate(request));
    }

    [Fact]
    public void CreateRoomRequest_name_exceeds_max_length_fails()
    {
        var request = new CreateRoomRequest(new string('x', 201));
        Assert.NotEmpty(Validate(request));
    }

    [Fact]
    public void CreateRoomRequest_description_exceeds_max_length_fails()
    {
        var request = new CreateRoomRequest("Room", new string('x', 1001));
        Assert.NotEmpty(Validate(request));
    }

    // ── InstructionTemplateRequest ──────────────────────────────

    [Fact]
    public void InstructionTemplateRequest_valid_passes()
    {
        var request = new InstructionTemplateRequest("Template 1", null, "Content here");
        Assert.Empty(Validate(request));
    }

    [Fact]
    public void InstructionTemplateRequest_empty_content_fails()
    {
        var request = new InstructionTemplateRequest("Template", null, "");
        Assert.NotEmpty(Validate(request));
    }

    // ── UpdateQuotaRequest ──────────────────────────────────────

    [Fact]
    public void UpdateQuotaRequest_valid_null_passes()
    {
        var request = new UpdateQuotaRequest(null, null, null);
        Assert.Empty(Validate(request));
    }

    [Fact]
    public void UpdateQuotaRequest_valid_values_passes()
    {
        var request = new UpdateQuotaRequest(100, 50_000, 5.0m);
        Assert.Empty(Validate(request));
    }

    [Fact]
    public void UpdateQuotaRequest_zero_requests_fails()
    {
        var request = new UpdateQuotaRequest(0, null, null);
        Assert.NotEmpty(Validate(request));
    }

    // ── ExecuteCommandRequest ───────────────────────────────────

    [Fact]
    public void ExecuteCommandRequest_valid_passes()
    {
        var request = new ExecuteCommandRequest("RUN_BUILD", null);
        Assert.Empty(Validate(request));
    }

    [Fact]
    public void ExecuteCommandRequest_empty_command_fails()
    {
        var request = new ExecuteCommandRequest("", null);
        Assert.NotEmpty(Validate(request));
    }

    // ── SendDmRequest ───────────────────────────────────────────

    [Fact]
    public void SendDmRequest_valid_passes()
    {
        var request = new SendDmRequest("Hello agent");
        Assert.Empty(Validate(request));
    }

    [Fact]
    public void SendDmRequest_empty_message_fails()
    {
        var request = new SendDmRequest("");
        Assert.NotEmpty(Validate(request));
    }

    // ── CompleteTaskRequest ─────────────────────────────────────

    [Fact]
    public void CompleteTaskRequest_valid_passes()
    {
        var request = new CompleteTaskRequest(5);
        Assert.Empty(Validate(request));
    }

    [Fact]
    public void CompleteTaskRequest_negative_commit_count_fails()
    {
        var request = new CompleteTaskRequest(-1);
        Assert.NotEmpty(Validate(request));
    }

    // ── UpdateTaskPrRequest ─────────────────────────────────────

    [Fact]
    public void UpdateTaskPrRequest_valid_passes()
    {
        var request = new UpdateTaskPrRequest("https://github.com/org/repo/pull/1", 1, PullRequestStatus.Open);
        Assert.Empty(Validate(request));
    }

    [Fact]
    public void UpdateTaskPrRequest_zero_number_fails()
    {
        var request = new UpdateTaskPrRequest("https://github.com/org/repo/pull/1", 0, PullRequestStatus.Open);
        Assert.NotEmpty(Validate(request));
    }

    // ── ScanRequest / SwitchWorkspaceRequest ────────────────────

    [Fact]
    public void ScanRequest_valid_passes()
    {
        var request = new ScanRequest("/home/user/project");
        Assert.Empty(Validate(request));
    }

    [Fact]
    public void ScanRequest_path_exceeds_max_length_fails()
    {
        var request = new ScanRequest(new string('x', 1001));
        Assert.NotEmpty(Validate(request));
    }

    // ── Enum validation ─────────────────────────────────────────

    [Fact]
    public void UpdateTaskStatusRequest_valid_enum_passes()
    {
        var request = new UpdateTaskStatusRequest(Shared.Models.TaskStatus.Active);
        Assert.Empty(Validate(request));
    }

    [Fact]
    public void UpdateTaskStatusRequest_invalid_enum_fails()
    {
        var request = new UpdateTaskStatusRequest((Shared.Models.TaskStatus)999);
        Assert.NotEmpty(Validate(request));
    }

    [Fact]
    public void UpdateTaskPrRequest_invalid_pr_status_fails()
    {
        var request = new UpdateTaskPrRequest("https://github.com/org/repo/pull/1", 1, (PullRequestStatus)999);
        Assert.NotEmpty(Validate(request));
    }

    [Fact]
    public void PostMessageRequest_invalid_message_kind_fails()
    {
        var request = new PostMessageRequest("room", "sender", "content", (MessageKind)999);
        Assert.NotEmpty(Validate(request));
    }

    [Fact]
    public void PhaseTransitionRequest_invalid_phase_fails()
    {
        var request = new PhaseTransitionRequest("room", (CollaborationPhase)999);
        Assert.NotEmpty(Validate(request));
    }

    // ── Memory import validation ────────────────────────────────

    [Fact]
    public void MemoryImportEntry_value_exceeds_500_fails()
    {
        var entry = new MemoryController.MemoryImportEntry
        {
            AgentId = "agent-1", Category = "fact", Key = "test",
            Value = new string('x', 501)
        };
        Assert.NotEmpty(Validate(entry));
    }

    [Fact]
    public void MemoryImportEntry_valid_passes()
    {
        var entry = new MemoryController.MemoryImportEntry
        {
            AgentId = "agent-1", Category = "fact", Key = "test",
            Value = "some value"
        };
        Assert.Empty(Validate(entry));
    }

    [Fact]
    public void MemoryImportEntry_ttl_out_of_range_fails()
    {
        var entry = new MemoryController.MemoryImportEntry
        {
            AgentId = "agent-1", Category = "fact", Key = "test",
            Value = "v", TtlHours = 0
        };
        Assert.NotEmpty(Validate(entry));
    }
}
