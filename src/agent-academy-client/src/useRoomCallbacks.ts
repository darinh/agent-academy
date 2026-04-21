import { useCallback, useState } from "react";
import { createRoom, createRoomSession, addAgentToRoom, removeAgentFromRoom } from "./api";
import type { CollaborationPhase } from "./api";

export interface RoomCallbackOptions {
  handlePhaseTransition: (phase: CollaborationPhase) => Promise<void>;
  handleRoomSelect: (id: string) => void;
  handleManualRefresh: () => void;
  setTab: (tab: string) => void;
}

export function useRoomCallbacks({
  handlePhaseTransition,
  handleRoomSelect,
  handleManualRefresh,
  setTab,
}: RoomCallbackOptions) {
  const [phaseTransitioning, setPhaseTransitioning] = useState(false);
  const [selectedWorkspaceId, setSelectedWorkspaceId] = useState<string | null>(null);

  const wrappedPhaseTransition = useCallback(
    async (phase: CollaborationPhase) => {
      setPhaseTransitioning(true);
      try { await handlePhaseTransition(phase); }
      finally { setPhaseTransitioning(false); }
    },
    [handlePhaseTransition],
  );

  const wrappedRoomSelect = useCallback(
    (id: string) => {
      setSelectedWorkspaceId(null);
      handleRoomSelect(id);
      setTab("chat");
    },
    [handleRoomSelect, setTab],
  );

  const handleCreateRoom = useCallback(
    async (name: string) => {
      try {
        const newRoom = await createRoom(name);
        handleRoomSelect(newRoom.id);
        setSelectedWorkspaceId(null);
        setTab("chat");
        handleManualRefresh();
      } catch (e) {
        console.error("Failed to create room:", e);
      }
    },
    [handleRoomSelect, handleManualRefresh, setTab],
  );

  const handleWorkspaceSelect = useCallback(
    (breakoutId: string) => {
      setSelectedWorkspaceId(breakoutId);
    },
    [],
  );

  const handleCreateSession = useCallback(
    async (roomId: string) => {
      try {
        await createRoomSession(roomId);
        handleManualRefresh();
      } catch (e) {
        console.error("Failed to create session:", e);
      }
    },
    [handleManualRefresh],
  );

  const handleToggleAgent = useCallback(
    async (roomId: string, agentId: string, currentlyInRoom: boolean) => {
      try {
        if (currentlyInRoom) {
          await removeAgentFromRoom(roomId, agentId);
        } else {
          await addAgentToRoom(roomId, agentId);
        }
        handleManualRefresh();
      } catch (e) {
        console.error("Failed to toggle agent:", e);
      }
    },
    [handleManualRefresh],
  );

  return {
    phaseTransitioning,
    selectedWorkspaceId,
    wrappedPhaseTransition,
    wrappedRoomSelect,
    handleCreateRoom,
    handleWorkspaceSelect,
    handleCreateSession,
    handleToggleAgent,
  };
}
