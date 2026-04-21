import { apiUrl, request } from "./core";

// --- Types ---

export type GoalCardVerdict = "Proceed" | "ProceedWithCaveat" | "Challenge";
export type GoalCardStatus = "Active" | "Completed" | "Challenged" | "Abandoned";

export interface GoalCard {
  id: string;
  agentId: string;
  agentName: string;
  roomId: string;
  taskId: string | null;
  taskDescription: string;
  intent: string;
  divergence: string;
  steelman: string;
  strawman: string;
  verdict: GoalCardVerdict;
  freshEyes1: string;
  freshEyes2: string;
  freshEyes3: string;
  promptVersion: number;
  status: GoalCardStatus;
  createdAt: string;
  updatedAt: string;
}

export interface GoalCardFilters {
  roomId?: string;
  agentId?: string;
  taskId?: string;
  status?: GoalCardStatus;
  verdict?: GoalCardVerdict;
}

// --- API functions ---

export function getGoalCards(filters?: GoalCardFilters): Promise<GoalCard[]> {
  const qs = new URLSearchParams();
  if (filters?.roomId) qs.set("roomId", filters.roomId);
  if (filters?.agentId) qs.set("agentId", filters.agentId);
  if (filters?.taskId) qs.set("taskId", filters.taskId);
  if (filters?.status) qs.set("status", filters.status);
  if (filters?.verdict) qs.set("verdict", filters.verdict);
  const query = qs.toString();
  return request<GoalCard[]>(apiUrl(`/api/goal-cards${query ? `?${query}` : ""}`));
}

export function getGoalCard(id: string): Promise<GoalCard> {
  return request<GoalCard>(apiUrl(`/api/goal-cards/${encodeURIComponent(id)}`));
}

export function updateGoalCardStatus(id: string, status: GoalCardStatus): Promise<GoalCard> {
  return request<GoalCard>(apiUrl(`/api/goal-cards/${encodeURIComponent(id)}/status`), {
    method: "PATCH",
    body: JSON.stringify({ status }),
  });
}
