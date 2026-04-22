import { useEffect } from "react";

/**
 * Keeps the document title in sync with the active room and phase.
 * Falls back to "Agent Academy" when no room is selected.
 */
export function useDocumentTitle(roomName?: string | null, phase?: string | null) {
  useEffect(() => {
    const name = roomName ?? "Agent Academy";
    document.title = phase ? `${name} · ${phase} | Agent Academy` : `${name} | Agent Academy`;
  }, [roomName, phase]);
}
