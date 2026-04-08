import { describe, expect, it } from "vitest";
import type {
  AgentDefinition,
  AgentLocation,
  AgentPresence,
  BreakoutRoom,
  RoomSnapshot,
  RoomStatus,
  CollaborationPhase,
} from "../api";
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

// ── Factories ──

function makeAgent(overrides: Partial<AgentDefinition> = {}): AgentDefinition {
  return {
    id: "agent-1",
    name: "Aristotle",
    role: "Planner",
    summary: "Plans the work",
    startupPrompt: "You are a planner.",
    model: null,
    capabilityTags: [],
    enabledTools: [],
    autoJoinDefaultRoom: true,
    ...overrides,
  };
}

function makeLocation(overrides: Partial<AgentLocation> = {}): AgentLocation {
  return {
    agentId: "agent-1",
    roomId: "room-main",
    state: "Idle",
    updatedAt: "2026-04-07T00:00:00Z",
    ...overrides,
  };
}

function makePresence(overrides: Partial<AgentPresence> = {}): AgentPresence {
  return {
    agentId: "agent-1",
    name: "Aristotle",
    role: "Planner",
    availability: "Available",
    isPreferred: false,
    lastActivityAt: "2026-04-07T00:00:00Z",
    activeCapabilities: [],
    ...overrides,
  };
}

function makeRoom(overrides: Partial<RoomSnapshot> = {}): RoomSnapshot {
  return {
    id: "room-1",
    name: "Main Room",
    status: "Active" as RoomStatus,
    currentPhase: "Planning" as CollaborationPhase,
    participants: [],
    recentMessages: [],
    createdAt: "2026-04-07T00:00:00Z",
    updatedAt: "2026-04-07T00:00:00Z",
    ...overrides,
  };
}

function makeBreakout(overrides: Partial<BreakoutRoom> = {}): BreakoutRoom {
  return {
    id: "br-1",
    name: "BR: Fix the bug",
    parentRoomId: "room-main",
    assignedAgentId: "agent-1",
    tasks: [],
    status: "Active" as RoomStatus,
    recentMessages: [],
    createdAt: "2026-04-07T00:00:00Z",
    updatedAt: "2026-04-07T00:00:00Z",
    ...overrides,
  };
}

// ── Tests ──

