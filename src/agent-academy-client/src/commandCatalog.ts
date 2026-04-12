import type { CommandMetadata, ExecuteCommandRequest } from "./api";

export type CommandFieldKind = "text" | "textarea" | "number";
export type CommandCategory = "workspace" | "code" | "git" | "operations";

export interface CommandFieldDefinition {
  name: string;
  label: string;
  kind: CommandFieldKind;
  description: string;
  placeholder?: string;
  required?: boolean;
  defaultValue?: string;
}

export interface HumanCommandDefinition {
  command: string;
  title: string;
  category: CommandCategory;
  description: string;
  detail: string;
  isAsync: boolean;
  fields: readonly CommandFieldDefinition[];
  isDestructive: boolean;
  destructiveWarning: string | null;
}

/**
 * Hardcoded fallback catalog — used when GET /api/commands/metadata is unavailable.
 * Intentionally limited to the original 11 commands; the server registry is the
 * single source of truth for the full set including room management commands.
 */
export const WEEK1_COMMANDS: readonly HumanCommandDefinition[] = [
  {
    command: "READ_FILE",
    title: "Read file",
    category: "code",
    description: "Inspect a repository file with optional line windows.",
    detail: "Best for source spelunking, spec checks, and quick spot reads without leaving the workspace.",
    isAsync: false,
    isDestructive: false,
    destructiveWarning: null,
    fields: [
      {
        name: "path",
        label: "Path",
        kind: "text",
        description: "Repository-relative file path.",
        placeholder: "src/AgentAcademy.Server/Program.cs",
        required: true,
      },
      {
        name: "startLine",
        label: "Start line",
        kind: "number",
        description: "Optional first line to include.",
        placeholder: "1",
      },
      {
        name: "endLine",
        label: "End line",
        kind: "number",
        description: "Optional final line to include.",
        placeholder: "120",
      },
    ],
  },
  {
    command: "SEARCH_CODE",
    title: "Search code",
    category: "code",
    description: "Run a focused repository text search.",
    detail: "Supports optional subpath and glob filters for tighter scans without exposing raw shell access.",
    isAsync: false,
    isDestructive: false,
    destructiveWarning: null,
    fields: [
      {
        name: "query",
        label: "Query",
        kind: "text",
        description: "Literal or regex-like grep pattern.",
        placeholder: "CommandController",
        required: true,
      },
      {
        name: "path",
        label: "Path filter",
        kind: "text",
        description: "Optional subdirectory inside the repo.",
        placeholder: "src/agent-academy-client/src",
      },
      {
        name: "glob",
        label: "Glob filter",
        kind: "text",
        description: "Optional include glob passed to grep.",
        placeholder: "*.tsx",
      },
    ],
  },
  {
    command: "LIST_ROOMS",
    title: "List rooms",
    category: "workspace",
    description: "Snapshot active rooms, phases, status, and participant counts.",
    detail: "Useful when triaging the collaboration state before jumping into a room.",
    isAsync: false,
    isDestructive: false,
    destructiveWarning: null,
    fields: [],
  },
  {
    command: "LIST_AGENTS",
    title: "List agents",
    category: "workspace",
    description: "Inspect agent locations, roles, and current state.",
    detail: "Shows where the team is parked without needing planner-only tooling.",
    isAsync: false,
    isDestructive: false,
    destructiveWarning: null,
    fields: [],
  },
  {
    command: "LIST_TASKS",
    title: "List tasks",
    category: "workspace",
    description: "Review all tasks, or filter by status or assignee.",
    detail: "The fastest way to check the queue from the UI before diving into a branch or room.",
    isAsync: false,
    isDestructive: false,
    destructiveWarning: null,
    fields: [
      {
        name: "status",
        label: "Status",
        kind: "text",
        description: "Optional task status filter.",
        placeholder: "Active",
      },
      {
        name: "assignee",
        label: "Assignee",
        kind: "text",
        description: "Optional agent id or name.",
        placeholder: "Athena",
      },
    ],
  },
  {
    command: "SHOW_DIFF",
    title: "Show diff",
    category: "git",
    description: "Inspect uncommitted changes or diff against a branch.",
    detail: "Returns a trimmed git diff summary so humans can review work without opening a terminal.",
    isAsync: false,
    isDestructive: false,
    destructiveWarning: null,
    fields: [
      {
        name: "branch",
        label: "Branch",
        kind: "text",
        description: "Optional branch to diff against.",
        placeholder: "develop",
      },
    ],
  },
  {
    command: "GIT_LOG",
    title: "Git log",
    category: "git",
    description: "Browse recent commits with optional file or date filtering.",
    detail: "Good for reconstructing recent moves before asking an agent to continue the work.",
    isAsync: false,
    isDestructive: false,
    destructiveWarning: null,
    fields: [
      {
        name: "count",
        label: "Count",
        kind: "number",
        description: "Optional limit, capped by the backend.",
        placeholder: "20",
      },
      {
        name: "since",
        label: "Since",
        kind: "text",
        description: "Optional git-compatible since expression.",
        placeholder: "2 days ago",
      },
      {
        name: "file",
        label: "File",
        kind: "text",
        description: "Optional file path filter.",
        placeholder: "src/agent-academy-client/src/App.tsx",
      },
    ],
  },
  {
    command: "SHOW_REVIEW_QUEUE",
    title: "Review queue",
    category: "workspace",
    description: "See tasks waiting on review or validation.",
    detail: "A fast reviewer-focused queue without exposing task mutation commands.",
    isAsync: false,
    isDestructive: false,
    destructiveWarning: null,
    fields: [],
  },
  {
    command: "ROOM_HISTORY",
    title: "Room history",
    category: "workspace",
    description: "Load recent messages from any room without navigating there.",
    detail: "Use this to grab context before entering a room or to review archived conversations.",
    isAsync: false,
    isDestructive: false,
    destructiveWarning: null,
    fields: [
      {
        name: "roomId",
        label: "Room ID",
        kind: "text",
        description: "Target room identifier.",
        placeholder: "agent-academy-main",
        required: true,
      },
      {
        name: "count",
        label: "Message count",
        kind: "number",
        description: "Optional number of messages to return.",
        placeholder: "20",
      },
    ],
  },
  {
    command: "RUN_BUILD",
    title: "Run build",
    category: "operations",
    description: "Kick off a backend build and poll for the result.",
    detail: "Async on purpose so the UI stays responsive while the server serializes build access.",
    isAsync: true,
    isDestructive: false,
    destructiveWarning: null,
    fields: [],
  },
  {
    command: "RUN_TESTS",
    title: "Run tests",
    category: "operations",
    description: "Launch the test suite with an optional scope hint.",
    detail: "Supports all, backend, frontend, or custom file filters with the backend polling contract.",
    isAsync: true,
    isDestructive: false,
    destructiveWarning: null,
    fields: [
      {
        name: "scope",
        label: "Scope",
        kind: "text",
        description: "Optional scope: all, backend, frontend, or file:<filter>.",
        placeholder: "frontend",
        defaultValue: "all",
      },
    ],
  },
] as const;

