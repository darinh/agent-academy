import { describe, it, expect } from "vitest";
import type {
  SearchResults,
  SearchScope,
  MessageSearchResult,
  TaskSearchResult,
} from "../api";

// ── API contract tests ──────────────────────────────────────────────────

describe("SearchPanel", () => {
  describe("SearchResults shape", () => {
    it("has messages, tasks, totalCount, and query fields", () => {
      const result: SearchResults = {
        messages: [],
        tasks: [],
        totalCount: 0,
        query: "test",
      };
      expect(result.messages).toEqual([]);
      expect(result.tasks).toEqual([]);
      expect(result.totalCount).toBe(0);
      expect(result.query).toBe("test");
    });

    it("MessageSearchResult has all required fields", () => {
      const msg: MessageSearchResult = {
        messageId: "m1",
        roomId: "r1",
        roomName: "Main",
        senderName: "Hephaestus",
        senderKind: "Agent",
        senderRole: "Engineer",
        snippet: "The «authentication» module",
        sentAt: "2026-04-12T00:00:00Z",
        sessionId: "s1",
        source: "room",
      };
      expect(msg.messageId).toBe("m1");
      expect(msg.roomId).toBe("r1");
      expect(msg.roomName).toBe("Main");
      expect(msg.source).toBe("room");
      expect(msg.sessionId).toBe("s1");
    });

    it("TaskSearchResult has all required fields", () => {
      const task: TaskSearchResult = {
        taskId: "t1",
        title: "Implement OAuth",
        status: "Active",
        assignedAgentName: "Hephaestus",
        snippet: "Add «OAuth» login flow",
        createdAt: "2026-04-12T00:00:00Z",
        roomId: "r1",
      };
      expect(task.taskId).toBe("t1");
      expect(task.status).toBe("Active");
      expect(task.assignedAgentName).toBe("Hephaestus");
      expect(task.roomId).toBe("r1");
    });

    it("MessageSearchResult supports breakout source", () => {
      const msg: MessageSearchResult = {
        messageId: "m2",
        roomId: "r1",
        roomName: "Main",
        senderName: "Socrates",
        senderKind: "Agent",
        senderRole: "Reviewer",
        snippet: "Review completed",
        sentAt: "2026-04-12T00:00:00Z",
        sessionId: null,
        source: "breakout",
      };
      expect(msg.source).toBe("breakout");
      expect(msg.sessionId).toBeNull();
    });

    it("TaskSearchResult supports null fields", () => {
      const task: TaskSearchResult = {
        taskId: "t2",
        title: "Fix bug",
        status: "Completed",
        assignedAgentName: null,
        snippet: "Fixed the «bug»",
        createdAt: "2026-04-12T00:00:00Z",
        roomId: null,
      };
      expect(task.assignedAgentName).toBeNull();
      expect(task.roomId).toBeNull();
    });
  });

  describe("SearchScope type", () => {
    it("accepts valid scope values", () => {
      const scopes: SearchScope[] = ["all", "messages", "tasks"];
      expect(scopes).toHaveLength(3);
      expect(scopes).toContain("all");
      expect(scopes).toContain("messages");
      expect(scopes).toContain("tasks");
    });
  });

  describe("renderSnippet logic", () => {
    // Test the snippet parsing logic independently
    it("splits on FTS5 markers", () => {
      const snippet = "The «authentication» module handles «JWT» tokens";
      const parts = snippet.split(/(«[^»]*»)/g);
      expect(parts).toEqual([
        "The ",
        "«authentication»",
        " module handles ",
        "«JWT»",
        " tokens",
      ]);
    });

    it("handles snippet with no markers", () => {
      const snippet = "No highlights here";
      const parts = snippet.split(/(«[^»]*»)/g);
      expect(parts).toEqual(["No highlights here"]);
    });

    it("handles snippet with adjacent markers", () => {
      const snippet = "«hello» «world»";
      const parts = snippet.split(/(«[^»]*»)/g);
      expect(parts).toEqual(["", "«hello»", " ", "«world»", ""]);
    });

    it("handles empty snippet", () => {
      const snippet = "";
      const parts = snippet.split(/(«[^»]*»)/g);
      expect(parts).toEqual([""]);
    });
  });

  describe("status color mapping", () => {
    const STATUS_COLOR: Record<string, string> = {
      Active: "active",
      InReview: "review",
      AwaitingValidation: "review",
      Approved: "done",
      Completed: "done",
      Blocked: "err",
      Cancelled: "cancel",
      ChangesRequested: "warn",
      Merging: "feat",
      Queued: "muted",
    };

    it("maps Active to active", () => {
      expect(STATUS_COLOR["Active"]).toBe("active");
    });

    it("maps InReview to review", () => {
      expect(STATUS_COLOR["InReview"]).toBe("review");
    });

    it("maps Completed to done", () => {
      expect(STATUS_COLOR["Completed"]).toBe("done");
    });

    it("maps Blocked to err", () => {
      expect(STATUS_COLOR["Blocked"]).toBe("err");
    });

    it("returns undefined for unknown status", () => {
      expect(STATUS_COLOR["Unknown"]).toBeUndefined();
    });
  });
});
