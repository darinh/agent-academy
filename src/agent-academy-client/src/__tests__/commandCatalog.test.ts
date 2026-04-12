import { describe, expect, it } from "vitest";
import {
  buildExecuteCommandRequest,
  createDefaultCommandDrafts,
  fromServerMetadata,
  getCommandDefinition,
  validateCommandDraft,
  WEEK1_COMMANDS,
} from "../commandCatalog";
import type { CommandMetadata } from "../api";

describe("command catalog", () => {
  it("documents the full Week 1 allowlist", () => {
    expect(WEEK1_COMMANDS).toHaveLength(11);
    expect(WEEK1_COMMANDS.map((command) => command.command)).toEqual([
      "READ_FILE",
      "SEARCH_CODE",
      "LIST_ROOMS",
      "LIST_AGENTS",
      "LIST_TASKS",
      "SHOW_DIFF",
      "GIT_LOG",
      "SHOW_REVIEW_QUEUE",
      "ROOM_HISTORY",
      "RUN_BUILD",
      "RUN_TESTS",
    ]);
  });

  it("prepares default drafts from command field defaults", () => {
    const drafts = createDefaultCommandDrafts();

    expect(drafts.RUN_TESTS.scope).toBe("all");
    expect(drafts.READ_FILE.path).toBe("");
  });

  it("flags missing required fields before execution", () => {
    const definition = getCommandDefinition("READ_FILE");

    expect(validateCommandDraft(definition, {})).toEqual(["Path is required."]);
    expect(validateCommandDraft(definition, { path: "src/App.tsx" })).toEqual([]);
  });

  it("builds scalar-only command payloads and trims empty fields", () => {
    const definition = getCommandDefinition("GIT_LOG");

    expect(buildExecuteCommandRequest(definition, {
      count: " 12 ",
      since: " ",
      file: " src/App.tsx ",
    })).toEqual({
      command: "GIT_LOG",
      args: {
        count: "12",
        file: "src/App.tsx",
      },
    });
  });

  it("creates default drafts from custom command list", () => {
    const custom = [
      {
        command: "CUSTOM_CMD",
        title: "Custom",
        category: "operations" as const,
        description: "A custom command",
        detail: "Detail text",
        isAsync: false,
        isDestructive: false,
        destructiveWarning: null,
        fields: [
          { name: "input", label: "Input", kind: "text" as const, description: "Some input", defaultValue: "hello" },
        ],
      },
    ];

    const drafts = createDefaultCommandDrafts(custom);
    expect(drafts.CUSTOM_CMD).toEqual({ input: "hello" });
    expect(Object.keys(drafts)).toEqual(["CUSTOM_CMD"]);
  });

  it("converts server metadata to local definitions", () => {
    const serverData: CommandMetadata[] = [
      {
        command: "READ_FILE",
        title: "Read file",
        category: "code",
        description: "Read a file",
        detail: "Detailed description",
        isAsync: false,
        fields: [
          { name: "path", label: "Path", kind: "text", description: "File path", required: true, placeholder: "src/main.ts" },
          { name: "startLine", label: "Start", kind: "number", description: "Start line" },
        ],
      },
      {
        command: "RUN_BUILD",
        title: "Build",
        category: "operations",
        description: "Run build",
        detail: "Async build",
        isAsync: true,
        fields: [],
      },
    ];

    const result = fromServerMetadata(serverData);

    expect(result).toHaveLength(2);
    expect(result[0].command).toBe("READ_FILE");
    expect(result[0].fields).toHaveLength(2);
    expect(result[0].fields[0].required).toBe(true);
    expect(result[0].fields[0].placeholder).toBe("src/main.ts");
    expect(result[0].fields[1].required).toBe(false);
    expect(result[0].fields[1].placeholder).toBeUndefined();
    expect(result[1].isAsync).toBe(true);
    expect(result[1].fields).toHaveLength(0);
  });

  it("handles missing optional fields in server metadata gracefully", () => {
    const serverData: CommandMetadata[] = [
      {
        command: "MINIMAL",
        title: "Minimal",
        category: "workspace",
        description: "Minimal command",
        detail: "No fields",
        isAsync: false,
        fields: [
          { name: "arg1", label: "Arg", kind: "text", description: "An arg" },
        ],
      },
    ];

    const result = fromServerMetadata(serverData);
    expect(result[0].fields[0].required).toBe(false);
    expect(result[0].fields[0].defaultValue).toBeUndefined();
    expect(result[0].fields[0].placeholder).toBeUndefined();
  });

  it("maps isDestructive and destructiveWarning from server metadata", () => {
    const serverData: CommandMetadata[] = [
      {
        command: "CLOSE_ROOM",
        title: "Close room",
        category: "operations",
        description: "Archive a room.",
        detail: "Permanent.",
        isAsync: false,
        isDestructive: true,
        destructiveWarning: "This archives the room permanently.",
        fields: [],
      },
      {
        command: "LIST_ROOMS",
        title: "List rooms",
        category: "workspace",
        description: "List rooms.",
        detail: "Safe.",
        isAsync: false,
        fields: [],
      },
    ];

    const result = fromServerMetadata(serverData);
    expect(result[0].isDestructive).toBe(true);
    expect(result[0].destructiveWarning).toBe("This archives the room permanently.");
    expect(result[1].isDestructive).toBe(false);
    expect(result[1].destructiveWarning).toBeNull();
  });

  it("includes confirm=true in args when confirm option is set", () => {
    const definition = getCommandDefinition("LIST_ROOMS");

    const withoutConfirm = buildExecuteCommandRequest(definition, {});
    expect(withoutConfirm.args).toBeUndefined();

    const withConfirm = buildExecuteCommandRequest(definition, {}, { confirm: true });
    expect(withConfirm.args).toEqual({ confirm: "true" });
  });

  it("merges confirm flag with existing args", () => {
    const definition = getCommandDefinition("READ_FILE");

    const result = buildExecuteCommandRequest(
      definition,
      { path: "test.ts" },
      { confirm: true },
    );
    expect(result.args).toEqual({ path: "test.ts", confirm: "true" });
  });

  it("includes isDestructive in WEEK1_COMMANDS entries", () => {
    for (const cmd of WEEK1_COMMANDS) {
      expect(cmd.isDestructive).toBe(false);
      expect(cmd.destructiveWarning).toBeNull();
    }
  });
});
