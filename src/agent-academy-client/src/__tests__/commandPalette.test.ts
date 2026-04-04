import { describe, expect, it, vi, beforeEach } from "vitest";

// Minimal DOM stubs for Fluent UI
vi.stubGlobal("document", {
  createElement: () => ({
    style: {},
    setAttribute: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    appendChild: () => {},
    removeChild: () => {},
    contains: () => false,
    querySelectorAll: () => [],
    getBoundingClientRect: () => ({ top: 0, left: 0, width: 0, height: 0, right: 0, bottom: 0 }),
  }),
  createTextNode: () => ({}),
  body: { appendChild: () => {}, removeChild: () => {} },
  head: { appendChild: () => {}, removeChild: () => {} },
  documentElement: { style: {} },
  addEventListener: () => {},
  removeEventListener: () => {},
  querySelector: () => null,
  querySelectorAll: () => [],
});

// Since CommandPalette is a React component with heavy Fluent UI dependencies,
// we test the underlying logic functions it uses from commandCatalog instead.
import {
  createDefaultCommandDrafts,
  validateCommandDraft,
  buildExecuteCommandRequest,
  fromServerMetadata,
  WEEK1_COMMANDS,
} from "../commandCatalog";
import type { HumanCommandDefinition } from "../commandCatalog";

describe("CommandPalette logic", () => {
  const MOCK_COMMANDS: HumanCommandDefinition[] = [
    {
      command: "READ_FILE",
      title: "Read file",
      category: "code",
      description: "Read a file",
      detail: "Detail",
      isAsync: false,
      fields: [
        { name: "path", label: "Path", kind: "text", description: "File path", required: true },
        { name: "startLine", label: "Start", kind: "number", description: "Line" },
      ],
    },
    {
      command: "RUN_BUILD",
      title: "Run build",
      category: "operations",
      description: "Run build",
      detail: "Detail",
      isAsync: true,
      fields: [],
    },
    {
      command: "LIST_ROOMS",
      title: "List rooms",
      category: "workspace",
      description: "List rooms",
      detail: "Detail",
      isAsync: false,
      fields: [],
    },
  ];

  describe("search filtering", () => {
    function filterCommands(commands: readonly HumanCommandDefinition[], query: string) {
      const q = query.toLowerCase().trim();
      if (!q) return [...commands];
      return commands.filter(
        (c) =>
          c.title.toLowerCase().includes(q) ||
          c.command.toLowerCase().includes(q) ||
          c.description.toLowerCase().includes(q) ||
          c.category.toLowerCase().includes(q),
      );
    }

    it("returns all commands with empty query", () => {
      const result = filterCommands(MOCK_COMMANDS, "");
      expect(result).toHaveLength(3);
    });

    it("filters by title", () => {
      const result = filterCommands(MOCK_COMMANDS, "read");
      expect(result).toHaveLength(1);
      expect(result[0].command).toBe("READ_FILE");
    });

    it("filters by command name", () => {
      const result = filterCommands(MOCK_COMMANDS, "RUN_BUILD");
      expect(result).toHaveLength(1);
      expect(result[0].command).toBe("RUN_BUILD");
    });

    it("filters by category", () => {
      const result = filterCommands(MOCK_COMMANDS, "workspace");
      expect(result).toHaveLength(1);
      expect(result[0].command).toBe("LIST_ROOMS");
    });

    it("returns empty for no matches", () => {
      const result = filterCommands(MOCK_COMMANDS, "zzzzz");
      expect(result).toHaveLength(0);
    });

    it("is case-insensitive", () => {
      const result = filterCommands(MOCK_COMMANDS, "BUILD");
      expect(result).toHaveLength(1);
      expect(result[0].command).toBe("RUN_BUILD");
    });
  });

  describe("category grouping", () => {
    const CATEGORY_ORDER = ["workspace", "code", "git", "operations"] as const;

    function groupByCategory(commands: readonly HumanCommandDefinition[]) {
      const groups: { category: string; items: HumanCommandDefinition[] }[] = [];
      for (const cat of CATEGORY_ORDER) {
        const items = commands.filter((c) => c.category === cat);
        if (items.length > 0) groups.push({ category: cat, items });
      }
      return groups;
    }

    it("groups commands by category in order", () => {
      const groups = groupByCategory(MOCK_COMMANDS);
      expect(groups.map((g) => g.category)).toEqual(["workspace", "code", "operations"]);
    });

    it("omits empty categories", () => {
      const groups = groupByCategory(MOCK_COMMANDS);
      expect(groups.find((g) => g.category === "git")).toBeUndefined();
    });
  });

  describe("validation integration", () => {
    it("rejects missing required fields", () => {
      const cmd = MOCK_COMMANDS[0]; // READ_FILE — path is required
      const errors = validateCommandDraft(cmd, { path: "", startLine: "" });
      expect(errors).toHaveLength(1);
      expect(errors[0]).toContain("Path");
    });

    it("passes with required fields filled", () => {
      const cmd = MOCK_COMMANDS[0];
      const errors = validateCommandDraft(cmd, { path: "src/main.ts", startLine: "" });
      expect(errors).toHaveLength(0);
    });
  });

  describe("command execution request building", () => {
    it("builds request with only non-empty fields", () => {
      const cmd = MOCK_COMMANDS[0];
      const req = buildExecuteCommandRequest(cmd, { path: "src/main.ts", startLine: "" });
      expect(req.command).toBe("READ_FILE");
      expect(req.args).toEqual({ path: "src/main.ts" });
    });

    it("builds request with no args for fieldless commands", () => {
      const cmd = MOCK_COMMANDS[1]; // RUN_BUILD — no fields
      const req = buildExecuteCommandRequest(cmd, {});
      expect(req.command).toBe("RUN_BUILD");
      expect(req.args).toBeUndefined();
    });
  });

  describe("fromServerMetadata", () => {
    it("preserves async flag", () => {
      const result = fromServerMetadata([
        {
          command: "RUN_BUILD",
          title: "Run build",
          category: "operations",
          description: "Build",
          detail: "Detail",
          isAsync: true,
          fields: [],
        },
      ]);
      expect(result[0].isAsync).toBe(true);
    });
  });
});
