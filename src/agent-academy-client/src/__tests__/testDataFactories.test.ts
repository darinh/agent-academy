import { describe, expect, it, beforeEach } from "vitest";
import {
  makeAgent,
  makePresence,
  makeLocation,
  makeMessage,
  makeTask,
  makeEvent,
  makeRoom,
  makeBreakoutRoom,
  makeGoalCardSummary,
  makeOverview,
  makeSprint,
  makeArtifact,
  makeErrorRecord,
  makeDmMessage,
  resetFactories,
} from "./helpers/testData";

describe("testData factories", () => {
  beforeEach(() => resetFactories());

  // ── Unique IDs ─────────────────────────────────────────────────────

  it("generates unique IDs across sequential calls", () => {
    const a1 = makeAgent();
    const a2 = makeAgent();
    expect(a1.id).not.toBe(a2.id);

    const m1 = makeMessage();
    const m2 = makeMessage();
    expect(m1.id).not.toBe(m2.id);
  });

  it("resets counters between tests", () => {
    const a = makeAgent();
    expect(a.id).toBe("agent-1");
  });

  // ── Override pattern ───────────────────────────────────────────────

  it("applies overrides to factory defaults", () => {
    const agent = makeAgent({ name: "Custom", role: "qa" });
    expect(agent.name).toBe("Custom");
    expect(agent.role).toBe("qa");
    // Non-overridden fields keep defaults
    expect(agent.autoJoinDefaultRoom).toBe(true);
  });

  // ── Individual factory completeness ────────────────────────────────

  it("makeAgent returns a complete AgentDefinition", () => {
    const a = makeAgent();
    expect(a.id).toBeDefined();
    expect(a.name).toBeDefined();
    expect(a.role).toBeDefined();
    expect(a.summary).toBeDefined();
    expect(a.startupPrompt).toBeDefined();
    expect(a.capabilityTags).toEqual([]);
    expect(a.enabledTools).toEqual([]);
    expect(a.autoJoinDefaultRoom).toBe(true);
  });

  it("makePresence returns a complete AgentPresence", () => {
    const p = makePresence();
    expect(p.agentId).toBeDefined();
    expect(p.name).toBeDefined();
    expect(p.availability).toBe("Available");
    expect(p.activeCapabilities).toEqual([]);
  });

  it("makeLocation returns a complete AgentLocation", () => {
    const l = makeLocation();
    expect(l.agentId).toBeDefined();
    expect(l.roomId).toBeDefined();
    expect(l.state).toBe("Idle");
  });

  it("makeMessage returns a complete ChatEnvelope", () => {
    const m = makeMessage();
    expect(m.id).toBeDefined();
    expect(m.roomId).toBeDefined();
    expect(m.senderId).toBeDefined();
    expect(m.senderName).toBeDefined();
    expect(m.senderKind).toBe("Agent");
    expect(m.kind).toBe("text");
    expect(m.content).toBeDefined();
    expect(m.sentAt).toBeDefined();
  });

  it("makeTask returns a complete TaskSnapshot", () => {
    const t = makeTask();
    expect(t.id).toBeDefined();
    expect(t.title).toBeDefined();
    expect(t.status).toBe("Queued");
    expect(t.currentPhase).toBe("Planning");
    expect(t.preferredRoles).toEqual([]);
    expect(t.type).toBe("Feature");
    expect(t.priority).toBe("Medium");
  });

  it("makeEvent returns a complete ActivityEvent", () => {
    const e = makeEvent();
    expect(e.id).toBeDefined();
    expect(e.type).toBe("MessagePosted");
    expect(e.severity).toBe("Info");
    expect(e.message).toBeDefined();
    expect(e.occurredAt).toBeDefined();
  });

  it("makeRoom returns a complete RoomSnapshot", () => {
    const r = makeRoom();
    expect(r.id).toBeDefined();
    expect(r.name).toBeDefined();
    expect(r.status).toBe("Active");
    expect(r.currentPhase).toBe("Planning");
    expect(r.participants).toEqual([]);
    expect(r.recentMessages).toEqual([]);
  });

  it("makeBreakoutRoom returns a complete BreakoutRoom", () => {
    const b = makeBreakoutRoom();
    expect(b.id).toBeDefined();
    expect(b.parentRoomId).toBeDefined();
    expect(b.assignedAgentId).toBeDefined();
    expect(b.tasks).toEqual([]);
    expect(b.status).toBe("Active");
  });

  it("makeGoalCardSummary returns zeroed summary", () => {
    const g = makeGoalCardSummary();
    expect(g.total).toBe(0);
    expect(g.active).toBe(0);
    expect(g.challenged).toBe(0);
    expect(g.completed).toBe(0);
  });

  it("makeOverview returns a complete WorkspaceOverview", () => {
    const o = makeOverview();
    expect(o.configuredAgents).toEqual([]);
    expect(o.rooms).toEqual([]);
    expect(o.recentActivity).toEqual([]);
    expect(o.agentLocations).toEqual([]);
    expect(o.breakoutRooms).toEqual([]);
    expect(o.goalCards).toBeDefined();
    expect(o.generatedAt).toBeDefined();
  });

  it("makeSprint returns a complete SprintSnapshot", () => {
    const s = makeSprint();
    expect(s.id).toBeDefined();
    expect(s.number).toBeGreaterThan(0);
    expect(s.status).toBe("Active");
    expect(s.currentStage).toBe("Planning");
  });

  it("makeArtifact returns a complete SprintArtifact", () => {
    const a = makeArtifact();
    expect(a.id).toBeGreaterThan(0);
    expect(a.sprintId).toBeDefined();
    expect(a.type).toBe("SprintPlan");
    expect(a.content).toBeDefined();
  });

  it("makeErrorRecord returns a complete ErrorRecord", () => {
    const e = makeErrorRecord();
    expect(e.agentId).toBeDefined();
    expect(e.errorType).toBe("RuntimeError");
    expect(e.message).toBeDefined();
    expect(e.recoverable).toBe(true);
  });

  it("makeDmMessage returns a complete DmMessage", () => {
    const d = makeDmMessage();
    expect(d.id).toBeDefined();
    expect(d.senderId).toBeDefined();
    expect(d.content).toBeDefined();
    expect(d.isFromHuman).toBe(true);
  });

  // ── Composition ────────────────────────────────────────────────────

  it("factories compose for complex test data", () => {
    const agent = makeAgent({ id: "arch-1", name: "Architect" });
    const presence = makePresence({ agentId: agent.id, name: agent.name });
    const room = makeRoom({
      participants: [presence],
      recentMessages: [makeMessage({ senderId: agent.id })],
    });
    const overview = makeOverview({
      configuredAgents: [agent],
      rooms: [room],
    });

    expect(overview.rooms[0].participants[0].agentId).toBe("arch-1");
    expect(overview.rooms[0].recentMessages[0].senderId).toBe("arch-1");
    expect(overview.configuredAgents[0].name).toBe("Architect");
  });
});
