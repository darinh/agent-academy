using AgentAcademy.Server.Services;
using AgentAcademy.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AgentAcademy.Server.Data;
using AgentAcademy.Server.Services.Contracts;

namespace AgentAcademy.Server.Commands.Handlers;

/// <summary>
/// Handles RUN_SELF_EVAL — opens the self-evaluation window at Implementation
/// (P1.4 design §4.2). Validates that:
/// <list type="number">
///   <item>The active sprint is Active and not blocked.</item>
///   <item>The current stage is Implementation.</item>
///   <item>At least one non-cancelled task exists.</item>
///   <item>At least one task has reached a terminal status (Completed or Cancelled).</item>
/// </list>
/// On success, sets <c>SelfEvaluationInFlight=true</c> so the next conversation
/// round injects the self-evaluation preamble. NO orchestrator wake — the
/// agent-driven path executes this command mid-round and the self-eval
/// preamble is picked up on the next preamble load. Wake-after-flag-flip
/// for the human/operator-override path is deferred to the API-endpoint PR.
/// </summary>
public sealed class RunSelfEvalHandler : ICommandHandler
{
    public string CommandName => "RUN_SELF_EVAL";

    public async Task<CommandEnvelope> ExecuteAsync(CommandEnvelope command, CommandContext context)
    {
        var roomService = context.Services.GetRequiredService<IRoomService>();
        var sprintService = context.Services.GetRequiredService<ISprintService>();
        var db = context.Services.GetRequiredService<AgentAcademyDbContext>();

        // Resolve sprint: explicit sprintId arg overrides the active workspace lookup.
        string? sprintId = null;
        if (command.Args.TryGetValue("sprintId", out var sidObj) && sidObj is string sid
            && !string.IsNullOrWhiteSpace(sid))
        {
            sprintId = sid;
        }

        Data.Entities.SprintEntity? sprint;
        if (sprintId is null)
        {
            var workspacePath = await roomService.GetActiveWorkspacePathAsync();
            if (string.IsNullOrEmpty(workspacePath))
            {
                return command with
                {
                    Status = CommandStatus.Error,
                    ErrorCode = CommandErrorCode.Validation,
                    Error = "No active workspace and no sprintId provided."
                };
            }
            sprint = await sprintService.GetActiveSprintAsync(workspacePath);
        }
        else
        {
            sprint = await sprintService.GetSprintByIdAsync(sprintId);
        }

        if (sprint is null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.NotFound,
                Error = sprintId is null
                    ? "No active sprint in the current workspace."
                    : $"Sprint {sprintId} not found."
            };
        }

        // Validation §4.2.
        if (sprint.Status != "Active")
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = $"Cannot run self-eval: sprint #{sprint.Number} is {sprint.Status}."
            };
        }

        if (sprint.BlockedAt is not null)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = $"Cannot run self-eval: sprint #{sprint.Number} is blocked ({sprint.BlockReason}). " +
                    "Unblock first."
            };
        }

        if (!string.Equals(sprint.CurrentStage, "Implementation", StringComparison.Ordinal))
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = $"Cannot run self-eval: sprint is in stage '{sprint.CurrentStage}', not Implementation."
            };
        }

        if (sprint.SelfEvaluationInFlight)
        {
            // Idempotent: re-running RUN_SELF_EVAL while already in-flight is
            // a no-op success so agents don't get blocked on transient retries.
            return command with
            {
                Status = CommandStatus.Success,
                Result = new Dictionary<string, object?>
                {
                    ["sprintId"] = sprint.Id,
                    ["sprintNumber"] = sprint.Number,
                    ["selfEvaluationInFlight"] = true,
                    ["attempts"] = sprint.SelfEvalAttempts,
                    ["message"] = $"Self-evaluation already in flight for sprint #{sprint.Number} " +
                        $"(attempt {sprint.SelfEvalAttempts + 1}). Submit a SelfEvaluationReport to close it."
                }
            };
        }

        var cancelled = nameof(Shared.Models.TaskStatus.Cancelled);
        var completed = nameof(Shared.Models.TaskStatus.Completed);

        var taskCounts = await db.Tasks
            .Where(t => t.SprintId == sprint.Id)
            .GroupBy(t => 1)
            .Select(g => new
            {
                NonCancelled = g.Count(t => t.Status != cancelled),
                Terminal = g.Count(t => t.Status == completed || t.Status == cancelled),
            })
            .FirstOrDefaultAsync();

        var nonCancelled = taskCounts?.NonCancelled ?? 0;
        var terminal = taskCounts?.Terminal ?? 0;

        if (nonCancelled == 0)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Cannot run self-eval: sprint has no non-cancelled tasks. " +
                    "Either add tasks or skip directly to FinalSynthesis."
            };
        }

        if (terminal == 0)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Validation,
                Error = "Cannot run self-eval: no task has reached a terminal status (Completed or Cancelled). " +
                    "Complete at least one task before evaluating."
            };
        }

        // Atomic flip: SelfEvaluationInFlight=true only if currently false and
        // sprint still Active+not-blocked+at Implementation. Avoids the
        // double-flip race when two RUN_SELF_EVAL calls land at once.
        var rowsFlipped = await db.Sprints
            .Where(s => s.Id == sprint.Id
                && s.Status == "Active"
                && s.BlockedAt == null
                && s.CurrentStage == "Implementation"
                && !s.SelfEvaluationInFlight)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.SelfEvaluationInFlight, true));

        if (rowsFlipped == 0)
        {
            return command with
            {
                Status = CommandStatus.Error,
                ErrorCode = CommandErrorCode.Conflict,
                Error = "Self-evaluation could not be opened — sprint state changed concurrently. Retry."
            };
        }

        return command with
        {
            Status = CommandStatus.Success,
            Result = new Dictionary<string, object?>
            {
                ["sprintId"] = sprint.Id,
                ["sprintNumber"] = sprint.Number,
                ["selfEvaluationInFlight"] = true,
                ["attempts"] = sprint.SelfEvalAttempts,
                ["message"] = $"Self-evaluation opened for sprint #{sprint.Number}. " +
                    $"Submit a SelfEvaluationReport for attempt {sprint.SelfEvalAttempts + 1}."
            }
        };
    }
}
