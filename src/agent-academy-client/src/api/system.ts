import type {
  ProviderStatus,
  ProviderConfigSchema,
  ConfigureResponse,
  ConnectResponse,
  TestNotificationResponse,
  DmThreadSummary,
  DmMessage,
  SearchResults,
  SearchScope,
  WorktreeStatusSnapshot,
} from "./types";
import { apiUrl, request, downloadFile } from "./core";

// ── Notification Providers ─────────────────────────────────────────────

const NOTIF_BASE = "/api/notifications";

export function getNotificationProviders(): Promise<ProviderStatus[]> {
  return request<ProviderStatus[]>(apiUrl(`${NOTIF_BASE}/providers`));
}

export function getProviderSchema(id: string): Promise<ProviderConfigSchema> {
  return request<ProviderConfigSchema>(apiUrl(`${NOTIF_BASE}/providers/${encodeURIComponent(id)}/schema`));
}

export function configureProvider(
  id: string,
  settings: Record<string, string>,
): Promise<ConfigureResponse> {
  return request<ConfigureResponse>(
    apiUrl(`${NOTIF_BASE}/providers/${encodeURIComponent(id)}/configure`),
    { method: "POST", body: JSON.stringify(settings) },
  );
}

export function connectProvider(id: string): Promise<ConnectResponse> {
  return request<ConnectResponse>(
    apiUrl(`${NOTIF_BASE}/providers/${encodeURIComponent(id)}/connect`),
    { method: "POST" },
  );
}

export function disconnectProvider(id: string): Promise<ConnectResponse> {
  return request<ConnectResponse>(
    apiUrl(`${NOTIF_BASE}/providers/${encodeURIComponent(id)}/disconnect`),
    { method: "POST" },
  );
}

export function testNotification(): Promise<TestNotificationResponse> {
  return request<TestNotificationResponse>(apiUrl(`${NOTIF_BASE}/test`), { method: "POST" });
}

// ── Direct Messaging ───────────────────────────────────────────────────

export function getDmThreads(): Promise<DmThreadSummary[]> {
  return request<DmThreadSummary[]>(apiUrl("/api/dm/threads"));
}

export function getDmThreadMessages(agentId: string): Promise<DmMessage[]> {
  return request<DmMessage[]>(
    apiUrl(`/api/dm/threads/${encodeURIComponent(agentId)}`),
  );
}

export function sendDmToAgent(
  agentId: string,
  message: string,
): Promise<DmMessage> {
  return request<DmMessage>(
    apiUrl(`/api/dm/threads/${encodeURIComponent(agentId)}`),
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ message }),
    },
  );
}

// ── Search ─────────────────────────────────────────────────────────────

export function searchWorkspace(
  query: string,
  options?: { scope?: SearchScope; messageLimit?: number; taskLimit?: number },
): Promise<SearchResults> {
  const params = new URLSearchParams({ q: query });
  if (options?.scope) params.set("scope", options.scope);
  if (options?.messageLimit != null) params.set("messageLimit", String(options.messageLimit));
  if (options?.taskLimit != null) params.set("taskLimit", String(options.taskLimit));
  return request<SearchResults>(apiUrl(`/api/search?${params}`));
}

// ── DM Conversation Export ──────────────────────────────────────────────

export function exportDmMessages(agentId: string, format: "json" | "markdown" = "json"): Promise<void> {
  const params = new URLSearchParams({ format });
  const ext = format === "markdown" ? "md" : "json";
  return downloadFile(apiUrl(`/api/export/dm/${encodeURIComponent(agentId)}/messages?${params}`), `dm-export.${ext}`);
}

// ── Worktrees ────────────────────────────────────────────────────────────

export function getWorktreeStatus(): Promise<WorktreeStatusSnapshot[]> {
  return request<WorktreeStatusSnapshot[]>(apiUrl("/api/worktrees"));
}
