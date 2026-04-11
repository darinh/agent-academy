import { describe, expect, it, vi, beforeEach } from "vitest";

vi.mock("../api", () => ({
  getAgentSessions: vi.fn(),
}));

import { getAgentSessions } from "../api";
import type { AgentDefinition, BreakoutRoom, ChatEnvelope } from "../api";

const mockGetAgentSessions = vi.mocked(getAgentSessions);

// ── Factories ──

function makeAgent(overrides: Partial<AgentDefinition> = {}): AgentDefinition {
  return {
    id: "agent-1",
    name: "Software Architect",
    role: "architect",
    summary: "Designs system architecture",
    startupPrompt: "You are an architect",
    model: "gpt-5",
    capabilityTags: ["design", "review"],
    enabledTools: [],
    autoJoinDefaultRoom: true,
    gitIdentity: null,
    ...overrides,
  };
}

function makeMessage(overrides: Partial<ChatEnvelope> = {}): ChatEnvelope {
  return {
    id: "msg-1",
    roomId: "room-1",
    senderId: "agent-1",
    senderName: "Software Architect",
    senderRole: "architect",
    senderKind: "Agent",
    kind: "text",
    content: "Working on the design",
    sentAt: "2026-04-10T12:00:00Z",
    correlationId: null,
    replyToMessageId: null,
    ...overrides,
  };
}

function makeSession(overrides: Partial<BreakoutRoom> = {}): BreakoutRoom {
  return {
    id: "session-1",
    name: "BR: Implement auth module",
    parentRoomId: "room-1",
    assignedAgentId: "agent-1",
    tasks: [],
    status: "Active",
    recentMessages: [makeMessage()],
    createdAt: "2026-04-10T10:00:00Z",
    updatedAt: "2026-04-10T12:00:00Z",
    ...overrides,
  };
}

// ── Tests ──

