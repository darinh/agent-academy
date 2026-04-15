import { memo, useState, useCallback } from "react";
import type { KeyboardEvent as ReactKeyboardEvent } from "react";
import {
  Button,
  mergeClasses,
  Spinner,
  Tooltip,
} from "@fluentui/react-components";
import { useSidebarStyles } from "./styles";
import { initials } from "./utils";
import { roleColor } from "./theme";
import type { AgentContextUsage, AgentDefinition, AgentLocation, AuthUser, BreakoutRoom, RoomSnapshot } from "./api";
import UserBadge from "./UserBadge";
import ContextMeter from "./ContextMeter";
import {
  phaseDotColor,
  compactRoomTooltip,
  isAgentThinking,
} from "./sidebarUtils";

/* ── View navigation items ─────────────────────────────────────── */

const NAV_ITEMS = [
  { value: "overview", icon: "🔲", label: "Overview" },
  { value: "search", icon: "🔍", label: "Search" },
  { value: "directMessages", icon: "✉️", label: "Messages" },
  { value: "plan", icon: "📄", label: "Plan" },
  { value: "tasks", icon: "📋", label: "Tasks" },
  { value: "artifacts", icon: "📦", label: "Artifacts" },
  { value: "timeline", icon: "⏱️", label: "Timeline" },
  { value: "sprint", icon: "🏃", label: "Sprint" },
  { value: "dashboard", icon: "📊", label: "Metrics" },
  { value: "commands", icon: "⌨️", label: "Commands" },
  { value: "memories", icon: "🧠", label: "Memory" },
  { value: "digests", icon: "📚", label: "Digests" },
  { value: "retrospectives", icon: "🔬", label: "Retros" },
] as const;

/* ── Sidebar Panel ───────────────────────────────────────────────── */

