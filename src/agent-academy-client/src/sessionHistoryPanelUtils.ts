export { formatTimestamp } from "./panelUtils";

export function formatRelativeTime(iso: string): string {
  const ms = Date.now() - new Date(iso).getTime();
  const seconds = Math.floor(ms / 1000);
  if (seconds < 60) return "just now";
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  return `${days}d ago`;
}

export function truncateSummary(summary: string, maxLen = 120): string {
  if (summary.length <= maxLen) return summary;
  return summary.slice(0, maxLen).trimEnd() + "…";
}

export const PAGE_SIZE = 10;
