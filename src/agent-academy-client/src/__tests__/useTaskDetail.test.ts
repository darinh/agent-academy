// @vitest-environment jsdom
import { describe, expect, it, vi, beforeEach } from "vitest";
import { renderHook, act, waitFor } from "@testing-library/react";
import type {
  TaskSnapshot,
  TaskComment,
  SpecTaskLink,
  CommandExecutionResponse,
} from "../api";

vi.mock("../api", () => ({
  executeCommand: vi.fn(),
  getTaskComments: vi.fn(),
  getTaskSpecLinks: vi.fn(),
  getTaskDependencies: vi.fn(),
  assignTask: vi.fn(),
}));

import { executeCommand, getTaskComments, getTaskSpecLinks, getTaskDependencies, assignTask } from "../api";
import { useTaskDetail } from "../taskList/useTaskDetail";

const mockExecuteCommand = vi.mocked(executeCommand);
const mockGetTaskComments = vi.mocked(getTaskComments);
const mockGetTaskSpecLinks = vi.mocked(getTaskSpecLinks);
const mockGetTaskDependencies = vi.mocked(getTaskDependencies);
const mockAssignTask = vi.mocked(assignTask);

// ── Factories ──

function makeTask(overrides: Partial<TaskSnapshot> = {}): TaskSnapshot {
  return {
    id: "task-1",
    title: "Test task",
    description: "A test task",
    successCriteria: "",
    status: "Active",
    currentPhase: "Implementation",
    currentPlan: "",
    validationStatus: "",
    validationSummary: "",
    implementationStatus: "",
    implementationSummary: "",
    preferredRoles: [],
    createdAt: "2026-04-01T00:00:00Z",
    updatedAt: "2026-04-01T00:00:00Z",
    ...overrides,
  };
}

function makeComment(overrides: Partial<TaskComment> = {}): TaskComment {
  return {
    id: "comment-1",
    taskId: "task-1",
    agentId: "agent-1",
    agentName: "Aristotle",
    commentType: "Comment",
    content: "Test comment",
    createdAt: "2026-04-01T00:00:00Z",
    ...overrides,
  };
}

function makeSpecLink(overrides: Partial<SpecTaskLink> = {}): SpecTaskLink {
  return {
    id: "link-1",
    taskId: "task-1",
    specSectionId: "003",
    linkType: "Implements",
    linkedByAgentId: "agent-1",
    linkedByAgentName: "Aristotle",
    createdAt: "2026-04-01T00:00:00Z",
    ...overrides,
  };
}

