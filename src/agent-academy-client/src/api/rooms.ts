import type {
  RoomSnapshot,
  AgentLocation,
  ConversationSessionSnapshot,
  RoomMessagesResponse,
  ChatEnvelope,
  CollaborationPhase,
  SessionListResponse,
  SessionStats,
} from "./types";
import { apiUrl, request } from "./core";

// ── Rooms ──────────────────────────────────────────────────────────────

export function getRooms(): Promise<RoomSnapshot[]> {
  return request<RoomSnapshot[]>(apiUrl("/api/rooms"));
}

export function getRoom(roomId: string): Promise<RoomSnapshot> {
  return request<RoomSnapshot>(apiUrl(`/api/rooms/${roomId}`));
}

export function createRoom(name: string, description?: string): Promise<RoomSnapshot> {
  return request<RoomSnapshot>(apiUrl("/api/rooms"), {
    method: "POST",
    body: JSON.stringify({ name, description }),
  });
}

export function renameRoom(roomId: string, name: string): Promise<RoomSnapshot> {
  return request<RoomSnapshot>(apiUrl(`/api/rooms/${roomId}/name`), {
    method: "PUT",
    body: JSON.stringify({ name }),
  });
}

export function addAgentToRoom(roomId: string, agentId: string): Promise<AgentLocation> {
  return request<AgentLocation>(apiUrl(`/api/rooms/${roomId}/agents/${agentId}`), {
    method: "POST",
  });
}

export function removeAgentFromRoom(roomId: string, agentId: string): Promise<AgentLocation> {
  return request<AgentLocation>(apiUrl(`/api/rooms/${roomId}/agents/${agentId}`), {
    method: "DELETE",
  });
}

// ── Messages ───────────────────────────────────────────────────────────

export function getRoomMessages(
  roomId: string,
  opts?: { after?: string; limit?: number; sessionId?: string },
): Promise<RoomMessagesResponse> {
  const params = new URLSearchParams();
  if (opts?.after) params.set("after", opts.after);
  if (opts?.limit) params.set("limit", String(opts.limit));
  if (opts?.sessionId) params.set("sessionId", opts.sessionId);
  const qs = params.toString();
  return request<RoomMessagesResponse>(apiUrl(`/api/rooms/${roomId}/messages${qs ? `?${qs}` : ""}`));
}

export function sendHumanMessage(roomId: string, content: string): Promise<ChatEnvelope> {
  return request<ChatEnvelope>(apiUrl(`/api/rooms/${roomId}/human`), {
    method: "POST",
    body: JSON.stringify({ content }),
  });
}

// ── Phase Transitions ──────────────────────────────────────────────────

export function transitionPhase(
  roomId: string,
  targetPhase: CollaborationPhase,
  reason?: string,
): Promise<RoomSnapshot> {
  return request<RoomSnapshot>(apiUrl(`/api/rooms/${roomId}/phase`), {
    method: "POST",
    body: JSON.stringify({ roomId, targetPhase, reason: reason ?? "" }),
  });
}

// ── Sessions ───────────────────────────────────────────────────────────

export function createRoomSession(roomId: string): Promise<ConversationSessionSnapshot> {
  return request<ConversationSessionSnapshot>(apiUrl(`/api/rooms/${roomId}/sessions`), {
    method: "POST",
  });
}

export function getSessions(
  status?: string,
  limit = 20,
  offset = 0,
  hoursBack?: number,
): Promise<SessionListResponse> {
  const params = new URLSearchParams();
  if (status) params.set("status", status);
  if (limit !== 20) params.set("limit", String(limit));
  if (offset > 0) params.set("offset", String(offset));
  if (hoursBack != null) params.set("hoursBack", String(hoursBack));
  const qs = params.toString();
  return request<SessionListResponse>(
    apiUrl(`/api/sessions${qs ? `?${qs}` : ""}`),
  );
}

export function getSessionStats(hoursBack?: number): Promise<SessionStats> {
  const qs = hoursBack != null ? `?hoursBack=${hoursBack}` : "";
  return request<SessionStats>(apiUrl(`/api/sessions/stats${qs}`));
}

export function getRoomSessions(
  roomId: string,
  status?: string,
  limit = 20,
  offset = 0,
): Promise<SessionListResponse> {
  const params = new URLSearchParams();
  if (status) params.set("status", status);
  if (limit !== 20) params.set("limit", String(limit));
  if (offset > 0) params.set("offset", String(offset));
  const qs = params.toString();
  return request<SessionListResponse>(
    apiUrl(
      `/api/rooms/${encodeURIComponent(roomId)}/sessions${qs ? `?${qs}` : ""}`,
    ),
  );
}
