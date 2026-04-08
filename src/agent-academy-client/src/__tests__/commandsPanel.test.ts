import { describe, expect, it } from "vitest";
import type { CommandExecutionStatus } from "../api";
import {
  badgeColorForCategory,
  badgeColorForStatus,
  findPrimaryList,
  findPreviewBlock,
  isRecord,
  MAX_HISTORY_ITEMS,
  POLL_INTERVAL_MS,
  readableLabel,
  summarizeResult,
} from "../commandsPanelUtils";

/* ------------------------------------------------------------------ */
/*  Constants                                                          */
/* ------------------------------------------------------------------ */

describe("CommandsPanel constants", () => {
  it("POLL_INTERVAL_MS is a positive number", () => {
    expect(POLL_INTERVAL_MS).toBeGreaterThan(0);
    expect(typeof POLL_INTERVAL_MS).toBe("number");
  });

  it("MAX_HISTORY_ITEMS is a positive number", () => {
    expect(MAX_HISTORY_ITEMS).toBeGreaterThan(0);
    expect(typeof MAX_HISTORY_ITEMS).toBe("number");
  });
});

/* ------------------------------------------------------------------ */
/*  badgeColorForCategory                                              */
/* ------------------------------------------------------------------ */

describe("badgeColorForCategory", () => {
  it("returns 'info' for code", () => {
    expect(badgeColorForCategory("code")).toBe("info");
  });

  it("returns 'warn' for git", () => {
    expect(badgeColorForCategory("git")).toBe("warn");
  });

  it("returns 'err' for operations", () => {
    expect(badgeColorForCategory("operations")).toBe("err");
  });

  it("returns 'ok' for workspace (default)", () => {
    expect(badgeColorForCategory("workspace")).toBe("ok");
  });

  it("returns 'ok' for unknown categories", () => {
    expect(badgeColorForCategory("")).toBe("ok");
    expect(badgeColorForCategory("custom")).toBe("ok");
    expect(badgeColorForCategory("admin")).toBe("ok");
  });
});

/* ------------------------------------------------------------------ */
/*  badgeColorForStatus                                                */
/* ------------------------------------------------------------------ */

describe("badgeColorForStatus", () => {
  it("returns 'ok' for completed", () => {
    expect(badgeColorForStatus("completed")).toBe("ok");
  });

  it("returns 'warn' for pending", () => {
    expect(badgeColorForStatus("pending")).toBe("warn");
  });

  it("returns 'err' for denied", () => {
    expect(badgeColorForStatus("denied")).toBe("err");
  });

  it("returns 'err' for failed", () => {
    expect(badgeColorForStatus("failed")).toBe("err");
  });

  it("returns 'err' for any unknown status", () => {
    expect(badgeColorForStatus("unknown" as CommandExecutionStatus)).toBe("err");
  });
});

/* ------------------------------------------------------------------ */
/*  isRecord                                                           */
/* ------------------------------------------------------------------ */

describe("isRecord", () => {
  it("returns true for plain objects", () => {
    expect(isRecord({})).toBe(true);
    expect(isRecord({ a: 1 })).toBe(true);
  });

  it("returns false for null", () => {
    expect(isRecord(null)).toBe(false);
  });

  it("returns false for arrays", () => {
    expect(isRecord([])).toBe(false);
    expect(isRecord([1, 2])).toBe(false);
  });

  it("returns false for primitives", () => {
    expect(isRecord(42)).toBe(false);
    expect(isRecord("hello")).toBe(false);
    expect(isRecord(true)).toBe(false);
    expect(isRecord(undefined)).toBe(false);
  });
});

/* ------------------------------------------------------------------ */
/*  readableLabel                                                      */
/* ------------------------------------------------------------------ */

