import { memo, useCallback, useMemo, useRef, useState, useEffect } from "react";
import type { AgentDefinition, AgentLocation, ConversationSessionSnapshot } from "../api";

interface SessionToolbarProps {
  roomId: string;
  sessions: ConversationSessionSnapshot[];
  selectedSessionId: string | null;
  onSessionChange: (sessionId: string | null) => void;
  onNewSession: () => void;
  configuredAgents: AgentDefinition[];
  agentLocations: AgentLocation[];
  onToggleAgent?: (roomId: string, agentId: string, present: boolean) => void;
  onCreateSession?: (roomId: string) => void;
}

export const SessionToolbar = memo(function SessionToolbar({
  roomId,
  sessions,
  selectedSessionId,
  onSessionChange,
  onNewSession,
  configuredAgents,
  agentLocations,
  onToggleAgent,
  onCreateSession,
}: SessionToolbarProps) {
  const [agentsOpen, setAgentsOpen] = useState(false);
  const agentsRef = useRef<HTMLDivElement>(null);

  const agentsInRoom = useMemo(
    () => new Set(agentLocations.filter(l => l.roomId === roomId).map(l => l.agentId)),
    [agentLocations, roomId],
  );

  // Close agents dropdown on outside click
  useEffect(() => {
    if (!agentsOpen) return;
    const handler = (e: MouseEvent) => {
      if (agentsRef.current && !agentsRef.current.contains(e.target as Node)) setAgentsOpen(false);
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [agentsOpen]);

  const handleToggleAgent = useCallback((agentId: string, currentlyInRoom: boolean) => {
    if (!onToggleAgent) return;
    onToggleAgent(roomId, agentId, currentlyInRoom);
  }, [roomId, onToggleAgent]);

  return (
    <div style={{
      display: "flex", alignItems: "center", gap: "8px",
      padding: "6px 12px", borderBottom: "1px solid var(--aa-border, #333)",
      fontSize: "12px", flexShrink: 0,
    }}>
      {onCreateSession && (
        <button
          onClick={onNewSession}
          style={{
            background: "var(--aa-surface, #1e1e2e)", border: "1px solid var(--aa-border, #333)",
            borderRadius: "4px", padding: "3px 10px", color: "inherit", cursor: "pointer",
            fontSize: "12px", whiteSpace: "nowrap",
          }}
        >
          + New Session
        </button>
      )}
      {sessions.length >= 0 && (
        <select
          value={selectedSessionId ?? ""}
          onChange={(e) => onSessionChange(e.target.value || null)}
          style={{
            background: "var(--aa-surface, #1e1e2e)", border: "1px solid var(--aa-border, #333)",
            borderRadius: "4px", padding: "3px 8px", color: "inherit", fontSize: "12px",
            maxWidth: "180px",
          }}
        >
          <option value="">Current session</option>
          {sessions
            .filter(s => s.status === "Archived")
            .map(s => (
              <option key={s.id} value={s.id}>
                Session #{s.sequenceNumber} ({s.messageCount} msgs)
              </option>
            ))}
        </select>
      )}
      {configuredAgents.length > 0 && onToggleAgent && (
        <div ref={agentsRef} style={{ position: "relative" }}>
          <button
            onClick={() => setAgentsOpen(!agentsOpen)}
            style={{
              background: "var(--aa-surface, #1e1e2e)", border: "1px solid var(--aa-border, #333)",
              borderRadius: "4px", padding: "3px 10px", color: "inherit", cursor: "pointer",
              fontSize: "12px", whiteSpace: "nowrap",
            }}
          >
            Agents ({agentsInRoom.size})
          </button>
          {agentsOpen && (
            <div style={{
              position: "absolute", right: 0, top: "100%", zIndex: 100,
              background: "var(--aa-panel, #181825)", border: "1px solid var(--aa-border, #333)",
              borderRadius: "6px", padding: "6px 0", minWidth: "180px",
              boxShadow: "0 4px 12px rgba(0,0,0,0.4)",
            }}>
              {configuredAgents.map((agent) => {
                const inRoom = agentsInRoom.has(agent.id);
                return (
                  <label
                    key={agent.id}
                    style={{
                      display: "flex", alignItems: "center", gap: "8px",
                      padding: "4px 12px", cursor: "pointer", fontSize: "12px",
                    }}
                  >
                    <input
                      type="checkbox"
                      checked={inRoom}
                      onChange={() => handleToggleAgent(agent.id, inRoom)}
                    />
                    <span>{agent.name}</span>
                    <span style={{ color: "var(--aa-soft)", fontSize: "11px", marginLeft: "auto" }}>
                      {agent.role}
                    </span>
                  </label>
                );
              })}
            </div>
          )}
        </div>
      )}
    </div>
  );
});
