import { apiUrl, request } from "./core";
import type { MemoryDto, BrowseMemoriesResponse, MemoryStatsResponse } from "./types";

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

export type { MemoryDto, BrowseMemoriesResponse, MemoryStatsResponse };
