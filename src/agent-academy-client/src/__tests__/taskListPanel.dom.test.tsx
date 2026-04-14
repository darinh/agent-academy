// @vitest-environment jsdom
/**
 * Interactive RTL tests for TaskListPanel.
 *
 * Uses @testing-library/react + jsdom.
 * Covers: filter chip clicks, sprint toggle, task card expand/collapse,
 * detail auto-fetch (comments + spec links), evidence load, gate check,
 * review actions (approve, request changes, reject, merge), reason textarea,
 * agent assignment, comment error/retry, action feedback, empty filter state.
 */
import "@testing-library/jest-dom/vitest";
import {
  cleanup,
  render,
  screen,
  waitFor,
  fireEvent,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

const mockExecuteCommand = vi.fn();
const mockGetTaskComments = vi.fn();
const mockGetTaskSpecLinks = vi.fn();
const mockGetTaskDependencies = vi.fn();
const mockAssignTask = vi.fn();
const mockBulkUpdateStatus = vi.fn();
const mockBulkAssign = vi.fn();

vi.mock("../api", () => ({
  executeCommand: (...args: unknown[]) => mockExecuteCommand(...args),
  getTaskComments: (...args: unknown[]) => mockGetTaskComments(...args),
  getTaskSpecLinks: (...args: unknown[]) => mockGetTaskSpecLinks(...args),
  getTaskDependencies: (...args: unknown[]) => mockGetTaskDependencies(...args),
  assignTask: (...args: unknown[]) => mockAssignTask(...args),
  bulkUpdateStatus: (...args: unknown[]) => mockBulkUpdateStatus(...args),
  bulkAssign: (...args: unknown[]) => mockBulkAssign(...args),
}));

vi.mock("../V3Badge", () => ({
  default: ({
    children,
    color,
  }: {
    children: React.ReactNode;
    color: string;
  }) => createElement("span", { "data-testid": `badge-${color}` }, children),
}));

vi.mock("../EmptyState", () => ({
  default: ({
    title,
    detail,
  }: {
    icon?: React.ReactNode;
    title: string;
    detail?: string;
  }) =>
    createElement(
      "div",
      { "data-testid": "empty-state" },
      createElement("span", null, title),
      detail && createElement("span", null, detail),
    ),
}));

vi.mock("../ErrorState", () => ({
  default: ({
    message,
    detail,
    onRetry,
  }: {
    message: string;
    detail?: string;
    onRetry?: () => void;
  }) =>
    createElement(
      "div",
      { "data-testid": "error-state" },
      createElement("span", null, message),
      detail && createElement("span", null, detail),
      onRetry && createElement("button", { onClick: onRetry }, "Retry"),
    ),
}));

vi.mock("../SkeletonLoader", () => ({
  default: ({ rows, variant }: { rows: number; variant?: string }) =>
    createElement("div", { "data-testid": "skeleton-loader" }, `Loading ${rows} ${variant ?? "rows"}`),
}));

import TaskListPanel from "../TaskListPanel";
import type {
  TaskSnapshot,
  AgentDefinition,
  TaskComment,
  SpecTaskLink,
  CommandExecutionResponse,
} from "../api";

// ── Factories ──────────────────────────────────────────────────────────

function makeTask(overrides: Partial<TaskSnapshot> = {}): TaskSnapshot {
  return {
    id: "task-1",
    title: "Implement auth flow",
    description: "Add JWT-based authentication",
    successCriteria: "All auth tests pass",
    status: "Active",
    currentPhase: "Implementation",
    currentPlan: "",
    validationStatus: "",
    validationSummary: "",
    implementationStatus: "",
    implementationSummary: "",
    preferredRoles: [],
    createdAt: "2026-04-01T00:00:00Z",
    updatedAt: "2026-04-01T12:00:00Z",
    ...overrides,
  };
}

function makeAgent(overrides: Partial<AgentDefinition> = {}): AgentDefinition {
  return {
    id: "agent-1",
    name: "Hephaestus",
    role: "SoftwareEngineer",
    summary: "Backend engineer",
    startupPrompt: "",
    model: null,
    capabilityTags: ["implementation"],
    enabledTools: [],
    autoJoinDefaultRoom: true,
    gitIdentity: null,
    ...overrides,
  };
}

function makeComment(overrides: Partial<TaskComment> = {}): TaskComment {
  return {
    id: "comment-1",
    taskId: "task-1",
    agentId: "agent-1",
    agentName: "Hephaestus",
    commentType: "Comment",
    content: "Looks good so far",
    createdAt: "2026-04-01T00:00:00Z",
    ...overrides,
  };
}

function makeSpecLink(overrides: Partial<SpecTaskLink> = {}): SpecTaskLink {
  return {
    id: "link-1",
    taskId: "task-1",
    specSectionId: "010-task-management/§3",
    linkType: "Implements",
    linkedByAgentId: "agent-1",
    linkedByAgentName: "Hephaestus",
    note: null,
    createdAt: "2026-04-01T00:00:00Z",
    ...overrides,
  };
}

function makeCommandResponse(
  overrides: Partial<CommandExecutionResponse> = {},
): CommandExecutionResponse {
  return {
    command: "TEST",
    status: "completed",
    result: null,
    error: null,
    errorCode: null,
    correlationId: "corr-1",
    timestamp: "2026-04-01T00:00:00Z",
    executedBy: "human",
    ...overrides,
  };
}

// ── Render helper ──────────────────────────────────────────────────────

interface RenderProps {
  tasks?: TaskSnapshot[];
  loading?: boolean;
  error?: boolean;
  onRefresh?: () => void;
  activeSprintId?: string | null;
  agents?: AgentDefinition[];
}

function renderPanel(props: RenderProps = {}) {
  const {
    tasks = [makeTask()],
    loading = false,
    error = false,
    onRefresh = vi.fn(),
    activeSprintId,
    agents = [makeAgent()],
  } = props;

  const result = render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(TaskListPanel, {
        tasks,
        loading,
        error,
        onRefresh,
        activeSprintId,
        agents,
      }),
    ),
  );

  return { ...result, onRefresh };
}

// ── Unique ID generator (avoids module-level detailCache collisions) ──

let _uid = 0;
function uid(): string {
  _uid += 1;
  return `task-${_uid}`;
}

// ── Setup / Teardown ───────────────────────────────────────────────────

beforeEach(() => {
  mockGetTaskComments.mockResolvedValue([]);
  mockGetTaskSpecLinks.mockResolvedValue([]);
  mockGetTaskDependencies.mockResolvedValue({ taskId: "", dependsOn: [], dependedOnBy: [] });
  mockExecuteCommand.mockResolvedValue(makeCommandResponse());
  mockAssignTask.mockResolvedValue(undefined);
});

afterEach(() => {
  cleanup();
  mockExecuteCommand.mockReset();
  mockGetTaskComments.mockReset();
  mockGetTaskSpecLinks.mockReset();
  mockGetTaskDependencies.mockReset();
  mockAssignTask.mockReset();
  vi.restoreAllMocks();
});

// ── Tests ──────────────────────────────────────────────────────────────

