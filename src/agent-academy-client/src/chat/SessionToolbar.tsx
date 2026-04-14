import { memo, useCallback, useMemo, useRef, useState, useEffect } from "react";
import type { AgentDefinition, AgentLocation, ConversationSessionSnapshot } from "../api";
import { exportRoomMessages, compactRoom } from "../api";

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
  onCompacted?: () => void;
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
  onCompacted,
}: SessionToolbarProps) {
  const [agentsOpen, setAgentsOpen] = useState(false);
  const [exportOpen, setExportOpen] = useState(false);
  const [exporting, setExporting] = useState(false);
  const [compacting, setCompacting] = useState(false);
  const [compactResult, setCompactResult] = useState<string | null>(null);
  const agentsRef = useRef<HTMLDivElement>(null);
  const exportRef = useRef<HTMLDivElement>(null);
  const compactTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const compactRequestRef = useRef(0);

  // Clear compact result timer and stale request on unmount or room change
  useEffect(() => {
    setCompacting(false);
    setCompactResult(null);
    compactRequestRef.current++;
    return () => {
      if (compactTimerRef.current) clearTimeout(compactTimerRef.current);
    };
  }, [roomId]);

  const agentsInRoom = useMemo(
    () => new Set(agentLocations.filter(l => l.roomId === roomId).map(l => l.agentId)),
    [agentLocations, roomId],
  );

  // Close agents dropdown on outside click
  useEffect(() => {
    if (!agentsOpen && !exportOpen) return;
    const handler = (e: MouseEvent) => {
      if (agentsOpen && agentsRef.current && !agentsRef.current.contains(e.target as Node)) setAgentsOpen(false);
      if (exportOpen && exportRef.current && !exportRef.current.contains(e.target as Node)) setExportOpen(false);
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [agentsOpen, exportOpen]);

  const handleToggleAgent = useCallback((agentId: string, currentlyInRoom: boolean) => {
    if (!onToggleAgent) return;
    onToggleAgent(roomId, agentId, currentlyInRoom);
  }, [roomId, onToggleAgent]);

  const handleExport = useCallback(async (format: "json" | "markdown") => {
    setExporting(true);
    setExportOpen(false);
    try {
      await exportRoomMessages(roomId, format);
    } catch {
      // Silently fail — download helper already throws on HTTP errors
    } finally {
      setExporting(false);
    }
  }, [roomId]);

  const handleCompact = useCallback(async () => {
    setCompacting(true);
    setCompactResult(null);
    if (compactTimerRef.current) clearTimeout(compactTimerRef.current);
    const requestId = ++compactRequestRef.current;
    try {
      const result = await compactRoom(roomId);
      if (requestId !== compactRequestRef.current) return;
      const msg = result.note
        ? result.note
        : `Compacted ${result.compactedSessions} session(s)`;
      setCompactResult(msg);
      onCompacted?.();
      compactTimerRef.current = setTimeout(() => setCompactResult(null), 4000);
    } catch {
      if (requestId !== compactRequestRef.current) return;
      setCompactResult("Failed to compact sessions");
      compactTimerRef.current = setTimeout(() => setCompactResult(null), 4000);
    } finally {
      if (requestId === compactRequestRef.current) setCompacting(false);
    }
  }, [roomId, onCompacted]);

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
      <button
        onClick={() => void handleCompact()}
        disabled={compacting}
        title="Reset agent sessions to free context window space"
        style={{
          background: "var(--aa-surface, #1e1e2e)", border: "1px solid var(--aa-border, #333)",
          borderRadius: "4px", padding: "3px 10px", color: "inherit", cursor: compacting ? "wait" : "pointer",
          fontSize: "12px", whiteSpace: "nowrap", opacity: compacting ? 0.6 : 1,
        }}
      >
        {compacting ? "Compacting…" : "⟳ Compact"}
      </button>
      {compactResult && (
        <span
          role="status"
          style={{
            fontSize: "11px",
            color: compactResult.startsWith("Failed") ? "var(--aa-copper, #dc3545)" : "var(--aa-green, #28a745)",
            whiteSpace: "nowrap",
          }}
        >
          {compactResult}
        </span>
      )}
      <div ref={exportRef} style={{ position: "relative", marginLeft: "auto" }}>
        <button
          onClick={() => setExportOpen(!exportOpen)}
          disabled={exporting}
          style={{
            background: "var(--aa-surface, #1e1e2e)", border: "1px solid var(--aa-border, #333)",
            borderRadius: "4px", padding: "3px 10px", color: "inherit", cursor: "pointer",
            fontSize: "12px", whiteSpace: "nowrap", opacity: exporting ? 0.6 : 1,
          }}
        >
          {exporting ? "Exporting…" : "Export ▾"}
        </button>
        {exportOpen && (
          <div style={{
            position: "absolute", right: 0, top: "100%", zIndex: 100,
            background: "var(--aa-panel, #181825)", border: "1px solid var(--aa-border, #333)",
            borderRadius: "6px", padding: "4px 0", minWidth: "140px",
            boxShadow: "0 4px 12px rgba(0,0,0,0.4)",
          }}>
            <button
              onClick={() => void handleExport("json")}
              style={{
                display: "block", width: "100%", textAlign: "left",
                background: "none", border: "none", color: "inherit",
                padding: "6px 12px", cursor: "pointer", fontSize: "12px",
              }}
            >
              Export as JSON
            </button>
            <button
              onClick={() => void handleExport("markdown")}
              style={{
                display: "block", width: "100%", textAlign: "left",
                background: "none", border: "none", color: "inherit",
                padding: "6px 12px", cursor: "pointer", fontSize: "12px",
              }}
            >
              Export as Markdown
            </button>
          </div>
        )}
      </div>
    </div>
  );
});