const SidebarPanel = memo(function SidebarPanel(props: {
  sidebarOpen: boolean;
  busy: boolean;
  rooms: RoomSnapshot[];
  room: RoomSnapshot | null;
  agentLocations: AgentLocation[];
  configuredAgents: AgentDefinition[];
  breakoutRooms: BreakoutRoom[];
  selectedWorkspaceId: string | null;
  thinkingByRoomIds: Map<string, Set<string>>;
  activeView: string;
  onViewChange: (view: string) => void;
  onRefresh: () => void;
  onToggleSidebar: () => void;
  onSelectRoom: (roomId: string) => void;
  onSelectWorkspace: (breakoutId: string) => void;
  onCreateRoom?: (name: string) => void;
  onCleanupRooms?: () => Promise<void>;
  onSwitchProject?: () => void;
  workspace?: { name: string; path: string } | null;
  user?: AuthUser | null;
  onLogout?: () => void;
  onOpenSettings?: () => void;
  sprintVersion?: number;
  contextUsage?: Map<string, AgentContextUsage>;
}) {
  const s = useSidebarStyles();
  const [creatingRoom, setCreatingRoom] = useState(false);
  const [newRoomName, setNewRoomName] = useState("");
  const [cleaningUp, setCleaningUp] = useState(false);

  const handleCleanup = useCallback(async () => {
    if (!props.onCleanupRooms) return;
    setCleaningUp(true);
    try { await props.onCleanupRooms(); } finally { setCleaningUp(false); }
  }, [props.onCleanupRooms]);

  const handleCreateRoom = useCallback(() => {
    const name = newRoomName.trim();
    if (name && props.onCreateRoom) {
      props.onCreateRoom(name);
      setNewRoomName("");
      setCreatingRoom(false);
    }
  }, [newRoomName, props.onCreateRoom]);

  const handleCreateRoomKeyDown = useCallback((e: ReactKeyboardEvent<HTMLInputElement>) => {
    if (e.key === "Enter") handleCreateRoom();
    else if (e.key === "Escape") { setCreatingRoom(false); setNewRoomName(""); }
  }, [handleCreateRoom]);

  return (
    <aside className={mergeClasses(s.sidebar, !props.sidebarOpen && s.sidebarCollapsed)}>
      {/* Brand */}
      <div className={s.sidebarHeader}>
        <div className={s.sidebarToolbar}>
          {props.sidebarOpen ? (
            <div className={s.brandBlock}>
              <div className={s.appTitle}>Agent Academy</div>
              <div className={s.appSubtitle}>
                {props.workspace ? props.workspace.name : "● Live"}
              </div>
            </div>
          ) : (
            <div className={s.eyebrow}>AA</div>
          )}
          <div className={s.sidebarUtilityRow}>
            {props.busy && <Spinner size="tiny" />}
            {props.sidebarOpen && (
              <Button
                appearance="subtle"
                size="small"
                className={s.sidebarIconButton}
                onClick={props.onRefresh}
                aria-label="Refresh"
              >
                ↻
              </Button>
            )}
          </div>
        </div>
      </div>

      {/* Body */}
      {props.sidebarOpen ? (
        <div className={s.sidebarBody}>
          {/* Sprint indicator */}
          {(props.sprintVersion ?? 0) > 0 && (
            <div className={s.sprintIndicator}>🏃 Sprint {props.sprintVersion}</div>
          )}

          {/* View Navigation */}
          <div className={s.navSection}>
            {NAV_ITEMS.map((item) => (
              <button
                key={item.value}
                className={mergeClasses(s.navItem, props.activeView === item.value && s.navItemActive)}
                onClick={() => props.onViewChange(item.value)}
                type="button"
              >
                <span className={s.navIcon}>{item.icon}</span>
                {item.label}
              </button>
            ))}
          </div>

          {/* Rooms */}
          <section className={s.section}>
            <div className={s.sectionHeader}>
              <div className={s.sectionLabel}>Rooms</div>
              <div style={{ display: "flex", alignItems: "center", gap: "4px" }}>
                <div className={s.sectionCount}>{props.rooms.length}</div>
                {props.onCleanupRooms && (
                  <Tooltip content="Archive idle rooms" relationship="label">
                    <Button
                      appearance="subtle"
                      size="small"
                      className={s.sidebarIconButton}
                      onClick={handleCleanup}
                      disabled={cleaningUp}
                      aria-label="Cleanup idle rooms"
                      style={{ minWidth: 0, padding: "0 4px", fontSize: "11px" }}
                    >
                      {cleaningUp ? <Spinner size="tiny" /> : "🧹"}
                    </Button>
                  </Tooltip>
                )}
                {props.onCreateRoom && (
                  <Button
                    appearance="subtle"
                    size="small"
                    className={s.sidebarIconButton}
                    onClick={() => setCreatingRoom(true)}
                    aria-label="Create room"
                    style={{ minWidth: 0, padding: "0 4px", fontSize: "13px" }}
                  >
                    +
                  </Button>
                )}
              </div>
            </div>
            {creatingRoom && (
              <div style={{ padding: "2px 8px 6px" }}>
                <input
                  autoFocus
                  type="text"
                  placeholder="Room name…"
                  value={newRoomName}
                  onChange={(e) => setNewRoomName(e.target.value)}
                  onKeyDown={handleCreateRoomKeyDown}
                  onBlur={() => { if (!newRoomName.trim()) setCreatingRoom(false); }}
                  style={{
                    width: "100%", background: "var(--aa-surface, #1e1e2e)",
                    border: "1px solid var(--aa-border, #333)", borderRadius: "4px",
                    padding: "4px 8px", color: "inherit", fontSize: "12px",
                    outline: "none",
                  }}
                />
              </div>
            )}
            <div className={s.roomList}>
              {props.rooms.map((candidate) => {
                const dotColor = phaseDotColor(candidate.currentPhase);
                return (
                  <button
                    key={candidate.id}
                    className={mergeClasses(s.roomButton, s.roomButtonHover, props.room?.id === candidate.id ? s.roomButtonActive : undefined)}
                    onClick={() => props.onSelectRoom(candidate.id)}
                    aria-label={`Select room ${candidate.name}`}
                    type="button"
                  >
                    <span className={s.roomPhaseDot} style={{ backgroundColor: dotColor }} />
                    <span className={s.roomButtonName}>{candidate.name}</span>
                    <span className={s.roomButtonCount}>{candidate.participants.length}</span>
                  </button>
                );
              })}
            </div>
          </section>

          {/* Agents */}
          {props.configuredAgents.length > 0 && (
            <section className={s.section}>
              <div className={s.sectionHeader}>
                <div className={s.sectionLabel}>Agents</div>
                <div className={s.sectionCount}>{props.configuredAgents.length}</div>
              </div>
              <div className={s.roomList}>
                {props.configuredAgents.map((agent) => {
                  const loc = props.agentLocations.find((l) => l.agentId === agent.id);
                  const state = loc?.state ?? "Idle";
                  const isWorking = state === "Working";
                  const thinking = isAgentThinking(props.thinkingByRoomIds, agent.id);
                  const rc = roleColor(agent.role);
                  const isSelected = props.selectedWorkspaceId === `agent:${agent.id}`;

                  return (
                    <button
                      key={agent.id}
                      className={mergeClasses(s.workspaceButton, s.roomButtonHover, isSelected ? s.roomButtonActive : undefined)}
                      onClick={() => props.onSelectWorkspace(`agent:${agent.id}`)}
                      aria-label={`View ${agent.name}'s sessions`}
                      type="button"
                      style={{ position: "relative" }}
                    >
                      <span className={s.roomPhaseDot} style={{ backgroundColor: isWorking ? rc.accent : "var(--aa-soft)" }} />
                      {thinking && (
                        <span style={{
                          position: "absolute", left: "6px", top: "2px", width: "10px", height: "10px",
                          borderRadius: "999px", border: "2px solid transparent",
                          borderTopColor: rc.accent, animation: "aa-spin 0.8s linear infinite",
                        }} />
                      )}
                      <div className={s.workspaceButtonBody}>
                        <div className={s.workspaceButtonTopRow}>
                          <span className={s.workspaceName}>{agent.name}</span>
                          <span
                            className={s.workspaceStateBadge}
                            style={{
                              backgroundColor: isWorking ? `${rc.accent}22` : "rgba(139, 148, 158, 0.08)",
                              color: isWorking ? rc.accent : "var(--aa-soft)",
                            }}
                          >
                            {state.toLowerCase()}
                          </span>
                          {props.contextUsage?.get(agent.id) && (
                            <ContextMeter usage={props.contextUsage.get(agent.id)!} />
                          )}
                        </div>
                      </div>
                    </button>
                  );
                })}
              </div>
            </section>
          )}

          {props.onSwitchProject && (
            <section className={s.section}>
              <Button
                appearance="subtle"
                size="small"
                onClick={props.onSwitchProject}
                style={{ width: "100%", justifyContent: "flex-start", gap: "8px", fontSize: "11px" }}
              >
                ⇄ Switch Project
              </Button>
            </section>
          )}
        </div>
      ) : (
        <div className={s.compactSidebar}>
          <Tooltip content="Expand sidebar" relationship="label" positioning="after">
            <Button
              appearance="subtle"
              size="small"
              className={s.compactButton}
              onClick={props.onToggleSidebar}
              aria-label="Expand sidebar"
            >
              ▷
            </Button>
          </Tooltip>
          {props.rooms.map((candidate) => {
            const dotColor = phaseDotColor(candidate.currentPhase);
            const tooltipText = compactRoomTooltip(candidate);
            return (
              <Tooltip key={candidate.id} content={tooltipText} relationship="label" positioning="after">
                <button
                  className={mergeClasses(s.compactButton, props.room?.id === candidate.id ? s.compactButtonActive : undefined)}
                  onClick={() => props.onSelectRoom(candidate.id)}
                  aria-label={candidate.name}
                  type="button"
                >
                  <span className={s.compactRoomDot} style={{ backgroundColor: dotColor }} />
                  {initials(candidate.name)}
                </button>
              </Tooltip>
            );
          })}
        </div>
      )}

      {/* User area at bottom */}
      {props.sidebarOpen && props.user && (
        <UserBadge
          user={props.user}
          onLogout={props.onLogout ?? (() => {})}
          onOpenSettings={props.onOpenSettings}
        />
      )}
    </aside>
  );
});

export default SidebarPanel;
