import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi, beforeEach } from "vitest";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import { createElement } from "react";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../api", () => ({
  executeCommand: vi.fn(),
  getTaskComments: vi.fn(),
  getTaskSpecLinks: vi.fn(),
  assignTask: vi.fn(),
}));

import TaskListPanel from "../TaskListPanel";
import type {
  TaskSnapshot,
  TaskStatus,
  AgentDefinition,
  TaskComment,
} from "../api";
import { executeCommand, getTaskComments, getTaskSpecLinks } from "../api";

const mockGetTaskComments = vi.mocked(getTaskComments);
const mockGetTaskSpecLinks = vi.mocked(getTaskSpecLinks);
const mockExecuteCommand = vi.mocked(executeCommand);

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

  return renderToStaticMarkup(
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
}

// ── Tests ──────────────────────────────────────────────────────────────

describe("TaskListPanel component rendering", () => {
  function mockCommandResponse(overrides: Partial<import("../api").CommandExecutionResponse> = {}): import("../api").CommandExecutionResponse {
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

  beforeEach(() => {
    vi.clearAllMocks();
    mockGetTaskComments.mockResolvedValue([]);
    mockGetTaskSpecLinks.mockResolvedValue([]);
    mockExecuteCommand.mockResolvedValue(mockCommandResponse());
  });

  // ── Empty / Loading / Error States ──

  describe("empty, loading, and error states", () => {
    it("renders skeleton loader when loading", () => {
      const html = renderPanel({ loading: true, tasks: [] });
      // SkeletonLoader renders placeholder rows
      expect(html).toBeTruthy();
      // Should not contain actual task titles
      expect(html).not.toContain("Implement auth flow");
    });

    it("renders error state with retry", () => {
      const html = renderPanel({ error: true });
      expect(html).toContain("Failed to load tasks");
      expect(html).toContain("Check your connection");
    });

    it("renders empty state when no tasks", () => {
      const html = renderPanel({ tasks: [] });
      expect(html).toContain("No tasks assigned");
      expect(html).toContain("Tasks will appear here");
    });
  });

  // ── Filter Bar ──

  describe("filter bar", () => {
    it("renders all filter chips", () => {
      const html = renderPanel();
      expect(html).toContain("All");
      expect(html).toContain("Review Queue");
      expect(html).toContain("Active");
      expect(html).toContain("Completed");
    });

    it("shows correct counts in filter chips", () => {
      const tasks = [
        makeTask({ id: "1", status: "Active" }),
        makeTask({ id: "2", status: "InReview" }),
        makeTask({ id: "3", status: "Completed" }),
        makeTask({ id: "4", status: "Queued" }),
      ];
      const html = renderPanel({ tasks });
      // There should be counts displayed — the All filter shows 4
      expect(html).toContain(">4<");
    });

    it("renders sprint filter when activeSprintId is provided", () => {
      const html = renderPanel({ activeSprintId: "sprint-1" });
      expect(html).toContain("Sprint");
    });

    it("does not render sprint filter when no activeSprintId", () => {
      const html = renderPanel({ activeSprintId: undefined });
      expect(html).not.toContain("🏃");
    });
  });

  // ── Task Card Rendering ──

  describe("task card rendering", () => {
    it("renders task title", () => {
      const html = renderPanel();
      expect(html).toContain("Implement auth flow");
    });

    it("renders status badge", () => {
      const html = renderPanel({
        tasks: [makeTask({ status: "InReview" })],
      });
      expect(html).toContain("InReview");
    });

    it("renders size badge when present", () => {
      const html = renderPanel({
        tasks: [makeTask({ size: "M" })],
      });
      expect(html).toContain(">M<");
    });

    it("renders type badge for Bug tasks", () => {
      const html = renderPanel({
        tasks: [makeTask({ type: "Bug" })],
      });
      expect(html).toContain("Bug");
    });

    it("does not render type badge for Feature tasks (default)", () => {
      const html = renderPanel({
        tasks: [makeTask({ type: "Feature" })],
      });
      // Feature type is explicitly filtered out in the component
      // The badge shouldn't be rendered as a separate type badge
      const titleOccurrences = html.split("Feature").length - 1;
      // Feature should not appear as a type badge (only as a status if applicable)
      expect(titleOccurrences).toBe(0);
    });

    it("renders assigned agent name", () => {
      const html = renderPanel({
        tasks: [makeTask({ assignedAgentName: "Hephaestus" })],
      });
      expect(html).toContain("Hephaestus");
    });

    it("renders branch name", () => {
      const html = renderPanel({
        tasks: [makeTask({ branchName: "feat/auth-flow" })],
      });
      expect(html).toContain("feat/auth-flow");
    });

    it("renders commit count", () => {
      const html = renderPanel({
        tasks: [makeTask({ commitCount: 5 })],
      });
      expect(html).toContain("5 commits");
    });

    it("renders singular commit for count of 1", () => {
      const html = renderPanel({
        tasks: [makeTask({ commitCount: 1 })],
      });
      expect(html).toContain("1 commit");
      expect(html).not.toContain("1 commits");
    });

    it("renders review round", () => {
      const html = renderPanel({
        tasks: [makeTask({ reviewRounds: 2 })],
      });
      expect(html).toContain("Round 2");
    });

    it("renders comment count", () => {
      const html = renderPanel({
        tasks: [makeTask({ commentCount: 3 })],
      });
      expect(html).toContain("💬");
    });

    it("renders Fleet badge when usedFleet is true", () => {
      const html = renderPanel({
        tasks: [makeTask({ usedFleet: true })],
      });
      expect(html).toContain("Fleet");
    });

    it("does not render Fleet badge when usedFleet is false", () => {
      const html = renderPanel({
        tasks: [makeTask({ usedFleet: false })],
      });
      // Fleet badge should not appear
      expect(html).not.toContain(">Fleet<");
    });
  });

  // ── Multiple Tasks + Ordering ──

  describe("multiple tasks and ordering", () => {
    it("renders all tasks", () => {
      const tasks = [
        makeTask({ id: "1", title: "Task A", updatedAt: "2026-04-01T10:00:00Z" }),
        makeTask({ id: "2", title: "Task B", updatedAt: "2026-04-01T11:00:00Z" }),
        makeTask({ id: "3", title: "Task C", updatedAt: "2026-04-01T12:00:00Z" }),
      ];
      const html = renderPanel({ tasks });
      expect(html).toContain("Task A");
      expect(html).toContain("Task B");
      expect(html).toContain("Task C");
    });

    it("sorts tasks by updatedAt descending (newest first)", () => {
      const tasks = [
        makeTask({ id: "1", title: "Oldest", updatedAt: "2026-04-01T08:00:00Z" }),
        makeTask({ id: "2", title: "Newest", updatedAt: "2026-04-01T14:00:00Z" }),
        makeTask({ id: "3", title: "Middle", updatedAt: "2026-04-01T11:00:00Z" }),
      ];
      const html = renderPanel({ tasks });
      const newestIdx = html.indexOf("Newest");
      const middleIdx = html.indexOf("Middle");
      const oldestIdx = html.indexOf("Oldest");
      expect(newestIdx).toBeLessThan(middleIdx);
      expect(middleIdx).toBeLessThan(oldestIdx);
    });
  });

  // ── Collapsed card does NOT render detail sections ──
  // NOTE: TaskDetail (spec links, evidence, gates, assignment picker) only renders
  // when a card is expanded via click. renderToStaticMarkup cannot simulate clicks,
  // so we verify that collapsed cards correctly omit detail content, and that the
  // component renders without errors for all task states. The existing
  // taskListPanelEnhanced.test.ts covers the detail logic (badge helpers, cache,
  // eligibility rules) as unit tests.

  describe("collapsed card omits detail content", () => {
    it("does not render spec link section labels in collapsed state", () => {
      const html = renderPanel({ tasks: [makeTask()] });
      expect(html).not.toContain("Spec Links");
      expect(html).not.toContain("Evidence Ledger");
      expect(html).not.toContain("Gate Status");
      expect(html).not.toContain("Description");
    });

    it("does not render action buttons in collapsed state", () => {
      const html = renderPanel({
        tasks: [makeTask({ status: "InReview" })],
      });
      // Action buttons (Approve, Request Changes) only appear in expanded detail
      expect(html).not.toContain("Approve");
      expect(html).not.toContain("Request Changes");
    });

    it("does not render assign picker in collapsed state for Queued task", () => {
      const html = renderPanel({
        tasks: [makeTask({ status: "Queued", assignedAgentId: null })],
        agents: [makeAgent()],
      });
      expect(html).not.toContain("Assign Agent");
    });

    it("does not render comments section in collapsed state", () => {
      mockGetTaskComments.mockResolvedValue([
        makeComment({ content: "Should not appear" }),
      ]);
      const html = renderPanel({ tasks: [makeTask({ commentCount: 5 })] });
      expect(html).not.toContain("Should not appear");
      // But comment count IS shown in the card meta
      expect(html).toContain("💬");
    });

    it("does not call getTaskSpecLinks or getTaskComments for collapsed tasks", () => {
      renderPanel({ tasks: [makeTask()] });
      // SSR renders only the initial collapsed state — no useEffect fires
      expect(mockGetTaskSpecLinks).not.toHaveBeenCalled();
      expect(mockGetTaskComments).not.toHaveBeenCalled();
    });

    it("does not call executeCommand for collapsed tasks", () => {
      renderPanel({ tasks: [makeTask()] });
      expect(mockExecuteCommand).not.toHaveBeenCalled();
    });
  });

  // ── Status-specific card rendering ──

  describe("status-specific card content", () => {
    it("InReview task shows status badge with correct text", () => {
      const html = renderPanel({
        tasks: [makeTask({ status: "InReview", title: "Review me" })],
      });
      expect(html).toContain("InReview");
      expect(html).toContain("Review me");
    });

    it("Approved task shows status badge", () => {
      const html = renderPanel({
        tasks: [makeTask({ status: "Approved" })],
      });
      expect(html).toContain("Approved");
    });

    it("Blocked task shows status badge", () => {
      const html = renderPanel({
        tasks: [makeTask({ status: "Blocked" })],
      });
      expect(html).toContain("Blocked");
    });

    it("Queued task without assignee does not show agent name in meta", () => {
      const html = renderPanel({
        tasks: [makeTask({ status: "Queued", assignedAgentName: null })],
      });
      expect(html).toContain("Queued");
      expect(html).not.toContain("👤");
    });

    it("task with assignee shows agent name in collapsed meta", () => {
      const html = renderPanel({
        tasks: [makeTask({ assignedAgentName: "Hephaestus" })],
      });
      expect(html).toContain("👤");
      expect(html).toContain("Hephaestus");
    });
  });

  // ── Status badges render for all statuses ──

  describe("status badges render correctly", () => {
    const statuses: TaskStatus[] = [
      "Active",
      "InReview",
      "AwaitingValidation",
      "Approved",
      "ChangesRequested",
      "Blocked",
      "Completed",
      "Cancelled",
      "Queued",
      "Merging",
    ];

    it.each(statuses)("renders %s status badge text in the card", (status) => {
      const html = renderPanel({
        tasks: [makeTask({ status, title: `Task-${status}` })],
      });
      // Both the status badge and the title must appear
      expect(html).toContain(status);
      expect(html).toContain(`Task-${status}`);
    });

    it("renders different badge for each status (no two produce identical HTML)", () => {
      const outputs = statuses.map((status) =>
        renderPanel({ tasks: [makeTask({ id: "same", title: "Same", status })] }),
      );
      // Each status should produce unique output (badge color varies)
      const unique = new Set(outputs);
      expect(unique.size).toBe(statuses.length);
    });
  });

  // ── Multiple agents don't crash rendering ──

  describe("agent props handling", () => {
    it("renders with zero agents without crashing", () => {
      const html = renderPanel({ agents: [] });
      expect(html).toContain("Implement auth flow");
    });

    it("renders with many agents without crashing", () => {
      const agents = Array.from({ length: 10 }, (_, i) =>
        makeAgent({ id: `a${i}`, name: `Agent${i}`, role: "SoftwareEngineer" }),
      );
      const html = renderPanel({ agents });
      expect(html).toContain("Implement auth flow");
    });
  });

  // ── Task Metadata Fields (collapsed card meta row) ──

  describe("task card metadata row", () => {
    it("shows implementation summary is not visible in collapsed state", () => {
      const html = renderPanel({
        tasks: [makeTask({ implementationSummary: "Implemented JWT auth with bcrypt" })],
      });
      // Implementation summary is in the expanded detail, not the card
      expect(html).not.toContain("Implemented JWT auth with bcrypt");
    });

    it("shows branch name in collapsed card meta", () => {
      const html = renderPanel({
        tasks: [makeTask({ branchName: "feat/jwt-auth" })],
      });
      expect(html).toContain("feat/jwt-auth");
    });

    it("shows commit count in collapsed card meta", () => {
      const html = renderPanel({
        tasks: [makeTask({ commitCount: 7 })],
      });
      expect(html).toContain("7 commits");
    });

    it("shows review round in collapsed card meta when > 0", () => {
      const html = renderPanel({
        tasks: [makeTask({ reviewRounds: 3 })],
      });
      expect(html).toContain("Round 3");
    });

    it("does not show review round when 0", () => {
      const html = renderPanel({
        tasks: [makeTask({ reviewRounds: 0 })],
      });
      expect(html).not.toContain("Round");
    });

    it("does not show commit count when 0", () => {
      const html = renderPanel({
        tasks: [makeTask({ commitCount: 0 })],
      });
      expect(html).not.toContain("commit");
    });

    it("does not show comment emoji when count is 0", () => {
      const html = renderPanel({
        tasks: [makeTask({ commentCount: 0 })],
      });
      expect(html).not.toContain("💬");
    });
  });

  // ── Edge Cases ──

  describe("edge cases", () => {
    it("handles task with all optional fields null", () => {
      const html = renderPanel({
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
      expect(html).toContain("Implement auth flow");
    });

    it("handles empty agents array", () => {
      const html = renderPanel({ agents: [] });
      expect(html).toBeTruthy();
    });

    it("handles many tasks without crashing", () => {
      const tasks = Array.from({ length: 50 }, (_, i) =>
        makeTask({
          id: `task-${i}`,
          title: `Task ${i}`,
          updatedAt: new Date(2026, 3, 1, i).toISOString(),
        }),
      );
      const html = renderPanel({ tasks });
      expect(html).toContain("Task 0");
      expect(html).toContain("Task 49");
    });

    it("renders with no onRefresh callback", () => {
      const html = renderPanel({ onRefresh: undefined });
      expect(html).toBeTruthy();
    });
  });
});
