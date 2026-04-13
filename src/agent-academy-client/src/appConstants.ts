import { webDarkTheme } from "@fluentui/react-components";
import type { Theme } from "@fluentui/react-components";
import type { ActivityEvent, ActivityEventType } from "./api";

/** View title lookup for header bar. */
export const VIEW_TITLES: Record<string, { title: string; meta: string }> = {
  chat: { title: "Conversation", meta: "Live room stream" },
  tasks: { title: "Tasks", meta: "Delivery queue" },
  plan: { title: "Room Plan", meta: "" },
  commands: { title: "Command Deck", meta: "" },
  sprint: { title: "Sprint", meta: "Active iteration" },
  timeline: { title: "Activity Timeline", meta: "" },
  dashboard: { title: "Metrics", meta: "System telemetry" },
  overview: { title: "Overview", meta: "Room state" },
  directMessages: { title: "Direct Messages", meta: "" },
  search: { title: "Search", meta: "Find messages & tasks" },
  memories: { title: "Agent Memory", meta: "Knowledge base" },
  digests: { title: "Learning Digests", meta: "Knowledge synthesis" },
};

export const TOAST_EVENT_TYPES: ReadonlySet<ActivityEventType> = new Set([
  "AgentErrorOccurred",
  "AgentWarningOccurred",
  "SubagentFailed",
  "AgentFinished",
  "TaskCreated",
  "PhaseChanged",
  "SubagentCompleted",
]);

export function toastIntent(evt: ActivityEvent): "error" | "warning" | "info" {
  if (evt.severity === "Error" || evt.type === "AgentErrorOccurred" || evt.type === "SubagentFailed") return "error";
  if (evt.severity === "Warning" || evt.type === "AgentWarningOccurred") return "warning";
  return "info";
}

/** Override Fluent UI's 14px/20px defaults to match v3 mockup's 13px/1.5 base. */
export const matrixTheme: Theme = {
  ...webDarkTheme,
  fontSizeBase200: "11px",
  fontSizeBase300: "13px",
  fontSizeBase400: "14px",
  fontSizeBase500: "16px",
  lineHeightBase200: "16px",
  lineHeightBase300: "18px",
  lineHeightBase400: "20px",
  lineHeightBase500: "22px",
};
