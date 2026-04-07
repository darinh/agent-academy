import type { ChatEnvelope } from "./api";
import type { ConnectionStatus } from "./useActivityHub";

/* ── Command Result Parsing ────────────────────────────────────── */

export type { ConnectionStatus };

export interface ParsedCommandResult {
  status: "Success" | "Error" | "Denied";
  command: string;
  correlationId: string;
  error?: string;
  detail?: string;
}

export function isCommandResultMessage(content: string): boolean {
  return content.startsWith("=== COMMAND RESULTS ===");
}

export function parseCommandResults(content: string): ParsedCommandResult[] {
  const results: ParsedCommandResult[] = [];
  const lines = content.split("\n");

  let current: ParsedCommandResult | null = null;
  const detailLines: string[] = [];

  const flushCurrent = () => {
    if (current) {
      if (detailLines.length > 0) current.detail = detailLines.join("\n").trim();
      results.push(current);
      detailLines.length = 0;
    }
  };

  for (const line of lines) {
    if (line.startsWith("=== ")) continue;

    const statusMatch = line.match(/^\[(Success|Error|Denied)\]\s+(\S+)\s+\(([^)]+)\)/);
    if (statusMatch) {
      flushCurrent();
      current = {
        status: statusMatch[1] as ParsedCommandResult["status"],
        command: statusMatch[2],
        correlationId: statusMatch[3],
      };
      continue;
    }

    if (!current) continue;

    if (line.startsWith("  Error: ")) {
      current.error = line.replace("  Error: ", "");
    } else {
      detailLines.push(line.startsWith("  ") ? line.slice(2) : line);
    }
  }
  flushCurrent();

  return results;
}

/* ── Message Filtering ─────────────────────────────────────────── */

export type MessageFilter = "system" | "commands";
export const FILTER_STORAGE_KEY = "agent-academy-chat-filters";

export function loadFilters(): Set<MessageFilter> {
  try {
    const raw = localStorage.getItem(FILTER_STORAGE_KEY);
    if (raw) return new Set(JSON.parse(raw) as MessageFilter[]);
  } catch { /* ignore */ }
  return new Set();
}

export function saveFilters(filters: Set<MessageFilter>) {
  try {
    localStorage.setItem(FILTER_STORAGE_KEY, JSON.stringify([...filters]));
  } catch { /* storage unavailable — filter state lives in memory only */ }
}

export function shouldHideMessage(msg: ChatEnvelope, hidden: Set<MessageFilter>): boolean {
  if (msg.senderKind !== "System") return false;
  const isCmdResult = isCommandResultMessage(msg.content);
  if (isCmdResult && hidden.has("commands")) return true;
  if (!isCmdResult && hidden.has("system")) return true;
  return false;
}

/* ── Connection Status Maps ────────────────────────────────────── */

export const STATUS_LABELS: Record<ConnectionStatus, string> = {
  connected: "Live — real-time updates active",
  connecting: "Connecting to live updates…",
  reconnecting: "Reconnecting…",
  disconnected: "Disconnected — falling back to polling",
};

export const STATUS_COLORS: Record<ConnectionStatus, string> = {
  connected: "#34d399",
  connecting: "#fbbf24",
  reconnecting: "#fbbf24",
  disconnected: "#f87171",
};

/* ── Message Length Threshold ──────────────────────────────────── */

export const MESSAGE_LENGTH_THRESHOLD = 300;
