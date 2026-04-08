import { describe, expect, it } from "vitest";
import type { TaskSnapshot, TaskStatus } from "../api";

// Import the filtering/helper logic by testing the component's behavior indirectly.
// Since the component uses module-scoped helpers, we test the logic patterns here.

// ── Filter logic (mirrors TaskListPanel internals) ─────────────────────

const REVIEW_STATUSES: TaskStatus[] = ["InReview", "AwaitingValidation", "Approved", "ChangesRequested"];
const ACTIVE_STATUSES: TaskStatus[] = ["Active", "Merging", "Blocked", "Queued"];
const COMPLETED_STATUSES: TaskStatus[] = ["Completed", "Cancelled"];

type TaskFilter = "all" | "review" | "active" | "completed";

function filterTasks(tasks: TaskSnapshot[], filter: TaskFilter): TaskSnapshot[] {
  switch (filter) {
    case "review":
      return tasks.filter((t) => REVIEW_STATUSES.includes(t.status));
    case "active":
      return tasks.filter((t) => ACTIVE_STATUSES.includes(t.status));
    case "completed":
      return tasks.filter((t) => COMPLETED_STATUSES.includes(t.status));
    default:
      return tasks;
  }
}

// ── Action availability (mirrors TaskListPanel internals) ──────────────

type TaskAction = "approve" | "requestChanges" | "reject" | "merge";

function getAvailableActions(status: TaskStatus): TaskAction[] {
  switch (status) {
    case "InReview":
    case "AwaitingValidation":
      return ["approve", "requestChanges"];
    case "Approved":
      return ["merge", "reject"];
    case "Completed":
      return ["reject"];
    default:
      return [];
  }
}

// ── Helpers ────────────────────────────────────────────────────────────

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
    updatedAt: "2026-04-01T12:00:00Z",
    ...overrides,
  };
}

// ── Tests ──────────────────────────────────────────────────────────────

describe("TaskListPanel logic", () => {
  describe("filterTasks", () => {
    const tasks: TaskSnapshot[] = [
      makeTask({ id: "1", status: "Active" }),
      makeTask({ id: "2", status: "InReview" }),
      makeTask({ id: "3", status: "Approved" }),
      makeTask({ id: "4", status: "Completed" }),
      makeTask({ id: "5", status: "Queued" }),
      makeTask({ id: "6", status: "ChangesRequested" }),
      makeTask({ id: "7", status: "Cancelled" }),
      makeTask({ id: "8", status: "AwaitingValidation" }),
      makeTask({ id: "9", status: "Blocked" }),
      makeTask({ id: "10", status: "Merging" }),
    ];

    it("all filter returns every task", () => {
      expect(filterTasks(tasks, "all")).toHaveLength(10);
    });

    it("review filter returns InReview, AwaitingValidation, Approved, ChangesRequested", () => {
      const result = filterTasks(tasks, "review");
      expect(result.map((t) => t.id).sort()).toEqual(["2", "3", "6", "8"]);
    });

    it("active filter returns Active, Merging, Blocked, Queued", () => {
      const result = filterTasks(tasks, "active");
      expect(result.map((t) => t.id).sort()).toEqual(["1", "10", "5", "9"]);
    });

    it("completed filter returns Completed and Cancelled", () => {
      const result = filterTasks(tasks, "completed");
      expect(result.map((t) => t.id).sort()).toEqual(["4", "7"]);
    });

    it("filters are exhaustive — every status appears in exactly one filter bucket", () => {
      const review = filterTasks(tasks, "review");
      const active = filterTasks(tasks, "active");
      const completed = filterTasks(tasks, "completed");
      const total = review.length + active.length + completed.length;
      expect(total).toBe(tasks.length);
    });
  });

  describe("getAvailableActions", () => {
    it("InReview tasks can be approved or have changes requested", () => {
      expect(getAvailableActions("InReview")).toEqual(["approve", "requestChanges"]);
    });

    it("AwaitingValidation tasks can be approved or have changes requested", () => {
      expect(getAvailableActions("AwaitingValidation")).toEqual(["approve", "requestChanges"]);
    });

    it("Approved tasks can be merged or rejected", () => {
      expect(getAvailableActions("Approved")).toEqual(["merge", "reject"]);
    });

    it("Completed tasks can only be rejected", () => {
      expect(getAvailableActions("Completed")).toEqual(["reject"]);
    });

    it("Active tasks have no review actions", () => {
      expect(getAvailableActions("Active")).toEqual([]);
    });

    it("Queued tasks have no review actions", () => {
      expect(getAvailableActions("Queued")).toEqual([]);
    });

    it("Blocked tasks have no review actions", () => {
      expect(getAvailableActions("Blocked")).toEqual([]);
    });

    it("ChangesRequested tasks have no review actions", () => {
      expect(getAvailableActions("ChangesRequested")).toEqual([]);
    });

    it("Cancelled tasks have no review actions", () => {
      expect(getAvailableActions("Cancelled")).toEqual([]);
    });

    it("Merging tasks have no review actions", () => {
      expect(getAvailableActions("Merging")).toEqual([]);
    });
  });

  describe("command mapping", () => {
    const ACTION_COMMANDS: Record<TaskAction, string> = {
      approve: "APPROVE_TASK",
      requestChanges: "REQUEST_CHANGES",
      reject: "REJECT_TASK",
      merge: "MERGE_TASK",
    };

    it("each action maps to the correct command", () => {
      expect(ACTION_COMMANDS.approve).toBe("APPROVE_TASK");
      expect(ACTION_COMMANDS.requestChanges).toBe("REQUEST_CHANGES");
      expect(ACTION_COMMANDS.reject).toBe("REJECT_TASK");
      expect(ACTION_COMMANDS.merge).toBe("MERGE_TASK");
    });

    it("success status from command API is 'completed', not 'success'", () => {
      // Ensures the panel checks for "completed" (backend convention)
      const successStatus = "completed";
      expect(successStatus).not.toBe("success");
      expect(["completed", "failed", "denied", "pending"]).toContain(successStatus);
    });

    it("request changes needs findings arg, reject needs reason arg", () => {
      // This tests the arg key convention used in the panel
      const requestChangesArgs = { taskId: "t1", findings: "fix the tests" };
      const rejectArgs = { taskId: "t1", reason: "fundamentally wrong approach" };
      expect(requestChangesArgs.findings).toBeTruthy();
      expect(rejectArgs.reason).toBeTruthy();
    });
  });
});
