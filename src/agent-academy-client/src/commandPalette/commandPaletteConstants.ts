import type { CommandCategory } from "../commandCatalog";

export const POLL_INTERVAL_MS = 2500;

export const CATEGORY_LABELS: Record<CommandCategory, string> = {
  workspace: "Workspace",
  code: "Code",
  git: "Git",
  operations: "Operations",
};

export const CATEGORY_ORDER: CommandCategory[] = ["workspace", "code", "git", "operations"];
