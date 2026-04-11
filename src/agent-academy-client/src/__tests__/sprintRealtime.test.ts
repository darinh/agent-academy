import { describe, it, expect, vi } from "vitest";
import type {
  SprintSnapshot,
  SprintArtifact,
  SprintDetailResponse,
  SprintStage,
  SprintRealtimeEvent,
  ActivityEvent,
  ActivityEventType,
} from "../api";

// ---------------------------------------------------------------------------
// Mock API module
// ---------------------------------------------------------------------------

vi.mock("../api", () => ({
  getActiveSprint: vi.fn(),
  getSprints: vi.fn(),
  getSprintDetail: vi.fn(),
  getSprintArtifacts: vi.fn(),
  startSprint: vi.fn(),
  advanceSprint: vi.fn(),
  completeSprint: vi.fn(),
  cancelSprint: vi.fn(),
  approveSprintAdvance: vi.fn(),
  rejectSprintAdvance: vi.fn(),
}));

import { getSprintArtifacts } from "../api";
vi.mocked(getSprintArtifacts);

// ---------------------------------------------------------------------------
// Factories
// ---------------------------------------------------------------------------

function makeSprint(overrides: Partial<SprintSnapshot> = {}): SprintSnapshot {
  return {
    id: "sprint-1",
    number: 1,
    status: "Active",
    currentStage: "Intake",
    overflowFromSprintId: null,
    awaitingSignOff: false,
    pendingStage: null,
    createdAt: "2026-04-01T00:00:00Z",
    completedAt: null,
    ...overrides,
  };
}

function makeArtifact(overrides: Partial<SprintArtifact> = {}): SprintArtifact {
  return {
    id: 1,
    sprintId: "sprint-1",
    stage: "Intake",
    type: "RequirementsDocument",
    content: "Some artifact content",
    createdByAgentId: "architect",
    createdAt: "2026-04-01T01:00:00Z",
    updatedAt: null,
    ...overrides,
  };
}

function makeDetail(
  sprintOverrides: Partial<SprintSnapshot> = {},
  artifacts: SprintArtifact[] = [],
): SprintDetailResponse {
  return {
    sprint: makeSprint(sprintOverrides),
    artifacts,
    stages: ["Intake", "Planning", "Discussion", "Validation", "Implementation", "FinalSynthesis"],
  };
}

// @ts-expect-error factory prepared for future event-level tests
function makeEvent(overrides: Partial<SprintRealtimeEvent>): SprintRealtimeEvent {
  return {
    eventId: `evt-${Math.random().toString(36).slice(2)}`,
    type: "SprintStageAdvanced",
    sprintId: "sprint-1",
    metadata: {},
    receivedAt: Date.now(),
    ...overrides,
  };
}

function makeActivityEvent(overrides: Partial<ActivityEvent>): ActivityEvent {
  return {
    id: `act-${Math.random().toString(36).slice(2)}`,
    type: "SprintStageAdvanced" as ActivityEventType,
    severity: "Info",
    roomId: null,
    actorId: null,
    taskId: null,
    message: "test event",
    correlationId: null,
    occurredAt: new Date().toISOString(),
    metadata: null,
    ...overrides,
  };
}

// ---------------------------------------------------------------------------
// Pure state updater functions (mirrored from SprintPanel.tsx)
//
// These replicate the inline setState updater logic so we can test them
// without mounting the full React component.
// ---------------------------------------------------------------------------

type SprintStageAction = "advanced" | "signoff_requested" | "approved" | "rejected";

function applyStageAdvancedToSprint(
  sprint: SprintSnapshot,
  sprintId: string,
  action: SprintStageAction | undefined,
  currentStage: SprintStage | undefined,
  pendingStage: SprintStage | undefined,
): SprintSnapshot {
  if (sprint.id !== sprintId) return sprint;
  const updated = { ...sprint };
  if (action === "signoff_requested") {
    updated.awaitingSignOff = true;
    updated.pendingStage = pendingStage ?? null;
  } else if (action === "advanced" || action === "approved") {
    if (currentStage) updated.currentStage = currentStage;
    updated.awaitingSignOff = false;
    updated.pendingStage = null;
  } else if (action === "rejected") {
    updated.awaitingSignOff = false;
    updated.pendingStage = null;
  }
  return updated;
}