describe("readableLabel", () => {
  it("capitalises a simple lowercase key", () => {
    expect(readableLabel("status")).toBe("Status");
  });

  it("inserts spaces before uppercase letters", () => {
    expect(readableLabel("exitCode")).toBe("Exit Code");
    expect(readableLabel("errorMessage")).toBe("Error Message");
  });

  it("handles multiple consecutive uppercase runs", () => {
    expect(readableLabel("taskId")).toBe("Task Id");
  });

  it("handles already capitalised first letter without adding a leading space", () => {
    expect(readableLabel("Status")).toBe("Status");
  });

  it("handles single character key", () => {
    expect(readableLabel("x")).toBe("X");
  });

  it("handles empty string", () => {
    expect(readableLabel("")).toBe("");
  });
});

/* ------------------------------------------------------------------ */
/*  summarizeResult                                                    */
/* ------------------------------------------------------------------ */

describe("summarizeResult", () => {
  it("returns empty array for non-record inputs", () => {
    expect(summarizeResult(null)).toEqual([]);
    expect(summarizeResult(undefined)).toEqual([]);
    expect(summarizeResult("text")).toEqual([]);
    expect(summarizeResult(42)).toEqual([]);
    expect(summarizeResult([1, 2])).toEqual([]);
  });

  it("extracts string values with readable labels", () => {
    const result = summarizeResult({ status: "ok", exitCode: 0 });
    expect(result).toEqual([
      ["Status", "ok"],
      ["Exit Code", "0"],
    ]);
  });

  it("extracts boolean values as strings", () => {
    const result = summarizeResult({ success: true });
    expect(result).toEqual([["Success", "true"]]);
  });

  it("ignores object and array values", () => {
    const result = summarizeResult({
      status: "ok",
      nested: { a: 1 },
      items: [1, 2],
    });
    expect(result).toEqual([["Status", "ok"]]);
  });

  it("ignores content/output/diff/matches/tasks/rooms/agents/commits/messages keys", () => {
    const result = summarizeResult({
      content: "body text",
      output: "stdout",
      diff: "+line",
      matches: ["a"],
      tasks: ["t1"],
      rooms: ["r1"],
      agents: ["a1"],
      commits: ["c1"],
      messages: ["m1"],
      status: "ok",
    });
    expect(result).toEqual([["Status", "ok"]]);
  });

  it("limits to 6 entries", () => {
    const result = summarizeResult({
      a: "1",
      b: "2",
      c: "3",
      d: "4",
      e: "5",
      f: "6",
      g: "7",
      h: "8",
    });
    expect(result).toHaveLength(6);
  });

  it("returns empty array for empty object", () => {
    expect(summarizeResult({})).toEqual([]);
  });
});

/* ------------------------------------------------------------------ */
/*  findPrimaryList                                                    */
/* ------------------------------------------------------------------ */

