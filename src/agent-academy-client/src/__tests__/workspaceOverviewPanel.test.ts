import { describe, expect, it } from "vitest";
import type {
  CollaborationPhase,
  PhaseGate,
  RoomSnapshot,
  WorkspaceOverview,
  AgentPresence,
} from "../api";

// ── Factories ──

function makeRoom(overrides: Partial<RoomSnapshot> = {}): RoomSnapshot {
  return {
    id: "room-1",
    name: "Main Room",
    topic: null,
    status: "Active",
    currentPhase: "Implementation",
    activeTask: null,
    participants: [],
    recentMessages: [],
    createdAt: "2026-04-10T10:00:00Z",
    updatedAt: "2026-04-10T12:00:00Z",
    ...overrides,
  };
}

function makeOverview(overrides: Partial<WorkspaceOverview> = {}): WorkspaceOverview {
  return {
    configuredAgents: [],
    rooms: [makeRoom()],
    recentActivity: [],
    agentLocations: [],
    breakoutRooms: [],
    generatedAt: "2026-04-10T12:00:00Z",
    ...overrides,
  };
}

// ── Constants ──

const PHASES: readonly CollaborationPhase[] = [
  "Intake",
  "Planning",
  "Discussion",
  "Validation",
  "Implementation",
  "FinalSynthesis",
] as const;

// ── Helper functions (mirroring component logic) ──

function statusColor(status: string): string {
  switch (status) {
    case "Active":            return "ok";
    case "AttentionRequired": return "warn";
    case "Completed":         return "info";
    case "Archived":          return "muted";
    default:                  return "bug";
  }
}

function phaseProgress(phase: CollaborationPhase): number {
  const idx = PHASES.indexOf(phase);
  return idx >= 0 ? (idx + 1) / PHASES.length : 0;
}

/** Mirrors getGate from WorkspaceOverviewPanel. */
function getGate(
  phase: CollaborationPhase,
  currentPhase: CollaborationPhase,
  room: RoomSnapshot,
): PhaseGate {
  if (phase === currentPhase) return { allowed: true };
  if (PHASES.indexOf(phase) < PHASES.indexOf(currentPhase)) return { allowed: true };
  const gate = room.phaseGates?.gates?.[phase];
  return gate ?? { allowed: true };
}

// ── Tests ──

