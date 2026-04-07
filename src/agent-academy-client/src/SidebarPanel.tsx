import { memo } from "react";
import {
  Button,
  mergeClasses,
  Spinner,
  Tooltip,
} from "@fluentui/react-components";
import { useStyles } from "./useStyles";
import { initials } from "./utils";
import { roleColor } from "./theme";
import type { AgentDefinition, AgentLocation, AuthUser, BreakoutRoom, RoomSnapshot } from "./api";
import UserBadge from "./UserBadge";
import {
  phaseDotColor,
  compactRoomTooltip,
  isAgentThinking,
} from "./sidebarUtils";

/* ── View navigation items ─────────────────────────────────────── */

const NAV_ITEMS = [
  { value: "chat", icon: "💬", label: "Conversation" },
  { value: "tasks", icon: "📋", label: "Tasks" },
  { value: "plan", icon: "📄", label: "Plan" },
  { value: "commands", icon: "⌨️", label: "Commands" },
  { value: "timeline", icon: "⏱️", label: "Timeline" },
  { value: "dashboard", icon: "📊", label: "Dashboard" },
  { value: "overview", icon: "🔲", label: "Overview" },
  { value: "directMessages", icon: "✉️", label: "Messages" },
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
  onSwitchProject?: () => void;
  workspace?: { name: string; path: string } | null;
  user?: AuthUser | null;
  onLogout?: () => void;
  onOpenSettings?: () => void;
}) {
  const s = useStyles();

  return (
    <aside className={mergeClasses(s.sidebar, !props.sidebarOpen && s.sidebarCollapsed)}>
      {/* Brand */}
      <div className={s.sidebarHeader}>
        <div className={s.sidebarToolbar}>
          {props.sidebarOpen ? (
            <div className={s.brandBlock}>
              <div className={s.appTitle}>Agent Academy</div>
              <div className={s.appSubtitle}>● Live</div>
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
              <div className={s.sectionCount}>{props.rooms.length}</div>
            </div>
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
                              backgroundColor: isWorking ? `${rc.accent}22` : "rgba(110, 118, 129, 0.15)",
                              color: isWorking ? rc.accent : "var(--aa-soft)",
                            }}
                          >
                            {state.toLowerCase()}
                          </span>
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
