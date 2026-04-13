import { describe, expect, it } from "vitest";
import type { TaskSnapshot, TaskStatus, TaskSize, TaskCommentType } from "../api";
import {
  filterTasks,
  filterCount,
  statusBadgeColor,
  typeBadgeColor,
  sizeBadgeColor,
  commentTypeBadge,
  specLinkBadge,
  evidencePhaseBadge,
  formatTime,
  getAvailableActions,
  getCached,
} from "../taskList/taskListHelpers";

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

describe("taskListHelpers", () => {
  describe("filterTasks", () => {
    const tasks = [
      makeTask({ id: "1", status: "Active" }),
      makeTask({ id: "2", status: "InReview" }),
      makeTask({ id: "3", status: "Completed" }),
      makeTask({ id: "4", status: "Queued" }),
      makeTask({ id: "5", status: "Approved" }),
      makeTask({ id: "6", status: "Cancelled" }),
      makeTask({ id: "7", status: "Blocked" }),
      makeTask({ id: "8", status: "AwaitingValidation" }),
      makeTask({ id: "9", status: "ChangesRequested" }),
      makeTask({ id: "10", status: "Merging" }),
    ];

    it("returns all tasks for 'all' filter", () => {
      expect(filterTasks(tasks, "all")).toHaveLength(10);
    });

    it("returns review statuses for 'review' filter", () => {
      const result = filterTasks(tasks, "review");
      expect(result.map((t) => t.status)).toEqual(
        expect.arrayContaining(["InReview", "AwaitingValidation", "Approved", "ChangesRequested"]),
      );
      expect(result).toHaveLength(4);
    });

    it("returns active statuses for 'active' filter", () => {
      const result = filterTasks(tasks, "active");
      expect(result.map((t) => t.status)).toEqual(
        expect.arrayContaining(["Active", "Merging", "Blocked", "Queued"]),
      );
      expect(result).toHaveLength(4);
    });

    it("returns completed statuses for 'completed' filter", () => {
      const result = filterTasks(tasks, "completed");
      expect(result.map((t) => t.status)).toEqual(
        expect.arrayContaining(["Completed", "Cancelled"]),
      );
      expect(result).toHaveLength(2);
    });
  });

  describe("filterCount", () => {
    it("returns count matching the filter", () => {
      const tasks = [
        makeTask({ status: "Active" }),
        makeTask({ status: "Completed" }),
      ];
      expect(filterCount(tasks, "active")).toBe(1);
      expect(filterCount(tasks, "completed")).toBe(1);
      expect(filterCount(tasks, "all")).toBe(2);
    });
  });

  describe("statusBadgeColor", () => {
    it.each<[TaskStatus, string]>([
      ["Active", "active"],
      ["Merging", "active"],
      ["InReview", "review"],
      ["AwaitingValidation", "warn"],
      ["Approved", "ok"],
      ["ChangesRequested", "warn"],
      ["Blocked", "err"],
      ["Completed", "done"],
      ["Cancelled", "cancel"],
      ["Queued", "info"],
    ])("maps %s → %s", (status, expected) => {
      expect(statusBadgeColor(status)).toBe(expected);
    });
  });

  describe("typeBadgeColor", () => {
    it("maps Bug → bug", () => expect(typeBadgeColor("Bug")).toBe("bug"));
    it("maps Feature → feat", () => expect(typeBadgeColor("Feature")).toBe("feat"));
    it("maps unknown → muted", () => expect(typeBadgeColor("Chore")).toBe("muted"));
  });

  describe("sizeBadgeColor", () => {
    it.each<[TaskSize, string]>([
      ["XS", "muted"],
      ["S", "muted"],
      ["M", "info"],
      ["L", "warn"],
      ["XL", "warn"],
    ])("maps %s → %s", (size, expected) => {
      expect(sizeBadgeColor(size)).toBe(expected);
    });
  });

  describe("commentTypeBadge", () => {
    it.each<[TaskCommentType, string]>([
      ["Comment", "muted"],
      ["Finding", "warn"],
      ["Blocker", "err"],
      ["Evidence", "ok"],
    ])("maps %s → %s", (type, expected) => {
      expect(commentTypeBadge(type)).toBe(expected);
    });
  });

  describe("specLinkBadge", () => {
    it.each([
      ["Implements", "ok"],
      ["Modifies", "warn"],
      ["Fixes", "err"],
      ["References", "info"],
      ["Unknown", "muted"],
    ])("maps %s → %s", (type, expected) => {
      expect(specLinkBadge(type)).toBe(expected);
    });
  });

  describe("evidencePhaseBadge", () => {
    it.each([
      ["Baseline", "info"],
      ["After", "ok"],
      ["Review", "review"],
      ["Other", "muted"],
    ])("maps %s → %s", (phase, expected) => {
      expect(evidencePhaseBadge(phase)).toBe(expected);
    });
  });

  describe("formatTime", () => {
    it("returns 'just now' for <60s ago", () => {
      const recent = new Date(Date.now() - 30_000).toISOString();
      expect(formatTime(recent)).toBe("just now");
    });

    it("returns minutes for <1h ago", () => {
      const fiveMin = new Date(Date.now() - 300_000).toISOString();
      expect(formatTime(fiveMin)).toBe("5m ago");
    });

    it("returns hours for <24h ago", () => {
      const twoHours = new Date(Date.now() - 7_200_000).toISOString();
      expect(formatTime(twoHours)).toBe("2h ago");
    });

    it("returns date for >24h ago", () => {
      const old = new Date(Date.now() - 172_800_000).toISOString();
      const result = formatTime(old);
      // Should be a date string like "4/11/2026"
      expect(result).toMatch(/\d/);
      expect(result).not.toContain("ago");
    });
  });

  describe("getAvailableActions", () => {
    it.each<[TaskStatus, string[]]>([
      ["InReview", ["approve", "requestChanges"]],
      ["AwaitingValidation", ["approve", "requestChanges"]],
      ["Approved", ["merge", "reject"]],
      ["Completed", ["reject"]],
      ["Active", []],
      ["Queued", []],
      ["Blocked", []],
      ["Cancelled", []],
      ["Merging", []],
      ["ChangesRequested", []],
    ])("status %s → %j", (status, expected) => {
      expect(getAvailableActions(status)).toEqual(expected);
    });
  });

  describe("getCached", () => {
    it("returns fresh entry for new task", () => {
      const entry = getCached("new-task-99", "2026-04-13T00:00:00Z");
      expect(entry.updatedAt).toBe("2026-04-13T00:00:00Z");
      expect(entry.comments).toBeUndefined();
      expect(entry.specLinks).toBeUndefined();
    });

    it("returns existing entry when updatedAt matches", () => {
      const entry1 = getCached("cache-test-1", "2026-04-13T00:00:00Z");
      entry1.comments = [];
      const entry2 = getCached("cache-test-1", "2026-04-13T00:00:00Z");
      expect(entry2.comments).toEqual([]);
    });

    it("returns fresh entry when updatedAt changes", () => {
      const entry1 = getCached("cache-test-2", "2026-04-13T00:00:00Z");
      entry1.comments = [];
      const entry2 = getCached("cache-test-2", "2026-04-13T01:00:00Z");
      expect(entry2.comments).toBeUndefined();
    });
  });
});
