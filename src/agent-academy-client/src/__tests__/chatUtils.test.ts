// @vitest-environment jsdom
import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
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
import type { ChatEnvelope } from "../api";
import type { ConnectionStatus } from "../useActivityHub";
import type { MessageFilter } from "../chatUtils";

/* ── Helpers ──────────────────────────────────────────────────── */

function makeEnvelope(overrides: Partial<ChatEnvelope> = {}): ChatEnvelope {
  return {
    id: "msg-1",
    roomId: "room-1",
    senderId: "system",
    senderName: "System",
    senderKind: "System",
    kind: "Text",
    content: "hello",
    timestamp: new Date().toISOString(),
    ...overrides,
  } as ChatEnvelope;
}

/* ── Tests ────────────────────────────────────────────────────── */

describe("chatUtils", () => {
  /* ── isCommandResultMessage ─────────────────────────────────── */

  describe("isCommandResultMessage", () => {
    it("returns true for command result headers", () => {
      expect(isCommandResultMessage("=== COMMAND RESULTS ===\n[Success] RUN_BUILD (abc123)")).toBe(true);
    });

    it("returns false for plain text", () => {
      expect(isCommandResultMessage("Hello world")).toBe(false);
    });

    it("returns false for empty string", () => {
      expect(isCommandResultMessage("")).toBe(false);
    });

    it("returns false for similar but not exact prefix", () => {
      expect(isCommandResultMessage("== COMMAND RESULTS ==")).toBe(false);
    });
  });

  /* ── parseCommandResults ────────────────────────────────────── */

  describe("parseCommandResults", () => {
    it("parses a single success result", () => {
      const content = [
        "=== COMMAND RESULTS ===",
        "[Success] RUN_BUILD (corr-1)",
        "  Build succeeded",
      ].join("\n");

      const results = parseCommandResults(content);
      expect(results).toHaveLength(1);
      expect(results[0]).toEqual({
        status: "Success",
        command: "RUN_BUILD",
        correlationId: "corr-1",
        detail: "Build succeeded",
      });
    });

    it("parses an error result with error line", () => {
      const content = [
        "=== COMMAND RESULTS ===",
        "[Error] SHOW_DIFF (corr-2)",
        "  Error: No changes found",
      ].join("\n");

      const results = parseCommandResults(content);
      expect(results).toHaveLength(1);
      expect(results[0].status).toBe("Error");
      expect(results[0].error).toBe("No changes found");
    });

    it("parses a denied result", () => {
      const content = [
        "=== COMMAND RESULTS ===",
        "[Denied] SHELL (corr-3)",
      ].join("\n");

      const results = parseCommandResults(content);
      expect(results).toHaveLength(1);
      expect(results[0].status).toBe("Denied");
      expect(results[0].command).toBe("SHELL");
    });

    it("parses multiple results", () => {
      const content = [
        "=== COMMAND RESULTS ===",
        "[Success] RUN_BUILD (corr-1)",
        "  OK",
        "[Error] RUN_TESTS (corr-2)",
        "  Error: 3 tests failed",
        "[Success] GIT_LOG (corr-3)",
        "  abc1234 Initial commit",
      ].join("\n");

      const results = parseCommandResults(content);
      expect(results).toHaveLength(3);
      expect(results[0].status).toBe("Success");
      expect(results[1].status).toBe("Error");
      expect(results[1].error).toBe("3 tests failed");
      expect(results[2].status).toBe("Success");
    });

    it("returns empty array for non-command content", () => {
      expect(parseCommandResults("Just a plain message")).toEqual([]);
    });

    it("handles multi-line detail blocks", () => {
      const content = [
        "=== COMMAND RESULTS ===",
        "[Success] SHOW_DIFF (corr-4)",
        "  diff --git a/file.ts",
        "  +new line",
        "  -old line",
      ].join("\n");

      const results = parseCommandResults(content);
      expect(results).toHaveLength(1);
      expect(results[0].detail).toContain("diff --git a/file.ts");
      expect(results[0].detail).toContain("+new line");
      expect(results[0].detail).toContain("-old line");
    });

    it("handles result with no detail or error", () => {
      const content = [
        "=== COMMAND RESULTS ===",
        "[Success] LIST_COMMANDS (corr-5)",
      ].join("\n");

      const results = parseCommandResults(content);
      expect(results).toHaveLength(1);
      expect(results[0].detail).toBeUndefined();
      expect(results[0].error).toBeUndefined();
    });
  });

  /* ── Filter persistence ─────────────────────────────────────── */

  describe("loadFilters / saveFilters", () => {
    let storage: Record<string, string>;

    beforeEach(() => {
      storage = {};
      vi.spyOn(Storage.prototype, "getItem").mockImplementation((key) => storage[key] ?? null);
      vi.spyOn(Storage.prototype, "setItem").mockImplementation((key, value) => {
        storage[key] = value;
      });
    });

    afterEach(() => {
      vi.restoreAllMocks();
    });

    it("returns empty set when no stored filters", () => {
      const filters = loadFilters();
      expect(filters.size).toBe(0);
    });

    it("round-trips saved filters", () => {
      const original = new Set<MessageFilter>(["system", "commands"]);
      saveFilters(original);
      const loaded = loadFilters();
      expect(loaded).toEqual(original);
    });

    it("saves a single filter", () => {
      saveFilters(new Set<MessageFilter>(["system"]));
      const loaded = loadFilters();
      expect(loaded.has("system")).toBe(true);
      expect(loaded.has("commands")).toBe(false);
    });

    it("uses correct storage key", () => {
      saveFilters(new Set<MessageFilter>(["system"]));
      expect(Storage.prototype.setItem).toHaveBeenCalledWith(
        FILTER_STORAGE_KEY,
        expect.any(String),
      );
    });

    it("returns empty set on corrupted storage", () => {
      storage[FILTER_STORAGE_KEY] = "not-valid-json{{{";
      const filters = loadFilters();
      expect(filters.size).toBe(0);
    });
  });

  /* ── shouldHideMessage ──────────────────────────────────────── */

  describe("shouldHideMessage", () => {
    it("never hides agent messages", () => {
      const msg = makeEnvelope({ senderKind: "Agent" });
      const hidden = new Set<MessageFilter>(["system", "commands"]);
      expect(shouldHideMessage(msg, hidden)).toBe(false);
    });

    it("never hides user messages", () => {
      const msg = makeEnvelope({ senderKind: "User" });
      const hidden = new Set<MessageFilter>(["system", "commands"]);
      expect(shouldHideMessage(msg, hidden)).toBe(false);
    });

    it("hides system messages when system filter active", () => {
      const msg = makeEnvelope({ senderKind: "System", content: "Phase changed to Planning" });
      expect(shouldHideMessage(msg, new Set<MessageFilter>(["system"]))).toBe(true);
    });

    it("hides command results when commands filter active", () => {
      const msg = makeEnvelope({
        senderKind: "System",
        content: "=== COMMAND RESULTS ===\n[Success] RUN_BUILD (x)",
      });
      expect(shouldHideMessage(msg, new Set<MessageFilter>(["commands"]))).toBe(true);
    });

    it("does not hide system messages when only commands filter active", () => {
      const msg = makeEnvelope({ senderKind: "System", content: "Agent joined" });
      expect(shouldHideMessage(msg, new Set<MessageFilter>(["commands"]))).toBe(false);
    });

    it("does not hide command results when only system filter active", () => {
      const msg = makeEnvelope({
        senderKind: "System",
        content: "=== COMMAND RESULTS ===\n[Success] GIT_LOG (y)",
      });
      expect(shouldHideMessage(msg, new Set<MessageFilter>(["system"]))).toBe(false);
    });

    it("shows system messages when no filters active", () => {
      const msg = makeEnvelope({ senderKind: "System" });
      expect(shouldHideMessage(msg, new Set())).toBe(false);
    });
  });

  /* ── Status maps ────────────────────────────────────────────── */

  describe("STATUS_LABELS", () => {
    const statuses: ConnectionStatus[] = ["connected", "connecting", "reconnecting", "disconnected"];

    it.each(statuses)("has a label for %s", (status) => {
      expect(STATUS_LABELS[status]).toBeTruthy();
      expect(typeof STATUS_LABELS[status]).toBe("string");
    });
  });

  describe("STATUS_COLORS", () => {
    const statuses: ConnectionStatus[] = ["connected", "connecting", "reconnecting", "disconnected"];

    it.each(statuses)("has a CSS color for %s", (status) => {
      expect(STATUS_COLORS[status]).toBeTruthy();
      expect(STATUS_COLORS[status]).toMatch(/var\(--aa-/);
    });
  });

  /* ── Constants ──────────────────────────────────────────────── */

  describe("MESSAGE_LENGTH_THRESHOLD", () => {
    it("is a positive number", () => {
      expect(MESSAGE_LENGTH_THRESHOLD).toBeGreaterThan(0);
    });
  });
});