const COMMAND_MAP = new Map(WEEK1_COMMANDS.map((command) => [command.command, command]));

export function getCommandDefinition(command: string): HumanCommandDefinition {
  const definition = COMMAND_MAP.get(command);
  if (!definition) {
    throw new Error(`Unknown command: ${command}`);
  }

  return definition;
}

export function createDefaultCommandDrafts(
  commands: readonly HumanCommandDefinition[] = WEEK1_COMMANDS,
): Record<string, Record<string, string>> {
  return commands.reduce<Record<string, Record<string, string>>>((acc, definition) => {
    acc[definition.command] = definition.fields.reduce<Record<string, string>>((fieldAcc, field) => {
      fieldAcc[field.name] = field.defaultValue ?? "";
      return fieldAcc;
    }, {});
    return acc;
  }, {});
}

export function validateCommandDraft(
  definition: HumanCommandDefinition,
  draft: Record<string, string>,
): string[] {
  return definition.fields
    .filter((field) => field.required)
    .filter((field) => !(draft[field.name] ?? "").trim())
    .map((field) => `${field.label} is required.`);
}

export function buildExecuteCommandRequest(
  definition: HumanCommandDefinition,
  draft: Record<string, string>,
  options?: { confirm?: boolean },
): ExecuteCommandRequest {
  const args: Record<string, string> = {};

  for (const field of definition.fields) {
    const rawValue = draft[field.name] ?? "";
    const trimmedValue = rawValue.trim();
    if (!trimmedValue) {
      continue;
    }

    args[field.name] = trimmedValue;
  }

  if (options?.confirm) {
    args.confirm = "true";
  }

  return {
    command: definition.command,
    args: Object.keys(args).length > 0 ? args : undefined,
  };
}

/** Convert server metadata response into local command definitions. */
export function fromServerMetadata(items: readonly CommandMetadata[]): HumanCommandDefinition[] {
  return items.map((item) => ({
    command: item.command,
    title: item.title,
    category: (item.category ?? "operations") as CommandCategory,
    description: item.description,
    detail: item.detail,
    isAsync: item.isAsync,
    isDestructive: item.isDestructive ?? false,
    destructiveWarning: item.destructiveWarning ?? null,
    fields: (item.fields ?? []).map((f) => ({
      name: f.name,
      label: f.label,
      kind: (f.kind ?? "text") as CommandFieldKind,
      description: f.description,
      placeholder: f.placeholder ?? undefined,
      required: f.required ?? false,
      defaultValue: f.defaultValue ?? undefined,
    })),
  }));
}