function makeCommandResponse(overrides: Partial<CommandExecutionResponse> = {}): CommandExecutionResponse {
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

const noop = () => {};
let taskSeq = 0;
function nextTaskId() { return `task-${++taskSeq}`; }

beforeEach(() => {
  vi.clearAllMocks();
  mockGetTaskComments.mockResolvedValue([]);
  mockGetTaskSpecLinks.mockResolvedValue([]);
  mockGetTaskDependencies.mockResolvedValue({ taskId: "task-1", dependsOn: [], dependedOnBy: [] });
});

describe("useTaskDetail", () => {
  describe("initial data fetching", () => {
    it("fetches comments on mount", async () => {
      const id = nextTaskId();
      const comments = [makeComment({ taskId: id })];
      mockGetTaskComments.mockResolvedValue(comments);

      const { result } = renderHook(() => useTaskDetail(makeTask({ id }), noop));

      await waitFor(() => {
        expect(result.current.commentsLoading).toBe(false);
      });
      expect(mockGetTaskComments).toHaveBeenCalledWith(id);
      expect(result.current.comments).toEqual(comments);
      expect(result.current.commentsError).toBe(false);
    });

    it("fetches spec links on mount", async () => {
      const id = nextTaskId();
      const links = [makeSpecLink({ taskId: id })];
      mockGetTaskSpecLinks.mockResolvedValue(links);

      const { result } = renderHook(() => useTaskDetail(makeTask({ id }), noop));

      await waitFor(() => {
        expect(result.current.specLinksLoading).toBe(false);
      });
      expect(mockGetTaskSpecLinks).toHaveBeenCalledWith(id);
      expect(result.current.specLinks).toEqual(links);
    });

    it("sets commentsError on fetch failure", async () => {
      const id = nextTaskId();
      mockGetTaskComments.mockRejectedValue(new Error("Network error"));

      const { result } = renderHook(() => useTaskDetail(makeTask({ id }), noop));

      await waitFor(() => {
        expect(result.current.commentsLoading).toBe(false);
      });
      expect(result.current.commentsError).toBe(true);
    });

    it("sets specLinks to empty on fetch failure", async () => {
      const id = nextTaskId();
      mockGetTaskSpecLinks.mockRejectedValue(new Error("Network error"));

      const { result } = renderHook(() => useTaskDetail(makeTask({ id }), noop));

      await waitFor(() => {
        expect(result.current.specLinksLoading).toBe(false);
      });
      expect(result.current.specLinks).toEqual([]);
    });
  });

  describe("evidence fetching", () => {
    it("fetches evidence via executeCommand", async () => {
      const id = nextTaskId();
      const evidenceRows = [
        { id: "ev-1", phase: "Baseline", checkName: "build", tool: "dotnet", passed: true, agentName: "Socrates", createdAt: "2026-04-01T00:00:00Z" },
      ];
      mockExecuteCommand.mockResolvedValue(
        makeCommandResponse({ result: { evidence: evidenceRows } }),
      );

      const { result } = renderHook(() => useTaskDetail(makeTask({ id }), noop));

      act(() => { result.current.fetchEvidence(); });

      await waitFor(() => {
        expect(result.current.evidenceLoading).toBe(false);
      });
      expect(mockExecuteCommand).toHaveBeenCalledWith({
        command: "QUERY_EVIDENCE",
        args: { taskId: id },
      });
      expect(result.current.evidence).toEqual(evidenceRows);
      expect(result.current.evidenceLoaded).toBe(true);
    });

    it("marks evidenceLoaded true even on fetch error", async () => {
      const id = nextTaskId();
      mockExecuteCommand.mockRejectedValue(new Error("fail"));

      const { result } = renderHook(() => useTaskDetail(makeTask({ id }), noop));

      act(() => { result.current.fetchEvidence(); });

      await waitFor(() => {
        expect(result.current.evidenceLoaded).toBe(true);
      });
      expect(result.current.evidenceLoading).toBe(false);
    });
  });

  describe("gate checking", () => {
    it("fetches gate results via CHECK_GATES command", async () => {
      const id = nextTaskId();
      const gateResult = {
        taskId: id,
        currentPhase: "Implementation",
        targetPhase: "Validation",
        met: true,
        requiredChecks: 3,
        passedChecks: 3,
        missingChecks: [],
        evidence: [],
        message: "All gates met",
      };
      mockExecuteCommand.mockResolvedValue(
        makeCommandResponse({ result: gateResult }),
      );

      const { result } = renderHook(() => useTaskDetail(makeTask({ id }), noop));

      act(() => { result.current.checkGates(); });

      await waitFor(() => {
        expect(result.current.gateLoading).toBe(false);
      });
      expect(mockExecuteCommand).toHaveBeenCalledWith({
        command: "CHECK_GATES",
        args: { taskId: id },
      });
      expect(result.current.gate).toEqual(gateResult);
    });

    it("silently handles gate check failure", async () => {
      const id = nextTaskId();
      mockExecuteCommand.mockRejectedValue(new Error("fail"));

      const { result } = renderHook(() => useTaskDetail(makeTask({ id }), noop));

      act(() => { result.current.checkGates(); });

      await waitFor(() => {
        expect(result.current.gateLoading).toBe(false);
      });
      expect(result.current.gate).toBeNull();
    });
  });

  describe("canCheckGates", () => {
    it.each([
      ["Active", true],
      ["AwaitingValidation", true],
      ["InReview", true],
      ["Queued", false],
      ["Completed", false],
      ["Cancelled", false],
      ["Blocked", false],
    ] as const)("status %s → canCheckGates = %s", (status, expected) => {
      const { result } = renderHook(() =>
        useTaskDetail(makeTask({ id: nextTaskId(), status }), noop),
      );
      expect(result.current.canCheckGates).toBe(expected);
    });
  });

  describe("canAssign", () => {
    it("true when status is Queued and no agent assigned", () => {
      const { result } = renderHook(() =>
        useTaskDetail(makeTask({ id: nextTaskId(), status: "Queued", assignedAgentId: undefined }), noop),
      );
      expect(result.current.canAssign).toBe(true);
    });

    it("false when status is Queued but agent is assigned", () => {
      const { result } = renderHook(() =>
        useTaskDetail(makeTask({ id: nextTaskId(), status: "Queued", assignedAgentId: "agent-1" }), noop),
      );
      expect(result.current.canAssign).toBe(false);
    });

    it("false when status is not Queued", () => {
      const { result } = renderHook(() =>
        useTaskDetail(makeTask({ id: nextTaskId(), status: "Active" }), noop),
      );
      expect(result.current.canAssign).toBe(false);
    });
  });

  describe("actions", () => {
    it("returns approve and requestChanges for InReview", () => {
      const { result } = renderHook(() =>
        useTaskDetail(makeTask({ id: nextTaskId(), status: "InReview" }), noop),
      );
      expect(result.current.actions).toEqual(["approve", "requestChanges"]);
    });

    it("returns merge and reject for Approved", () => {
      const { result } = renderHook(() =>
        useTaskDetail(makeTask({ id: nextTaskId(), status: "Approved" }), noop),
      );
      expect(result.current.actions).toEqual(["merge", "reject"]);
    });

    it("returns empty for Active", () => {
      const { result } = renderHook(() =>
        useTaskDetail(makeTask({ id: nextTaskId(), status: "Active" }), noop),
      );
      expect(result.current.actions).toEqual([]);
    });
  });

  describe("handleAction", () => {
    it("executes approve action and calls onRefresh", async () => {
      const id = nextTaskId();
      mockExecuteCommand.mockResolvedValue(makeCommandResponse());
      const onRefresh = vi.fn();

      const { result } = renderHook(() =>
        useTaskDetail(makeTask({ id, status: "InReview" }), onRefresh),
      );

      // Wait for mount effects to settle
      await waitFor(() => { expect(result.current.commentsLoading).toBe(false); });

      await act(async () => { result.current.handleAction("approve"); });

      expect(mockExecuteCommand).toHaveBeenCalledWith({
        command: "APPROVE_TASK",
        args: { taskId: id },
      });
      expect(result.current.actionResult).toEqual({ ok: true, message: "Approve successful" });
      expect(onRefresh).toHaveBeenCalled();
    });

    it("prompts for reason on requestChanges first call", async () => {
      const { result } = renderHook(() =>
        useTaskDetail(makeTask({ id: nextTaskId(), status: "InReview" }), noop),
      );

      act(() => { result.current.handleAction("requestChanges"); });

      expect(result.current.reasonAction).toBe("requestChanges");
      expect(mockExecuteCommand).not.toHaveBeenCalled();
    });

    it("submits requestChanges with findings when reason provided", async () => {
      const id = nextTaskId();
      mockExecuteCommand.mockResolvedValue(makeCommandResponse());

      const { result } = renderHook(() =>
        useTaskDetail(makeTask({ id, status: "InReview" }), noop),
      );

      await waitFor(() => { expect(result.current.commentsLoading).toBe(false); });

      // First call opens reason prompt
      act(() => { result.current.handleAction("requestChanges"); });
      expect(result.current.reasonAction).toBe("requestChanges");

      // Set reason text
      act(() => { result.current.setReasonText("Needs more tests"); });

      // Second call submits
      await act(async () => { result.current.handleAction("requestChanges"); });

      expect(mockExecuteCommand).toHaveBeenCalledWith({
        command: "REQUEST_CHANGES",
        args: { taskId: id, findings: "Needs more tests" },
      });
    });

    it("submits reject with reason arg", async () => {
      const id = nextTaskId();
      mockExecuteCommand.mockResolvedValue(makeCommandResponse());

      const { result } = renderHook(() =>
        useTaskDetail(makeTask({ id, status: "Approved" }), noop),
      );

      await waitFor(() => { expect(result.current.commentsLoading).toBe(false); });

      act(() => { result.current.handleAction("reject"); });
      act(() => { result.current.setReasonText("Not aligned with spec"); });
      await act(async () => { result.current.handleAction("reject"); });

      expect(mockExecuteCommand).toHaveBeenCalledWith({
        command: "REJECT_TASK",
        args: { taskId: id, reason: "Not aligned with spec" },
      });
    });

    it("sets error result on denied status", async () => {
      const id = nextTaskId();
      mockExecuteCommand.mockResolvedValue(
        makeCommandResponse({ status: "denied", error: "Insufficient role" }),
      );

      const { result } = renderHook(() =>
        useTaskDetail(makeTask({ id, status: "InReview" }), noop),
      );

      await waitFor(() => { expect(result.current.commentsLoading).toBe(false); });

      // Use act + waitFor pattern for the async action
      act(() => { result.current.handleAction("approve"); });

      await waitFor(() => {
        expect(result.current.actionPending).toBeNull();
      });

      expect(result.current.actionResult).toEqual({
        ok: false,
        message: "Insufficient role",
      });
    });

    it("sets error result on exception", async () => {
      const id = nextTaskId();
      mockExecuteCommand.mockRejectedValue(new Error("Network down"));

      const { result } = renderHook(() =>
        useTaskDetail(makeTask({ id, status: "InReview" }), noop),
      );

      await waitFor(() => { expect(result.current.commentsLoading).toBe(false); });

      act(() => { result.current.handleAction("approve"); });

      await waitFor(() => {
        expect(result.current.actionPending).toBeNull();
      });

      expect(result.current.actionResult).toEqual({
        ok: false,
        message: "Network down",
      });
    });
  });

  describe("cancelReason", () => {
    it("clears reason state", () => {
      const { result } = renderHook(() =>
        useTaskDetail(makeTask({ id: nextTaskId(), status: "InReview" }), noop),
      );

      act(() => { result.current.handleAction("requestChanges"); });
      act(() => { result.current.setReasonText("some text"); });
      expect(result.current.reasonAction).toBe("requestChanges");

      act(() => { result.current.cancelReason(); });

      expect(result.current.reasonAction).toBeNull();
      expect(result.current.reasonText).toBe("");
    });
  });

  describe("handleAssign", () => {
    it("assigns task and calls onRefresh", async () => {
      const id = nextTaskId();
      mockAssignTask.mockResolvedValue(makeTask({ id }));
      const onRefresh = vi.fn();

      const { result } = renderHook(() =>
        useTaskDetail(makeTask({ id, status: "Queued" }), onRefresh),
      );

      await waitFor(() => { expect(result.current.commentsLoading).toBe(false); });

      await act(async () => {
        result.current.handleAssign({
          id: "socrates",
          name: "Socrates",
          role: "Reviewer",
          summary: "",
          startupPrompt: "",
          capabilityTags: [],
          enabledTools: [],
          autoJoinDefaultRoom: true,
        });
      });

      expect(mockAssignTask).toHaveBeenCalledWith(id, "socrates", "Socrates");
      expect(result.current.actionResult).toEqual({ ok: true, message: "Assigned to Socrates" });
      expect(result.current.showAssignPicker).toBe(false);
      expect(onRefresh).toHaveBeenCalled();
    });

    it("sets error on assignment failure", async () => {
      mockAssignTask.mockRejectedValue(new Error("Agent busy"));

      const { result } = renderHook(() =>
        useTaskDetail(makeTask({ id: nextTaskId(), status: "Queued" }), noop),
      );

      await waitFor(() => { expect(result.current.commentsLoading).toBe(false); });

      act(() => {
        result.current.handleAssign({
          id: "socrates",
          name: "Socrates",
          role: "Reviewer",
          summary: "",
          startupPrompt: "",
          capabilityTags: [],
          enabledTools: [],
          autoJoinDefaultRoom: true,
        });
      });

      await waitFor(() => {
        expect(result.current.assignPending).toBe(false);
      });

      expect(result.current.actionResult).toEqual({
        ok: false,
        message: "Agent busy",
      });
    });
  });
});
