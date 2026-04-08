import type { AgentDefinition, AgentLocation, BreakoutRoom, CollaborationPhase, RoomSnapshot } from "./api";

/* ── Phase dot colors ─────────────────────────────────────────── */

export const PHASE_DOT_COLORS: Record<CollaborationPhase, string> = {
  Intake: "var(--aa-soft)",
  Planning: "var(--aa-cyan)",
  Discussion: "var(--aa-plum)",
  Implementation: "var(--aa-lime)",
  Validation: "var(--aa-gold)",
  FinalSynthesis: "var(--aa-copper)",
};

const DEFAULT_DOT_COLOR = "var(--aa-soft)";

export function phaseDotColor(phase: CollaborationPhase | string): string {
  return (PHASE_DOT_COLORS as Record<string, string>)[phase] ?? DEFAULT_DOT_COLOR;
}

/* ── Sidebar stats ────────────────────────────────────────────── */

export function countActiveRooms(rooms: RoomSnapshot[]): number {
  return rooms.filter((r) => r.status === "Active" || r.status === "AttentionRequired").length;
}

export function countWorkingAgents(locations: AgentLocation[]): number {
  return locations.filter((l) => l.state === "Working").length;
}

export function countActiveBreakouts(breakouts: BreakoutRoom[]): number {
  return breakouts.filter((b) => b.status === "Active").length;
}

/* ── Agent-room mapping ───────────────────────────────────────── */

export function buildAgentsByRoom(
  locations: AgentLocation[],
  agents: AgentDefinition[],
): Map<string, AgentDefinition[]> {
  const map = new Map<string, AgentDefinition[]>();
  for (const loc of locations) {
    const agent = agents.find((a) => a.id === loc.agentId);
    if (!agent) continue;
    const list = map.get(loc.roomId) ?? [];
    list.push(agent);
    map.set(loc.roomId, list);
  }
  return map;
}

/* ── Compact sidebar tooltip ──────────────────────────────────── */

export function compactRoomTooltip(room: RoomSnapshot): string {
  return `${room.name} · ${room.currentPhase} · ${room.participants.length} agents`;
}

/* ── Agent session helpers ────────────────────────────────────── */

export function getActiveBreakout(
  breakouts: BreakoutRoom[],
  agentId: string,
): BreakoutRoom | undefined {
  return breakouts
    .filter((br) => br.assignedAgentId === agentId)
    .find((br) => br.status === "Active");
}

export function getBreakoutTaskName(breakout: BreakoutRoom | undefined): string | null {
  return breakout?.name?.replace(/^BR:\s*/, "") ?? null;
}

export function isAgentThinking(
  thinkingByRoomIds: Map<string, Set<string>>,
  agentId: string,
): boolean {
  for (const ids of thinkingByRoomIds.values()) {
    if (ids.has(agentId)) return true;
  }
  return false;
}
