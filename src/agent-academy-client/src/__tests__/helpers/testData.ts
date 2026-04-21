/**
 * Shared test data factories for frontend unit tests.
 *
 * Each factory returns a complete, valid object with sensible defaults.
 * Pass `Partial<T>` overrides to customize specific fields.
 * Auto-incrementing counters ensure unique IDs across calls within a test.
 *
 * Usage:
 *   import { makeAgent, makeRoom, resetFactories } from "../helpers/testData";
 *
 *   beforeEach(() => resetFactories());
 *
 *   const agent = makeAgent({ name: "Custom Name" });
 *   const room = makeRoom({ participants: [makePresence()] });
 */
import type {
  ActivityEvent,
  ActivityEventType,
  AgentDefinition,
  AgentLocation,
  AgentPresence,
  BreakoutRoom,
  ChatEnvelope,
  DmMessage,
  ErrorRecord,
  GoalCardSummary,
  RoomSnapshot,
  SprintArtifact,
  SprintSnapshot,
  TaskSnapshot,
  WorkspaceOverview,
} from "../../api";

// ── Counter ────────────────────────────────────────────────────────────

let _seq = 0;
function seq(): number {
  return ++_seq;
}

/** Reset auto-increment counters. Call in `beforeEach` for isolation. */
export function resetFactories(): void {
  _seq = 0;
}

// ── Factories ──────────────────────────────────────────────────────────

export function makeAgent(
  overrides: Partial<AgentDefinition> = {},
): AgentDefinition {
  const n = seq();
  return {
    id: `agent-${n}`,
    name: `Agent ${n}`,
    role: "engineer",
    summary: `Test agent ${n}`,
    startupPrompt: "You are a test agent.",
    model: null,
    capabilityTags: [],
    enabledTools: [],
    autoJoinDefaultRoom: true,
    gitIdentity: null,
    ...overrides,
  };
}

export function makePresence(
  overrides: Partial<AgentPresence> = {},
): AgentPresence {
  const n = seq();
  return {
    agentId: `agent-${n}`,
    name: `Agent ${n}`,
    role: "engineer",
    availability: "Available",
    isPreferred: false,
    lastActivityAt: "2026-01-01T00:00:00Z",
    activeCapabilities: [],
    ...overrides,
  };
}

export function makeLocation(
  overrides: Partial<AgentLocation> = {},
): AgentLocation {
  const n = seq();
  return {
    agentId: `agent-${n}`,
    roomId: `room-${n}`,
    state: "Idle",
    breakoutRoomId: null,
    updatedAt: "2026-01-01T00:00:00Z",
    ...overrides,
  };
}

export function makeMessage(
  overrides: Partial<ChatEnvelope> = {},
): ChatEnvelope {
  const n = seq();
  return {
    id: `msg-${n}`,
    roomId: "room-1",
    senderId: `agent-${n}`,
    senderName: `Agent ${n}`,
    senderRole: "engineer",
    senderKind: "Agent",
    kind: "text",
    content: `Test message ${n}`,
    sentAt: "2026-01-01T00:00:00Z",
    correlationId: null,
    replyToMessageId: null,
    ...overrides,
  };
}

export function makeTask(
  overrides: Partial<TaskSnapshot> = {},
): TaskSnapshot {
  const n = seq();
  return {
    id: `task-${n}`,
    title: `Test Task ${n}`,
    description: `Description for task ${n}`,
    successCriteria: "All tests pass",
    status: "Queued",
    currentPhase: "Planning",
    currentPlan: "",
    validationStatus: "",
    validationSummary: "",
    implementationStatus: "",
    implementationSummary: "",
    preferredRoles: [],
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z",
    size: null,
    startedAt: null,
    completedAt: null,
    assignedAgentId: null,
    assignedAgentName: null,
    usedFleet: false,
    fleetModels: [],
    branchName: null,
    pullRequestUrl: null,
    pullRequestNumber: null,
    pullRequestStatus: null,
    reviewerAgentId: null,
    reviewRounds: 0,
    testsCreated: [],
    commitCount: 0,
    mergeCommitSha: null,
    commentCount: 0,
    type: "Feature",
    sprintId: null,
    dependsOnTaskIds: null,
    blockingTaskIds: null,
    priority: "Medium",
    ...overrides,
  };
}

