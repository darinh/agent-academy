import { describe, expect, it } from "vitest";
import {
  buildExecuteCommandRequest,
  createDefaultCommandDrafts,
  getCommandDefinition,
  validateCommandDraft,
  WEEK1_COMMANDS,
} from "../commandCatalog";

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
});
