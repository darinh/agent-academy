import { memo } from "react";
import {
  Button,
  mergeClasses,
  Spinner,
} from "@fluentui/react-components";
import { useStyles } from "./useStyles";
import { initials } from "./utils";
import { roleColor } from "./theme";
import type { AgentDefinition, AgentLocation, RoomSnapshot } from "./api";

const PHASE_DOT_COLORS: Record<string, string> = {
  Intake: "#94a3b8",
  Planning: "#6cb6ff",
  Discussion: "#a78bfa",
  Implementation: "#34d399",
  Validation: "#fbbf24",
  FinalSynthesis: "#f472b6",
};

/* ── Sidebar Panel ───────────────────────────────────────────────── */

const SidebarPanel = memo(function SidebarPanel(props: {
  sidebarOpen: boolean;
  busy: boolean;
  rooms: RoomSnapshot[];
  room: RoomSnapshot | null;
  agentLocations: AgentLocation[];
  configuredAgents: AgentDefinition[];
  thinkingByRoomIds: Map<string, Set<string>>;
  onRefresh: () => void;
  onToggleSidebar: () => void;
  onSelectRoom: (roomId: string) => void;
  onSwitchProject?: () => void;
  workspace?: { name: string; path: string } | null;
}) {
  const s = useStyles();

  // Build a map of roomId → agents in that room
  const agentsByRoom = new Map<string, AgentDefinition[]>();
  for (const loc of props.agentLocations) {
    const agent = props.configuredAgents.find((a) => a.id === loc.agentId);
    if (!agent) continue;
    const list = agentsByRoom.get(loc.roomId) ?? [];
    list.push(agent);
    agentsByRoom.set(loc.roomId, list);
  }

  return (
    <aside className={mergeClasses(s.sidebar, !props.sidebarOpen && s.sidebarCollapsed)}>
      {/* Header */}
      <div className={s.sidebarHeader}>
        <div className={s.sidebarToolbar}>
          {props.sidebarOpen ? (
            <div className={s.appTitle}>Agent Academy</div>
          ) : (
            <div className={s.eyebrow}>Live</div>
          )}
          <div style={{ display: "flex", alignItems: "center", gap: "6px" }}>
            {props.busy && <Spinner size="tiny" />}
            {props.sidebarOpen && (
              <Button appearance="subtle" size="small" onClick={props.onRefresh} aria-label="Refresh">
                ↻
              </Button>
            )}
            <Button
              appearance="subtle"
              size="small"
              onClick={props.onToggleSidebar}
              aria-label={props.sidebarOpen ? "Collapse sidebar" : "Expand sidebar"}
            >
              {props.sidebarOpen ? "◁" : "▷"}
            </Button>
          </div>
        </div>

        {props.sidebarOpen && props.workspace && (
          <div style={{ marginTop: "4px" }}>
            <div style={{ display: "flex", alignItems: "center", gap: "6px", fontSize: "11px", color: "#7c90b2", padding: "0 12px" }}>
              <span>📁</span>
              <span style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap", flex: 1 }} title={props.workspace.path}>
                {props.workspace.name}
              </span>
              {props.onSwitchProject && (
                <Button
                  appearance="transparent"
                  size="small"
                  onClick={props.onSwitchProject}
                  aria-label="Switch project"
                  style={{ minWidth: "auto", padding: "0 4px", fontSize: "11px", color: "#7c90b2", height: "20px" }}
                >
                  ⇄
                </Button>
              )}
            </div>
          </div>
        )}
      </div>

      {/* Body */}
      {props.sidebarOpen ? (
        <div className={s.sidebarBody}>
          <section className={s.section} style={{ borderTop: "none" }}>
            <div className={s.sectionHeader}>
              <div className={s.sectionLabel}>Rooms</div>
            </div>
            <div className={s.roomList}>
              {props.rooms.map((candidate) => {
                const dotColor = PHASE_DOT_COLORS[candidate.currentPhase] ?? "#94a3b8";
                const roomAgents = agentsByRoom.get(candidate.id) ?? [];
                return (
                  <button
                    key={candidate.id}
                    className={mergeClasses(s.roomButton, s.roomButtonHover, props.room?.id === candidate.id ? s.roomButtonActive : undefined)}
                    onClick={() => props.onSelectRoom(candidate.id)}
                    aria-label={`Select room ${candidate.name}`}
                    type="button"
                  >
                    <div className={s.roomButtonIcon}>{initials(candidate.name)}</div>
                    <div style={{ minWidth: 0 }}>
                      <div style={{ display: "flex", alignItems: "center", gap: "8px" }}>
                        <div className={s.roomButtonName}>{candidate.name}</div>
                        <span style={{ width: "6px", height: "6px", borderRadius: "999px", backgroundColor: dotColor, flexShrink: 0 }} />
                      </div>
                    </div>
                    {roomAgents.length > 0 && (
                      <div className={s.roomAgentList}>
                        {roomAgents.map((agent) => {
                          const rc = roleColor(agent.role);
                          const isThinking = props.thinkingByRoomIds.get(candidate.id)?.has(agent.id) ?? false;
                          return (
                            <div key={agent.id} className={s.roomAgentItem}>
                              <span style={{ position: "relative", display: "inline-flex", alignItems: "center", justifyContent: "center", width: "16px", height: "16px", flexShrink: 0 }}>
                                <span className={s.agentStateDot} style={{ backgroundColor: rc.accent }} />
                                {isThinking && (
                                  <span
                                    style={{
                                      position: "absolute",
                                      inset: 0,
                                      borderRadius: "999px",
                                      border: `2px solid transparent`,
                                      borderTopColor: rc.accent,
                                      animation: "aa-spin 0.8s linear infinite",
                                    }}
                                  />
                                )}
                              </span>
                              <span>{agent.name}</span>
                            </div>
                          );
                        })}
                      </div>
                    )}
                  </button>
                );
              })}
            </div>
          </section>

          {props.onSwitchProject && (
            <section className={s.section}>
              <Button
                appearance="subtle"
                size="small"
                onClick={props.onSwitchProject}
                style={{ width: "100%", justifyContent: "flex-start", gap: "8px" }}
              >
                ⇄ Switch Project
              </Button>
            </section>
          )}
        </div>
      ) : (
        <div className={s.compactSidebar}>
          {props.rooms.map((candidate) => (
            <button
              key={candidate.id}
              className={mergeClasses(s.compactButton, props.room?.id === candidate.id ? s.compactButtonActive : undefined)}
              onClick={() => props.onSelectRoom(candidate.id)}
              title={candidate.name}
              aria-label={candidate.name}
              type="button"
            >
              {initials(candidate.name)}
            </button>
          ))}
        </div>
      )}
    </aside>
  );
});

export default SidebarPanel;
