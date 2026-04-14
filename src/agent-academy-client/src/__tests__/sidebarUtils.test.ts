import { describe, expect, it } from "vitest";
import {
  PHASE_DOT_COLORS,
  phaseDotColor,
  countActiveRooms,
  countWorkingAgents,
  countActiveBreakouts,
  buildAgentsByRoom,
  compactRoomTooltip,
  getActiveBreakout,
  getBreakoutTaskName,
  isAgentThinking,
} from "../sidebarUtils";
import type {
  AgentDefinition,
  AgentLocation,
  BreakoutRoom,
  CollaborationPhase,
  RoomSnapshot,
} from "../api";

/* ── Helpers ──────────────────────────────────────────────────── */

function makeRoom(overrides: Partial<RoomSnapshot> = {}): RoomSnapshot {
  return {
    id: "room-1",
    name: "Main",
    status: "Active",
    currentPhase: "Discussion",
    participants: [],
    recentMessages: [],
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    ...overrides,
  } as RoomSnapshot;
}

function makeLocation(overrides: Partial<AgentLocation> = {}): AgentLocation {
  return {
    agentId: "agent-1",
    roomId: "room-1",
    state: "Working",
    updatedAt: new Date().toISOString(),
    ...overrides,
  };
}

function makeAgent(overrides: Partial<AgentDefinition> = {}): AgentDefinition {
  return {
    id: "agent-1",
    name: "Planner",
    role: "Planner",
    summary: "Plans stuff",
    startupPrompt: "",
    ...overrides,
  } as AgentDefinition;
}

function makeBreakout(overrides: Partial<BreakoutRoom> = {}): BreakoutRoom {
  return {
    id: "br-1",
    name: "BR: Fix auth",
    parentRoomId: "room-1",
    assignedAgentId: "agent-1",
    tasks: [],
    status: "Active",
    recentMessages: [],
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    ...overrides,
  } as BreakoutRoom;
}

/* ── Tests ────────────────────────────────────────────────────── */