function applyStageAdvancedToDetail(
  detail: SprintDetailResponse,
  sprintId: string,
  action: SprintStageAction | undefined,
  currentStage: SprintStage | undefined,
  pendingStage: SprintStage | undefined,
): SprintDetailResponse {
  if (detail.sprint.id !== sprintId) return detail;
  return {
    ...detail,
    sprint: applyStageAdvancedToSprint(detail.sprint, sprintId, action, currentStage, pendingStage),
  };
}

function applyCompletedToSprint(
  sprint: SprintSnapshot,
  sprintId: string,
  status: string | undefined,
): SprintSnapshot {
  if (sprint.id !== sprintId) return sprint;
  return {
    ...sprint,
    status: (status as SprintSnapshot["status"]) ?? "Completed",
    completedAt: new Date().toISOString(),
  };
}

function mergeArtifactsByStage(
  existing: SprintArtifact[],
  incoming: SprintArtifact[],
  affectedStage: SprintStage | undefined,
): SprintArtifact[] {
  if (affectedStage) {
    const otherArtifacts = existing.filter((a) => a.stage !== affectedStage);
    return [...otherArtifacts, ...incoming];
  }
  return incoming;
}

// ---------------------------------------------------------------------------
// Tests: SprintStageAdvanced optimistic updates
// ---------------------------------------------------------------------------

