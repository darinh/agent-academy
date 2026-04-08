import type { ActivityEventType } from "./api";

export function relativeTime(iso: string): string {
  const diff = Date.now() - new Date(iso).getTime();
  const secs = Math.floor(diff / 1000);
  if (secs < 60) return "just now";
  const mins = Math.floor(secs / 60);
  if (mins < 60) return `${mins}m ago`;
  const hrs = Math.floor(mins / 60);
  if (hrs < 24) return `${hrs}h ago`;
  const days = Math.floor(hrs / 24);
  return `${days}d ago`;
}

export type SeverityColor = "informative" | "warning" | "danger";

export function severityColor(severity: "Info" | "Warning" | "Error"): SeverityColor {
  const map: Record<string, SeverityColor> = {
    Info: "informative",
    Warning: "warning",
    Error: "danger",
  };
  return map[severity] ?? "informative";
}

export type EventCategory = "agent" | "message" | "task" | "phase" | "subagent" | "room" | "other";

export function eventCategory(type: ActivityEventType): EventCategory {
  if (type.startsWith("Agent")) return "agent";
  if (type.startsWith("Message")) return "message";
  if (type.startsWith("Task")) return "task";
  if (type === "PhaseChanged") return "phase";
  if (type.startsWith("Subagent")) return "subagent";
  if (type.startsWith("Room")) return "room";
  return "other";
}