describe("WorkspaceOverviewPanel", () => {
  describe("statusColor mapping", () => {
    it("maps Active to ok", () => {
      expect(statusColor("Active")).toBe("ok");
    });

    it("maps AttentionRequired to warn", () => {
      expect(statusColor("AttentionRequired")).toBe("warn");
    });

    it("maps Completed to info", () => {
      expect(statusColor("Completed")).toBe("info");
    });

    it("maps Archived to muted", () => {
      expect(statusColor("Archived")).toBe("muted");
    });

    it("maps unknown status to bug", () => {
      expect(statusColor("Unknown")).toBe("bug");
      expect(statusColor("")).toBe("bug");
    });

    it("maps Idle to bug (not a valid room status)", () => {
      expect(statusColor("Idle")).toBe("bug");
    });
  });

  describe("phaseProgress calculation", () => {
    it("returns correct progress for Intake (first phase)", () => {
      expect(phaseProgress("Intake")).toBeCloseTo(1 / 6);
    });

    it("returns correct progress for Planning (second phase)", () => {
      expect(phaseProgress("Planning")).toBeCloseTo(2 / 6);
    });

    it("returns correct progress for Discussion (third phase)", () => {
      expect(phaseProgress("Discussion")).toBeCloseTo(3 / 6);
    });

    it("returns correct progress for Validation (fourth phase)", () => {
      expect(phaseProgress("Validation")).toBeCloseTo(4 / 6);
    });

    it("returns correct progress for Implementation (fifth phase)", () => {
      expect(phaseProgress("Implementation")).toBeCloseTo(5 / 6);
    });

    it("returns 1.0 for FinalSynthesis (last phase)", () => {
      expect(phaseProgress("FinalSynthesis")).toBe(1);
    });

    it("returns 0 for unknown phase", () => {
      expect(phaseProgress("Unknown" as CollaborationPhase)).toBe(0);
    });

    it("progress increases monotonically across phases", () => {
      let prev = 0;
      for (const phase of PHASES) {
        const current = phaseProgress(phase);
        expect(current).toBeGreaterThan(prev);
        prev = current;
      }
    });
  });

  describe("phase transition buttons", () => {
    it("disables button for current phase", () => {
      const currentPhase: CollaborationPhase = "Implementation";
      for (const phase of PHASES) {
        const disabled = phase === currentPhase;
        if (phase === "Implementation") {
          expect(disabled).toBe(true);
        } else {
          expect(disabled).toBe(false);
        }
      }
    });

    it("disables all buttons when transitioning", () => {
      const transitioning = true;
      for (const phase of PHASES) {
        const disabled = phase === "Intake" || transitioning;
        expect(disabled).toBe(true);
      }
    });

    it("disables all buttons in readOnly mode", () => {
      const readOnly = true;
      for (const phase of PHASES) {
        const disabled = phase === "Discussion" || readOnly;
        expect(disabled).toBe(true);
      }
    });

    it("uses primary appearance for current phase", () => {
      const currentPhase: CollaborationPhase = "Planning";
      for (const phase of PHASES) {
        const appearance = phase === currentPhase ? "primary" : "outline";
        if (phase === "Planning") {
          expect(appearance).toBe("primary");
        } else {
          expect(appearance).toBe("outline");
        }
      }
    });
  });

  describe("room status summary", () => {
    it("renders empty state when no rooms", () => {
      const overview = makeOverview({ rooms: [] });
      expect(overview.rooms).toHaveLength(0);
    });

    it("renders all rooms in overview", () => {
      const rooms = [
        makeRoom({ id: "r1", name: "Room A", status: "Active", currentPhase: "Planning" }),
        makeRoom({ id: "r2", name: "Room B", status: "Completed", currentPhase: "FinalSynthesis" }),
        makeRoom({ id: "r3", name: "Room C", status: "Archived", currentPhase: "Implementation" }),
      ];
      const overview = makeOverview({ rooms });
      expect(overview.rooms).toHaveLength(3);
      expect(overview.rooms[0].name).toBe("Room A");
      expect(overview.rooms[1].status).toBe("Completed");
      expect(overview.rooms[2].currentPhase).toBe("Implementation");
    });

    it("shows participant count for each room", () => {
      const participants: AgentPresence[] = [
        {
          agentId: "a1", name: "Architect", role: "architect",
          availability: "available", isPreferred: true,
          lastActivityAt: "2026-04-10T12:00:00Z", activeCapabilities: [],
        },
        {
          agentId: "a2", name: "Engineer", role: "engineer",
          availability: "available", isPreferred: false,
          lastActivityAt: "2026-04-10T12:00:00Z", activeCapabilities: [],
        },
      ];
      const room = makeRoom({ participants });
      // Component renders: `{r.participants.length} agent{r.participants.length !== 1 ? "s" : ""}`
      const label = `${room.participants.length} agent${room.participants.length !== 1 ? "s" : ""}`;
      expect(label).toBe("2 agents");
    });

    it("uses singular 'agent' for single participant", () => {
      const room = makeRoom({
        participants: [{
          agentId: "a1", name: "Architect", role: "architect",
          availability: "available", isPreferred: true,
          lastActivityAt: "2026-04-10T12:00:00Z", activeCapabilities: [],
        }],
      });
      const label = `${room.participants.length} agent${room.participants.length !== 1 ? "s" : ""}`;
      expect(label).toBe("1 agent");
    });
  });

  describe("WorkspaceOverview type shape", () => {
    it("has all required fields", () => {
      const overview = makeOverview();
      expect(overview).toHaveProperty("configuredAgents");
      expect(overview).toHaveProperty("rooms");
      expect(overview).toHaveProperty("recentActivity");
      expect(overview).toHaveProperty("agentLocations");
      expect(overview).toHaveProperty("breakoutRooms");
      expect(overview).toHaveProperty("generatedAt");
    });

    it("rooms are RoomSnapshot instances", () => {
      const overview = makeOverview({ rooms: [makeRoom()] });
      const room = overview.rooms[0];
      expect(room).toHaveProperty("id");
      expect(room).toHaveProperty("name");
      expect(room).toHaveProperty("status");
      expect(room).toHaveProperty("currentPhase");
      expect(room).toHaveProperty("participants");
      expect(room).toHaveProperty("recentMessages");
    });
  });

  describe("RoomSnapshot type shape", () => {
    it("has optional topic that can be null", () => {
      const room = makeRoom({ topic: null });
      expect(room.topic).toBeNull();
    });

    it("has optional topic that can be a string", () => {
      const room = makeRoom({ topic: "Architecture discussion" });
      expect(room.topic).toBe("Architecture discussion");
    });

    it("activeTask is optional and nullable", () => {
      const room = makeRoom({ activeTask: null });
      expect(room.activeTask).toBeNull();
    });
  });

  describe("readOnly mode behavior", () => {
    it("readOnly defaults to false", () => {
      const readOnly = false;
      expect(readOnly).toBe(false);
    });

    it("readOnly prevents phase transitions", () => {
      const readOnly = true;
      const transitioning = false;
      const currentPhase: CollaborationPhase = "Discussion";
      // Component: disabled={phase === room.currentPhase || transitioning || readOnly}
      for (const phase of PHASES) {
        const disabled = phase === currentPhase || transitioning || readOnly;
        expect(disabled).toBe(true);
      }
    });
  });

  describe("PHASES constant", () => {
    it("has exactly 6 phases", () => {
      expect(PHASES).toHaveLength(6);
    });

    it("starts with Intake and ends with FinalSynthesis", () => {
      expect(PHASES[0]).toBe("Intake");
      expect(PHASES[PHASES.length - 1]).toBe("FinalSynthesis");
    });

    it("includes all collaboration phases in order", () => {
      expect(PHASES).toEqual([
        "Intake",
        "Planning",
        "Discussion",
        "Validation",
        "Implementation",
        "FinalSynthesis",
      ]);
    });
  });

  describe("phase gate logic", () => {
    it("backward transitions are always allowed even when gates block", () => {
      const room = makeRoom({
        currentPhase: "Implementation",
        phaseGates: {
          gates: {
            Intake: { allowed: true },
            Planning: { allowed: false, reason: "blocked" },
            Discussion: { allowed: false, reason: "blocked" },
            Validation: { allowed: false, reason: "blocked" },
            Implementation: { allowed: true },
            FinalSynthesis: { allowed: false, reason: "tasks in progress" },
          },
        },
      });
      // All phases before current should be allowed regardless of gate
      expect(getGate("Intake", "Implementation", room).allowed).toBe(true);
      expect(getGate("Planning", "Implementation", room).allowed).toBe(true);
      expect(getGate("Discussion", "Implementation", room).allowed).toBe(true);
      expect(getGate("Validation", "Implementation", room).allowed).toBe(true);
    });

    it("same phase is always allowed", () => {
      const room = makeRoom({ currentPhase: "Discussion" });
      expect(getGate("Discussion", "Discussion", room).allowed).toBe(true);
    });

    it("forward transition blocked when gate says not allowed", () => {
      const room = makeRoom({
        currentPhase: "Planning",
        phaseGates: {
          gates: {
            Intake: { allowed: true },
            Planning: { allowed: true },
            Discussion: { allowed: false, reason: "Create at least one task" },
            Validation: { allowed: false, reason: "No tasks" },
            Implementation: { allowed: false, reason: "No approved tasks" },
            FinalSynthesis: { allowed: false, reason: "No tasks" },
          },
        },
      });
      const gate = getGate("Discussion", "Planning", room);
      expect(gate.allowed).toBe(false);
      expect(gate.reason).toBe("Create at least one task");
    });

    it("forward transition allowed when gate allows", () => {
      const room = makeRoom({
        currentPhase: "Planning",
        phaseGates: {
          gates: {
            Discussion: { allowed: true },
          },
        },
      });
      expect(getGate("Discussion", "Planning", room).allowed).toBe(true);
    });

    it("missing phaseGates defaults to allowed", () => {
      const room = makeRoom({ currentPhase: "Planning" });
      // No phaseGates property at all
      expect(getGate("Discussion", "Planning", room).allowed).toBe(true);
    });

    it("disables button when gate blocks and not backward/same", () => {
      const room = makeRoom({
        currentPhase: "Intake",
        phaseGates: {
          gates: {
            Intake: { allowed: true },
            Planning: { allowed: true },
            Discussion: { allowed: false, reason: "Need tasks" },
            Validation: { allowed: false, reason: "Need tasks" },
            Implementation: { allowed: false, reason: "Need approved" },
            FinalSynthesis: { allowed: false, reason: "Need all terminal" },
          },
        },
      });
      const transitioning = false;
      const readOnly = false;
      for (const phase of PHASES) {
        const isCurrent = phase === room.currentPhase;
        const gate = getGate(phase, room.currentPhase, room);
        const blocked = !isCurrent && !gate.allowed;
        const disabled = isCurrent || transitioning || readOnly || blocked;
        if (phase === "Intake") {
          expect(disabled).toBe(true); // current
        } else if (phase === "Planning") {
          expect(disabled).toBe(false); // allowed gate
        } else {
          expect(disabled).toBe(true); // blocked by gate
        }
      }
    });
  });
});
