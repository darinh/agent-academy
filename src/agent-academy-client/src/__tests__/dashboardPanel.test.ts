import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import type {
  WorkspaceOverview,
  RoomSnapshot,
  CollaborationPhase,
  AgentDefinition,
  ActivityEvent,
} from "../api";
import {
  phaseColor,
  loadTimeRange,
  saveTimeRange,
  TIME_RANGES,
  TIME_RANGE_KEY,
} from "../dashboardUtils";
import type { TimeRange } from "../dashboardUtils";

// ── Factories ──

function makeAgent(overrides: Partial<AgentDefinition> = {}): AgentDefinition {
  return {
    id: "agent-1",
    name: "Software Engineer",
    role: "engineer",
    summary: "Writes code",
    startupPrompt: "You are an engineer.",
    model: null,
    capabilityTags: [],
    enabledTools: [],
    autoJoinDefaultRoom: true,
    gitIdentity: null,
    ...overrides,
  };
}

function makeRoom(overrides: Partial<RoomSnapshot> = {}): RoomSnapshot {
  return {
    id: "room-1",
    name: "Main Room",
    topic: null,
    status: "Active",
    currentPhase: "Planning",
    activeTask: null,
    participants: [],
    recentMessages: [],
    createdAt: "2026-04-01T00:00:00Z",
    updatedAt: "2026-04-01T01:00:00Z",
    ...overrides,
  };
}

function makeEvent(overrides: Partial<ActivityEvent> = {}): ActivityEvent {
  return {
    id: "evt-1",
    type: "MessagePosted",
    severity: "Info",
    roomId: "room-1",
    actorId: "agent-1",
    taskId: null,
    ...overrides,
  } as ActivityEvent;
}

function makeOverview(overrides: Partial<WorkspaceOverview> = {}): WorkspaceOverview {
  return {
    configuredAgents: [makeAgent()],
    rooms: [makeRoom()],
    recentActivity: [makeEvent()],
    agentLocations: [],
    breakoutRooms: [],
    generatedAt: "2026-04-01T00:00:00Z",
    ...overrides,
  };
}

// ── Tests ──

