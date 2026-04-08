import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import type { ChatEnvelope } from "../api";
import {
  isCommandResultMessage,
  parseCommandResults,
  loadFilters,
  saveFilters,
  shouldHideMessage,
  STATUS_LABELS,
  STATUS_COLORS,
  MESSAGE_LENGTH_THRESHOLD,
  FILTER_STORAGE_KEY,
} from "../chatUtils";
import type { MessageFilter, ConnectionStatus } from "../chatUtils";

// ── Factories ──

function makeMessage(overrides: Partial<ChatEnvelope> = {}): ChatEnvelope {
  return {
    id: "msg-1",
    roomId: "room-1",
    senderId: "agent-1",
    senderName: "Software Engineer",
    senderRole: "engineer",
    senderKind: "Agent",
    kind: "text",
    content: "Hello, world!",
    sentAt: "2026-04-01T12:00:00Z",
    correlationId: null,
    replyToMessageId: null,
    ...overrides,
  };
}

function makeSystemMessage(content: string): ChatEnvelope {
  return makeMessage({
    id: `sys-${Math.random().toString(36).slice(2, 8)}`,
    senderId: "system",
    senderName: "System",
    senderRole: null,
    senderKind: "System",
    content,
  });
}

// ── Tests ──

describe("ChatPanel utilities", () => {
  describe("isCommandResultMessage", () => {
    it("returns true for command result messages", () => {
      expect(isCommandResultMessage("=== COMMAND RESULTS ===\n[Success] LIST_ROOMS (abc-123)")).toBe(true);
    });

    it("returns true for the exact prefix", () => {
      expect(isCommandResultMessage("=== COMMAND RESULTS ===")).toBe(true);
    });

    it("returns false for regular messages", () => {
      expect(isCommandResultMessage("Hello, world!")).toBe(false);
    });

    it("returns false for messages starting with different ===", () => {
      expect(isCommandResultMessage("=== OTHER HEADER ===")).toBe(false);
    });

    it("returns false for empty string", () => {
      expect(isCommandResultMessage("")).toBe(false);
    });
  });

  describe("parseCommandResults", () => {
    it("parses a single success result", () => {
      const input = [
        "=== COMMAND RESULTS ===",
        "[Success] LIST_ROOMS (corr-001)",
        "  Room 1: Main Room",
        "  Room 2: Breakout",
      ].join("\n");

      const results = parseCommandResults(input);
      expect(results).toHaveLength(1);
      expect(results[0].status).toBe("Success");
      expect(results[0].command).toBe("LIST_ROOMS");
      expect(results[0].correlationId).toBe("corr-001");
      expect(results[0].detail).toBe("Room 1: Main Room\nRoom 2: Breakout");
    });

    it("parses an error result with error message", () => {
      const input = [
        "=== COMMAND RESULTS ===",
        "[Error] READ_FILE (corr-002)",
        "  Error: File not found: /missing.txt",
      ].join("\n");

      const results = parseCommandResults(input);
      expect(results).toHaveLength(1);
      expect(results[0].status).toBe("Error");
      expect(results[0].command).toBe("READ_FILE");
      expect(results[0].error).toBe("File not found: /missing.txt");
    });

    it("parses a denied result", () => {
      const input = [
        "=== COMMAND RESULTS ===",
        "[Denied] CLOSE_ROOM (corr-003)",
        "  Error: Insufficient permissions",
      ].join("\n");

      const results = parseCommandResults(input);
      expect(results).toHaveLength(1);
      expect(results[0].status).toBe("Denied");
    });

    it("parses multiple results", () => {
      const input = [
        "=== COMMAND RESULTS ===",
        "[Success] LIST_ROOMS (corr-001)",
        "  Main Room",
        "[Error] READ_FILE (corr-002)",
        "  Error: Not found",
        "[Denied] CLOSE_ROOM (corr-003)",
      ].join("\n");

      const results = parseCommandResults(input);
      expect(results).toHaveLength(3);
      expect(results[0].status).toBe("Success");
      expect(results[1].status).toBe("Error");
      expect(results[2].status).toBe("Denied");
    });

    it("returns empty array for non-command content", () => {
      expect(parseCommandResults("Just some text")).toEqual([]);
    });

    it("returns empty array for empty string", () => {
      expect(parseCommandResults("")).toEqual([]);
    });

    it("strips 2-space indent from detail lines", () => {
      const input = [
        "=== COMMAND RESULTS ===",
        "[Success] LIST_AGENTS (corr-004)",
        "  architect: System Architect",
        "  engineer: Software Engineer",
      ].join("\n");

      const results = parseCommandResults(input);
      expect(results[0].detail).toBe("architect: System Architect\nengineer: Software Engineer");
    });

    it("handles result with no detail", () => {
      const input = [
        "=== COMMAND RESULTS ===",
        "[Success] CLEANUP_ROOMS (corr-006)",
      ].join("\n");

      const results = parseCommandResults(input);
      expect(results).toHaveLength(1);
      expect(results[0].detail).toBeUndefined();
    });

    it("handles result with both error and detail", () => {
      const input = [
        "=== COMMAND RESULTS ===",
        "[Error] RUN_TESTS (corr-007)",
        "  Error: 3 tests failed",
        "  test_auth.py::test_login FAILED",
        "  test_auth.py::test_logout FAILED",
      ].join("\n");

      const results = parseCommandResults(input);
      expect(results[0].error).toBe("3 tests failed");
      expect(results[0].detail).toBe("test_auth.py::test_login FAILED\ntest_auth.py::test_logout FAILED");
    });
  });

  describe("message filter persistence", () => {
    let storage: Record<string, string>;

    beforeEach(() => {
      storage = {};
      vi.stubGlobal("localStorage", {
        getItem: vi.fn((key: string) => storage[key] ?? null),
        setItem: vi.fn((key: string, value: string) => { storage[key] = value; }),
        removeItem: vi.fn((key: string) => { delete storage[key]; }),
      });
    });

    afterEach(() => {
      vi.unstubAllGlobals();
    });

    it("loadFilters returns empty set when nothing stored", () => {
      expect(loadFilters().size).toBe(0);
    });

    it("saveFilters stores filter set as JSON array", () => {
      saveFilters(new Set<MessageFilter>(["system"]));
      expect(storage[FILTER_STORAGE_KEY]).toBe('["system"]');
    });

    it("round-trips a single filter", () => {
      saveFilters(new Set<MessageFilter>(["commands"]));
      const loaded = loadFilters();
      expect(loaded.has("commands")).toBe(true);
      expect(loaded.size).toBe(1);
    });

    it("round-trips multiple filters", () => {
      saveFilters(new Set<MessageFilter>(["system", "commands"]));
      const loaded = loadFilters();
      expect(loaded.has("system")).toBe(true);
      expect(loaded.has("commands")).toBe(true);
    });

    it("round-trips empty filter set", () => {
      saveFilters(new Set<MessageFilter>());
      expect(loadFilters().size).toBe(0);
    });

    it("handles invalid JSON gracefully", () => {
      storage[FILTER_STORAGE_KEY] = "not-json";
      expect(loadFilters().size).toBe(0);
    });

    it("handles localStorage exceptions gracefully", () => {
      vi.stubGlobal("localStorage", {
        getItem: () => { throw new Error("SecurityError"); },
        setItem: () => { throw new Error("QuotaExceededError"); },
      });
      expect(loadFilters().size).toBe(0);
      expect(() => saveFilters(new Set(["system" as MessageFilter]))).not.toThrow();
    });
  });

  describe("shouldHideMessage", () => {
    it("never hides agent messages", () => {
      const msg = makeMessage({ senderKind: "Agent" });
      expect(shouldHideMessage(msg, new Set(["system", "commands"]))).toBe(false);
    });

    it("never hides user messages", () => {
      const msg = makeMessage({ senderKind: "User" });
      expect(shouldHideMessage(msg, new Set(["system", "commands"]))).toBe(false);
    });

    it("hides system messages when system filter is active", () => {
      const msg = makeSystemMessage("Phase changed to Implementation");
      expect(shouldHideMessage(msg, new Set(["system"]))).toBe(true);
    });

    it("shows system messages when no filters active", () => {
      const msg = makeSystemMessage("Phase changed to Implementation");
      expect(shouldHideMessage(msg, new Set())).toBe(false);
    });

    it("hides command result messages when commands filter is active", () => {
      const msg = makeSystemMessage("=== COMMAND RESULTS ===\n[Success] LIST_ROOMS (abc)");
      expect(shouldHideMessage(msg, new Set(["commands"]))).toBe(true);
    });

    it("shows command results when only system filter is active", () => {
      const msg = makeSystemMessage("=== COMMAND RESULTS ===\n[Success] LIST_ROOMS (abc)");
      expect(shouldHideMessage(msg, new Set(["system"]))).toBe(false);
    });

    it("hides both when both filters active", () => {
      const sys = makeSystemMessage("Phase changed");
      const cmd = makeSystemMessage("=== COMMAND RESULTS ===\n[Success] LIST_ROOMS (abc)");
      const both = new Set<MessageFilter>(["system", "commands"]);
      expect(shouldHideMessage(sys, both)).toBe(true);
      expect(shouldHideMessage(cmd, both)).toBe(true);
    });
  });

  describe("STATUS_LABELS", () => {
    it("has a label for every connection status", () => {
      const statuses: ConnectionStatus[] = ["connected", "connecting", "reconnecting", "disconnected"];
      for (const s of statuses) {
        expect(typeof STATUS_LABELS[s]).toBe("string");
      }
    });

    it("connected label mentions live or real-time", () => {
      expect(STATUS_LABELS.connected.toLowerCase()).toMatch(/live|real-time/);
    });
  });

  describe("STATUS_COLORS", () => {
    it("has a CSS variable color for every status", () => {
      const statuses: ConnectionStatus[] = ["connected", "connecting", "reconnecting", "disconnected"];
      for (const s of statuses) {
        expect(STATUS_COLORS[s]).toMatch(/^var\(--aa-/);
      }
    });

    it("connecting and reconnecting share the same color", () => {
      expect(STATUS_COLORS.connecting).toBe(STATUS_COLORS.reconnecting);
    });
  });

  describe("MESSAGE_LENGTH_THRESHOLD", () => {
    it("is 300 characters", () => {
      expect(MESSAGE_LENGTH_THRESHOLD).toBe(300);
    });
  });
});