describe("sidebarUtils", () => {
  // ── phaseDotColor ─────────────────────────────────────────────

  describe("phaseDotColor", () => {
    it("returns correct color for each known phase", () => {
      expect(phaseDotColor("Intake")).toBe("var(--aa-soft)");
      expect(phaseDotColor("Planning")).toBe("var(--aa-cyan)");
      expect(phaseDotColor("Discussion")).toBe("var(--aa-plum)");
      expect(phaseDotColor("Implementation")).toBe("var(--aa-lime)");
      expect(phaseDotColor("Validation")).toBe("var(--aa-gold)");
      expect(phaseDotColor("FinalSynthesis")).toBe("var(--aa-copper)");
    });

    it("returns default gray for unknown phase", () => {
      expect(phaseDotColor("SomeNewPhase")).toBe("var(--aa-soft)");
    });

    it("returns default gray for empty string", () => {
      expect(phaseDotColor("")).toBe("var(--aa-soft)");
    });

    it("PHASE_DOT_COLORS has exactly 6 entries", () => {
      expect(Object.keys(PHASE_DOT_COLORS)).toHaveLength(6);
    });
  });

  // ── countActiveRooms ─────────────────────────────────────────

  describe("countActiveRooms", () => {
    it("counts Active rooms", () => {
      const rooms = [
        makeRoom({ id: "r1", status: "Active" }),
        makeRoom({ id: "r2", status: "Idle" }),
      ];
      expect(countActiveRooms(rooms)).toBe(1);
    });

    it("counts AttentionRequired rooms", () => {
      const rooms = [
        makeRoom({ id: "r1", status: "AttentionRequired" }),
        makeRoom({ id: "r2", status: "Completed" }),
      ];
      expect(countActiveRooms(rooms)).toBe(1);
    });

    it("counts both Active and AttentionRequired", () => {
      const rooms = [
        makeRoom({ id: "r1", status: "Active" }),
        makeRoom({ id: "r2", status: "AttentionRequired" }),
        makeRoom({ id: "r3", status: "Idle" }),
        makeRoom({ id: "r4", status: "Archived" }),
      ];
      expect(countActiveRooms(rooms)).toBe(2);
    });

    it("returns 0 for empty array", () => {
      expect(countActiveRooms([])).toBe(0);
    });

    it("returns 0 when all rooms are inactive", () => {
      const rooms = [
        makeRoom({ id: "r1", status: "Idle" }),
        makeRoom({ id: "r2", status: "Completed" }),
        makeRoom({ id: "r3", status: "Archived" }),
      ];
      expect(countActiveRooms(rooms)).toBe(0);
    });
  });

  // ── countWorkingAgents ───────────────────────────────────────

  describe("countWorkingAgents", () => {
    it("counts Working agents", () => {
      const locs = [
        makeLocation({ agentId: "a1", state: "Working" }),
        makeLocation({ agentId: "a2", state: "Idle" }),
        makeLocation({ agentId: "a3", state: "Working" }),
      ];
      expect(countWorkingAgents(locs)).toBe(2);
    });

    it("returns 0 when no agents are working", () => {
      const locs = [
        makeLocation({ agentId: "a1", state: "Idle" }),
        makeLocation({ agentId: "a2", state: "Thinking" }),
      ];
      expect(countWorkingAgents(locs)).toBe(0);
    });

    it("returns 0 for empty array", () => {
      expect(countWorkingAgents([])).toBe(0);
    });
  });

  // ── countActiveBreakouts ─────────────────────────────────────

  describe("countActiveBreakouts", () => {
    it("counts Active breakouts", () => {
      const breakouts = [
        makeBreakout({ id: "b1", status: "Active" }),
        makeBreakout({ id: "b2", status: "Completed" }),
        makeBreakout({ id: "b3", status: "Active" }),
      ];
      expect(countActiveBreakouts(breakouts)).toBe(2);
    });

    it("returns 0 for empty array", () => {
      expect(countActiveBreakouts([])).toBe(0);
    });

    it("returns 0 when none active", () => {
      const breakouts = [
        makeBreakout({ id: "b1", status: "Archived" }),
        makeBreakout({ id: "b2", status: "Completed" }),
      ];
      expect(countActiveBreakouts(breakouts)).toBe(0);
    });
  });

  // ── buildAgentsByRoom ────────────────────────────────────────

  describe("buildAgentsByRoom", () => {
    it("maps agents to their rooms", () => {
      const agents = [
        makeAgent({ id: "a1", name: "Aristotle" }),
        makeAgent({ id: "a2", name: "Athena" }),
      ];
      const locations = [
        makeLocation({ agentId: "a1", roomId: "room-1" }),
        makeLocation({ agentId: "a2", roomId: "room-1" }),
      ];
      const map = buildAgentsByRoom(locations, agents);
      expect(map.get("room-1")).toHaveLength(2);
      expect(map.get("room-1")![0].name).toBe("Aristotle");
      expect(map.get("room-1")![1].name).toBe("Athena");
    });

    it("handles agents in different rooms", () => {
      const agents = [
        makeAgent({ id: "a1", name: "Aristotle" }),
        makeAgent({ id: "a2", name: "Athena" }),
      ];
      const locations = [
        makeLocation({ agentId: "a1", roomId: "room-1" }),
        makeLocation({ agentId: "a2", roomId: "room-2" }),
      ];
      const map = buildAgentsByRoom(locations, agents);
      expect(map.get("room-1")).toHaveLength(1);
      expect(map.get("room-2")).toHaveLength(1);
    });

    it("skips locations with unknown agent IDs", () => {
      const agents = [makeAgent({ id: "a1" })];
      const locations = [
        makeLocation({ agentId: "a1", roomId: "room-1" }),
        makeLocation({ agentId: "a-unknown", roomId: "room-1" }),
      ];
      const map = buildAgentsByRoom(locations, agents);
      expect(map.get("room-1")).toHaveLength(1);
    });

    it("returns empty map for empty inputs", () => {
      const map = buildAgentsByRoom([], []);
      expect(map.size).toBe(0);
    });

    it("returns empty map when no agents match locations", () => {
      const agents = [makeAgent({ id: "a1" })];
      const locations = [makeLocation({ agentId: "a-other", roomId: "room-1" })];
      const map = buildAgentsByRoom(locations, agents);
      expect(map.size).toBe(0);
    });
  });

  // ── compactRoomTooltip ───────────────────────────────────────

  describe("compactRoomTooltip", () => {
    it("formats tooltip with room name, phase, and agent count", () => {
      const room = makeRoom({
        name: "Main Room",
        currentPhase: "Planning",
        participants: [makePresence(), makePresence({ agentId: "a2", name: "Athena" })],
      });
      expect(compactRoomTooltip(room)).toBe("Main Room · Planning · 2 agents");
    });

    it("shows 0 agents when no participants", () => {
      const room = makeRoom({ name: "Empty Room", currentPhase: "Intake", participants: [] });
      expect(compactRoomTooltip(room)).toBe("Empty Room · Intake · 0 agents");
    });

    it("handles single agent", () => {
      const room = makeRoom({
        name: "Solo Room",
        currentPhase: "Implementation",
        participants: [makePresence()],
      });
      expect(compactRoomTooltip(room)).toBe("Solo Room · Implementation · 1 agents");
    });
  });

  // ── getActiveBreakout ────────────────────────────────────────

  describe("getActiveBreakout", () => {
    it("returns the active breakout for an agent", () => {
      const breakouts = [
        makeBreakout({ id: "b1", assignedAgentId: "a1", status: "Active" }),
        makeBreakout({ id: "b2", assignedAgentId: "a2", status: "Active" }),
      ];
      const result = getActiveBreakout(breakouts, "a1");
      expect(result?.id).toBe("b1");
    });

    it("returns undefined when agent has no breakout", () => {
      const breakouts = [
        makeBreakout({ id: "b1", assignedAgentId: "a2", status: "Active" }),
      ];
      expect(getActiveBreakout(breakouts, "a1")).toBeUndefined();
    });

    it("ignores non-active breakouts", () => {
      const breakouts = [
        makeBreakout({ id: "b1", assignedAgentId: "a1", status: "Completed" }),
        makeBreakout({ id: "b2", assignedAgentId: "a1", status: "Archived" }),
      ];
      expect(getActiveBreakout(breakouts, "a1")).toBeUndefined();
    });

    it("returns the first active breakout if multiple exist", () => {
      const breakouts = [
        makeBreakout({ id: "b1", assignedAgentId: "a1", status: "Active", name: "BR: First" }),
        makeBreakout({ id: "b2", assignedAgentId: "a1", status: "Active", name: "BR: Second" }),
      ];
      const result = getActiveBreakout(breakouts, "a1");
      expect(result?.id).toBe("b1");
    });

    it("returns undefined for empty array", () => {
      expect(getActiveBreakout([], "a1")).toBeUndefined();
    });
  });

  // ── getBreakoutTaskName ──────────────────────────────────────

  describe("getBreakoutTaskName", () => {
    it("strips BR: prefix", () => {
      const breakout = makeBreakout({ name: "BR: Fix the login bug" });
      expect(getBreakoutTaskName(breakout)).toBe("Fix the login bug");
    });

    it("strips BR: with extra spaces", () => {
      const breakout = makeBreakout({ name: "BR:  Double space test" });
      expect(getBreakoutTaskName(breakout)).toBe("Double space test");
    });

    it("returns name unchanged if no BR: prefix", () => {
      const breakout = makeBreakout({ name: "Some other breakout" });
      expect(getBreakoutTaskName(breakout)).toBe("Some other breakout");
    });

    it("returns null for undefined breakout", () => {
      expect(getBreakoutTaskName(undefined)).toBeNull();
    });
  });

  // ── isAgentThinking ──────────────────────────────────────────

  describe("isAgentThinking", () => {
    it("returns true when agent is in a thinking set", () => {
      const map = new Map<string, Set<string>>([
        ["room-1", new Set(["a1", "a2"])],
      ]);
      expect(isAgentThinking(map, "a1")).toBe(true);
    });

    it("returns false when agent is not in any thinking set", () => {
      const map = new Map<string, Set<string>>([
        ["room-1", new Set(["a2", "a3"])],
      ]);
      expect(isAgentThinking(map, "a1")).toBe(false);
    });

    it("returns true when agent is thinking in any room", () => {
      const map = new Map<string, Set<string>>([
        ["room-1", new Set(["a2"])],
        ["room-2", new Set(["a1"])],
      ]);
      expect(isAgentThinking(map, "a1")).toBe(true);
    });

    it("returns false for empty map", () => {
      const map = new Map<string, Set<string>>();
      expect(isAgentThinking(map, "a1")).toBe(false);
    });

    it("returns false when all sets are empty", () => {
      const map = new Map<string, Set<string>>([
        ["room-1", new Set()],
        ["room-2", new Set()],
      ]);
      expect(isAgentThinking(map, "a1")).toBe(false);
    });
  });
});