describe("DashboardPanel", () => {
  describe("phaseColor mapping", () => {
    it("maps every CollaborationPhase to a badge color", () => {
      expect(phaseColor("Intake")).toBe("informative");
      expect(phaseColor("Planning")).toBe("warning");
      expect(phaseColor("Discussion")).toBe("important");
      expect(phaseColor("Validation")).toBe("severe");
      expect(phaseColor("Implementation")).toBe("success");
      expect(phaseColor("FinalSynthesis")).toBe("subtle");
    });

    it("returns distinct colors for each phase", () => {
      const phases: CollaborationPhase[] = [
        "Intake", "Planning", "Discussion",
        "Validation", "Implementation", "FinalSynthesis",
      ];
      const colors = phases.map(phaseColor);
      expect(new Set(colors).size).toBe(6);
    });
  });

  describe("stat card computation", () => {
    it("counts rooms from overview", () => {
      const ov = makeOverview({
        rooms: [makeRoom({ id: "r1" }), makeRoom({ id: "r2" }), makeRoom({ id: "r3" })],
      });
      expect(ov.rooms.length).toBe(3);
    });

    it("counts agents from overview", () => {
      const ov = makeOverview({
        configuredAgents: [
          makeAgent({ id: "a1" }),
          makeAgent({ id: "a2" }),
        ],
      });
      expect(ov.configuredAgents.length).toBe(2);
    });

    it("counts active tasks (rooms with activeTask != null)", () => {
      const ov = makeOverview({
        rooms: [
          makeRoom({ id: "r1", activeTask: { id: "t1", title: "Fix bug" } as RoomSnapshot["activeTask"] }),
          makeRoom({ id: "r2", activeTask: null }),
          makeRoom({ id: "r3", activeTask: { id: "t2", title: "Add feature" } as RoomSnapshot["activeTask"] }),
        ],
      });
      const activeTasks = ov.rooms.filter((r) => r.activeTask).length;
      expect(activeTasks).toBe(2);
    });

    it("counts zero active tasks when all rooms have no task", () => {
      const ov = makeOverview({
        rooms: [
          makeRoom({ activeTask: null }),
          makeRoom({ id: "r2", activeTask: null }),
        ],
      });
      const activeTasks = ov.rooms.filter((r) => r.activeTask).length;
      expect(activeTasks).toBe(0);
    });

    it("counts recent events from overview", () => {
      const ov = makeOverview({
        recentActivity: [makeEvent(), makeEvent({ id: "evt-2" }), makeEvent({ id: "evt-3" })],
      });
      expect(ov.recentActivity.length).toBe(3);
    });

    it("handles empty overview gracefully", () => {
      const ov = makeOverview({
        configuredAgents: [],
        rooms: [],
        recentActivity: [],
      });
      expect(ov.configuredAgents.length).toBe(0);
      expect(ov.rooms.length).toBe(0);
      expect(ov.recentActivity.length).toBe(0);
      expect(ov.rooms.filter((r) => r.activeTask).length).toBe(0);
    });
  });

  describe("phase distribution", () => {
    it("builds phase counts from rooms", () => {
      const rooms = [
        makeRoom({ id: "r1", currentPhase: "Planning" }),
        makeRoom({ id: "r2", currentPhase: "Planning" }),
        makeRoom({ id: "r3", currentPhase: "Implementation" }),
        makeRoom({ id: "r4", currentPhase: "Intake" }),
      ];

      const phaseCounts = new Map<CollaborationPhase, number>();
      for (const room of rooms) {
        phaseCounts.set(room.currentPhase, (phaseCounts.get(room.currentPhase) ?? 0) + 1);
      }

      expect(phaseCounts.get("Planning")).toBe(2);
      expect(phaseCounts.get("Implementation")).toBe(1);
      expect(phaseCounts.get("Intake")).toBe(1);
      expect(phaseCounts.has("Discussion")).toBe(false);
      expect(phaseCounts.size).toBe(3);
    });

    it("returns empty map for no rooms", () => {
      const phaseCounts = new Map<CollaborationPhase, number>();
      expect(phaseCounts.size).toBe(0);
    });

    it("handles single room", () => {
      const rooms = [makeRoom({ currentPhase: "FinalSynthesis" })];
      const phaseCounts = new Map<CollaborationPhase, number>();
      for (const room of rooms) {
        phaseCounts.set(room.currentPhase, (phaseCounts.get(room.currentPhase) ?? 0) + 1);
      }
      expect(phaseCounts.get("FinalSynthesis")).toBe(1);
      expect(phaseCounts.size).toBe(1);
    });

    it("handles all rooms in same phase", () => {
      const rooms = [
        makeRoom({ id: "r1", currentPhase: "Validation" }),
        makeRoom({ id: "r2", currentPhase: "Validation" }),
        makeRoom({ id: "r3", currentPhase: "Validation" }),
      ];
      const phaseCounts = new Map<CollaborationPhase, number>();
      for (const room of rooms) {
        phaseCounts.set(room.currentPhase, (phaseCounts.get(room.currentPhase) ?? 0) + 1);
      }
      expect(phaseCounts.get("Validation")).toBe(3);
      expect(phaseCounts.size).toBe(1);
    });

    it("generates correct plural label", () => {
      function roomLabel(count: number): string {
        return `${count} room${count !== 1 ? "s" : ""}`;
      }
      expect(roomLabel(1)).toBe("1 room");
      expect(roomLabel(2)).toBe("2 rooms");
      expect(roomLabel(0)).toBe("0 rooms");
      expect(roomLabel(100)).toBe("100 rooms");
    });
  });

  describe("TIME_RANGES constant", () => {
    it("has exactly 4 entries", () => {
      expect(TIME_RANGES).toHaveLength(4);
    });

    it("maps labels to correct hour values", () => {
      expect(TIME_RANGES[0]).toEqual({ label: "24h", value: 24 });
      expect(TIME_RANGES[1]).toEqual({ label: "7d", value: 168 });
      expect(TIME_RANGES[2]).toEqual({ label: "30d", value: 720 });
      expect(TIME_RANGES[3]).toEqual({ label: "All", value: undefined });
    });

    it("7d = 168 hours", () => {
      expect(7 * 24).toBe(168);
      expect(TIME_RANGES[1].value).toBe(168);
    });

    it("30d = 720 hours", () => {
      expect(30 * 24).toBe(720);
      expect(TIME_RANGES[2].value).toBe(720);
    });
  });

  describe("time range persistence", () => {
    let storage: Record<string, string>;

    beforeEach(() => {
      storage = {};
      vi.stubGlobal("localStorage", {
        getItem: vi.fn((key: string) => storage[key] ?? null),
        setItem: vi.fn((key: string, value: string) => { storage[key] = value; }),
        removeItem: vi.fn((key: string) => { delete storage[key]; }),
      });
    });

    afterEach(() => {
      vi.unstubAllGlobals();
    });

    it("saveTimeRange stores 24 as '24'", () => {
      saveTimeRange(24);
      expect(storage[TIME_RANGE_KEY]).toBe("24");
    });

    it("saveTimeRange stores 168 as '168'", () => {
      saveTimeRange(168);
      expect(storage[TIME_RANGE_KEY]).toBe("168");
    });

    it("saveTimeRange stores 720 as '720'", () => {
      saveTimeRange(720);
      expect(storage[TIME_RANGE_KEY]).toBe("720");
    });

    it("saveTimeRange stores undefined as 'all'", () => {
      saveTimeRange(undefined);
      expect(storage[TIME_RANGE_KEY]).toBe("all");
    });

    it("loadTimeRange returns 24 when stored", () => {
      storage[TIME_RANGE_KEY] = "24";
      expect(loadTimeRange()).toBe(24);
    });

    it("loadTimeRange returns 168 when stored", () => {
      storage[TIME_RANGE_KEY] = "168";
      expect(loadTimeRange()).toBe(168);
    });

    it("loadTimeRange returns 720 when stored", () => {
      storage[TIME_RANGE_KEY] = "720";
      expect(loadTimeRange()).toBe(720);
    });

    it("loadTimeRange returns undefined for 'all'", () => {
      storage[TIME_RANGE_KEY] = "all";
      expect(loadTimeRange()).toBeUndefined();
    });

    it("loadTimeRange returns undefined when key is absent", () => {
      expect(loadTimeRange()).toBeUndefined();
    });

    it("loadTimeRange returns undefined for invalid stored values", () => {
      storage[TIME_RANGE_KEY] = "garbage";
      expect(loadTimeRange()).toBeUndefined();
    });

    it("loadTimeRange returns undefined for non-allowed number", () => {
      storage[TIME_RANGE_KEY] = "48";
      expect(loadTimeRange()).toBeUndefined();
    });

    it("round-trips correctly for each valid value", () => {
      for (const v of [24, 168, 720, undefined] as TimeRange[]) {
        saveTimeRange(v);
        expect(loadTimeRange()).toBe(v);
      }
    });

    it("handles localStorage exceptions gracefully", () => {
      vi.stubGlobal("localStorage", {
        getItem: () => { throw new Error("SecurityError"); },
        setItem: () => { throw new Error("QuotaExceededError"); },
      });
      // Should not throw
      expect(loadTimeRange()).toBeUndefined();
      expect(() => saveTimeRange(24)).not.toThrow();
    });
  });

  describe("WorkspaceOverview shape", () => {
    it("represents a full overview with all fields", () => {
      const ov = makeOverview();
      expect(ov.configuredAgents).toHaveLength(1);
      expect(ov.rooms).toHaveLength(1);
      expect(ov.recentActivity).toHaveLength(1);
      expect(ov.agentLocations).toEqual([]);
      expect(ov.breakoutRooms).toEqual([]);
      expect(ov.generatedAt).toBe("2026-04-01T00:00:00Z");
    });

    it("rooms include phase and task fields needed by dashboard", () => {
      const room = makeRoom({
        currentPhase: "Implementation",
        activeTask: { id: "t1", title: "Test" } as RoomSnapshot["activeTask"],
      });
      expect(room.currentPhase).toBe("Implementation");
      expect(room.activeTask).not.toBeNull();
    });
  });
});
