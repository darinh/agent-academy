import type { CollaborationPhase } from "./api";
import type { BadgeColor } from "./V3Badge";

export type TimeRange = 24 | 168 | 720 | undefined;

export function phaseColor(phase: CollaborationPhase): BadgeColor {
  const map: Record<CollaborationPhase, BadgeColor> = {
    Intake: "info",
    Planning: "warn",
    Discussion: "active",
    Validation: "review",
    Implementation: "ok",
    FinalSynthesis: "muted",
  };
  return map[phase];
}

export const TIME_RANGES: { label: string; value: TimeRange }[] = [
  { label: "24h", value: 24 },
  { label: "7d", value: 168 },
  { label: "30d", value: 720 },
  { label: "All", value: undefined },
];

export const TIME_RANGE_KEY = "agent-academy-dashboard-timerange";

export function loadTimeRange(): TimeRange {
  try {
    const raw = localStorage.getItem(TIME_RANGE_KEY);
    if (raw === "all") return undefined;
    const n = Number(raw);
    if (n === 24 || n === 168 || n === 720) return n;
  } catch { /* ignore */ }
  return undefined;
}

export function saveTimeRange(v: TimeRange) {
  try {
    localStorage.setItem(TIME_RANGE_KEY, v == null ? "all" : String(v));
  } catch { /* ignore */ }
}
