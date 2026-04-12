import type {
  UsageSummary,
  LlmUsageRecord,
  AgentUsageSummary,
  ErrorSummary,
  ErrorRecord,
  AgentAnalyticsSummary,
  AgentAnalyticsDetail,
} from "./types";
import { apiUrl, request, downloadFile } from "./core";

// ── Usage / LLM Tracking ──────────────────────────────────────────────

export function getGlobalUsage(hoursBack?: number): Promise<UsageSummary> {
  const qs = hoursBack != null ? `?hoursBack=${hoursBack}` : "";
  return request<UsageSummary>(apiUrl(`/api/usage${qs}`));
}

export function getGlobalUsageRecords(agentId?: string, limit = 50): Promise<LlmUsageRecord[]> {
  const params = new URLSearchParams();
  if (agentId) params.set("agentId", agentId);
  if (limit !== 50) params.set("limit", String(limit));
  const qs = params.toString();
  return request<LlmUsageRecord[]>(apiUrl(`/api/usage/records${qs ? `?${qs}` : ""}`));
}

export function getRoomUsage(roomId: string): Promise<UsageSummary> {
  return request<UsageSummary>(apiUrl(`/api/rooms/${roomId}/usage`));
}

export function getRoomUsageByAgent(roomId: string): Promise<AgentUsageSummary[]> {
  return request<AgentUsageSummary[]>(apiUrl(`/api/rooms/${roomId}/usage/agents`));
}

// ── Errors / Agent Error Tracking ─────────────────────────────────────

export function getGlobalErrorSummary(hoursBack?: number): Promise<ErrorSummary> {
  const qs = hoursBack != null ? `?hoursBack=${hoursBack}` : "";
  return request<ErrorSummary>(apiUrl(`/api/errors${qs}`));
}

export function getGlobalErrorRecords(agentId?: string, hoursBack?: number, limit = 50): Promise<ErrorRecord[]> {
  const params = new URLSearchParams();
  if (agentId) params.set("agentId", agentId);
  if (hoursBack != null) params.set("hoursBack", String(hoursBack));
  if (limit !== 50) params.set("limit", String(limit));
  const qs = params.toString();
  return request<ErrorRecord[]>(apiUrl(`/api/errors/records${qs ? `?${qs}` : ""}`));
}

export function getRoomErrors(roomId: string, limit = 50): Promise<ErrorRecord[]> {
  return request<ErrorRecord[]>(apiUrl(`/api/rooms/${roomId}/errors?limit=${limit}`));
}

// ── Agent Analytics ───────────────────────────────────────────────────

export function getAgentAnalytics(hoursBack?: number): Promise<AgentAnalyticsSummary> {
  const qs = hoursBack != null ? `?hoursBack=${hoursBack}` : "";
  return request<AgentAnalyticsSummary>(apiUrl(`/api/analytics/agents${qs}`));
}

export function getAgentAnalyticsDetail(agentId: string, hoursBack?: number): Promise<AgentAnalyticsDetail> {
  const qs = hoursBack != null ? `?hoursBack=${hoursBack}` : "";
  return request<AgentAnalyticsDetail>(apiUrl(`/api/analytics/agents/${encodeURIComponent(agentId)}${qs}`));
}

// ── Export / Download ──────────────────────────────────────────────────

export function exportAgentAnalytics(hoursBack?: number, format: "csv" | "json" = "csv"): Promise<void> {
  const params = new URLSearchParams({ format });
  if (hoursBack != null) params.set("hoursBack", String(hoursBack));
  return downloadFile(apiUrl(`/api/export/agents?${params}`), `agent-analytics.${format}`);
}

export function exportUsageRecords(
  options?: { hoursBack?: number; agentId?: string; limit?: number; format?: "csv" | "json" },
): Promise<void> {
  const format = options?.format ?? "csv";
  const params = new URLSearchParams({ format });
  if (options?.hoursBack != null) params.set("hoursBack", String(options.hoursBack));
  if (options?.agentId) params.set("agentId", options.agentId);
  if (options?.limit != null) params.set("limit", String(options.limit));
  return downloadFile(apiUrl(`/api/export/usage?${params}`), `usage-records.${format}`);
}
