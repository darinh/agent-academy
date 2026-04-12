import type {
  TaskSnapshot,
  TaskStatus,
  PullRequestStatus,
  TaskComment,
  SpecTaskLink,
  TaskAssignmentRequest,
  TaskAssignmentResult,
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

export function getTask(taskId: string): Promise<TaskSnapshot> {
  return request<TaskSnapshot>(apiUrl(`/api/tasks/${taskId}`));
}

export function assignTask(taskId: string, agentId: string, agentName: string): Promise<TaskSnapshot> {
  return request<TaskSnapshot>(apiUrl(`/api/tasks/${taskId}/assign`), {
    method: "PUT",
    body: JSON.stringify({ agentId, agentName }),
  });
}

export function updateTaskStatus(taskId: string, status: TaskStatus): Promise<TaskSnapshot> {
  return request<TaskSnapshot>(apiUrl(`/api/tasks/${taskId}/status`), {
    method: "PUT",
    body: JSON.stringify({ status }),
  });
}

export function updateTaskBranch(taskId: string, branchName: string): Promise<TaskSnapshot> {
  return request<TaskSnapshot>(apiUrl(`/api/tasks/${taskId}/branch`), {
    method: "PUT",
    body: JSON.stringify({ branchName }),
  });
}

export function updateTaskPr(
  taskId: string, url: string, number: number, status: PullRequestStatus,
): Promise<TaskSnapshot> {
  return request<TaskSnapshot>(apiUrl(`/api/tasks/${taskId}/pr`), {
    method: "PUT",
    body: JSON.stringify({ url, number, status }),
  });
}

export function completeTask(
  taskId: string, commitCount: number, testsCreated?: string[],
): Promise<TaskSnapshot> {
  return request<TaskSnapshot>(apiUrl(`/api/tasks/${taskId}/complete`), {
    method: "PUT",
    body: JSON.stringify({ commitCount, testsCreated }),
  });
}

export function getTaskComments(taskId: string): Promise<TaskComment[]> {
  return request<TaskComment[]>(apiUrl(`/api/tasks/${taskId}/comments`));
}

export function getTaskSpecLinks(taskId: string): Promise<SpecTaskLink[]> {
  return request<SpecTaskLink[]>(apiUrl(`/api/tasks/${taskId}/specs`));
}
