import { memo, useState, useEffect, useCallback } from "react";
import {
  Button,
  Spinner,
} from "@fluentui/react-components";
import { roleColor } from "./theme";
import V3Badge from "./V3Badge";
import type { BadgeColor } from "./V3Badge";
import type { AgentDefinition, AgentLocation, BreakoutRoom } from "./api";
import { getAgentSessions } from "./api";
import ChatPanel from "./ChatPanel";
import type { ThinkingAgent } from "./useWorkspace";
import type { ConnectionStatus } from "./useActivityHub";
import { formatElapsed } from "./panelUtils";

interface AgentSessionPanelProps {
  agent: AgentDefinition;
  location?: AgentLocation;
  thinkingAgents: ThinkingAgent[];
  connectionStatus: ConnectionStatus;
  onSendMessage: (roomId: string, message: string) => Promise<boolean>;
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
  const stateColor: BadgeColor = state === "Working" ? "ok" : state === "Presenting" ? "warn" : "info";
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
        borderBottom: "1px solid var(--aa-border)",
        display: "flex",
        alignItems: "center",
        gap: "12px",
        flexShrink: 0,
      }}>
        <div style={{
          width: "36px", height: "36px", borderRadius: "10px",
          display: "flex", alignItems: "center", justifyContent: "center",
          fontWeight: 600, fontSize: "16px", color: "white",
          background: `linear-gradient(135deg, ${rc.accent}, ${rc.accent}88)`,
        }}>
          {agent.name.charAt(0)}
        </div>
        <div>
          <div style={{ fontWeight: 600, fontSize: "14px" }}>{agent.name}</div>
          <div style={{ fontSize: "12px", color: "var(--aa-muted)", display: "flex", alignItems: "center", gap: "6px" }}>
            <span>{agent.role}</span>
            <V3Badge color={stateColor}>{state}</V3Badge>
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
        <div style={{ padding: "20px", textAlign: "center", color: "var(--aa-copper)" }}>
          Failed to load sessions.{" "}
          <Button appearance="subtle" size="small" onClick={loadSessions}>Retry</Button>
        </div>
      ) : sessions.length === 0 ? (
        <div style={{ padding: "20px", textAlign: "center", color: "var(--aa-muted)" }}>
          No sessions yet. This agent hasn't been assigned any breakout tasks.
        </div>
      ) : (
        <div style={{ flex: 1, overflow: "auto" }}>
          {/* Current session conversation */}
          {displaySession && (
            <div style={{ height: "100%", display: "flex", flexDirection: "column" }}>
              <div style={{
                padding: "8px 20px",
                backgroundColor: "var(--aa-panel)",
                borderBottom: "1px solid var(--aa-border)",
                display: "flex",
                alignItems: "center",
                gap: "8px",
                flexShrink: 0,
              }}>
                <V3Badge color={displaySession.status === "Active" ? "ok" : "info"}>
                  {displaySession.status}
                </V3Badge>
                <span style={{ fontSize: "13px", fontWeight: 500 }}>
                  {displaySession.name.replace(/^BR:\s*/, "")}
                </span>
                <span style={{ fontSize: "11px", color: "var(--aa-muted)", marginLeft: "auto" }}>
                  {displaySession.recentMessages.length} messages
                  {" · "}
                  {formatElapsed(displaySession.createdAt, displaySession.updatedAt)}
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
            <div style={{ borderTop: "1px solid var(--aa-border)" }}>
              <div style={{
                padding: "8px 20px",
                fontSize: "11px",
                fontWeight: 600,
                textTransform: "uppercase",
                letterSpacing: "0.5px",
                color: "var(--aa-muted)",
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
                    background: expandedSessionId === session.id ? "var(--aa-panel)" : "transparent",
                    color: "var(--aa-text)",
                    cursor: "pointer",
                    textAlign: "left",
                    fontSize: "13px",
                  }}
                >
                  <span style={{ color: "var(--aa-muted)" }}>
                    {expandedSessionId === session.id ? "▾" : "▸"}
                  </span>
                  <span style={{ flex: 1, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
                    {session.name.replace(/^BR:\s*/, "")}
                  </span>
                  <span style={{ fontSize: "11px", color: "var(--aa-muted)", flexShrink: 0 }}>
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
