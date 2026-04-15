import type {
  TaskSnapshot,
  TaskStatus,
  TaskComment,
  SpecTaskLink,
  TaskAssignmentRequest,
  TaskAssignmentResult,
  TaskDependencyInfo,
  BulkOperationResult,
} from "./types";
import { apiUrl, request } from "./core";

export function submitTask(req: TaskAssignmentRequest): Promise<TaskAssignmentResult> {
  return request<TaskAssignmentResult>(apiUrl("/api/tasks"), {
    method: "POST",
    body: JSON.stringify(req),
  });
}

export function getTasks(sprintId?: string): Promise<TaskSnapshot[]> {
  const params = sprintId ? `?sprintId=${encodeURIComponent(sprintId)}` : "";
  return request<TaskSnapshot[]>(apiUrl(`/api/tasks${params}`));
}

export function assignTask(taskId: string, agentId: string, agentName: string): Promise<TaskSnapshot> {
  return request<TaskSnapshot>(apiUrl(`/api/tasks/${taskId}/assign`), {
    method: "PUT",
    body: JSON.stringify({ agentId, agentName }),
  });
}

export function getTaskComments(taskId: string): Promise<TaskComment[]> {
  return request<TaskComment[]>(apiUrl(`/api/tasks/${taskId}/comments`));
}

export function getTaskSpecLinks(taskId: string): Promise<SpecTaskLink[]> {
  return request<SpecTaskLink[]>(apiUrl(`/api/tasks/${taskId}/specs`));
}

export function getTaskDependencies(taskId: string): Promise<TaskDependencyInfo> {
  return request<TaskDependencyInfo>(apiUrl(`/api/tasks/${taskId}/dependencies`));
}

// ── Bulk Operations ────────────────────────────────────────────────────

export function bulkUpdateStatus(taskIds: string[], status: TaskStatus): Promise<BulkOperationResult> {
  return request<BulkOperationResult>(apiUrl("/api/tasks/bulk/status"), {
    method: "POST",
    body: JSON.stringify({ taskIds, status }),
  });
}

export function bulkAssign(
  taskIds: string[], agentId: string, agentName: string,
): Promise<BulkOperationResult> {
  return request<BulkOperationResult>(apiUrl("/api/tasks/bulk/assign"), {
    method: "POST",
    body: JSON.stringify({ taskIds, agentId, agentName }),
  });
}
