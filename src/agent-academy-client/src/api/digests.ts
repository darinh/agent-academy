import { apiUrl, request } from "./core";

// --- Types ---

export interface DigestListItem {
  id: number;
  createdAt: string;
  summary: string;
  memoriesCreated: number;
  retrospectivesProcessed: number;
  status: string;
}

export interface DigestListResponse {
  digests: DigestListItem[];
  total: number;
  limit: number;
  offset: number;
}

export interface DigestSourceItem {
  commentId: string;
  taskId: string;
  agentId: string;
  content: string;
  createdAt: string;
}

export interface DigestDetailResponse {
  id: number;
  createdAt: string;
  summary: string;
  memoriesCreated: number;
  retrospectivesProcessed: number;
  status: string;
  sources: DigestSourceItem[];
}

export interface DigestStatsResponse {
  totalDigests: number;
  byStatus: Record<string, number>;
  totalMemoriesCreated: number;
  totalRetrospectivesProcessed: number;
  undigestedRetrospectives: number;
  lastCompletedAt: string | null;
}

// --- API functions ---

export interface ListDigestsParams {
  status?: string;
  limit?: number;
  offset?: number;
}

export function listDigests(params?: ListDigestsParams): Promise<DigestListResponse> {
  const qs = new URLSearchParams();
  if (params?.status) qs.set("status", params.status);
  if (params?.limit != null) qs.set("limit", String(params.limit));
  if (params?.offset != null) qs.set("offset", String(params.offset));
  const query = qs.toString();
  return request<DigestListResponse>(apiUrl(`/api/digests${query ? `?${query}` : ""}`));
}

export function getDigest(id: number): Promise<DigestDetailResponse> {
  return request<DigestDetailResponse>(apiUrl(`/api/digests/${id}`));
}

export function getDigestStats(): Promise<DigestStatsResponse> {
  return request<DigestStatsResponse>(apiUrl("/api/digests/stats"));
}