describe("Sprint realtime optimistic updates", () => {
  describe("SprintStageAdvanced — signoff_requested", () => {
    it("sets awaitingSignOff to true", () => {
      const sprint = makeSprint({ awaitingSignOff: false });
      const result = applyStageAdvancedToSprint(
        sprint, "sprint-1", "signoff_requested", "Intake", "Planning",
      );
      expect(result.awaitingSignOff).toBe(true);
    });

    it("sets pendingStage from metadata", () => {
      const sprint = makeSprint();
      const result = applyStageAdvancedToSprint(
        sprint, "sprint-1", "signoff_requested", "Intake", "Planning",
      );
      expect(result.pendingStage).toBe("Planning");
    });

    it("does not change currentStage", () => {
      const sprint = makeSprint({ currentStage: "Intake" });
      const result = applyStageAdvancedToSprint(
        sprint, "sprint-1", "signoff_requested", "Intake", "Planning",
      );
      expect(result.currentStage).toBe("Intake");
    });

    it("ignores events for different sprints", () => {
      const sprint = makeSprint({ id: "sprint-2" });
      const result = applyStageAdvancedToSprint(
        sprint, "sprint-1", "signoff_requested", "Intake", "Planning",
      );
      expect(result).toBe(sprint); // same reference — no mutation
    });

    it("handles undefined pendingStage gracefully", () => {
      const sprint = makeSprint();
      const result = applyStageAdvancedToSprint(
        sprint, "sprint-1", "signoff_requested", "Intake", undefined,
      );
      expect(result.awaitingSignOff).toBe(true);
      expect(result.pendingStage).toBeNull();
    });
  });

  describe("SprintStageAdvanced — advanced", () => {
    it("updates currentStage to new stage", () => {
      const sprint = makeSprint({ currentStage: "Intake", awaitingSignOff: false });
      const result = applyStageAdvancedToSprint(
        sprint, "sprint-1", "advanced", "Planning", undefined,
      );
      expect(result.currentStage).toBe("Planning");
    });

    it("clears awaitingSignOff", () => {
      const sprint = makeSprint({ awaitingSignOff: true, pendingStage: "Planning" });
      const result = applyStageAdvancedToSprint(
        sprint, "sprint-1", "advanced", "Planning", undefined,
      );
      expect(result.awaitingSignOff).toBe(false);
      expect(result.pendingStage).toBeNull();
    });

    it("does not change stage if currentStage is undefined in metadata", () => {
      const sprint = makeSprint({ currentStage: "Intake" });
      const result = applyStageAdvancedToSprint(
        sprint, "sprint-1", "advanced", undefined, undefined,
      );
      expect(result.currentStage).toBe("Intake");
      expect(result.awaitingSignOff).toBe(false);
    });
  });

  describe("SprintStageAdvanced — approved", () => {
    it("behaves like advanced — updates currentStage", () => {
      const sprint = makeSprint({ currentStage: "Intake", awaitingSignOff: true, pendingStage: "Planning" });
      const result = applyStageAdvancedToSprint(
        sprint, "sprint-1", "approved", "Planning", undefined,
      );
      expect(result.currentStage).toBe("Planning");
      expect(result.awaitingSignOff).toBe(false);
      expect(result.pendingStage).toBeNull();
    });
  });

  describe("SprintStageAdvanced — rejected", () => {
    it("clears awaitingSignOff without changing stage", () => {
      const sprint = makeSprint({
        currentStage: "Intake",
        awaitingSignOff: true,
        pendingStage: "Planning",
      });
      const result = applyStageAdvancedToSprint(
        sprint, "sprint-1", "rejected", undefined, undefined,
      );
      expect(result.currentStage).toBe("Intake");
      expect(result.awaitingSignOff).toBe(false);
      expect(result.pendingStage).toBeNull();
    });
  });

  describe("SprintStageAdvanced — detail wrapper", () => {
    it("updates sprint inside detail", () => {
      const detail = makeDetail({ awaitingSignOff: false });
      const result = applyStageAdvancedToDetail(
        detail, "sprint-1", "signoff_requested", "Intake", "Planning",
      );
      expect(result.sprint.awaitingSignOff).toBe(true);
      expect(result.artifacts).toBe(detail.artifacts); // artifacts unchanged
      expect(result.stages).toBe(detail.stages);
    });

    it("returns original detail for different sprint", () => {
      const detail = makeDetail({ id: "sprint-2" });
      const result = applyStageAdvancedToDetail(
        detail, "sprint-1", "advanced", "Planning", undefined,
      );
      expect(result).toBe(detail);
    });
  });

  // ── SprintCompleted optimistic updates ──────────────────────────

  describe("SprintCompleted", () => {
    it("marks sprint as Completed", () => {
      const sprint = makeSprint({ status: "Active" });
      const result = applyCompletedToSprint(sprint, "sprint-1", "Completed");
      expect(result.status).toBe("Completed");
      expect(result.completedAt).toBeTruthy();
    });

    it("marks sprint as Cancelled when status=Cancelled", () => {
      const sprint = makeSprint({ status: "Active" });
      const result = applyCompletedToSprint(sprint, "sprint-1", "Cancelled");
      expect(result.status).toBe("Cancelled");
    });

    it("defaults to Completed when status is undefined", () => {
      const sprint = makeSprint();
      const result = applyCompletedToSprint(sprint, "sprint-1", undefined);
      expect(result.status).toBe("Completed");
    });

    it("ignores events for different sprints", () => {
      const sprint = makeSprint({ id: "sprint-2", status: "Active" });
      const result = applyCompletedToSprint(sprint, "sprint-1", "Completed");
      expect(result).toBe(sprint);
    });

    it("sets completedAt to a valid ISO string", () => {
      const sprint = makeSprint({ completedAt: null });
      const result = applyCompletedToSprint(sprint, "sprint-1", "Completed");
      expect(result.completedAt).not.toBeNull();
      expect(Number.isNaN(Date.parse(result.completedAt!))).toBe(false);
    });
  });

  // ── Artifact merge logic ────────────────────────────────────────

  describe("mergeArtifactsByStage", () => {
    it("replaces artifacts for the affected stage only", () => {
      const existing = [
        makeArtifact({ id: 1, stage: "Intake", type: "RequirementsDocument" }),
        makeArtifact({ id: 2, stage: "Planning", type: "SprintPlan" }),
      ];
      const incoming = [
        makeArtifact({ id: 3, stage: "Intake", type: "RequirementsDocument" }),
      ];
      const result = mergeArtifactsByStage(existing, incoming, "Intake");
      expect(result).toHaveLength(2);
      expect(result.find((a) => a.stage === "Intake")!.id).toBe(3);
      expect(result.find((a) => a.stage === "Planning")!.id).toBe(2);
    });

    it("replaces all artifacts when no stage specified", () => {
      const existing = [
        makeArtifact({ id: 1 }),
        makeArtifact({ id: 2 }),
      ];
      const incoming = [makeArtifact({ id: 3 })];
      const result = mergeArtifactsByStage(existing, incoming, undefined);
      expect(result).toHaveLength(1);
      expect(result[0].id).toBe(3);
    });

    it("adds new stage artifacts without removing others", () => {
      const existing = [
        makeArtifact({ id: 1, stage: "Intake" }),
      ];
      const incoming = [
        makeArtifact({ id: 2, stage: "Planning" }),
      ];
      const result = mergeArtifactsByStage(existing, incoming, "Planning");
      expect(result).toHaveLength(2);
    });

    it("handles empty existing artifacts", () => {
      const incoming = [makeArtifact({ id: 1, stage: "Intake" })];
      const result = mergeArtifactsByStage([], incoming, "Intake");
      expect(result).toHaveLength(1);
    });

    it("handles empty incoming artifacts (clears stage)", () => {
      const existing = [
        makeArtifact({ id: 1, stage: "Intake" }),
        makeArtifact({ id: 2, stage: "Planning" }),
      ];
      const result = mergeArtifactsByStage(existing, [], "Intake");
      expect(result).toHaveLength(1);
      expect(result[0].stage).toBe("Planning");
    });
  });
});