describe("sidebarUtils", () => {
  /* ── Phase dot colors ───────────────────────────────────────── */

  describe("PHASE_DOT_COLORS", () => {
    const phases: CollaborationPhase[] = [
      "Intake", "Planning", "Discussion",
      "Validation", "Implementation", "FinalSynthesis",
    ];

    it.each(phases)("has a CSS variable for %s", (phase) => {
      expect(PHASE_DOT_COLORS[phase]).toMatch(/var\(--aa-/);
    });
  });

  describe("phaseDotColor", () => {
    it("returns known color for valid phase", () => {
      expect(phaseDotColor("Intake")).toBe("var(--aa-soft)");
      expect(phaseDotColor("Implementation")).toBe("var(--aa-lime)");
    });

    it("returns default color for unknown phase", () => {
      expect(phaseDotColor("UnknownPhase")).toBe("var(--aa-soft)");
    });
  });

  /* ── Sidebar stats ──────────────────────────────────────────── */

  describe("countActiveRooms", () => {
    it("returns 0 for empty array", () => {
      expect(countActiveRooms([])).toBe(0);
    });

    it("counts Active and AttentionRequired rooms", () => {
      const rooms = [
        makeRoom({ status: "Active" }),
        makeRoom({ status: "AttentionRequired" }),
        makeRoom({ status: "Completed" }),
        makeRoom({ status: "Archived" }),
        makeRoom({ status: "Idle" }),
      ];
      expect(countActiveRooms(rooms)).toBe(2);
    });
  });

  describe("countWorkingAgents", () => {
    it("returns 0 for empty array", () => {
      expect(countWorkingAgents([])).toBe(0);
    });

    it("counts only Working agents", () => {
      const locations = [
        makeLocation({ state: "Working" }),
        makeLocation({ state: "Idle" }),
        makeLocation({ state: "Working" }),
      ];
      expect(countWorkingAgents(locations)).toBe(2);
    });
  });

  describe("countActiveBreakouts", () => {
    it("returns 0 for empty array", () => {
      expect(countActiveBreakouts([])).toBe(0);
    });

    it("counts only Active breakouts", () => {
      const breakouts = [
        makeBreakout({ status: "Active" }),
        makeBreakout({ status: "Completed" }),
        makeBreakout({ status: "Active" }),
      ];
      expect(countActiveBreakouts(breakouts)).toBe(2);
    });
  });

  /* ── Agent-room mapping ─────────────────────────────────────── */

  describe("buildAgentsByRoom", () => {
    it("returns empty map for empty inputs", () => {
      expect(buildAgentsByRoom([], []).size).toBe(0);
    });

    it("groups agents by their room", () => {
      const agents = [
        makeAgent({ id: "a1", name: "Planner" }),
        makeAgent({ id: "a2", name: "Engineer" }),
        makeAgent({ id: "a3", name: "Writer" }),
      ];
      const locations = [
        makeLocation({ agentId: "a1", roomId: "room-1" }),
        makeLocation({ agentId: "a2", roomId: "room-1" }),
        makeLocation({ agentId: "a3", roomId: "room-2" }),
      ];

      const map = buildAgentsByRoom(locations, agents);
      expect(map.get("room-1")).toHaveLength(2);
      expect(map.get("room-2")).toHaveLength(1);
      expect(map.get("room-2")![0].name).toBe("Writer");
    });

    it("skips locations for unknown agents", () => {
      const agents = [makeAgent({ id: "a1" })];
      const locations = [
        makeLocation({ agentId: "a1", roomId: "room-1" }),
        makeLocation({ agentId: "unknown", roomId: "room-1" }),
      ];

      const map = buildAgentsByRoom(locations, agents);
      expect(map.get("room-1")).toHaveLength(1);
    });
  });

  /* ── Compact tooltip ────────────────────────────────────────── */

  describe("compactRoomTooltip", () => {
    it("includes room name, phase, and participant count", () => {
      const room = makeRoom({
        name: "Design",
        currentPhase: "Planning",
        participants: [{ agentId: "a1" }, { agentId: "a2" }] as RoomSnapshot["participants"],
      });
      const tooltip = compactRoomTooltip(room);
      expect(tooltip).toContain("Design");
      expect(tooltip).toContain("Planning");
      expect(tooltip).toContain("2 agents");
    });
  });

  /* ── Breakout helpers ───────────────────────────────────────── */

  describe("getActiveBreakout", () => {
    it("returns undefined when no breakouts", () => {
      expect(getActiveBreakout([], "agent-1")).toBeUndefined();
    });

    it("returns the active breakout for the agent", () => {
      const breakouts = [
        makeBreakout({ id: "br-1", assignedAgentId: "a1", status: "Active" }),
        makeBreakout({ id: "br-2", assignedAgentId: "a2", status: "Active" }),
        makeBreakout({ id: "br-3", assignedAgentId: "a1", status: "Completed" }),
      ];
      const result = getActiveBreakout(breakouts, "a1");
      expect(result?.id).toBe("br-1");
    });

    it("returns undefined when agent has no active breakout", () => {
      const breakouts = [
        makeBreakout({ assignedAgentId: "a1", status: "Completed" }),
      ];
      expect(getActiveBreakout(breakouts, "a1")).toBeUndefined();
    });
  });

  describe("getBreakoutTaskName", () => {
    it("strips BR: prefix from breakout name", () => {
      expect(getBreakoutTaskName(makeBreakout({ name: "BR: Fix login" }))).toBe("Fix login");
    });

    it("returns full name when no BR: prefix", () => {
      expect(getBreakoutTaskName(makeBreakout({ name: "Custom room" }))).toBe("Custom room");
    });

    it("returns null for undefined breakout", () => {
      expect(getBreakoutTaskName(undefined)).toBeNull();
    });
  });

  /* ── Thinking state ─────────────────────────────────────────── */

  describe("isAgentThinking", () => {
    it("returns false for empty map", () => {
      expect(isAgentThinking(new Map(), "a1")).toBe(false);
    });

    it("returns true when agent is in any room's thinking set", () => {
      const map = new Map<string, Set<string>>([
        ["room-1", new Set(["a1", "a2"])],
        ["room-2", new Set(["a3"])],
      ]);
      expect(isAgentThinking(map, "a1")).toBe(true);
    });

    it("returns false when agent is not in any thinking set", () => {
      const map = new Map<string, Set<string>>([
        ["room-1", new Set(["a2"])],
      ]);
      expect(isAgentThinking(map, "a1")).toBe(false);
    });

    it("returns true even if agent is in a different room", () => {
      const map = new Map<string, Set<string>>([
        ["room-1", new Set()],
        ["room-2", new Set(["a1"])],
      ]);
      expect(isAgentThinking(map, "a1")).toBe(true);
    });
  });
});