describe("findPrimaryList", () => {
  it("returns empty array for non-record inputs", () => {
    expect(findPrimaryList(null)).toEqual([]);
    expect(findPrimaryList(undefined)).toEqual([]);
    expect(findPrimaryList(42)).toEqual([]);
    expect(findPrimaryList("hello")).toEqual([]);
  });

  it("extracts from tasks array", () => {
    const result = findPrimaryList({
      tasks: [
        { title: "Task 1", status: "Active" },
        { title: "Task 2", status: "Completed" },
      ],
    });
    expect(result).toEqual([
      { primary: "Task 1", secondary: "Active" },
      { primary: "Task 2", secondary: "Completed" },
    ]);
  });

  it("extracts from matches array with file+line", () => {
    const result = findPrimaryList({
      matches: [
        { file: "src/main.ts", line: 42 },
      ],
    });
    expect(result).toEqual([
      { primary: "src/main.ts", secondary: "line 42" },
    ]);
  });

  it("extracts from agents array with name+role", () => {
    const result = findPrimaryList({
      agents: [
        { name: "Hephaestus", role: "Engineer", status: "Idle" },
      ],
    });
    expect(result).toEqual([
      { primary: "Hephaestus", secondary: "Idle · Engineer" },
    ]);
  });

  it("extracts from commits array with sha+message", () => {
    const result = findPrimaryList({
      commits: [
        { sha: "abc123", message: "fix bug" },
      ],
    });
    expect(result).toEqual([
      { primary: "abc123", secondary: "fix bug" },
    ]);
  });

  it("extracts from rooms array with name", () => {
    const result = findPrimaryList({
      rooms: [
        { name: "Main Room" },
      ],
    });
    expect(result).toEqual([
      { primary: "Main Room", secondary: undefined },
    ]);
  });

  it("extracts from messages array with sender+content", () => {
    const result = findPrimaryList({
      messages: [
        { sender: "Aristotle", content: "Status update" },
      ],
    });
    expect(result).toEqual([
      { primary: "Aristotle", secondary: "Status update" },
    ]);
  });

  it("prefers first matching array key (matches > tasks > ...)", () => {
    const result = findPrimaryList({
      tasks: [{ title: "Task 1" }],
      matches: [{ file: "a.ts" }],
    });
    expect(result[0].primary).toBe("a.ts");
  });

  it("handles primitive array entries", () => {
    const result = findPrimaryList({
      tasks: ["simple string", 42],
    });
    expect(result).toEqual([
      { primary: "simple string" },
      { primary: "42" },
    ]);
  });

  it("falls back to 'Result item' when no identifying field exists", () => {
    const result = findPrimaryList({
      tasks: [{ unknownKey: "val" }],
    });
    expect(result[0].primary).toBe("Result item");
  });

  it("limits to 6 entries", () => {
    const tasks = Array.from({ length: 10 }, (_, i) => ({ title: `T${i}` }));
    const result = findPrimaryList({ tasks });
    expect(result).toHaveLength(6);
  });

  it("returns empty when no recognized array key exists", () => {
    expect(findPrimaryList({ custom: [1, 2] })).toEqual([]);
  });

  it("returns empty for empty object", () => {
    expect(findPrimaryList({})).toEqual([]);
  });

  it("concatenates multiple secondary values with ' · '", () => {
    const result = findPrimaryList({
      tasks: [{ title: "X", status: "Active", phase: "Planning", assignedTo: "Hephaestus" }],
    });
    expect(result[0].secondary).toBe("Active · Planning · Hephaestus");
  });

  it("uses id as fallback primary when no title/name/file/sender/sha", () => {
    const result = findPrimaryList({
      tasks: [{ id: "abc-123", status: "InReview" }],
    });
    expect(result[0].primary).toBe("abc-123");
  });
});

/* ------------------------------------------------------------------ */
/*  findPreviewBlock                                                   */
/* ------------------------------------------------------------------ */

describe("findPreviewBlock", () => {
  it("returns null for non-record inputs", () => {
    expect(findPreviewBlock(null)).toBeNull();
    expect(findPreviewBlock(undefined)).toBeNull();
    expect(findPreviewBlock(42)).toBeNull();
    expect(findPreviewBlock("text")).toBeNull();
  });

  it("returns content when present", () => {
    expect(findPreviewBlock({ content: "file body" })).toBe("file body");
  });

  it("returns output when content is absent", () => {
    expect(findPreviewBlock({ output: "stdout text" })).toBe("stdout text");
  });

  it("returns diff when content and output are absent", () => {
    expect(findPreviewBlock({ diff: "+line1\n-line2" })).toBe("+line1\n-line2");
  });

  it("prefers content over output and diff", () => {
    expect(findPreviewBlock({
      content: "body",
      output: "stdout",
      diff: "+line",
    })).toBe("body");
  });

  it("skips empty/whitespace-only strings", () => {
    expect(findPreviewBlock({ content: "  ", output: "real" })).toBe("real");
    expect(findPreviewBlock({ content: "", output: "", diff: "" })).toBeNull();
  });

  it("returns null when object has no preview keys", () => {
    expect(findPreviewBlock({ status: "ok", exitCode: 0 })).toBeNull();
  });

  it("returns null for empty object", () => {
    expect(findPreviewBlock({})).toBeNull();
  });

  it("ignores non-string values in preview keys", () => {
    expect(findPreviewBlock({ content: 42, output: true })).toBeNull();
  });
});