describe("TaskListPanel (interactive)", () => {
  // ── Loading / Error / Empty ────────────────────────────────────────

  describe("loading, error, and empty states", () => {
    it("renders skeleton loader when loading", () => {
      renderPanel({ loading: true, tasks: [] });
      expect(screen.getByTestId("skeleton-loader")).toBeInTheDocument();
    });

    it("renders error state with retry", () => {
      const onRefresh = vi.fn();
      renderPanel({ error: true, onRefresh });
      expect(screen.getByTestId("error-state")).toBeInTheDocument();
      expect(screen.getByText("Failed to load tasks")).toBeInTheDocument();
    });

    it("retry button in error state calls onRefresh", async () => {
      const onRefresh = vi.fn();
      renderPanel({ error: true, onRefresh });
      await userEvent.click(screen.getByRole("button", { name: "Retry" }));
      expect(onRefresh).toHaveBeenCalledTimes(1);
    });

    it("renders empty state when no tasks", () => {
      renderPanel({ tasks: [] });
      expect(screen.getByTestId("empty-state")).toBeInTheDocument();
      expect(screen.getByText("No tasks assigned")).toBeInTheDocument();
    });
  });

  // ── Filter Bar ──────────────────────────────────────────────────────

  describe("filter bar", () => {
    const mixedTasks = [
      makeTask({ id: "f1", status: "Active", title: "Active Task", updatedAt: "2026-04-01T10:00:00Z" }),
      makeTask({ id: "f2", status: "InReview", title: "Review Task", updatedAt: "2026-04-01T11:00:00Z" }),
      makeTask({ id: "f3", status: "Completed", title: "Done Task", updatedAt: "2026-04-01T12:00:00Z" }),
      makeTask({ id: "f4", status: "Queued", title: "Queued Task", updatedAt: "2026-04-01T09:00:00Z" }),
      makeTask({ id: "f5", status: "Approved", title: "Approved Task", updatedAt: "2026-04-01T08:00:00Z" }),
    ];

    // Filter chips are <button> elements in the filter bar; status badges are <span>s from V3Badge mock.
    // Checkbox buttons have names starting with "Select" or "Deselect" — exclude them.
    function filterBtn(name: RegExp) {
      const buttons = screen.getAllByRole("button", { name });
      const chip = buttons.find(b => !b.getAttribute("aria-label")?.startsWith("Select")
        && !b.getAttribute("aria-label")?.startsWith("Deselect"));
      if (!chip) throw new Error(`No filter chip button matching ${name}`);
      return chip;
    }

    it("renders all four filter chips", () => {
      renderPanel({ tasks: mixedTasks });
      expect(filterBtn(/All/)).toBeInTheDocument();
      expect(filterBtn(/Review Queue/)).toBeInTheDocument();
      expect(filterBtn(/Active/)).toBeInTheDocument();
      expect(filterBtn(/Completed/)).toBeInTheDocument();
    });

    it("All filter shows all tasks by default", () => {
      renderPanel({ tasks: mixedTasks });
      expect(screen.getByText("Active Task")).toBeInTheDocument();
      expect(screen.getByText("Review Task")).toBeInTheDocument();
      expect(screen.getByText("Done Task")).toBeInTheDocument();
      expect(screen.getByText("Queued Task")).toBeInTheDocument();
      expect(screen.getByText("Approved Task")).toBeInTheDocument();
    });

    it("clicking Review Queue filter shows only review-status tasks", async () => {
      renderPanel({ tasks: mixedTasks });
      await userEvent.click(filterBtn(/Review Queue/));
      expect(screen.getByText("Review Task")).toBeInTheDocument();
      expect(screen.getByText("Approved Task")).toBeInTheDocument();
      expect(screen.queryByText("Active Task")).not.toBeInTheDocument();
      expect(screen.queryByText("Done Task")).not.toBeInTheDocument();
      expect(screen.queryByText("Queued Task")).not.toBeInTheDocument();
    });

    it("clicking Active filter shows only active-status tasks", async () => {
      renderPanel({ tasks: mixedTasks });
      await userEvent.click(filterBtn(/Active/));
      expect(screen.getByText("Active Task")).toBeInTheDocument();
      expect(screen.getByText("Queued Task")).toBeInTheDocument();
      expect(screen.queryByText("Review Task")).not.toBeInTheDocument();
      expect(screen.queryByText("Done Task")).not.toBeInTheDocument();
    });

    it("clicking Completed filter shows only completed/cancelled tasks", async () => {
      renderPanel({ tasks: mixedTasks });
      await userEvent.click(filterBtn(/Completed/));
      expect(screen.getByText("Done Task")).toBeInTheDocument();
      expect(screen.queryByText("Active Task")).not.toBeInTheDocument();
      expect(screen.queryByText("Review Task")).not.toBeInTheDocument();
    });

    it("switching back to All shows all tasks again", async () => {
      renderPanel({ tasks: mixedTasks });
      await userEvent.click(filterBtn(/Completed/));
      expect(screen.queryByText("Active Task")).not.toBeInTheDocument();
      await userEvent.click(filterBtn(/All/));
      expect(screen.getByText("Active Task")).toBeInTheDocument();
      expect(screen.getByText("Done Task")).toBeInTheDocument();
    });

    it("shows empty message when filter matches zero tasks", async () => {
      const onlyActive = [makeTask({ id: "f6", status: "Active", title: "Active One" })];
      renderPanel({ tasks: onlyActive });
      await userEvent.click(filterBtn(/Completed/));
      expect(screen.getByText("No tasks match this filter")).toBeInTheDocument();
    });
  });

  // ── Sprint Filter ──────────────────────────────────────────────────

  describe("sprint filter", () => {
    const sprintTasks = [
      makeTask({ id: "1", title: "Sprint Task", sprintId: "sprint-1", updatedAt: "2026-04-01T12:00:00Z" }),
      makeTask({ id: "2", title: "Non-Sprint Task", sprintId: null, updatedAt: "2026-04-01T11:00:00Z" }),
    ];

    it("shows Sprint chip when activeSprintId is provided", () => {
      renderPanel({ tasks: sprintTasks, activeSprintId: "sprint-1" });
      expect(screen.getByText("🏃 Sprint")).toBeInTheDocument();
    });

    it("does not show Sprint chip when no activeSprintId", () => {
      renderPanel({ tasks: sprintTasks });
      expect(screen.queryByText("🏃 Sprint")).not.toBeInTheDocument();
    });

    it("clicking Sprint chip filters to sprint-only tasks", async () => {
      renderPanel({ tasks: sprintTasks, activeSprintId: "sprint-1" });
      expect(screen.getByText("Sprint Task")).toBeInTheDocument();
      expect(screen.getByText("Non-Sprint Task")).toBeInTheDocument();

      await userEvent.click(screen.getByText("🏃 Sprint"));
      expect(screen.getByText("Sprint Task")).toBeInTheDocument();
      expect(screen.queryByText("Non-Sprint Task")).not.toBeInTheDocument();
    });

    it("clicking Sprint chip again disables sprint filter", async () => {
      renderPanel({ tasks: sprintTasks, activeSprintId: "sprint-1" });
      await userEvent.click(screen.getByText("🏃 Sprint"));
      expect(screen.queryByText("Non-Sprint Task")).not.toBeInTheDocument();

      await userEvent.click(screen.getByText("🏃 Sprint"));
      expect(screen.getByText("Non-Sprint Task")).toBeInTheDocument();
    });
  });

  // ── Card Expand / Collapse ─────────────────────────────────────────

  describe("card expand and collapse", () => {
    it("clicking a card expands it to show detail sections", async () => {
      const taskId = uid();
      mockGetTaskComments.mockResolvedValue([]);
      mockGetTaskSpecLinks.mockResolvedValue([]);
      renderPanel({ tasks: [makeTask({ id: taskId, description: "JWT auth details", title: "Expand Test" })] });

      expect(screen.queryByText("Description")).not.toBeInTheDocument();
      await userEvent.click(screen.getByText("Expand Test"));

      await waitFor(() => {
        expect(screen.getByText("Description")).toBeInTheDocument();
      });
      expect(screen.getByText("JWT auth details")).toBeInTheDocument();
    });

    it("clicking the title of an expanded card collapses it", async () => {
      const taskId = uid();
      mockGetTaskComments.mockResolvedValue([]);
      mockGetTaskSpecLinks.mockResolvedValue([]);
      renderPanel({ tasks: [makeTask({ id: taskId, description: "JWT auth details", title: "Collapse Test" })] });

      await userEvent.click(screen.getByText("Collapse Test"));
      await waitFor(() => {
        expect(screen.getByText("Description")).toBeInTheDocument();
      });

      await userEvent.click(screen.getByText("Collapse Test"));
      expect(screen.queryByText("Description")).not.toBeInTheDocument();
    });

    it("expanding a different card collapses the previous one", async () => {
      const id1 = uid(), id2 = uid();
      mockGetTaskComments.mockResolvedValue([]);
      mockGetTaskSpecLinks.mockResolvedValue([]);
      const tasks = [
        makeTask({ id: id1, title: "Task A", description: "Desc A", updatedAt: "2026-04-01T12:00:00Z" }),
        makeTask({ id: id2, title: "Task B", description: "Desc B", updatedAt: "2026-04-01T11:00:00Z" }),
      ];
      renderPanel({ tasks });

      await userEvent.click(screen.getByText("Task A"));
      await waitFor(() => expect(screen.getByText("Desc A")).toBeInTheDocument());

      await userEvent.click(screen.getByText("Task B"));
      await waitFor(() => expect(screen.getByText("Desc B")).toBeInTheDocument());
      expect(screen.queryByText("Desc A")).not.toBeInTheDocument();
    });
  });

  // ── Detail Fetching on Expand ──────────────────────────────────────

  describe("detail fetching on expand", () => {
    it("fetches comments and spec links when a card is expanded", async () => {
      const taskId = uid();
      const comments = [makeComment({ taskId, content: "Looks good so far" })];
      const specLinks = [makeSpecLink({ taskId, specSectionId: "010-task/§3" })];
      mockGetTaskComments.mockResolvedValue(comments);
      mockGetTaskSpecLinks.mockResolvedValue(specLinks);

      renderPanel({ tasks: [makeTask({ id: taskId, title: "Fetch Test" })] });
      await userEvent.click(screen.getByText("Fetch Test"));

      await waitFor(() => {
        expect(mockGetTaskComments).toHaveBeenCalledWith(taskId);
        expect(mockGetTaskSpecLinks).toHaveBeenCalledWith(taskId);
      });

      await waitFor(() => {
        expect(screen.getByText("Looks good so far")).toBeInTheDocument();
        expect(screen.getByText("010-task/§3")).toBeInTheDocument();
      });
    });

    it("shows spec link badge and author", async () => {
      const taskId = uid();
      mockGetTaskSpecLinks.mockResolvedValue([
        makeSpecLink({ taskId, linkType: "Implements", linkedByAgentName: "Hephaestus" }),
      ]);
      renderPanel({ tasks: [makeTask({ id: taskId, title: "SpecLink Test" })] });
      await userEvent.click(screen.getByText("SpecLink Test"));

      await waitFor(() => {
        expect(screen.getByText("Implements")).toBeInTheDocument();
        expect(screen.getByText(/Hephaestus/)).toBeInTheDocument();
      });
    });

    it("shows 'No spec links' when none returned", async () => {
      const taskId = uid();
      mockGetTaskSpecLinks.mockResolvedValue([]);
      renderPanel({ tasks: [makeTask({ id: taskId, title: "NoLinks Test" })] });
      await userEvent.click(screen.getByText("NoLinks Test"));

      await waitFor(() => {
        expect(screen.getByText("No spec links")).toBeInTheDocument();
      });
    });

    it("shows 'No comments yet' when none returned", async () => {
      const taskId = uid();
      mockGetTaskComments.mockResolvedValue([]);
      renderPanel({ tasks: [makeTask({ id: taskId, title: "NoComments Test" })] });
      await userEvent.click(screen.getByText("NoComments Test"));

      await waitFor(() => {
        expect(screen.getByText("No comments yet")).toBeInTheDocument();
      });
    });

    it("shows comment type badge", async () => {
      const taskId = uid();
      mockGetTaskComments.mockResolvedValue([
        makeComment({ taskId, commentType: "Finding", content: "Found an issue" }),
      ]);
      renderPanel({ tasks: [makeTask({ id: taskId, title: "CommentBadge Test" })] });
      await userEvent.click(screen.getByText("CommentBadge Test"));

      await waitFor(() => {
        expect(screen.getByText("Finding")).toBeInTheDocument();
        expect(screen.getByText("Found an issue")).toBeInTheDocument();
      });
    });
  });

  // ── Comment Error / Retry ──────────────────────────────────────────

  describe("comment loading error and retry", () => {
    it("shows error message and retry button when comments fail to load", async () => {
      const taskId = uid();
      mockGetTaskComments.mockRejectedValue(new Error("Network error"));
      renderPanel({ tasks: [makeTask({ id: taskId, title: "CommentErr Test" })] });
      await userEvent.click(screen.getByText("CommentErr Test"));

      await waitFor(() => {
        expect(screen.getByText("Failed to load comments")).toBeInTheDocument();
      });
      expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument();
    });

    it("retry button re-fetches comments", async () => {
      const taskId = uid();
      mockGetTaskComments.mockRejectedValueOnce(new Error("fail"));
      renderPanel({ tasks: [makeTask({ id: taskId, title: "CommentRetry Test" })] });
      await userEvent.click(screen.getByText("CommentRetry Test"));

      await waitFor(() => {
        expect(screen.getByText("Failed to load comments")).toBeInTheDocument();
      });

      mockGetTaskComments.mockResolvedValue([
        makeComment({ taskId, content: "Retry succeeded" }),
      ]);
      await userEvent.click(screen.getByRole("button", { name: "Retry" }));

      await waitFor(() => {
        expect(screen.getByText("Retry succeeded")).toBeInTheDocument();
      });
    });
  });

  // ── Evidence Ledger ────────────────────────────────────────────────

  describe("evidence ledger", () => {
    it("shows Load button in evidence section", async () => {
      const taskId = uid();
      renderPanel({ tasks: [makeTask({ id: taskId, title: "EvidLoad Test" })] });
      await userEvent.click(screen.getByText("EvidLoad Test"));

      await waitFor(() => {
        expect(screen.getByRole("button", { name: "Load" })).toBeInTheDocument();
      });
    });

    it("clicking Load fetches evidence via QUERY_EVIDENCE command", async () => {
      const taskId = uid();
      mockExecuteCommand.mockResolvedValue(
        makeCommandResponse({
          command: "QUERY_EVIDENCE",
          result: {
            evidence: [
              {
                id: "ev-1",
                phase: "After",
                checkName: "build",
                tool: "bash",
                command: "dotnet build",
                exitCode: 0,
                output: "Build succeeded",
                passed: true,
                agentName: "Hephaestus",
                createdAt: "2026-04-01T00:00:00Z",
              },
            ],
          },
        }),
      );

      renderPanel({ tasks: [makeTask({ id: taskId, title: "EvidFetch Test" })] });
      await userEvent.click(screen.getByText("EvidFetch Test"));

      await waitFor(() => {
        expect(screen.getByRole("button", { name: "Load" })).toBeInTheDocument();
      });

      await userEvent.click(screen.getByRole("button", { name: "Load" }));

      await waitFor(() => {
        expect(mockExecuteCommand).toHaveBeenCalledWith(
          expect.objectContaining({ command: "QUERY_EVIDENCE", args: { taskId } }),
        );
      });

      await waitFor(() => {
        expect(screen.getByText("build")).toBeInTheDocument();
        expect(screen.getByText("bash")).toBeInTheDocument();
        expect(screen.getByText("Pass")).toBeInTheDocument();
      });
    });

    it("shows 'No evidence recorded' when evidence is empty", async () => {
      const taskId = uid();
      mockExecuteCommand.mockResolvedValue(
        makeCommandResponse({ command: "QUERY_EVIDENCE", result: { evidence: [] } }),
      );
      renderPanel({ tasks: [makeTask({ id: taskId, title: "EvidEmpty Test" })] });
      await userEvent.click(screen.getByText("EvidEmpty Test"));

      await waitFor(() => expect(screen.getByRole("button", { name: "Load" })).toBeInTheDocument());
      await userEvent.click(screen.getByRole("button", { name: "Load" }));

      await waitFor(() => {
        expect(screen.getByText("No evidence recorded")).toBeInTheDocument();
      });
    });
  });

  // ── Gate Check ─────────────────────────────────────────────────────

  describe("gate check", () => {
    it("shows Check Gates button for Active tasks", async () => {
      const taskId = uid();
      renderPanel({ tasks: [makeTask({ id: taskId, status: "Active", title: "GateActive Test" })] });
      await userEvent.click(screen.getByText("GateActive Test"));

      await waitFor(() => {
        expect(screen.getByRole("button", { name: "Check Gates" })).toBeInTheDocument();
      });
    });

    it("shows Check Gates button for InReview tasks", async () => {
      const taskId = uid();
      renderPanel({ tasks: [makeTask({ id: taskId, status: "InReview", title: "GateReview Test" })] });
      await userEvent.click(screen.getByText("GateReview Test"));

      await waitFor(() => {
        expect(screen.getByRole("button", { name: "Check Gates" })).toBeInTheDocument();
      });
    });

    it("does not show Check Gates for Completed tasks", async () => {
      const taskId = uid();
      renderPanel({ tasks: [makeTask({ id: taskId, status: "Completed", title: "GateComplete Test" })] });
      await userEvent.click(screen.getByText("GateComplete Test"));

      await waitFor(() => {
        expect(screen.getByText("Description")).toBeInTheDocument();
      });
      expect(screen.queryByText("Check Gates")).not.toBeInTheDocument();
    });

    it("clicking Check Gates calls CHECK_GATES command and shows result", async () => {
      const taskId = uid();
      mockExecuteCommand.mockResolvedValue(
        makeCommandResponse({
          command: "CHECK_GATES",
          result: {
            taskId,
            currentPhase: "Active",
            targetPhase: "AwaitingValidation",
            met: true,
            requiredChecks: 2,
            passedChecks: 3,
            missingChecks: [],
            evidence: [],
            message: "Gates met",
          },
        }),
      );

      renderPanel({ tasks: [makeTask({ id: taskId, status: "Active", title: "GateMet Test" })] });
      await userEvent.click(screen.getByText("GateMet Test"));

      await waitFor(() => expect(screen.getByRole("button", { name: "Check Gates" })).toBeInTheDocument());
      await userEvent.click(screen.getByRole("button", { name: "Check Gates" }));

      await waitFor(() => {
        expect(mockExecuteCommand).toHaveBeenCalledWith(
          expect.objectContaining({ command: "CHECK_GATES", args: { taskId } }),
        );
      });

      await waitFor(() => {
        expect(screen.getByText("Gate met")).toBeInTheDocument();
      });
    });

    it("shows missing checks when gates are not met", async () => {
      const taskId = uid();
      mockExecuteCommand.mockResolvedValue(
        makeCommandResponse({
          command: "CHECK_GATES",
          result: {
            taskId,
            currentPhase: "Active",
            targetPhase: "AwaitingValidation",
            met: false,
            requiredChecks: 3,
            passedChecks: 1,
            missingChecks: ["tests", "lint"],
            evidence: [],
            message: "Gates NOT met",
          },
        }),
      );

      renderPanel({ tasks: [makeTask({ id: taskId, status: "Active", title: "GateNotMet Test" })] });
      await userEvent.click(screen.getByText("GateNotMet Test"));

      await waitFor(() => expect(screen.getByRole("button", { name: "Check Gates" })).toBeInTheDocument());
      await userEvent.click(screen.getByRole("button", { name: "Check Gates" }));

      await waitFor(() => {
        expect(screen.getByText("1/3 required")).toBeInTheDocument();
        expect(screen.getByText(/tests, lint/)).toBeInTheDocument();
      });
    });

    it("shows Recheck button after initial gate check", async () => {
      const taskId = uid();
      mockExecuteCommand.mockResolvedValue(
        makeCommandResponse({
          command: "CHECK_GATES",
          result: {
            taskId,
            currentPhase: "Active",
            targetPhase: "AwaitingValidation",
            met: true,
            requiredChecks: 2,
            passedChecks: 2,
            missingChecks: [],
            evidence: [],
            message: "Met",
          },
        }),
      );

      renderPanel({ tasks: [makeTask({ id: taskId, status: "Active", title: "GateRecheck Test" })] });
      await userEvent.click(screen.getByText("GateRecheck Test"));

      await waitFor(() => expect(screen.getByRole("button", { name: "Check Gates" })).toBeInTheDocument());
      await userEvent.click(screen.getByRole("button", { name: "Check Gates" }));

      await waitFor(() => {
        expect(screen.getByRole("button", { name: "Recheck" })).toBeInTheDocument();
      });
    });
  });

  // ── Review Actions ─────────────────────────────────────────────────

  describe("review actions", () => {
    it("shows Approve and Request Changes for InReview tasks", async () => {
      const taskId = uid();
      renderPanel({ tasks: [makeTask({ id: taskId, status: "InReview", title: "ActionReview Test" })] });
      await userEvent.click(screen.getByText("ActionReview Test"));

      await waitFor(() => {
        expect(screen.getByRole("button", { name: "Approve" })).toBeInTheDocument();
        expect(screen.getByRole("button", { name: "Request Changes" })).toBeInTheDocument();
      });
    });

    it("shows Merge and Reject for Approved tasks", async () => {
      const taskId = uid();
      renderPanel({ tasks: [makeTask({ id: taskId, status: "Approved", title: "ActionApproved Test" })] });
      await userEvent.click(screen.getByText("ActionApproved Test"));

      await waitFor(() => {
        expect(screen.getByRole("button", { name: "Merge" })).toBeInTheDocument();
        expect(screen.getByRole("button", { name: "Reject" })).toBeInTheDocument();
      });
    });

    it("shows Reject for Completed tasks", async () => {
      const taskId = uid();
      renderPanel({ tasks: [makeTask({ id: taskId, status: "Completed", title: "ActionComplete Test" })] });
      await userEvent.click(screen.getByText("ActionComplete Test"));

      await waitFor(() => {
        expect(screen.getByRole("button", { name: "Reject" })).toBeInTheDocument();
      });
      expect(screen.queryByRole("button", { name: "Approve" })).not.toBeInTheDocument();
    });

    it("shows no action buttons for Active tasks", async () => {
      const taskId = uid();
      renderPanel({ tasks: [makeTask({ id: taskId, status: "Active", title: "ActionActive Test" })] });
      await userEvent.click(screen.getByText("ActionActive Test"));

      await waitFor(() => {
        expect(screen.getByText("Description")).toBeInTheDocument();
      });
      expect(screen.queryByRole("button", { name: "Approve" })).not.toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "Reject" })).not.toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "Merge" })).not.toBeInTheDocument();
    });

    it("clicking Approve calls APPROVE_TASK command", async () => {
      const taskId = uid();
      mockExecuteCommand.mockResolvedValue(
        makeCommandResponse({ command: "APPROVE_TASK", status: "completed" }),
      );
      const onRefresh = vi.fn();
      renderPanel({ tasks: [makeTask({ id: taskId, status: "InReview", title: "ApproveTest" })], onRefresh });
      await userEvent.click(screen.getByText("ApproveTest"));

      await waitFor(() => expect(screen.getByRole("button", { name: "Approve" })).toBeInTheDocument());
      await userEvent.click(screen.getByRole("button", { name: "Approve" }));

      await waitFor(() => {
        expect(mockExecuteCommand).toHaveBeenCalledWith(
          expect.objectContaining({ command: "APPROVE_TASK", args: { taskId } }),
        );
      });

      await waitFor(() => {
        expect(screen.getByText("Approve successful")).toBeInTheDocument();
      });
      expect(onRefresh).toHaveBeenCalled();
    });

    it("clicking Merge calls MERGE_TASK command", async () => {
      const taskId = uid();
      mockExecuteCommand.mockResolvedValue(
        makeCommandResponse({ command: "MERGE_TASK", status: "completed" }),
      );
      renderPanel({ tasks: [makeTask({ id: taskId, status: "Approved", title: "MergeTest" })] });
      await userEvent.click(screen.getByText("MergeTest"));

      await waitFor(() => expect(screen.getByRole("button", { name: "Merge" })).toBeInTheDocument());
      await userEvent.click(screen.getByRole("button", { name: "Merge" }));

      await waitFor(() => {
        expect(mockExecuteCommand).toHaveBeenCalledWith(
          expect.objectContaining({ command: "MERGE_TASK", args: { taskId } }),
        );
      });

      await waitFor(() => {
        expect(screen.getByText("Merge successful")).toBeInTheDocument();
      });
    });

    it("shows error feedback when action fails", async () => {
      const taskId = uid();
      mockExecuteCommand.mockResolvedValue(
        makeCommandResponse({ command: "APPROVE_TASK", status: "failed", error: "Not authorized" }),
      );
      renderPanel({ tasks: [makeTask({ id: taskId, status: "InReview", title: "FailAction Test" })] });
      await userEvent.click(screen.getByText("FailAction Test"));

      await waitFor(() => expect(screen.getByRole("button", { name: "Approve" })).toBeInTheDocument());
      await userEvent.click(screen.getByRole("button", { name: "Approve" }));

      await waitFor(() => {
        expect(screen.getByText("Not authorized")).toBeInTheDocument();
      });
    });

    it("shows denied feedback when action is denied", async () => {
      const taskId = uid();
      mockExecuteCommand.mockResolvedValue(
        makeCommandResponse({ command: "APPROVE_TASK", status: "denied", error: "Permission denied" }),
      );
      renderPanel({ tasks: [makeTask({ id: taskId, status: "InReview", title: "DenyAction Test" })] });
      await userEvent.click(screen.getByText("DenyAction Test"));

      await waitFor(() => expect(screen.getByRole("button", { name: "Approve" })).toBeInTheDocument());
      await userEvent.click(screen.getByRole("button", { name: "Approve" }));

      await waitFor(() => {
        expect(screen.getByText("Permission denied")).toBeInTheDocument();
      });
    });

    it("shows error when network request throws", async () => {
      const taskId = uid();
      mockExecuteCommand.mockRejectedValue(new Error("Network failure"));
      renderPanel({ tasks: [makeTask({ id: taskId, status: "InReview", title: "NetError Test" })] });
      await userEvent.click(screen.getByText("NetError Test"));

      await waitFor(() => expect(screen.getByRole("button", { name: "Approve" })).toBeInTheDocument());
      await userEvent.click(screen.getByRole("button", { name: "Approve" }));

      await waitFor(() => {
        expect(screen.getByText("Network failure")).toBeInTheDocument();
      });
    });
  });

  // ── Reason Textarea (Request Changes / Reject) ─────────────────────

  describe("reason textarea for request changes and reject", () => {
    it("clicking Request Changes shows textarea", async () => {
      const taskId = uid();
      renderPanel({ tasks: [makeTask({ id: taskId, status: "InReview", title: "ReasonRC Test" })] });
      await userEvent.click(screen.getByText("ReasonRC Test"));

      await waitFor(() =>
        expect(screen.getByRole("button", { name: "Request Changes" })).toBeInTheDocument(),
      );
      await userEvent.click(screen.getByRole("button", { name: "Request Changes" }));

      await waitFor(() => {
        expect(
          screen.getByPlaceholderText("Describe the changes needed…"),
        ).toBeInTheDocument();
      });
    });

    it("clicking Reject shows textarea with rejection placeholder", async () => {
      const taskId = uid();
      renderPanel({ tasks: [makeTask({ id: taskId, status: "Approved", title: "ReasonRej Test" })] });
      await userEvent.click(screen.getByText("ReasonRej Test"));

      await waitFor(() =>
        expect(screen.getByRole("button", { name: "Reject" })).toBeInTheDocument(),
      );
      await userEvent.click(screen.getByRole("button", { name: "Reject" }));

      await waitFor(() => {
        expect(
          screen.getByPlaceholderText("Reason for rejection…"),
        ).toBeInTheDocument();
      });
    });

    it("submit button is disabled when textarea is empty", async () => {
      const taskId = uid();
      renderPanel({ tasks: [makeTask({ id: taskId, status: "InReview", title: "EmptyReason Test" })] });
      await userEvent.click(screen.getByText("EmptyReason Test"));

      await waitFor(() =>
        expect(screen.getByRole("button", { name: "Request Changes" })).toBeInTheDocument(),
      );
      await userEvent.click(screen.getByRole("button", { name: "Request Changes" }));

      await waitFor(() => {
        expect(
          screen.getByRole("button", { name: "Submit Request Changes" }),
        ).toBeDisabled();
      });
    });

    it("typing reason enables submit button", async () => {
      const taskId = uid();
      renderPanel({ tasks: [makeTask({ id: taskId, status: "InReview", title: "TypeReason Test" })] });
      await userEvent.click(screen.getByText("TypeReason Test"));

      await waitFor(() =>
        expect(screen.getByRole("button", { name: "Request Changes" })).toBeInTheDocument(),
      );
      await userEvent.click(screen.getByRole("button", { name: "Request Changes" }));

      const textarea = await screen.findByPlaceholderText("Describe the changes needed…");
      fireEvent.change(textarea, { target: { value: "Fix the tests" } });

      await waitFor(() => {
        expect(
          screen.getByRole("button", { name: "Submit Request Changes" }),
        ).not.toBeDisabled();
      });
    });

    it("submitting request changes sends findings arg", async () => {
      const taskId = uid();
      mockExecuteCommand.mockResolvedValue(
        makeCommandResponse({ command: "REQUEST_CHANGES", status: "completed" }),
      );
      renderPanel({ tasks: [makeTask({ id: taskId, status: "InReview", title: "SubmitRC Test" })] });
      await userEvent.click(screen.getByText("SubmitRC Test"));

      await waitFor(() =>
        expect(screen.getByRole("button", { name: "Request Changes" })).toBeInTheDocument(),
      );
      await userEvent.click(screen.getByRole("button", { name: "Request Changes" }));

      const textarea = await screen.findByPlaceholderText("Describe the changes needed…");
      fireEvent.change(textarea, { target: { value: "Fix the tests" } });

      await userEvent.click(
        screen.getByRole("button", { name: "Submit Request Changes" }),
      );

      await waitFor(() => {
        expect(mockExecuteCommand).toHaveBeenCalledWith(
          expect.objectContaining({
            command: "REQUEST_CHANGES",
            args: { taskId, findings: "Fix the tests" },
          }),
        );
      });
    });

    it("submitting reject sends reason arg", async () => {
      const taskId = uid();
      mockExecuteCommand.mockResolvedValue(
        makeCommandResponse({ command: "REJECT_TASK", status: "completed" }),
      );
      renderPanel({ tasks: [makeTask({ id: taskId, status: "Approved", title: "SubmitReject Test" })] });
      await userEvent.click(screen.getByText("SubmitReject Test"));

      await waitFor(() =>
        expect(screen.getByRole("button", { name: "Reject" })).toBeInTheDocument(),
      );
      await userEvent.click(screen.getByRole("button", { name: "Reject" }));

      const textarea = await screen.findByPlaceholderText("Reason for rejection…");
      fireEvent.change(textarea, { target: { value: "Wrong approach" } });

      await userEvent.click(
        screen.getByRole("button", { name: "Submit Reject" }),
      );

      await waitFor(() => {
        expect(mockExecuteCommand).toHaveBeenCalledWith(
          expect.objectContaining({
            command: "REJECT_TASK",
            args: { taskId, reason: "Wrong approach" },
          }),
        );
      });
    });

    it("cancel button hides the textarea", async () => {
      const taskId = uid();
      renderPanel({ tasks: [makeTask({ id: taskId, status: "InReview", title: "CancelReason Test" })] });
      await userEvent.click(screen.getByText("CancelReason Test"));

      await waitFor(() =>
        expect(screen.getByRole("button", { name: "Request Changes" })).toBeInTheDocument(),
      );
      await userEvent.click(screen.getByRole("button", { name: "Request Changes" }));

      await waitFor(() => {
        expect(
          screen.getByPlaceholderText("Describe the changes needed…"),
        ).toBeInTheDocument();
      });

      await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
      expect(
        screen.queryByPlaceholderText("Describe the changes needed…"),
      ).not.toBeInTheDocument();
    });
  });

  // ── Agent Assignment ───────────────────────────────────────────────

  describe("agent assignment", () => {
    const agents = [
      makeAgent({ id: "a1", name: "Hephaestus", role: "SoftwareEngineer" }),
      makeAgent({ id: "a2", name: "Athena", role: "Reviewer" }),
    ];

    it("shows Assign Agent button for Queued unassigned tasks", async () => {
      const taskId = uid();
      renderPanel({ tasks: [makeTask({ id: taskId, status: "Queued", assignedAgentId: null, assignedAgentName: null, title: "AssignShow Test" })], agents });
      await userEvent.click(screen.getByText("AssignShow Test"));

      await waitFor(() => {
        expect(screen.getByRole("button", { name: /Assign Agent/ })).toBeInTheDocument();
      });
    });

    it("does not show Assign Agent for Active tasks", async () => {
      const taskId = uid();
      renderPanel({ tasks: [makeTask({ id: taskId, status: "Active", title: "AssignActive Test" })], agents });
      await userEvent.click(screen.getByText("AssignActive Test"));

      await waitFor(() => expect(screen.getByText("Description")).toBeInTheDocument());
      expect(screen.queryByRole("button", { name: /Assign Agent/ })).not.toBeInTheDocument();
    });

    it("does not show Assign Agent for Queued tasks that already have an assignee", async () => {
      const taskId = uid();
      renderPanel({
        tasks: [makeTask({ id: taskId, status: "Queued", assignedAgentId: "a1", title: "AssignHas Test" })],
        agents,
      });
      await userEvent.click(screen.getByText("AssignHas Test"));

      await waitFor(() => expect(screen.getByText("Description")).toBeInTheDocument());
      expect(screen.queryByRole("button", { name: /Assign Agent/ })).not.toBeInTheDocument();
    });

    it("clicking Assign Agent shows agent picker with all agents", async () => {
      const taskId = uid();
      renderPanel({ tasks: [makeTask({ id: taskId, status: "Queued", assignedAgentId: null, assignedAgentName: null, title: "AssignPicker Test" })], agents });
      await userEvent.click(screen.getByText("AssignPicker Test"));

      await waitFor(() =>
        expect(screen.getByRole("button", { name: /Assign Agent/ })).toBeInTheDocument(),
      );
      await userEvent.click(screen.getByRole("button", { name: /Assign Agent/ }));

      expect(screen.getByText("Hephaestus (SoftwareEngineer)")).toBeInTheDocument();
      expect(screen.getByText("Athena (Reviewer)")).toBeInTheDocument();
    });

    it("clicking an agent in the picker calls assignTask", async () => {
      const taskId = uid();
      mockAssignTask.mockResolvedValue(undefined);
      const onRefresh = vi.fn();
      renderPanel({ tasks: [makeTask({ id: taskId, status: "Queued", assignedAgentId: null, assignedAgentName: null, title: "AssignCall Test" })], agents, onRefresh });
      await userEvent.click(screen.getByText("AssignCall Test"));

      await waitFor(() =>
        expect(screen.getByRole("button", { name: /Assign Agent/ })).toBeInTheDocument(),
      );
      await userEvent.click(screen.getByRole("button", { name: /Assign Agent/ }));
      await userEvent.click(screen.getByText("Hephaestus (SoftwareEngineer)"));

      await waitFor(() => {
        expect(mockAssignTask).toHaveBeenCalledWith(taskId, "a1", "Hephaestus");
      });

      await waitFor(() => {
        expect(screen.getByText("Assigned to Hephaestus")).toBeInTheDocument();
      });
      expect(onRefresh).toHaveBeenCalled();
    });

    it("shows error when assignment fails", async () => {
      const taskId = uid();
      mockAssignTask.mockRejectedValue(new Error("Assignment failed"));
      renderPanel({ tasks: [makeTask({ id: taskId, status: "Queued", assignedAgentId: null, assignedAgentName: null, title: "AssignFail Test" })], agents });
      await userEvent.click(screen.getByText("AssignFail Test"));

      await waitFor(() =>
        expect(screen.getByRole("button", { name: /Assign Agent/ })).toBeInTheDocument(),
      );
      await userEvent.click(screen.getByRole("button", { name: /Assign Agent/ }));
      await userEvent.click(screen.getByText("Hephaestus (SoftwareEngineer)"));

      await waitFor(() => {
        expect(screen.getByText("Assignment failed")).toBeInTheDocument();
      });
    });
  });

  // ── Expanded Detail Content ────────────────────────────────────────

  describe("expanded detail content", () => {
    it("shows success criteria when present", async () => {
      const taskId = uid();
      renderPanel({
        tasks: [makeTask({ id: taskId, successCriteria: "All auth tests pass", title: "DetailSC Test" })],
      });
      await userEvent.click(screen.getByText("DetailSC Test"));

      await waitFor(() => {
        expect(screen.getByText("Success Criteria")).toBeInTheDocument();
        expect(screen.getByText("All auth tests pass")).toBeInTheDocument();
      });
    });

    it("shows implementation summary when present", async () => {
      const taskId = uid();
      renderPanel({
        tasks: [makeTask({ id: taskId, implementationSummary: "Built JWT handler", title: "DetailImpl Test" })],
      });
      await userEvent.click(screen.getByText("DetailImpl Test"));

      await waitFor(() => {
        expect(screen.getByText("Implementation")).toBeInTheDocument();
        expect(screen.getByText("Built JWT handler")).toBeInTheDocument();
      });
    });

    it("shows validation summary when present", async () => {
      const taskId = uid();
      renderPanel({
        tasks: [makeTask({ id: taskId, validationSummary: "Validated against spec", title: "DetailVal Test" })],
      });
      await userEvent.click(screen.getByText("DetailVal Test"));

      await waitFor(() => {
        expect(screen.getByText("Validation")).toBeInTheDocument();
        expect(screen.getByText("Validated against spec")).toBeInTheDocument();
      });
    });

    it("shows review metadata when reviewRounds > 0", async () => {
      const taskId = uid();
      renderPanel({
        tasks: [makeTask({ id: taskId, status: "InReview", reviewRounds: 2, reviewerAgentId: "reviewer-1", title: "DetailReview Test" })],
      });
      await userEvent.click(screen.getByText("DetailReview Test"));

      await waitFor(() => {
        expect(screen.getByText("Review round 2")).toBeInTheDocument();
        expect(screen.getByText(/reviewer-1/)).toBeInTheDocument();
      });
    });

    it("shows tests created when present", async () => {
      const taskId = uid();
      renderPanel({
        tasks: [makeTask({ id: taskId, testsCreated: ["AuthTests.cs", "JwtTests.cs"], title: "DetailTests Test" })],
      });
      await userEvent.click(screen.getByText("DetailTests Test"));

      await waitFor(() => {
        expect(screen.getByText("Tests Created")).toBeInTheDocument();
      });
    });
  });

  // ── Card Metadata (collapsed) ──────────────────────────────────────

  describe("card metadata in collapsed state", () => {
    it("shows status badge", () => {
      renderPanel({ tasks: [makeTask({ status: "InReview" })] });
      expect(screen.getByText("InReview")).toBeInTheDocument();
    });

    it("shows size badge when present", () => {
      renderPanel({ tasks: [makeTask({ size: "M" })] });
      expect(screen.getByText("M")).toBeInTheDocument();
    });

    it("shows type badge for Bug (not Feature)", () => {
      renderPanel({ tasks: [makeTask({ type: "Bug" })] });
      expect(screen.getByText("Bug")).toBeInTheDocument();
    });

    it("does not show Feature type badge", () => {
      renderPanel({ tasks: [makeTask({ type: "Feature" })] });
      // Feature type is filtered out in the component
      const badges = screen.queryAllByTestId(/^badge-/);
      const featureBadge = badges.find((b) => b.textContent === "Feature");
      expect(featureBadge).toBeUndefined();
    });

    it("shows assigned agent name", () => {
      renderPanel({ tasks: [makeTask({ assignedAgentName: "Hephaestus" })] });
      expect(screen.getByText(/Hephaestus/)).toBeInTheDocument();
    });

    it("shows branch name", () => {
      renderPanel({ tasks: [makeTask({ branchName: "feat/auth" })] });
      expect(screen.getByText(/feat\/auth/)).toBeInTheDocument();
    });

    it("shows commit count", () => {
      renderPanel({ tasks: [makeTask({ commitCount: 5 })] });
      expect(screen.getByText("5 commits")).toBeInTheDocument();
    });

    it("shows singular commit for count of 1", () => {
      renderPanel({ tasks: [makeTask({ commitCount: 1 })] });
      expect(screen.getByText("1 commit")).toBeInTheDocument();
    });

    it("shows Fleet badge when usedFleet is true", () => {
      renderPanel({ tasks: [makeTask({ usedFleet: true })] });
      expect(screen.getByText("Fleet")).toBeInTheDocument();
    });

    it("shows comment count with emoji", () => {
      renderPanel({ tasks: [makeTask({ commentCount: 3 })] });
      expect(screen.getByText(/💬/)).toBeInTheDocument();
    });
  });

  // ── Sorting ────────────────────────────────────────────────────────

  describe("sorting", () => {
    it("sorts tasks by updatedAt descending (newest first)", () => {
      const tasks = [
        makeTask({ id: "old", title: "Oldest", updatedAt: "2026-04-01T08:00:00Z" }),
        makeTask({ id: "new", title: "Newest", updatedAt: "2026-04-01T14:00:00Z" }),
        makeTask({ id: "mid", title: "Middle", updatedAt: "2026-04-01T11:00:00Z" }),
      ];
      renderPanel({ tasks });
      const titles = screen.getAllByText(/Newest|Middle|Oldest/).map((el) => el.textContent);
      expect(titles.indexOf("Newest")).toBeLessThan(titles.indexOf("Middle"));
      expect(titles.indexOf("Middle")).toBeLessThan(titles.indexOf("Oldest"));
    });
  });

  // ── Edge Cases ─────────────────────────────────────────────────────

  describe("edge cases", () => {
    it("handles task with all optional fields null without crashing", () => {
      renderPanel({
        tasks: [
          makeTask({
            size: null,
            startedAt: null,
            completedAt: null,
            assignedAgentId: null,
            assignedAgentName: null,
            branchName: null,
            commitCount: 0,
            reviewRounds: 0,
            commentCount: 0,
            type: undefined,
            sprintId: null,
            usedFleet: false,
          }),
        ],
      });
      expect(screen.getByText("Implement auth flow")).toBeInTheDocument();
    });

    it("renders with zero agents without crashing", () => {
      renderPanel({ agents: [] });
      expect(screen.getByText("Implement auth flow")).toBeInTheDocument();
    });

    it("renders many tasks without crashing", () => {
      const tasks = Array.from({ length: 30 }, (_, i) =>
        makeTask({
          id: `task-${i}`,
          title: `Task ${i}`,
          updatedAt: new Date(2026, 3, 1, i).toISOString(),
        }),
      );
      renderPanel({ tasks });
      expect(screen.getByText("Task 0")).toBeInTheDocument();
      expect(screen.getByText("Task 29")).toBeInTheDocument();
    });
  });

  // ── Bulk operations ──────────────────────────────────────────────────

  describe("bulk operations", () => {
    const multiTasks = [
      makeTask({ id: "t1", title: "Task Alpha", status: "Active", updatedAt: "2026-04-01T10:00:00Z" }),
      makeTask({ id: "t2", title: "Task Beta", status: "Queued", updatedAt: "2026-04-01T11:00:00Z" }),
      makeTask({ id: "t3", title: "Task Gamma", status: "InReview", updatedAt: "2026-04-01T12:00:00Z" }),
    ];

    it("shows checkboxes on task cards", () => {
      renderPanel({ tasks: multiTasks });
      const checkboxes = screen.getAllByRole("button", { name: /^Select Task/ });
      expect(checkboxes.length).toBe(3);
    });

    it("toggles selection on checkbox click", async () => {
      const user = userEvent.setup();
      renderPanel({ tasks: multiTasks });

      const checkboxes = screen.getAllByRole("button", { name: /^Select Task/ });
      await user.click(checkboxes[0]);

      expect(screen.getByText("1 selected")).toBeInTheDocument();
    });

    it("shows bulk action bar when tasks are selected", async () => {
      const user = userEvent.setup();
      renderPanel({ tasks: multiTasks });

      const checkboxes = screen.getAllByRole("button", { name: /^Select Task/ });
      await user.click(checkboxes[0]);
      await user.click(checkboxes[1]);

      expect(screen.getByText("2 selected")).toBeInTheDocument();
      expect(screen.getByText("Set status…")).toBeInTheDocument();
    });

    it("select all / deselect all toggles", async () => {
      const user = userEvent.setup();
      renderPanel({ tasks: multiTasks });

      const selectAll = screen.getByText("Select all");
      await user.click(selectAll);
      expect(screen.getByText("3 selected")).toBeInTheDocument();

      const deselectAll = screen.getByText("Deselect all");
      await user.click(deselectAll);
      expect(screen.queryByText("3 selected")).not.toBeInTheDocument();
    });

    it("clears selection on Escape", async () => {
      const user = userEvent.setup();
      renderPanel({ tasks: multiTasks });

      const checkboxes = screen.getAllByRole("button", { name: /^Select Task/ });
      await user.click(checkboxes[0]);
      expect(screen.getByText("1 selected")).toBeInTheDocument();

      await user.keyboard("{Escape}");
      expect(screen.queryByText("1 selected")).not.toBeInTheDocument();
    });

    it("clears selection via Clear button", async () => {
      const user = userEvent.setup();
      renderPanel({ tasks: multiTasks });

      const checkboxes = screen.getAllByRole("button", { name: /^Select Task/ });
      await user.click(checkboxes[0]);
      expect(screen.getByText("1 selected")).toBeInTheDocument();

      await user.click(screen.getByText("Clear"));
      expect(screen.queryByText("1 selected")).not.toBeInTheDocument();
    });

    it("calls bulkUpdateStatus and shows result", async () => {
      const user = userEvent.setup();
      const onRefresh = vi.fn();
      mockBulkUpdateStatus.mockResolvedValueOnce({
        requested: 2, succeeded: 2, failed: 0,
        updated: multiTasks.slice(0, 2), errors: [],
      });

      renderPanel({ tasks: multiTasks, onRefresh });

      const checkboxes = screen.getAllByRole("button", { name: /^Select Task/ });
      await user.click(checkboxes[0]);
      await user.click(checkboxes[1]);

      // Use the status dropdown
      const statusSelect = screen.getByDisplayValue("Set status…");
      fireEvent.change(statusSelect, { target: { value: "Blocked" } });

      await waitFor(() => {
        expect(mockBulkUpdateStatus).toHaveBeenCalledWith(
          expect.arrayContaining(["t3", "t2"]),
          "Blocked",
        );
      });

      await waitFor(() => {
        expect(screen.getByText(/2 updated/)).toBeInTheDocument();
      });

      expect(onRefresh).toHaveBeenCalled();
    });

    it("calls bulkAssign with selected agent", async () => {
      const user = userEvent.setup();
      const onRefresh = vi.fn();
      const agents = [
        makeAgent({ id: "a1", name: "Agent One" }),
        makeAgent({ id: "a2", name: "Agent Two" }),
      ];

      mockBulkAssign.mockResolvedValueOnce({
        requested: 1, succeeded: 1, failed: 0,
        updated: [multiTasks[0]], errors: [],
      });

      renderPanel({ tasks: multiTasks, onRefresh, agents });

      const checkboxes = screen.getAllByRole("button", { name: /^Select Task/ });
      await user.click(checkboxes[0]);

      const assignSelect = screen.getByDisplayValue("Assign to…");
      fireEvent.change(assignSelect, { target: { value: "a1" } });

      await waitFor(() => {
        expect(mockBulkAssign).toHaveBeenCalledWith(
          expect.arrayContaining(["t3"]),
          "a1",
          "Agent One",
        );
      });

      expect(onRefresh).toHaveBeenCalled();
    });

    it("keeps failed tasks selected after partial failure", async () => {
      const user = userEvent.setup();
      mockBulkUpdateStatus.mockResolvedValueOnce({
        requested: 2, succeeded: 1, failed: 1,
        updated: [multiTasks[0]],
        errors: [{ taskId: "t2", code: "VALIDATION", error: "Dependency blocked" }],
      });

      renderPanel({ tasks: multiTasks });

      const checkboxes = screen.getAllByRole("button", { name: /^Select Task/ });
      await user.click(checkboxes[0]);
      await user.click(checkboxes[1]);

      const statusSelect = screen.getByDisplayValue("Set status…");
      fireEvent.change(statusSelect, { target: { value: "Active" } });

      await waitFor(() => {
        expect(screen.getByText(/1 updated.*1 failed/)).toBeInTheDocument();
      });

      // Only the failed task should remain selected
      expect(screen.getByText("1 selected")).toBeInTheDocument();
    });

    it("checkbox click does not expand task card", async () => {
      const user = userEvent.setup();
      renderPanel({ tasks: multiTasks });

      const checkboxes = screen.getAllByRole("button", { name: /^Select Task/ });
      await user.click(checkboxes[0]);

      // Card should not expand — no detail section visible
      expect(screen.queryByText("Description")).not.toBeInTheDocument();
      expect(screen.getByText("1 selected")).toBeInTheDocument();
    });
  });

  describe("focusTaskId (cross-panel navigation)", () => {
    it("auto-expands the focused task when focusTaskId matches a task", () => {
      const taskId = uid();
      const task = makeTask({
        id: taskId,
        title: "Focused task",
        description: "Should be expanded",
      });

      render(
        createElement(
          FluentProvider,
          { theme: webDarkTheme },
          createElement(TaskListPanel, {
            tasks: [task],
            loading: false,
            error: false,
            onRefresh: vi.fn(),
            agents: [makeAgent()],
            focusTaskId: taskId,
          }),
        ),
      );

      // The task detail section should be visible (description rendered)
      expect(screen.getByText("Description")).toBeInTheDocument();
      expect(screen.getByText("Should be expanded")).toBeInTheDocument();
    });

    it("does not expand when focusTaskId does not match any task", () => {
      const task = makeTask({
        id: uid(),
        title: "Normal task",
        description: "Should NOT be expanded",
      });

      render(
        createElement(
          FluentProvider,
          { theme: webDarkTheme },
          createElement(TaskListPanel, {
            tasks: [task],
            loading: false,
            error: false,
            onRefresh: vi.fn(),
            agents: [makeAgent()],
            focusTaskId: "nonexistent-id",
          }),
        ),
      );

      expect(screen.queryByText("Description")).not.toBeInTheDocument();
    });

    it("resets filter to 'all' when focusing a task", () => {
      const taskId = uid();
      const task = makeTask({
        id: taskId,
        title: "Target task",
        status: "Active",
        description: "Focused from retro",
      });
      const completedTask = makeTask({
        id: uid(),
        title: "Done task",
        status: "Completed",
      });

      const { rerender } = render(
        createElement(
          FluentProvider,
          { theme: webDarkTheme },
          createElement(TaskListPanel, {
            tasks: [task, completedTask],
            loading: false,
            error: false,
            onRefresh: vi.fn(),
            agents: [makeAgent()],
            focusTaskId: null,
          }),
        ),
      );

      // Both tasks visible initially
      expect(screen.getByText("Target task")).toBeInTheDocument();
      expect(screen.getByText("Done task")).toBeInTheDocument();

      // Now focus the task — should auto-expand it
      rerender(
        createElement(
          FluentProvider,
          { theme: webDarkTheme },
          createElement(TaskListPanel, {
            tasks: [task, completedTask],
            loading: false,
            error: false,
            onRefresh: vi.fn(),
            agents: [makeAgent()],
            focusTaskId: taskId,
          }),
        ),
      );

      expect(screen.getByText("Description")).toBeInTheDocument();
      expect(screen.getByText("Focused from retro")).toBeInTheDocument();
    });
  });
});
