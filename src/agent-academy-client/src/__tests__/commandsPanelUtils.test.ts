import { describe, expect, it } from "vitest";
import {
  badgeColorForCategory,
  badgeColorForStatus,
  isRecord,
  summarizeResult,
  findPrimaryList,
  findPreviewBlock,
  readableLabel,
  POLL_INTERVAL_MS,
  MAX_HISTORY_ITEMS,
} from "../commandsPanelUtils";

describe("commandsPanelUtils", () => {
  /* ── badgeColorForCategory ──────────────────────────────────── */

  describe("badgeColorForCategory", () => {
    it('returns "info" for code category', () => {
      expect(badgeColorForCategory("code")).toBe("info");
    });

    it('returns "warn" for git category', () => {
      expect(badgeColorForCategory("git")).toBe("warn");
    });

    it('returns "err" for operations category', () => {
      expect(badgeColorForCategory("operations")).toBe("err");
    });

    it('returns "ok" for unknown categories', () => {
      expect(badgeColorForCategory("unknown")).toBe("ok");
      expect(badgeColorForCategory("")).toBe("ok");
    });
  });

  /* ── badgeColorForStatus ────────────────────────────────────── */

  describe("badgeColorForStatus", () => {
    it('returns "ok" for completed', () => {
      expect(badgeColorForStatus("completed")).toBe("ok");
    });

    it('returns "warn" for pending', () => {
      expect(badgeColorForStatus("pending")).toBe("warn");
    });

    it('returns "err" for denied', () => {
      expect(badgeColorForStatus("denied")).toBe("err");
    });

    it('returns "err" for failed', () => {
      expect(badgeColorForStatus("failed")).toBe("err");
    });
  });

  /* ── isRecord ───────────────────────────────────────────────── */

  describe("isRecord", () => {
    it("returns true for plain objects", () => {
      expect(isRecord({ a: 1 })).toBe(true);
      expect(isRecord({})).toBe(true);
    });

    it("returns false for arrays", () => {
      expect(isRecord([1, 2])).toBe(false);
    });

    it("returns false for null", () => {
      expect(isRecord(null)).toBe(false);
    });

    it("returns false for primitives", () => {
      expect(isRecord("string")).toBe(false);
      expect(isRecord(42)).toBe(false);
      expect(isRecord(true)).toBe(false);
      expect(isRecord(undefined)).toBe(false);
    });
  });

  /* ── summarizeResult ────────────────────────────────────────── */

  describe("summarizeResult", () => {
    it("returns empty array for non-record input", () => {
      expect(summarizeResult("string")).toEqual([]);
      expect(summarizeResult(null)).toEqual([]);
      expect(summarizeResult(42)).toEqual([]);
    });

    it("extracts scalar fields as [label, value] pairs", () => {
      const result = summarizeResult({ exitCode: 0, branch: "main", success: true });
      expect(result).toEqual([
        ["Exit Code", "0"],
        ["Branch", "main"],
        ["Success", "true"],
      ]);
    });

    it("ignores complex fields and ignored keys", () => {
      const result = summarizeResult({
        content: "long text",
        output: "stdout",
        diff: "+line",
        matches: [],
        exitCode: 0,
      });
      expect(result).toEqual([["Exit Code", "0"]]);
    });

    it("caps at 6 entries", () => {
      const input = Object.fromEntries(
        Array.from({ length: 10 }, (_, i) => [`field${i}`, `value${i}`]),
      );
      const result = summarizeResult(input);
      expect(result.length).toBeLessThanOrEqual(6);
    });

    it("returns empty array for object with only ignored keys", () => {
      expect(summarizeResult({ content: "x", output: "y", tasks: [] })).toEqual([]);
    });
  });

  /* ── findPrimaryList ────────────────────────────────────────── */

  describe("findPrimaryList", () => {
    it("returns empty array for non-record input", () => {
      expect(findPrimaryList("string")).toEqual([]);
      expect(findPrimaryList(null)).toEqual([]);
    });

    it("extracts items from tasks array using title as primary", () => {
      const result = findPrimaryList({
        tasks: [
          { title: "Fix bug", status: "Active" },
          { title: "Add feature", phase: "Planning" },
        ],
      });
      expect(result).toHaveLength(2);
      expect(result[0].primary).toBe("Fix bug");
      expect(result[0].secondary).toContain("Active");
      expect(result[1].primary).toBe("Add feature");
      expect(result[1].secondary).toContain("Planning");
    });

    it("uses name fallback for primary", () => {
      const result = findPrimaryList({ agents: [{ name: "Planner", role: "Planner" }] });
      expect(result[0].primary).toBe("Planner");
      expect(result[0].secondary).toContain("Planner");
    });

    it("uses sha fallback for primary", () => {
      const result = findPrimaryList({ commits: [{ sha: "abc1234", message: "init" }] });
      expect(result[0].primary).toBe("abc1234");
    });

    it("caps at 6 items", () => {
      const items = Array.from({ length: 10 }, (_, i) => ({ title: `Task ${i}` }));
      expect(findPrimaryList({ tasks: items }).length).toBeLessThanOrEqual(6);
    });

    it("returns empty array when no candidate arrays found", () => {
      expect(findPrimaryList({ exitCode: 0, branch: "main" })).toEqual([]);
    });

    it("handles primitive entries in candidate array", () => {
      const result = findPrimaryList({ matches: ["file1.ts", "file2.ts"] });
      expect(result[0].primary).toBe("file1.ts");
      expect(result[0].secondary).toBeUndefined();
    });

    it("prefers matches key first in priority order", () => {
      const result = findPrimaryList({
        matches: [{ file: "match.ts" }],
        tasks: [{ title: "Task 1" }],
      });
      expect(result[0].primary).toBe("match.ts");
    });
  });

  /* ── findPreviewBlock ───────────────────────────────────────── */

  describe("findPreviewBlock", () => {
    it("returns null for non-record input", () => {
      expect(findPreviewBlock(null)).toBeNull();
      expect(findPreviewBlock("string")).toBeNull();
    });

    it("returns content field when present", () => {
      expect(findPreviewBlock({ content: "Hello world" })).toBe("Hello world");
    });

    it("falls back to output field", () => {
      expect(findPreviewBlock({ output: "Build succeeded" })).toBe("Build succeeded");
    });

    it("falls back to diff field", () => {
      expect(findPreviewBlock({ diff: "+new line" })).toBe("+new line");
    });

    it("returns null when no preview fields exist", () => {
      expect(findPreviewBlock({ exitCode: 0 })).toBeNull();
    });

    it("skips empty strings", () => {
      expect(findPreviewBlock({ content: "", output: "real output" })).toBe("real output");
    });

    it("skips whitespace-only strings", () => {
      expect(findPreviewBlock({ content: "   ", output: "real output" })).toBe("real output");
    });
  });

  /* ── readableLabel ──────────────────────────────────────────── */

  describe("readableLabel", () => {
    it("converts camelCase to spaced words with initial capital", () => {
      expect(readableLabel("exitCode")).toBe("Exit Code");
    });

    it("handles single word", () => {
      expect(readableLabel("branch")).toBe("Branch");
    });

    it("handles already capitalized input", () => {
      expect(readableLabel("Status")).toBe("Status");
    });

    it("handles multi-word camelCase", () => {
      expect(readableLabel("assignedAgentId")).toBe("Assigned Agent Id");
    });
  });

  /* ── Constants ──────────────────────────────────────────────── */

  describe("constants", () => {
    it("POLL_INTERVAL_MS is a positive number", () => {
      expect(POLL_INTERVAL_MS).toBeGreaterThan(0);
    });

    it("MAX_HISTORY_ITEMS is a positive number", () => {
      expect(MAX_HISTORY_ITEMS).toBeGreaterThan(0);
    });
  });
});