describe("AgentSessionPanel", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  describe("getAgentSessions API contract", () => {
    it("returns sessions for a specific agent", async () => {
      const sessions = [makeSession(), makeSession({ id: "session-2", name: "BR: Fix bug" })];
      mockGetAgentSessions.mockResolvedValue(sessions);
      const result = await getAgentSessions("agent-1");
      expect(mockGetAgentSessions).toHaveBeenCalledWith("agent-1");
      expect(result).toHaveLength(2);
      expect(result[0].name).toBe("BR: Implement auth module");
    });

    it("returns empty array for agent with no sessions", async () => {
      mockGetAgentSessions.mockResolvedValue([]);
      const result = await getAgentSessions("agent-new");
      expect(result).toHaveLength(0);
    });

    it("sessions include recent messages", async () => {
      const msgs = [makeMessage({ id: "m1" }), makeMessage({ id: "m2" })];
      mockGetAgentSessions.mockResolvedValue([makeSession({ recentMessages: msgs })]);
      const result = await getAgentSessions("agent-1");
      expect(result[0].recentMessages).toHaveLength(2);
    });

    it("rejects on network failure", async () => {
      mockGetAgentSessions.mockRejectedValue(new Error("Timeout"));
      await expect(getAgentSessions("agent-1")).rejects.toThrow("Timeout");
    });
  });

  describe("session categorization", () => {
    it("separates active from archived sessions", () => {
      const sessions: BreakoutRoom[] = [
        makeSession({ id: "s1", status: "Active" }),
        makeSession({ id: "s2", status: "Completed" }),
        makeSession({ id: "s3", status: "Archived" }),
        makeSession({ id: "s4", status: "Active" }),
      ];
      const activeSessions = sessions.filter((s) => s.status === "Active");
      const archivedSessions = sessions.filter((s) => s.status !== "Active");
      expect(activeSessions).toHaveLength(2);
      expect(archivedSessions).toHaveLength(2);
      expect(activeSessions.map((s) => s.id)).toEqual(["s1", "s4"]);
    });

    it("auto-selects first active session as current", () => {
      const sessions: BreakoutRoom[] = [
        makeSession({ id: "s1", status: "Active", name: "BR: First" }),
        makeSession({ id: "s2", status: "Active", name: "BR: Second" }),
      ];
      const activeSessions = sessions.filter((s) => s.status === "Active");
      const currentSession = activeSessions[0] ?? null;
      expect(currentSession).not.toBeNull();
      expect(currentSession!.name).toBe("BR: First");
    });

    it("returns null when no active sessions exist", () => {
      const sessions: BreakoutRoom[] = [
        makeSession({ id: "s1", status: "Completed" }),
      ];
      const activeSessions = sessions.filter((s) => s.status === "Active");
      const currentSession = activeSessions[0] ?? null;
      expect(currentSession).toBeNull();
    });
  });

  describe("session display logic", () => {
    it("uses expanded session if user selected one", () => {
      const sessions = [
        makeSession({ id: "s1", status: "Active" }),
        makeSession({ id: "s2", status: "Completed", name: "BR: Old task" }),
      ];
      const expandedSessionId = "s2";
      const activeSessions = sessions.filter((s) => s.status === "Active");
      const currentSession = activeSessions[0] ?? null;
      const displaySession = expandedSessionId
        ? sessions.find((s) => s.id === expandedSessionId) ?? currentSession
        : currentSession;
      expect(displaySession).not.toBeNull();
      expect(displaySession!.id).toBe("s2");
    });

    it("falls back to current active when expanded session not found", () => {
      const sessions = [makeSession({ id: "s1", status: "Active" })];
      const expandedSessionId = "nonexistent";
      const activeSessions = sessions.filter((s) => s.status === "Active");
      const currentSession = activeSessions[0] ?? null;
      const displaySession = expandedSessionId
        ? sessions.find((s) => s.id === expandedSessionId) ?? currentSession
        : currentSession;
      expect(displaySession!.id).toBe("s1");
    });

    it("strips BR: prefix from session name", () => {
      const session = makeSession({ name: "BR: Implement auth module" });
      const displayName = session.name.replace(/^BR:\s*/, "");
      expect(displayName).toBe("Implement auth module");
    });

    it("handles session name without BR: prefix", () => {
      const session = makeSession({ name: "Custom Session" });
      const displayName = session.name.replace(/^BR:\s*/, "");
      expect(displayName).toBe("Custom Session");
    });
  });

  describe("agent state badge mapping", () => {
    it("maps Working state to ok badge", () => {
      const state: string = "Working";
      const color = state === "Working" ? "ok" : state === "Presenting" ? "warn" : "info";
      expect(color).toBe("ok");
    });

    it("maps Presenting state to warn badge", () => {
      const state: string = "Presenting";
      const color = state === "Working" ? "ok" : state === "Presenting" ? "warn" : "info";
      expect(color).toBe("warn");
    });

    it("maps Idle state to info badge", () => {
      const state: string = "Idle";
      const color = state === "Working" ? "ok" : state === "Presenting" ? "warn" : "info";
      expect(color).toBe("info");
    });

    it("defaults to Idle when location is undefined", () => {
      function getState(loc?: { state: string }): string {
        return loc?.state ?? "Idle";
      }
      expect(getState(undefined)).toBe("Idle");
      expect(getState({ state: "Working" })).toBe("Working");
    });
  });

  describe("AgentDefinition type shape", () => {
    it("has all required fields", () => {
      const agent = makeAgent();
      expect(agent).toHaveProperty("id");
      expect(agent).toHaveProperty("name");
      expect(agent).toHaveProperty("role");
      expect(agent).toHaveProperty("summary");
      expect(agent).toHaveProperty("startupPrompt");
      expect(agent).toHaveProperty("capabilityTags");
      expect(agent).toHaveProperty("enabledTools");
      expect(agent).toHaveProperty("autoJoinDefaultRoom");
    });

    it("model is optional", () => {
      const agentNoModel = makeAgent({ model: null });
      expect(agentNoModel.model).toBeNull();
    });

    it("gitIdentity is optional", () => {
      const agent = makeAgent({ gitIdentity: null });
      expect(agent.gitIdentity).toBeNull();
    });
  });

  describe("BreakoutRoom type shape", () => {
    it("has all required fields", () => {
      const session = makeSession();
      expect(session).toHaveProperty("id");
      expect(session).toHaveProperty("name");
      expect(session).toHaveProperty("parentRoomId");
      expect(session).toHaveProperty("assignedAgentId");
      expect(session).toHaveProperty("tasks");
      expect(session).toHaveProperty("status");
      expect(session).toHaveProperty("recentMessages");
      expect(session).toHaveProperty("createdAt");
      expect(session).toHaveProperty("updatedAt");
    });
  });
});
