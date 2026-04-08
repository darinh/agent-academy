import type { CollaborationPhase, TaskStatus } from "./api";
import type { BadgeColor } from "./V3Badge";

export const PHASES: readonly CollaborationPhase[] = [
  "Intake", "Planning", "Discussion",
  "Validation", "Implementation", "FinalSynthesis",
] as const;

export function taskStatusBadge(status: TaskStatus): BadgeColor {
  switch (status) {
    case "Active":             return "active";
    case "Blocked":            return "err";
    case "AwaitingValidation": return "warn";
    case "Completed":          return "done";
    case "Cancelled":          return "cancel";
    case "Queued": default:    return "info";
  }
}

export function workstreamBadge(status: string): BadgeColor {
  switch (status) {
    case "Completed":  return "done";
    case "InProgress": return "active";
    case "Blocked":    return "warn";
    case "Ready":      return "info";
    default:           return "muted";
  }
}

export function phaseBadge(
  phase: CollaborationPhase,
  currentPhase: CollaborationPhase,
): BadgeColor {
  const currentIdx = PHASES.indexOf(currentPhase);
  const phaseIdx = PHASES.indexOf(phase);
  if (phaseIdx < currentIdx) return "done";
  if (phaseIdx === currentIdx) return "active";
  return "muted";
}
