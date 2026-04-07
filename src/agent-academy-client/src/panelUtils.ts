/**
 * Shared formatting helpers used across multiple dashboard panels.
 *
 * Consolidated from ErrorsPanel, AuditLogPanel, RoomStatsPanel, UsagePanel,
 * RestartHistoryPanel, and SessionHistoryPanel to eliminate duplication.
 */

export function formatTimestamp(iso: string, includeSeconds = true): string {
  const d = new Date(iso);
  return d.toLocaleString(undefined, {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    ...(includeSeconds ? { second: "2-digit" } : {}),
  });
}

export function formatTokenCount(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`;
  return String(n);
}

export function formatCost(cost: number): string {
  if (cost === 0) return "$0.00";
  if (cost < 0.01) return `$${cost.toFixed(4)}`;
  return `$${cost.toFixed(2)}`;
}

export function errorTypeBadge(
  errorType: string,
): { color: "danger" | "warning" | "important" | "informative"; label: string } {
  switch (errorType) {
    case "authentication":
      return { color: "danger", label: "Auth" };
    case "authorization":
      return { color: "danger", label: "Authz" };
    case "quota":
      return { color: "warning", label: "Quota" };
    case "transient":
      return { color: "important", label: "Transient" };
    default:
      return { color: "informative", label: errorType };
  }
}

// ── Elapsed time formatting ──

export interface FormatElapsedOptions {
  /** Smallest time unit to display. Default: "minutes" */
  granularity?: "seconds" | "minutes";
  /** Largest time unit to display. Default: "hours" */
  maxUnit?: "hours" | "days";
  /** Label appended when endIso is null/undefined (still running). */
  runningLabel?: string;
}

/**
 * Format the elapsed time between two ISO timestamps.
 *
 * Replaces three panel-specific `formatDuration` variants:
 * - AgentSessionPanel: `formatElapsed(start, end)` — defaults
 * - TaskListPanel: `formatElapsed(start, end, { maxUnit: "days" })`
 * - RestartHistoryPanel: `formatElapsed(start, end, { granularity: "seconds", runningLabel: "(running)" })`
 */
export function formatElapsed(
  startIso: string,
  endIso?: string | null,
  options?: FormatElapsedOptions,
): string {
  const { granularity = "minutes", maxUnit = "hours", runningLabel } = options ?? {};
  const endMs = endIso ? new Date(endIso).getTime() : Date.now();
  const ms = endMs - new Date(startIso).getTime();
  const label = formatElapsedMs(ms, granularity, maxUnit);
  if (!endIso && runningLabel) return `${label} ${runningLabel}`;
  return label;
}

function formatElapsedMs(
  ms: number,
  granularity: "seconds" | "minutes",
  maxUnit: "hours" | "days",
): string {
  const seconds = Math.floor(ms / 1000);
  const minutes = Math.floor(seconds / 60);
  const hours = Math.floor(minutes / 60);

  if (granularity === "seconds") {
    if (seconds < 60) return `${seconds}s`;
    if (minutes < 60) return `${minutes}m ${seconds % 60}s`;
    return `${hours}h ${minutes % 60}m`;
  }

  // granularity === "minutes"
  if (minutes < 60) return `${minutes}m`;
  if (maxUnit === "days" && hours >= 24) {
    const days = Math.floor(hours / 24);
    return `${days}d ${hours % 24}h`;
  }
  return `${hours}h ${minutes % 60}m`;
}
