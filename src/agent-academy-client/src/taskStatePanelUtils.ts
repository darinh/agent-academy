import type { CollaborationPhase, TaskStatus } from "./api";

export type BadgeColor = "informative" | "success" | "warning" | "important" | "danger" | "subtle";

export const PHASES: readonly CollaborationPhase[] = [
  "Intake", "Planning", "Discussion",
  "Validation", "Implementation", "FinalSynthesis",
] as const;

export function taskStatusColor(status: TaskStatus): BadgeColor {
  switch (status) {
    case "Active":             return "success";
    case "Blocked":            return "danger";
    case "AwaitingValidation": return "warning";
    case "Completed":          return "informative";
    case "Cancelled":          return "subtle";
    case "Queued": default:    return "important";
  }
}

export function workstreamColor(status: string): BadgeColor {
  switch (status) {
    case "Completed":  return "success";
    case "InProgress": return "informative";
    case "Blocked":    return "warning";
    case "Ready":      return "important";
    default:           return "subtle";
  }
}

export function phaseColor(
  phase: CollaborationPhase,
  currentPhase: CollaborationPhase,
): "informative" | "success" | "subtle" {
  const currentIdx = PHASES.indexOf(currentPhase);
  const phaseIdx = PHASES.indexOf(phase);
  if (phaseIdx < currentIdx) return "success";
  if (phaseIdx === currentIdx) return "informative";
  return "subtle";
}
