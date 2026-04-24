import type {
  TaskSnapshot,
  TaskStatus,
  TaskPriority,
  TaskComment,
  SpecTaskLink,
  TaskAssignmentRequest,
  TaskAssignmentResult,
  TaskDependencyInfo,
  BulkOperationResult,
  SpecVersionInfo,
  SpecSearchResult,
  PullRequestStatus,
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

// ── Single Task Updates ────────────────────────────────────────────────

export function updateTaskStatus(taskId: string, status: TaskStatus): Promise<TaskSnapshot> {
  return request<TaskSnapshot>(apiUrl(`/api/tasks/${encodeURIComponent(taskId)}/status`), {
    method: "PUT",
    body: JSON.stringify({ status }),
  });
}

export function updateTaskPriority(taskId: string, priority: TaskPriority): Promise<TaskSnapshot> {
  return request<TaskSnapshot>(apiUrl(`/api/tasks/${encodeURIComponent(taskId)}/priority`), {
    method: "PUT",
    body: JSON.stringify({ priority }),
  });
}

export function updateTaskBranch(taskId: string, branchName: string): Promise<TaskSnapshot> {
  return request<TaskSnapshot>(apiUrl(`/api/tasks/${encodeURIComponent(taskId)}/branch`), {
    method: "PUT",
    body: JSON.stringify({ branchName }),
  });
}

/**
 * Associates a task with a sprint, or clears the association when
 * `sprintId` is null. Throws if the task or sprint is not found.
 */
export function updateTaskSprint(taskId: string, sprintId: string | null): Promise<TaskSnapshot> {
  return request<TaskSnapshot>(apiUrl(`/api/tasks/${encodeURIComponent(taskId)}/sprint`), {
    method: "PUT",
    body: JSON.stringify({ sprintId }),
  });
}

export function updateTaskPr(
  taskId: string,
  url: string,
  number: number,
  status: PullRequestStatus,
): Promise<TaskSnapshot> {
  return request<TaskSnapshot>(apiUrl(`/api/tasks/${encodeURIComponent(taskId)}/pr`), {
    method: "PUT",
    body: JSON.stringify({ url, number, status }),
  });
}

export function completeTask(
  taskId: string,
  commitCount: number,
  testsCreated?: string[],
): Promise<TaskSnapshot> {
  return request<TaskSnapshot>(apiUrl(`/api/tasks/${encodeURIComponent(taskId)}/complete`), {
    method: "PUT",
    body: JSON.stringify({ commitCount, testsCreated }),
  });
}

export function removeTaskDependency(taskId: string, dependsOnTaskId: string): Promise<TaskDependencyInfo> {
  return request<TaskDependencyInfo>(
    apiUrl(`/api/tasks/${encodeURIComponent(taskId)}/dependencies/${encodeURIComponent(dependsOnTaskId)}`),
    { method: "DELETE" },
  );
}

// ── Spec Integration ───────────────────────────────────────────────────

export function getSpecTasks(sectionId: string): Promise<SpecTaskLink[]> {
  return request<SpecTaskLink[]>(apiUrl(`/api/specs/${encodeURIComponent(sectionId)}/tasks`));
}

export function getSpecVersion(): Promise<SpecVersionInfo> {
  return request<SpecVersionInfo>(apiUrl("/api/specs/version"));
}

export function searchSpecs(query: string, limit = 10): Promise<SpecSearchResult[]> {
  const params = new URLSearchParams({ q: query });
  if (limit !== 10) params.set("limit", String(limit));
  return request<SpecSearchResult[]>(apiUrl(`/api/specs/search?${params}`));
}
