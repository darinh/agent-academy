import type {
  WorkspaceOverview,
  InstanceHealthResult,
  WorkspaceMeta,
  ProjectScanResult,
  OnboardResult,
  BrowseResult,
  PlanContent,
  RestartHistoryResponse,
  RestartStatsDto,
  SystemSettings,
} from "./types";
import { apiUrl, request, downloadFile } from "./core";

// ── Overview & Health ──────────────────────────────────────────────────

export function getOverview(): Promise<WorkspaceOverview> {
  return request<WorkspaceOverview>(apiUrl("/api/overview"));
}

export function getInstanceHealth(): Promise<InstanceHealthResult> {
  return request<InstanceHealthResult>(apiUrl("/api/health/instance"));
}

// ── Restart History ────────────────────────────────────────────────────

export function getRestartHistory(limit = 20, offset = 0): Promise<RestartHistoryResponse> {
  return request<RestartHistoryResponse>(apiUrl(`/api/system/restarts?limit=${limit}&offset=${offset}`));
}

export function getRestartStats(hours = 24): Promise<RestartStatsDto> {
  return request<RestartStatsDto>(apiUrl(`/api/system/restarts/stats?hours=${hours}`));
}

// ── Workspace / Project ────────────────────────────────────────────────

export function getActiveWorkspace(): Promise<{ active: WorkspaceMeta | null; dataDir: string | null }> {
  return fetch(apiUrl("/api/workspace"))
    .then(async (res) => {
      if (!res.ok) return { active: null, dataDir: null };
      return (await res.json()) as { active: WorkspaceMeta | null; dataDir: string | null };
    });
}

export function listWorkspaces(): Promise<WorkspaceMeta[]> {
  return fetch(apiUrl("/api/workspaces"))
    .then(async (res) => {
      if (!res.ok) return [];
      return (await res.json()) as WorkspaceMeta[];
    });
}

export function switchWorkspace(wsPath: string): Promise<WorkspaceMeta> {
  return request<WorkspaceMeta>(apiUrl("/api/workspace"), {
    method: "PUT",
    body: JSON.stringify({ path: wsPath }),
  });
}

export function scanProject(dirPath: string): Promise<ProjectScanResult> {
  return request<ProjectScanResult>(apiUrl("/api/workspaces/scan"), {
    method: "POST",
    body: JSON.stringify({ path: dirPath }),
  });
}

export function onboardProject(dirPath: string): Promise<OnboardResult> {
  return request<OnboardResult>(apiUrl("/api/workspaces/onboard"), {
    method: "POST",
    body: JSON.stringify({ path: dirPath }),
  });
}

export function browseDirectory(dirPath?: string): Promise<BrowseResult> {
  const params = new URLSearchParams();
  if (dirPath) params.set("path", dirPath);
  const qs = params.toString();
  return request<BrowseResult>(apiUrl(`/api/filesystem/browse${qs ? `?${qs}` : ""}`));
}

// ── Plan ───────────────────────────────────────────────────────────────

export function getPlan(roomId: string): Promise<PlanContent | null> {
  return fetch(apiUrl(`/api/rooms/${roomId}/plan`))
    .then(async (res) => {
      if (res.status === 404) return null;
      if (!res.ok) throw new Error("Failed to load plan");
      return (await res.json()) as PlanContent;
    });
}

export function setPlan(roomId: string, content: string): Promise<void> {
  return request(apiUrl(`/api/rooms/${roomId}/plan`), {
    method: "PUT",
    body: JSON.stringify({ content }),
  });
}

export function deletePlan(roomId: string): Promise<void> {
  return request(apiUrl(`/api/rooms/${roomId}/plan`), {
    method: "DELETE",
  });
}

// ── System Settings ────────────────────────────────────────────────────

export async function getSystemSettings(): Promise<SystemSettings> {
  return request<SystemSettings>(apiUrl("/api/settings"));
}

export async function updateSystemSettings(
  settings: SystemSettings,
): Promise<SystemSettings> {
  return request<SystemSettings>(apiUrl("/api/settings"), {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(settings),
  });
}

// ── Data Export ─────────────────────────────────────────────────────────

export function exportAgents(format: "json" | "csv" = "json"): Promise<void> {
  const ext = format === "csv" ? "csv" : "json";
  return downloadFile(apiUrl(`/api/export/agents?format=${format}`), `agents-export.${ext}`);
}

export function exportUsage(
  format: "json" | "csv" = "json",
  hoursBack?: number,
): Promise<void> {
  const params = new URLSearchParams({ format });
  if (hoursBack != null) params.set("hoursBack", String(hoursBack));
  const ext = format === "csv" ? "csv" : "json";
  return downloadFile(apiUrl(`/api/export/usage?${params}`), `usage-export.${ext}`);
}

// ── Setting by Key ─────────────────────────────────────────────────────

export function getSettingByKey(key: string): Promise<{ key: string; value: string }> {
  return request<{ key: string; value: string }>(apiUrl(`/api/settings/${encodeURIComponent(key)}`));
}
