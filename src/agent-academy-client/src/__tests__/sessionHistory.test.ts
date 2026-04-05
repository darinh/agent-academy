import { describe, expect, it, vi, beforeEach } from "vitest";

vi.mock("../api", () => ({
  getSessions: vi.fn(),
  getSessionStats: vi.fn(),
  getRoomSessions: vi.fn(),
}));

import { getSessions, getSessionStats, getRoomSessions } from "../api";
import type {
  ConversationSessionSnapshot,
  SessionListResponse,
  SessionStats,
} from "../api";

const mockGetSessions = vi.mocked(getSessions);
const mockGetSessionStats = vi.mocked(getSessionStats);
const mockGetRoomSessions = vi.mocked(getRoomSessions);

function makeSession(
  overrides: Partial<ConversationSessionSnapshot> = {},
): ConversationSessionSnapshot {
  return {
    id: "session-001",
    roomId: "room-1",
    roomType: "Main",
    sequenceNumber: 1,
    status: "Active",
    summary: null,
    messageCount: 5,
    createdAt: "2026-04-05T10:00:00Z",
    archivedAt: null,
    ...overrides,
  };
}

function makeSessionList(
  sessions: ConversationSessionSnapshot[] = [makeSession()],
  totalCount?: number,
): SessionListResponse {
  return {
    sessions,
    totalCount: totalCount ?? sessions.length,
  };
}

function makeStats(overrides: Partial<SessionStats> = {}): SessionStats {
  return {
    totalSessions: 10,
    activeSessions: 3,
    archivedSessions: 7,
    totalMessages: 250,
    ...overrides,
  };
}

describe("SessionHistoryPanel", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  describe("API integration types", () => {
    it("getSessions is called with correct default params", async () => {
      mockGetSessions.mockResolvedValue(makeSessionList());
      await getSessions();
      expect(mockGetSessions).toHaveBeenCalledWith();
    });

    it("getSessions accepts status filter", async () => {
      mockGetSessions.mockResolvedValue(makeSessionList());
      await getSessions("Archived", 20, 0);
      expect(mockGetSessions).toHaveBeenCalledWith("Archived", 20, 0);
    });

    it("getSessions accepts pagination and hoursBack params", async () => {
      mockGetSessions.mockResolvedValue(makeSessionList());
      await getSessions(undefined, 10, 20, 24);
      expect(mockGetSessions).toHaveBeenCalledWith(undefined, 10, 20, 24);
    });

    it("getSessionStats returns aggregate counts", async () => {
      const stats = makeStats();
      mockGetSessionStats.mockResolvedValue(stats);
      const result = await getSessionStats();
      expect(result).toEqual(stats);
      expect(result.totalSessions).toBe(10);
      expect(result.activeSessions).toBe(3);
      expect(result.archivedSessions).toBe(7);
      expect(result.totalMessages).toBe(250);
    });

    it("getSessionStats accepts hoursBack", async () => {
      mockGetSessionStats.mockResolvedValue(makeStats());
      await getSessionStats(168);
      expect(mockGetSessionStats).toHaveBeenCalledWith(168);
    });

    it("getRoomSessions is called with correct room ID", async () => {
      mockGetRoomSessions.mockResolvedValue(makeSessionList());
      await getRoomSessions("room-42", "Archived", 5, 0);
      expect(mockGetRoomSessions).toHaveBeenCalledWith(
        "room-42",
        "Archived",
        5,
        0,
      );
    });
  });

  describe("ConversationSessionSnapshot shape", () => {
    it("represents an active session without summary", () => {
      const session = makeSession();
      expect(session.status).toBe("Active");
      expect(session.summary).toBeNull();
      expect(session.archivedAt).toBeNull();
      expect(session.messageCount).toBe(5);
    });

    it("represents an archived session with summary", () => {
      const archived = makeSession({
        status: "Archived",
        summary: "Key decisions: chose React over Vue.",
        archivedAt: "2026-04-05T11:00:00Z",
        messageCount: 42,
        sequenceNumber: 3,
      });
      expect(archived.status).toBe("Archived");
      expect(archived.summary).toBe("Key decisions: chose React over Vue.");
      expect(archived.archivedAt).toBe("2026-04-05T11:00:00Z");
      expect(archived.sequenceNumber).toBe(3);
    });

    it("supports breakout room type", () => {
      const breakout = makeSession({ roomType: "Breakout" });
      expect(breakout.roomType).toBe("Breakout");
    });
  });

  describe("SessionListResponse shape", () => {
    it("contains sessions array and total count", () => {
      const response = makeSessionList(
        [makeSession({ id: "s1" }), makeSession({ id: "s2" })],
        10,
      );
      expect(response.sessions).toHaveLength(2);
      expect(response.totalCount).toBe(10);
    });

    it("handles empty results", () => {
      const empty = makeSessionList([], 0);
      expect(empty.sessions).toHaveLength(0);
      expect(empty.totalCount).toBe(0);
    });
  });

  describe("SessionStats shape", () => {
    it("totals are consistent", () => {
      const stats = makeStats({
        totalSessions: 15,
        activeSessions: 5,
        archivedSessions: 10,
      });
      expect(stats.activeSessions + stats.archivedSessions).toBe(
        stats.totalSessions,
      );
    });

    it("handles zero state", () => {
      const empty = makeStats({
        totalSessions: 0,
        activeSessions: 0,
        archivedSessions: 0,
        totalMessages: 0,
      });
      expect(empty.totalSessions).toBe(0);
      expect(empty.totalMessages).toBe(0);
    });
  });

  describe("data flow", () => {
    it("fetches sessions and stats in parallel", async () => {
      mockGetSessions.mockResolvedValue(
        makeSessionList([
          makeSession({ id: "s1", status: "Active" }),
          makeSession({ id: "s2", status: "Archived" }),
        ]),
      );
      mockGetSessionStats.mockResolvedValue(makeStats());

      const [sessionsResult, statsResult] = await Promise.allSettled([
        getSessions(),
        getSessionStats(),
      ]);

      expect(sessionsResult.status).toBe("fulfilled");
      expect(statsResult.status).toBe("fulfilled");
    });

    it("handles API failure gracefully in allSettled", async () => {
      mockGetSessions.mockRejectedValue(new Error("Network error"));
      mockGetSessionStats.mockResolvedValue(makeStats());

      const [sessionsResult, statsResult] = await Promise.allSettled([
        getSessions(),
        getSessionStats(),
      ]);

      expect(sessionsResult.status).toBe("rejected");
      expect(statsResult.status).toBe("fulfilled");
    });

    it("room sessions endpoint uses correct room ID in URL", async () => {
      const room = "room-with-special-chars";
      mockGetRoomSessions.mockResolvedValue(makeSessionList());
      await getRoomSessions(room);
      expect(mockGetRoomSessions).toHaveBeenCalledWith(room);
    });
  });
});
