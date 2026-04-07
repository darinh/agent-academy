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