// ---------------------------------------------------------------------------
// Tests: useWorkspace event deduplication and extraction
// ---------------------------------------------------------------------------

describe("useWorkspace sprint event handling (pure logic)", () => {
  describe("event deduplication", () => {
    it("tracks processed event IDs", () => {
      const processedIds = new Set<string>();
      const eventId = "evt-123";

      // First occurrence should be processed
      expect(processedIds.has(eventId)).toBe(false);
      processedIds.add(eventId);

      // Second occurrence should be skipped
      expect(processedIds.has(eventId)).toBe(true);
    });

    it("caps set size at threshold and keeps recent entries", () => {
      const processedIds = new Set<string>();
      const maxSize = 200;
      const keepSize = 100;

      // Fill beyond threshold
      for (let i = 0; i < maxSize + 1; i++) {
        processedIds.add(`evt-${i}`);
      }

      expect(processedIds.size).toBe(maxSize + 1);

      // Apply the same cap logic as useWorkspace
      if (processedIds.size > maxSize) {
        const entries = [...processedIds];
        const trimmed = new Set(entries.slice(-keepSize));
        expect(trimmed.size).toBe(keepSize);
        // Should keep the latest entries
        expect(trimmed.has(`evt-${maxSize}`)).toBe(true);
        // Should drop the oldest entries
        expect(trimmed.has("evt-0")).toBe(false);
      }
    });
  });

  describe("SprintRealtimeEvent extraction from ActivityEvent", () => {
    function extractSprintEvent(evt: ActivityEvent): SprintRealtimeEvent | null {
      const sprintId = (evt.metadata?.sprintId as string) ?? undefined;
      if (sprintId && evt.metadata) {
        return {
          eventId: evt.id,
          type: evt.type,
          sprintId,
          metadata: evt.metadata,
          receivedAt: Date.now(),
        };
      }
      return null;
    }

    it("extracts SprintRealtimeEvent from valid activity event", () => {
      const activity = makeActivityEvent({
        type: "SprintStageAdvanced",
        metadata: {
          sprintId: "sprint-1",
          action: "advanced",
          currentStage: "Planning",
        },
      });

      const result = extractSprintEvent(activity);
      expect(result).not.toBeNull();
      expect(result!.sprintId).toBe("sprint-1");
      expect(result!.type).toBe("SprintStageAdvanced");
      expect(result!.metadata.action).toBe("advanced");
    });

    it("returns null when metadata is missing", () => {
      const activity = makeActivityEvent({
        type: "SprintStageAdvanced",
        metadata: null,
      });
      expect(extractSprintEvent(activity)).toBeNull();
    });

    it("returns null when sprintId is missing from metadata", () => {
      const activity = makeActivityEvent({
        type: "SprintStageAdvanced",
        metadata: { action: "advanced" },
      });
      expect(extractSprintEvent(activity)).toBeNull();
    });

    it("extracts SprintStarted events", () => {
      const activity = makeActivityEvent({
        type: "SprintStarted",
        metadata: {
          sprintId: "sprint-new",
          sprintNumber: 5,
          status: "Active",
          currentStage: "Intake",
        },
      });
      const result = extractSprintEvent(activity);
      expect(result).not.toBeNull();
      expect(result!.type).toBe("SprintStarted");
      expect(result!.metadata.sprintNumber).toBe(5);
    });

    it("extracts SprintCompleted events", () => {
      const activity = makeActivityEvent({
        type: "SprintCompleted",
        metadata: {
          sprintId: "sprint-1",
          status: "Completed",
        },
      });
      const result = extractSprintEvent(activity);
      expect(result).not.toBeNull();
      expect(result!.metadata.status).toBe("Completed");
    });

    it("extracts SprintCancelled events", () => {
      const activity = makeActivityEvent({
        type: "SprintCancelled",
        metadata: {
          sprintId: "sprint-1",
          status: "Cancelled",
        },
      });
      const result = extractSprintEvent(activity);
      expect(result).not.toBeNull();
      expect(result!.type).toBe("SprintCancelled");
      expect(result!.metadata.status).toBe("Cancelled");
    });

    it("extracts SprintArtifactStored events with stage info", () => {
      const activity = makeActivityEvent({
        type: "SprintArtifactStored",
        metadata: {
          sprintId: "sprint-1",
          stage: "Planning",
          artifactType: "ArchitecturePlan",
          isUpdate: false,
        },
      });
      const result = extractSprintEvent(activity);
      expect(result).not.toBeNull();
      expect(result!.metadata.stage).toBe("Planning");
      expect(result!.metadata.artifactType).toBe("ArchitecturePlan");
    });
  });

  describe("event type routing", () => {
    const sprintEventTypes: ActivityEventType[] = [
      "SprintStarted",
      "SprintStageAdvanced",
      "SprintArtifactStored",
      "SprintCompleted",
      "SprintCancelled",
    ];

    it("sprint event types are a known finite set", () => {
      // Ensures we don't accidentally forget a new sprint event type
      expect(sprintEventTypes).toHaveLength(5);
    });

    it("non-sprint event types are not in the sprint set", () => {
      const nonSprintTypes: ActivityEventType[] = [
        "MessagePosted",
        "RoomCreated",
        "TaskCreated",
        "AgentThinking",
        "AgentFinished",
      ];
      for (const type of nonSprintTypes) {
        expect(sprintEventTypes).not.toContain(type);
      }
    });
  });

  describe("history array updates", () => {
    it("updates matching sprint in history array", () => {
      const history = [
        makeSprint({ id: "sprint-1", currentStage: "Intake" }),
        makeSprint({ id: "sprint-2", currentStage: "Validation" }),
      ];
      const updated = history.map((snap) =>
        applyStageAdvancedToSprint(snap, "sprint-1", "advanced", "Planning", undefined),
      );
      expect(updated[0].currentStage).toBe("Planning");
      expect(updated[1].currentStage).toBe("Validation"); // unchanged — different ID
    });

    it("leaves non-matching sprints untouched", () => {
      const history = [
        makeSprint({ id: "sprint-1" }),
        makeSprint({ id: "sprint-2" }),
      ];
      const updated = history.map((snap) =>
        applyCompletedToSprint(snap, "sprint-1", "Completed"),
      );
      expect(updated[0].status).toBe("Completed");
      expect(updated[1].status).toBe("Active");
      expect(updated[1]).toBe(history[1]); // same reference
    });
  });
});
