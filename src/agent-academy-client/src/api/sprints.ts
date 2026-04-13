import type {
  SprintDetailResponse,
  SprintListResponse,
  SprintSnapshot,
  SprintArtifact,
  SprintStage,
  SprintScheduleRequest,
  SprintScheduleResponse,
} from "./types";
import { apiUrl, request } from "./core";

export async function getActiveSprint(): Promise<SprintDetailResponse | null> {
  const res = await fetch(apiUrl("/api/sprints/active"), { credentials: "include" });
  if (res.status === 204) return null;
  if (!res.ok) throw new Error("Failed to fetch active sprint");
  return res.json() as Promise<SprintDetailResponse>;
}

export function getSprints(limit = 20, offset = 0): Promise<SprintListResponse> {
  const params = new URLSearchParams();
  if (limit !== 20) params.set("limit", String(limit));
  if (offset > 0) params.set("offset", String(offset));
  const qs = params.toString();
  return request<SprintListResponse>(apiUrl(`/api/sprints${qs ? `?${qs}` : ""}`));
}

export async function getSprintDetail(id: string): Promise<SprintDetailResponse | null> {
  const res = await fetch(apiUrl(`/api/sprints/${encodeURIComponent(id)}`), {
    credentials: "include",
  });
  if (res.status === 404) return null;
  if (!res.ok) throw new Error("Failed to fetch sprint");
  return res.json() as Promise<SprintDetailResponse>;
}

export function getSprintArtifacts(
  id: string,
  stage?: SprintStage,
): Promise<SprintArtifact[]> {
  const params = new URLSearchParams();
  if (stage) params.set("stage", stage);
  const qs = params.toString();
  return request<SprintArtifact[]>(
    apiUrl(`/api/sprints/${encodeURIComponent(id)}/artifacts${qs ? `?${qs}` : ""}`),
  );
}

export async function startSprint(): Promise<SprintDetailResponse> {
  const res = await fetch(apiUrl("/api/sprints"), {
    method: "POST",
    credentials: "include",
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? "Failed to start sprint");
  }
  return res.json() as Promise<SprintDetailResponse>;
}

export async function advanceSprint(id: string): Promise<SprintDetailResponse> {
  const res = await fetch(apiUrl(`/api/sprints/${encodeURIComponent(id)}/advance`), {
    method: "POST",
    credentials: "include",
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? "Failed to advance sprint");
  }
  return res.json() as Promise<SprintDetailResponse>;
}

export async function completeSprint(id: string, force = false): Promise<SprintSnapshot> {
  const qs = force ? "?force=true" : "";
  const res = await fetch(apiUrl(`/api/sprints/${encodeURIComponent(id)}/complete${qs}`), {
    method: "POST",
    credentials: "include",
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? "Failed to complete sprint");
  }
  return res.json() as Promise<SprintSnapshot>;
}

export async function cancelSprint(id: string): Promise<SprintSnapshot> {
  const res = await fetch(apiUrl(`/api/sprints/${encodeURIComponent(id)}/cancel`), {
    method: "POST",
    credentials: "include",
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? "Failed to cancel sprint");
  }
  return res.json() as Promise<SprintSnapshot>;
}

export async function approveSprintAdvance(id: string): Promise<SprintDetailResponse> {
  const res = await fetch(apiUrl(`/api/sprints/${encodeURIComponent(id)}/approve-advance`), {
    method: "POST",
    credentials: "include",
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? "Failed to approve sprint advance");
  }
  return res.json() as Promise<SprintDetailResponse>;
}

export async function rejectSprintAdvance(id: string): Promise<SprintSnapshot> {
  const res = await fetch(apiUrl(`/api/sprints/${encodeURIComponent(id)}/reject-advance`), {
    method: "POST",
    credentials: "include",
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? "Failed to reject sprint advance");
  }
  return res.json() as Promise<SprintSnapshot>;
}

// ── Sprint Schedule ───────────────────────────────────────────────────

export async function getSprintSchedule(): Promise<SprintScheduleResponse | null> {
  const res = await fetch(apiUrl("/api/sprints/schedule"), { credentials: "include" });
  if (res.status === 404) return null;
  if (!res.ok) throw new Error("Failed to fetch sprint schedule");
  return res.json() as Promise<SprintScheduleResponse>;
}

export async function upsertSprintSchedule(
  body: SprintScheduleRequest,
): Promise<SprintScheduleResponse> {
  const res = await fetch(apiUrl("/api/sprints/schedule"), {
    method: "PUT",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  if (!res.ok) {
    const err = await res.json().catch(() => ({}));
    throw new Error((err as { error?: string }).error ?? "Failed to save sprint schedule");
  }
  return res.json() as Promise<SprintScheduleResponse>;
}

export async function deleteSprintSchedule(): Promise<void> {
  const res = await fetch(apiUrl("/api/sprints/schedule"), {
    method: "DELETE",
    credentials: "include",
  });
  if (res.status === 404) return;
  if (!res.ok) throw new Error("Failed to delete sprint schedule");
}
