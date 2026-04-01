import { memo, useState, useEffect, useCallback } from "react";
import {
  Badge,
  Button,
  Spinner,
} from "@fluentui/react-components";
import { roleColor } from "./theme";
import type { AgentDefinition, AgentLocation, BreakoutRoom } from "./api";
import { getAgentSessions } from "./api";
import ChatPanel from "./ChatPanel";
import type { ThinkingAgent } from "./useWorkspace";
import type { ConnectionStatus } from "./useActivityHub";

interface AgentSessionPanelProps {
  agent: AgentDefinition;
  location?: AgentLocation;
  thinkingAgents: ThinkingAgent[];
  connectionStatus: ConnectionStatus;
  onSendMessage: (roomId: string, message: string) => Promise<boolean>;
}

function formatDuration(start: string, end: string): string {
  const ms = new Date(end).getTime() - new Date(start).getTime();
  const mins = Math.floor(ms / 60_000);
  if (mins < 60) return `${mins}m`;
  const hrs = Math.floor(mins / 60);
  return `${hrs}h ${mins % 60}m`;
}

function formatTime(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
}

const AgentSessionPanel = memo(function AgentSessionPanel({
  agent,
  location,
  thinkingAgents,
  connectionStatus,
  onSendMessage,
}: AgentSessionPanelProps) {
  const [sessions, setSessions] = useState<BreakoutRoom[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  const [expandedSessionId, setExpandedSessionId] = useState<string | null>(null);

  const loadSessions = useCallback(async () => {
    setLoading(true);
    setError(false);
    try {
      const data = await getAgentSessions(agent.id);
      setSessions(data);
    } catch {
      setError(true);
    } finally {
      setLoading(false);
    }
  }, [agent.id]);

  useEffect(() => {
    void loadSessions();
  }, [loadSessions]);

  const rc = roleColor(agent.role);
  const state = location?.state ?? "Idle";
  const activeSessions = sessions.filter((s) => s.status === "Active");
  const archivedSessions = sessions.filter((s) => s.status !== "Active");

  // Auto-expand the most recent active session
  const currentSession = activeSessions[0] ?? null;

  // If user clicks a session, show it; otherwise show the current active one
  const displaySession =
    expandedSessionId
      ? sessions.find((s) => s.id === expandedSessionId) ?? currentSession
      : currentSession;

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", overflow: "hidden" }}>
      {/* Agent header */}
      <div style={{
        padding: "16px 20px",
        borderBottom: "1px solid #2a2f3a",
        display: "flex",
        alignItems: "center",
        gap: "12px",
        flexShrink: 0,
      }}>
        <div style={{
          width: "36px", height: "36px", borderRadius: "10px",
          display: "flex", alignItems: "center", justifyContent: "center",
          fontWeight: 600, fontSize: "16px", color: "#fff",
          background: `linear-gradient(135deg, ${rc.accent}, ${rc.accent}88)`,
        }}>
          {agent.name.charAt(0)}
        </div>
        <div>
          <div style={{ fontWeight: 600, fontSize: "14px" }}>{agent.name}</div>
          <div style={{ fontSize: "12px", color: "#7c90b2", display: "flex", alignItems: "center", gap: "6px" }}>
            <span>{agent.role}</span>
            <Badge
              appearance="filled"
              size="small"
              color={state === "Working" ? "success" : state === "Presenting" ? "warning" : "informative"}
            >
              {state}
            </Badge>
          </div>
        </div>
        <div style={{ marginLeft: "auto" }}>
          <Button appearance="subtle" size="small" onClick={loadSessions}>↻</Button>
        </div>
      </div>

      {loading ? (
        <div style={{ display: "flex", justifyContent: "center", padding: "40px" }}>
          <Spinner size="small" label="Loading sessions..." />
        </div>
      ) : error ? (
        <div style={{ padding: "20px", textAlign: "center", color: "#f87171" }}>
          Failed to load sessions.{" "}
          <Button appearance="subtle" size="small" onClick={loadSessions}>Retry</Button>
        </div>
      ) : sessions.length === 0 ? (
        <div style={{ padding: "20px", textAlign: "center", color: "#7c90b2" }}>
          No sessions yet. This agent hasn't been assigned any breakout tasks.
        </div>
      ) : (
        <div style={{ flex: 1, overflow: "auto" }}>
          {/* Current session conversation */}
          {displaySession && (
            <div style={{ height: "100%", display: "flex", flexDirection: "column" }}>
              <div style={{
                padding: "8px 20px",
                backgroundColor: "#1a1f2a",
                borderBottom: "1px solid #2a2f3a",
                display: "flex",
                alignItems: "center",
                gap: "8px",
                flexShrink: 0,
              }}>
                <Badge
                  appearance="filled"
                  size="small"
                  color={displaySession.status === "Active" ? "success" : "informative"}
                >
                  {displaySession.status}
                </Badge>
                <span style={{ fontSize: "13px", fontWeight: 500 }}>
                  {displaySession.name.replace(/^BR:\s*/, "")}
                </span>
                <span style={{ fontSize: "11px", color: "#7c90b2", marginLeft: "auto" }}>
                  {displaySession.recentMessages.length} messages
                  {" · "}
                  {formatDuration(displaySession.createdAt, displaySession.updatedAt)}
                </span>
              </div>
              <div style={{ flex: 1, overflow: "hidden" }}>
                <ChatPanel
                  room={{
                    id: displaySession.id,
                    name: displaySession.name,
                    status: displaySession.status,
                    currentPhase: "Implementation",
                    activeTask: null,
                    participants: [],
                    recentMessages: displaySession.recentMessages,
                    createdAt: displaySession.createdAt,
                    updatedAt: displaySession.updatedAt,
                  }}
                  thinkingAgents={thinkingAgents}
                  connectionStatus={connectionStatus}
                  onSendMessage={onSendMessage}
                  readOnly
                />
              </div>
            </div>
          )}

          {/* Past sessions list */}
          {archivedSessions.length > 0 && (
            <div style={{ borderTop: "1px solid #2a2f3a" }}>
              <div style={{
                padding: "8px 20px",
                fontSize: "11px",
                fontWeight: 600,
                textTransform: "uppercase",
                letterSpacing: "0.5px",
                color: "#7c90b2",
              }}>
                Past Sessions ({archivedSessions.length})
              </div>
              {archivedSessions.map((session) => (
                <button
                  key={session.id}
                  onClick={() => setExpandedSessionId(
                    expandedSessionId === session.id ? null : session.id,
                  )}
                  type="button"
                  style={{
                    display: "flex",
                    alignItems: "center",
                    gap: "10px",
                    width: "100%",
                    padding: "8px 20px",
                    border: "none",
                    background: expandedSessionId === session.id ? "#1a1f2a" : "transparent",
                    color: "#c8d0dc",
                    cursor: "pointer",
                    textAlign: "left",
                    fontSize: "13px",
                  }}
                >
                  <span style={{ color: "#7c90b2" }}>
                    {expandedSessionId === session.id ? "▾" : "▸"}
                  </span>
                  <span style={{ flex: 1, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
                    {session.name.replace(/^BR:\s*/, "")}
                  </span>
                  <span style={{ fontSize: "11px", color: "#7c90b2", flexShrink: 0 }}>
                    {session.recentMessages.length} msgs · {formatTime(session.updatedAt)}
                  </span>
                </button>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
});

export default AgentSessionPanel;
