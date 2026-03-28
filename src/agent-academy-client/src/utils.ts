import type { ActivityEvent, RoomSnapshot, WorkspaceOverview } from "./api";

export function initials(name: string): string {
  const parts = name.split(/\s+/).filter(Boolean);
  return parts.slice(0, 2).map((p) => p[0]).join("").toUpperCase();
}

export function formatTime(value: string): string {
  return new Date(value).toLocaleString([], {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
  });
}

export function sameRoomSnapshot(current: RoomSnapshot, next: RoomSnapshot): boolean {
  return (
    current.id === next.id &&
    current.status === next.status &&
    current.currentPhase === next.currentPhase &&
    current.updatedAt === next.updatedAt &&
    (current.activeTask?.id ?? "") === (next.activeTask?.id ?? "") &&
    current.recentMessages.length === next.recentMessages.length &&
    (current.recentMessages.at(-1)?.id ?? "") === (next.recentMessages.at(-1)?.id ?? "") &&
    current.participants.length === next.participants.length
  );
}

export function workspaceChanged(
  current: WorkspaceOverview,
  next: WorkspaceOverview,
): boolean {
  if (current.configuredAgents.length !== next.configuredAgents.length) return true;
  if (current.rooms.length !== next.rooms.length) return true;

  for (let i = 0; i < current.rooms.length; i++) {
    const left = current.rooms[i];
    const right = next.rooms[i];
    if (!right || !sameRoomSnapshot(left, right)) return true;
  }

  return false;
}

export function sameActivityFeed(
  current: ActivityEvent[],
  next: ActivityEvent[],
): boolean {
  if (current.length !== next.length) return false;
  for (let i = 0; i < current.length; i++) {
    const left = current[i];
    const right = next[i];
    if (!right || left.id !== right.id || left.type !== right.type) return false;
  }
  return true;
}
