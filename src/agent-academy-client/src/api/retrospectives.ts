import { apiUrl, request } from "./core";

// --- Types ---

export interface RetrospectiveListItem {
  id: string;
  taskId: string;
  taskTitle: string;
  agentId: string;
  agentName: string;
  contentPreview: string;
  createdAt: string;
}

export interface RetrospectiveListResponse {
  retrospectives: RetrospectiveListItem[];
  total: number;
  limit: number;
  offset: number;
}

export interface RetrospectiveDetailResponse {
  id: string;
  taskId: string;
  taskTitle: string;
  taskStatus: string;
  agentId: string;
  agentName: string;
  content: string;
  createdAt: string;
  taskCompletedAt: string | null;
}

export interface RetrospectiveAgentStat {
  agentId: string;
  agentName: string;
  count: number;
}

export interface RetrospectiveStatsResponse {
  totalRetrospectives: number;
  byAgent: RetrospectiveAgentStat[];
  averageContentLength: number;
  latestRetrospectiveAt: string | null;
}

// --- API functions ---

export interface ListRetrospectivesParams {
  agentId?: string;
  limit?: number;
  offset?: number;
}

export function listRetrospectives(params?: ListRetrospectivesParams): Promise<RetrospectiveListResponse> {
  const qs = new URLSearchParams();
  if (params?.agentId) qs.set("agentId", params.agentId);
  if (params?.limit != null) qs.set("limit", String(params.limit));
  if (params?.offset != null) qs.set("offset", String(params.offset));
  const query = qs.toString();
  return request<RetrospectiveListResponse>(apiUrl(`/api/retrospectives${query ? `?${query}` : ""}`));
}

export function getRetrospective(commentId: string): Promise<RetrospectiveDetailResponse> {
  return request<RetrospectiveDetailResponse>(apiUrl(`/api/retrospectives/${encodeURIComponent(commentId)}`));
}

export function getRetrospectiveStats(): Promise<RetrospectiveStatsResponse> {
  return request<RetrospectiveStatsResponse>(apiUrl("/api/retrospectives/stats"));
}