export function makeEvent(
  overrides: Partial<ActivityEvent> = {},
): ActivityEvent {
  const n = seq();
  return {
    id: `evt-${n}`,
    type: "MessagePosted" as ActivityEventType,
    severity: "Info",
    roomId: "room-1",
    actorId: "agent-1",
    taskId: null,
    message: `Activity event ${n}`,
    correlationId: null,
    occurredAt: "2026-01-01T00:00:00Z",
    metadata: null,
    ...overrides,
  };
}

export function makeRoom(
  overrides: Partial<RoomSnapshot> = {},
): RoomSnapshot {
  const n = seq();
  return {
    id: `room-${n}`,
    name: `Room ${n}`,
    topic: null,
    status: "Active",
    currentPhase: "Planning",
    activeTask: null,
    participants: [],
    recentMessages: [],
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z",
    phaseGates: null,
    ...overrides,
  };
}

export function makeBreakoutRoom(
  overrides: Partial<BreakoutRoom> = {},
): BreakoutRoom {
  const n = seq();
  return {
    id: `breakout-${n}`,
    name: `Breakout ${n}`,
    parentRoomId: "room-1",
    assignedAgentId: "agent-1",
    tasks: [],
    status: "Active",
    recentMessages: [],
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z",
    ...overrides,
  };
}

export function makeGoalCardSummary(
  overrides: Partial<GoalCardSummary> = {},
): GoalCardSummary {
  return {
    total: 0,
    active: 0,
    challenged: 0,
    completed: 0,
    abandoned: 0,
    verdictProceed: 0,
    verdictProceedWithCaveat: 0,
    verdictChallenge: 0,
    ...overrides,
  };
}

export function makeOverview(
  overrides: Partial<WorkspaceOverview> = {},
): WorkspaceOverview {
  return {
    configuredAgents: [],
    rooms: [],
    recentActivity: [],
    agentLocations: [],
    breakoutRooms: [],
    goalCards: makeGoalCardSummary(),
    generatedAt: "2026-01-01T00:00:00Z",
    ...overrides,
  };
}

export function makeSprint(
  overrides: Partial<SprintSnapshot> = {},
): SprintSnapshot {
  const n = seq();
  return {
    id: `sprint-${n}`,
    number: n,
    status: "Active",
    currentStage: "Planning",
    overflowFromSprintId: null,
    awaitingSignOff: false,
    pendingStage: null,
    signOffRequestedAt: null,
    createdAt: "2026-01-01T00:00:00Z",
    completedAt: null,
    ...overrides,
  };
}

export function makeArtifact(
  overrides: Partial<SprintArtifact> = {},
): SprintArtifact {
  const n = seq();
  return {
    id: n,
    sprintId: "sprint-1",
    stage: "Planning",
    type: "SprintPlan",
    content: `Artifact content ${n}`,
    createdByAgentId: "agent-1",
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: null,
    ...overrides,
  };
}

export function makeErrorRecord(
  overrides: Partial<ErrorRecord> = {},
): ErrorRecord {
  const n = seq();
  return {
    agentId: `agent-${n}`,
    roomId: "room-1",
    errorType: "RuntimeError",
    message: `Test error ${n}`,
    recoverable: true,
    timestamp: "2026-01-01T00:00:00Z",
    ...overrides,
  };
}

export function makeDmMessage(
  overrides: Partial<DmMessage> = {},
): DmMessage {
  const n = seq();
  return {
    id: `dm-${n}`,
    senderId: "user-1",
    senderName: "Human",
    senderRole: null,
    content: `DM content ${n}`,
    sentAt: "2026-01-01T00:00:00Z",
    isFromHuman: true,
    ...overrides,
  };
}
