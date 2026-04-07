import type { CommandExecutionStatus } from "./api";

export const POLL_INTERVAL_MS = 2500;
export const MAX_HISTORY_ITEMS = 10;

export function badgeColorForCategory(category: string): "informative" | "success" | "warning" | "important" {
  switch (category) {
    case "code":
      return "informative";
    case "git":
      return "warning";
    case "operations":
      return "important";
    default:
      return "success";
  }
}

export function badgeColorForStatus(status: CommandExecutionStatus): "success" | "warning" | "danger" | "important" {
  switch (status) {
    case "completed":
      return "success";
    case "pending":
      return "warning";
    case "denied":
      return "important";
    default:
      return "danger";
  }
}

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export function summarizeResult(result: unknown): Array<[string, string]> {
  if (!isRecord(result)) {
    return [];
  }

  const ignoredKeys = new Set(["content", "output", "diff", "matches", "tasks", "rooms", "agents", "commits", "messages"]);

  return Object.entries(result)
    .filter(([key, value]) => !ignoredKeys.has(key) && (typeof value === "string" || typeof value === "number" || typeof value === "boolean"))
    .slice(0, 6)
    .map(([key, value]) => [readableLabel(key), String(value)]);
}

export function findPrimaryList(result: unknown): Array<{ primary: string; secondary?: string }> {
  if (!isRecord(result)) {
    return [];
  }

  const candidate = ["matches", "tasks", "rooms", "agents", "commits", "messages"]
    .map((key) => result[key])
    .find(Array.isArray);

  if (!Array.isArray(candidate)) {
    return [];
  }

  return candidate.slice(0, 6).map((entry) => {
    if (isRecord(entry)) {
      const primary = String(
        entry.title ?? entry.name ?? entry.file ?? entry.sender ?? entry.sha ?? entry.id ?? "Result item",
      );
      const secondaryValues = [
        entry.status,
        entry.phase,
        entry.role,
        entry.text,
        entry.message,
        entry.content,
        entry.assignedTo,
        entry.line ? `line ${entry.line}` : undefined,
      ].filter((value): value is unknown => value != null);

      return {
        primary,
        secondary: secondaryValues.length > 0 ? secondaryValues.map((value) => String(value)).join(" · ") : undefined,
      };
    }

    return {
      primary: String(entry),
    };
  });
}

export function findPreviewBlock(result: unknown): string | null {
  if (!isRecord(result)) {
    return null;
  }

  for (const key of ["content", "output", "diff"]) {
    const value = result[key];
    if (typeof value === "string" && value.trim()) {
      return value;
    }
  }

  return null;
}

export function readableLabel(key: string): string {
  return key
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/^./, (char) => char.toUpperCase());
}
