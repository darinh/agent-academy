import { apiUrl, request } from "./core";
import type {
  MemoryDto,
  BrowseMemoriesResponse,
  MemoryStatsResponse,
  MemoryExportResponse,
  MemoryImportEntry,
  MemoryImportResponse,
} from "./types";

export interface BrowseMemoriesParams {
  agentId: string;
  category?: string;
  search?: string;
  includeExpired?: boolean;
}

export function browseMemories(params: BrowseMemoriesParams): Promise<BrowseMemoriesResponse> {
  const qs = new URLSearchParams({ agentId: params.agentId });
  if (params.category) qs.set("category", params.category);
  if (params.search) qs.set("search", params.search);
  if (params.includeExpired) qs.set("includeExpired", "true");
  return request<BrowseMemoriesResponse>(apiUrl(`/api/memories/browse?${qs}`));
}

export function getMemoryStats(agentId: string): Promise<MemoryStatsResponse> {
  return request<MemoryStatsResponse>(apiUrl(`/api/memories/stats?agentId=${encodeURIComponent(agentId)}`));
}

export function deleteMemory(agentId: string, key: string): Promise<{ status: string; agentId: string; key: string }> {
  const qs = new URLSearchParams({ agentId, key });
  return request(apiUrl(`/api/memories?${qs}`), { method: "DELETE" });
}

// ── Memory Export/Import ───────────────────────────────────────────────

export function exportMemories(agentId?: string, category?: string): Promise<MemoryExportResponse> {
  const qs = new URLSearchParams();
  if (agentId) qs.set("agentId", agentId);
  if (category) qs.set("category", category);
  const query = qs.toString();
  return request<MemoryExportResponse>(apiUrl(`/api/memories/export${query ? `?${query}` : ""}`));
}

export function importMemories(memories: MemoryImportEntry[]): Promise<MemoryImportResponse> {
  return request<MemoryImportResponse>(apiUrl("/api/memories/import"), {
    method: "POST",
    body: JSON.stringify({ memories }),
  });
}

export function deleteExpiredMemories(agentId?: string): Promise<{ removed: number }> {
  const qs = agentId ? `?agentId=${encodeURIComponent(agentId)}` : "";
  return request<{ removed: number }>(apiUrl(`/api/memories/expired${qs}`), { method: "DELETE" });
}

export type { MemoryDto, BrowseMemoriesResponse, MemoryStatsResponse, MemoryExportResponse, MemoryImportEntry, MemoryImportResponse };
