// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { renderHook, act } from "@testing-library/react";
import type { RoomSnapshot, AgentLocation, ConversationSessionSnapshot } from "../api";

vi.mock("../api", () => ({
  createRoom: vi.fn(),
  createRoomSession: vi.fn(),
  addAgentToRoom: vi.fn(),
  removeAgentFromRoom: vi.fn(),
}));

import { createRoom, createRoomSession, addAgentToRoom, removeAgentFromRoom } from "../api";
import { useRoomCallbacks } from "../useRoomCallbacks";

const mockCreateRoom = vi.mocked(createRoom);
const mockCreateRoomSession = vi.mocked(createRoomSession);
const mockAddAgentToRoom = vi.mocked(addAgentToRoom);
const mockRemoveAgentFromRoom = vi.mocked(removeAgentFromRoom);

function makeRoomSnapshot(overrides: Partial<RoomSnapshot> = {}): RoomSnapshot {
  return {
    id: "room-1",
    name: "Test Room",
    status: "Active",
    currentPhase: "Planning",
    participants: [],
    recentMessages: [],
    createdAt: "2026-04-01T00:00:00Z",
    updatedAt: "2026-04-01T00:00:00Z",
    ...overrides,
  };
}

function setup(overrides: Partial<Parameters<typeof useRoomCallbacks>[0]> = {}) {
  const options = {
    handlePhaseTransition: vi.fn().mockResolvedValue(undefined),
    handleRoomSelect: vi.fn(),
    handleManualRefresh: vi.fn(),
    setTab: vi.fn(),
    ...overrides,
  };
  const hook = renderHook(() => useRoomCallbacks(options));
  return { ...hook, options };
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe("useRoomCallbacks", () => {
  describe("initial state", () => {
    it("starts with phaseTransitioning false", () => {
      const { result } = setup();
      expect(result.current.phaseTransitioning).toBe(false);
    });

    it("starts with selectedWorkspaceId null", () => {
      const { result } = setup();
      expect(result.current.selectedWorkspaceId).toBeNull();
    });
  });

  describe("wrappedPhaseTransition", () => {
    it("calls handlePhaseTransition and manages transitioning state", async () => {
      const { result, options } = setup();

      await act(async () => {
        await result.current.wrappedPhaseTransition("Implementation");
      });

      expect(options.handlePhaseTransition).toHaveBeenCalledWith("Implementation");
      expect(result.current.phaseTransitioning).toBe(false);
    });

    it("resets transitioning state even on error", async () => {
      const handlePhaseTransition = vi.fn().mockRejectedValue(new Error("fail"));
      const { result } = setup({ handlePhaseTransition });

      try {
        await act(async () => {
          await result.current.wrappedPhaseTransition("Review");
        });
      } catch {
        // expected
      }

      expect(result.current.phaseTransitioning).toBe(false);
    });
  });

  describe("wrappedRoomSelect", () => {
    it("selects room, clears workspace, and switches to chat tab", () => {
      const { result, options } = setup();

      // First set a workspace
      act(() => {
        result.current.handleWorkspaceSelect("ws-1");
      });
      expect(result.current.selectedWorkspaceId).toBe("ws-1");

      // Now select a room — should clear workspace
      act(() => {
        result.current.wrappedRoomSelect("room-1");
      });

      expect(options.handleRoomSelect).toHaveBeenCalledWith("room-1");
      expect(options.setTab).toHaveBeenCalledWith("chat");
      expect(result.current.selectedWorkspaceId).toBeNull();
    });
  });

  describe("handleCreateRoom", () => {
    it("creates room, selects it, and refreshes", async () => {
      mockCreateRoom.mockResolvedValue(makeRoomSnapshot({ id: "new-room" }));
      const { result, options } = setup();

      await act(async () => {
        await result.current.handleCreateRoom("New Room");
      });

      expect(mockCreateRoom).toHaveBeenCalledWith("New Room");
      expect(options.handleRoomSelect).toHaveBeenCalledWith("new-room");
      expect(options.setTab).toHaveBeenCalledWith("chat");
      expect(options.handleManualRefresh).toHaveBeenCalled();
    });

    it("handles room creation failure gracefully", async () => {
      const consoleSpy = vi.spyOn(console, "error").mockImplementation(() => {});
      mockCreateRoom.mockRejectedValue(new Error("Server error"));
      const { result, options } = setup();

      await act(async () => {
        await result.current.handleCreateRoom("Bad Room");
      });

      expect(options.handleRoomSelect).not.toHaveBeenCalled();
      expect(consoleSpy).toHaveBeenCalled();
      consoleSpy.mockRestore();
    });
  });

  describe("handleWorkspaceSelect", () => {
    it("sets selectedWorkspaceId", () => {
      const { result } = setup();

      act(() => {
        result.current.handleWorkspaceSelect("breakout-1");
      });

      expect(result.current.selectedWorkspaceId).toBe("breakout-1");
    });
  });

  describe("handleCreateSession", () => {
    it("creates session and refreshes", async () => {
      mockCreateRoomSession.mockResolvedValue({} as ConversationSessionSnapshot);
      const { result, options } = setup();

      await act(async () => {
        await result.current.handleCreateSession("room-1");
      });

      expect(mockCreateRoomSession).toHaveBeenCalledWith("room-1");
      expect(options.handleManualRefresh).toHaveBeenCalled();
    });

    it("handles session creation failure gracefully", async () => {
      const consoleSpy = vi.spyOn(console, "error").mockImplementation(() => {});
      mockCreateRoomSession.mockRejectedValue(new Error("fail"));
      const { result, options } = setup();

      await act(async () => {
        await result.current.handleCreateSession("room-1");
      });

      expect(options.handleManualRefresh).not.toHaveBeenCalled();
      expect(consoleSpy).toHaveBeenCalled();
      consoleSpy.mockRestore();
    });
  });

  describe("handleToggleAgent", () => {
    it("removes agent when currently in room", async () => {
      mockRemoveAgentFromRoom.mockResolvedValue({} as AgentLocation);
      const { result, options } = setup();

      await act(async () => {
        await result.current.handleToggleAgent("room-1", "agent-1", true);
      });

      expect(mockRemoveAgentFromRoom).toHaveBeenCalledWith("room-1", "agent-1");
      expect(mockAddAgentToRoom).not.toHaveBeenCalled();
      expect(options.handleManualRefresh).toHaveBeenCalled();
    });

    it("adds agent when not currently in room", async () => {
      mockAddAgentToRoom.mockResolvedValue({} as AgentLocation);
      const { result, options } = setup();

      await act(async () => {
        await result.current.handleToggleAgent("room-1", "agent-1", false);
      });

      expect(mockAddAgentToRoom).toHaveBeenCalledWith("room-1", "agent-1");
      expect(mockRemoveAgentFromRoom).not.toHaveBeenCalled();
      expect(options.handleManualRefresh).toHaveBeenCalled();
    });

    it("handles toggle failure gracefully", async () => {
      const consoleSpy = vi.spyOn(console, "error").mockImplementation(() => {});
      mockAddAgentToRoom.mockRejectedValue(new Error("fail"));
      const { result, options } = setup();

      await act(async () => {
        await result.current.handleToggleAgent("room-1", "agent-1", false);
      });

      expect(options.handleManualRefresh).not.toHaveBeenCalled();
      expect(consoleSpy).toHaveBeenCalled();
      consoleSpy.mockRestore();
    });
  });
});
