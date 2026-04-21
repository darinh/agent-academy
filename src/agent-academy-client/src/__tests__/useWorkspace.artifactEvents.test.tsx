// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ActivityEvent, WorkspaceOverview } from "../api";
import { useWorkspace } from "../useWorkspace";

const mockGetOverview = vi.fn();
const mockGetInstanceHealth = vi.fn();
const mockSendHumanMessage = vi.fn();
const mockTransitionPhase = vi.fn();
const mockSubmitTask = vi.fn();
const mockGetRoomContextUsage = vi.fn();

let realtimeHandler: ((evt: ActivityEvent) => void) | null = null;

vi.mock("../api", () => ({
  getOverview: (...args: unknown[]) => mockGetOverview(...args),
  getInstanceHealth: (...args: unknown[]) => mockGetInstanceHealth(...args),
  sendHumanMessage: (...args: unknown[]) => mockSendHumanMessage(...args),
  transitionPhase: (...args: unknown[]) => mockTransitionPhase(...args),
  submitTask: (...args: unknown[]) => mockSubmitTask(...args),
  getRoomContextUsage: (...args: unknown[]) => mockGetRoomContextUsage(...args),
}));

vi.mock("../useActivityHub", () => ({
  useActivityHub: (handler: (evt: ActivityEvent) => void) => {
    realtimeHandler = handler;
    return "connected" as const;
  },
}));

vi.mock("../useActivitySSE", () => ({
  useActivitySSE: () => "disconnected" as const,
}));

function makeOverview(): WorkspaceOverview {
  return {
    configuredAgents: [],
    rooms: [],
    recentActivity: [],
    agentLocations: [],
    breakoutRooms: [],
    goalCards: { total: 0, active: 0, challenged: 0, completed: 0, abandoned: 0, verdictProceed: 0, verdictProceedWithCaveat: 0, verdictChallenge: 0 },
    generatedAt: new Date("2026-04-01T00:00:00Z").toISOString(),
  };
}

function makeActivityEvent(type: ActivityEvent["type"]): ActivityEvent {
  return {
    id: `evt-${type}`,
    type,
    severity: "Info",
    roomId: "room-1",
    actorId: "agent-1",
    taskId: null,
    message: `event ${type}`,
    correlationId: null,
    occurredAt: new Date("2026-04-01T00:00:00Z").toISOString(),
    metadata: null,
  };
}

describe("useWorkspace artifact realtime updates", () => {
  beforeEach(() => {
    realtimeHandler = null;
    mockGetOverview.mockResolvedValue(makeOverview());
    mockGetInstanceHealth.mockResolvedValue({ instanceId: "instance-1" });
    mockSendHumanMessage.mockResolvedValue(undefined);
    mockTransitionPhase.mockResolvedValue(undefined);
    mockSubmitTask.mockResolvedValue({ room: { id: "room-1" } });
    mockGetRoomContextUsage.mockResolvedValue([]);
  });

  it("increments artifactVersion for ArtifactEvaluated events", async () => {
    const { result } = renderHook(() => useWorkspace());
    await waitFor(() => expect(result.current.busy).toBe(false));

    expect(result.current.artifactVersion).toBe(0);
    expect(realtimeHandler).not.toBeNull();

    act(() => {
      realtimeHandler?.(makeActivityEvent("ArtifactEvaluated"));
    });

    expect(result.current.artifactVersion).toBe(1);
  });

  it("does not increment artifactVersion for unrelated events", async () => {
    const { result } = renderHook(() => useWorkspace());
    await waitFor(() => expect(result.current.busy).toBe(false));

    act(() => {
      realtimeHandler?.(makeActivityEvent("MessagePosted"));
    });

    expect(result.current.artifactVersion).toBe(0);
  });
});
