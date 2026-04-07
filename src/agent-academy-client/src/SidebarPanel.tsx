import { memo, useState, useRef, useCallback } from "react";
import {
  Button,
  mergeClasses,
  Spinner,
  Tooltip,
} from "@fluentui/react-components";
import { useStyles } from "./useStyles";
import { initials } from "./utils";
import { roleColor } from "./theme";
import type { AgentDefinition, AgentLocation, BreakoutRoom, RoomSnapshot } from "./api";
import { renameRoom } from "./api";
import {
  phaseDotColor,
  buildAgentsByRoom,
  compactRoomTooltip,
  getActiveBreakout,
  getBreakoutTaskName,
  isAgentThinking,
} from "./sidebarUtils";

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
  onRefresh: () => void;
  onToggleSidebar: () => void;
  onSelectRoom: (roomId: string) => void;
  onSelectWorkspace: (breakoutId: string) => void;
  onSwitchProject?: () => void;
  workspace?: { name: string; path: string } | null;
}) {
  const s = useStyles();
  const agentsByRoom = buildAgentsByRoom(props.agentLocations, props.configuredAgents);

  return (
    <aside className={mergeClasses(s.sidebar, !props.sidebarOpen && s.sidebarCollapsed)}>
      {/* Header */}
      <div className={s.sidebarHeader}>
        <div className={s.sidebarToolbar}>
          {props.sidebarOpen ? (
            <div className={s.brandBlock}>
              <div className={s.appTitle}>Agent Academy</div>
              <div className={s.appSubtitle}>Mission control for spec-aware delivery</div>
            </div>
          ) : (
            <div className={s.eyebrow}>Live</div>
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
            <Button
              appearance="subtle"
              size="small"
              className={s.sidebarIconButton}
              onClick={props.onToggleSidebar}
              aria-label={props.sidebarOpen ? "Collapse sidebar" : "Expand sidebar"}
            >
              {props.sidebarOpen ? "◁" : "▷"}
            </Button>
          </div>
        </div>

        {props.sidebarOpen && props.workspace && (
          <div className={s.sidebarProjectCard}>
            <div className={s.sidebarProjectRow}>
              <div style={{ minWidth: 0, flex: 1 }}>
                <div className={s.sidebarProjectName} title={props.workspace.name}>{props.workspace.name}</div>
              </div>
              {props.onSwitchProject && (
                <Button
                  appearance="transparent"
                  size="small"
                  className={s.sidebarProjectAction}
                  onClick={props.onSwitchProject}
                  aria-label="Switch project"
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
              <div className={s.sectionCount}>{props.rooms.length}</div>
            </div>
            <div className={s.roomList}>
              {props.rooms.map((candidate) => {
                const dotColor = phaseDotColor(candidate.currentPhase);
                const roomAgents = agentsByRoom.get(candidate.id) ?? [];
                return (
                  <RoomButton
                    key={candidate.id}
                    room={candidate}
                    isActive={props.room?.id === candidate.id}
                    dotColor={dotColor}
                    roomAgents={roomAgents}
                    thinkingAgentIds={props.thinkingByRoomIds.get(candidate.id)}
                    onSelect={() => props.onSelectRoom(candidate.id)}
                    onRenamed={props.onRefresh}
                    s={s}
                  />
                );
              })}
            </div>
          </section>

          {/* Agent Sessions (grouped by agent, not per breakout room) */}
          {props.configuredAgents.length > 0 && (
            <section className={s.section}>
              <div className={s.sectionHeader}>
                <div className={s.sectionLabel}>Agent Sessions</div>
                <div className={s.sectionCount}>{props.configuredAgents.length}</div>
              </div>
              <div className={s.roomList}>
                {props.configuredAgents.map((agent) => {
                  const loc = props.agentLocations.find((l) => l.agentId === agent.id);
                  const activeBreakout = getActiveBreakout(props.breakoutRooms, agent.id);
                  const state = loc?.state ?? "Idle";
                  const isWorking = state === "Working";
                  const thinking = isAgentThinking(props.thinkingByRoomIds, agent.id);
                  const rc = roleColor(agent.role);
                  const isSelected = props.selectedWorkspaceId === `agent:${agent.id}`;
                  const taskName = getBreakoutTaskName(activeBreakout);

                  return (
                    <button
                      key={agent.id}
                      className={mergeClasses(s.workspaceButton, s.roomButtonHover, isSelected ? s.roomButtonActive : undefined)}
                      onClick={() => props.onSelectWorkspace(`agent:${agent.id}`)}
                      aria-label={`View ${agent.name}'s sessions`}
                      type="button"
                      >
                        <span style={{ position: "relative", display: "inline-flex", alignItems: "center", justifyContent: "center", flexShrink: 0 }}>
                          <div className={s.workspaceIcon} style={{ background: `linear-gradient(135deg, ${rc.accent}, ${rc.accent}88)` }}>
                            {agent.name.charAt(0)}
                        </div>
                        {thinking && (
                          <span style={{
                            position: "absolute", inset: "-2px", borderRadius: "999px",
                            border: "2px solid transparent", borderTopColor: rc.accent,
                            animation: "aa-spin 0.8s linear infinite",
                          }} />
                        )}
                        </span>
                        <div className={s.workspaceButtonBody}>
                          <div className={s.workspaceButtonTopRow}>
                            <span className={s.workspaceName}>{agent.name}</span>
                            <span
                              className={s.workspaceStateBadge}
                            style={{
                              backgroundColor: isWorking ? `${rc.accent}33` : "#ffffff11",
                              color: isWorking ? rc.accent : "#7c90b2",
                              animation: isWorking ? "aa-pulse 2s ease-in-out infinite" : undefined,
                            }}
                            >
                              {state}
                            </span>
                          </div>
                          <div className={s.workspaceRole}>{agent.role}</div>
                          <div className={s.workspaceTask}>
                            {taskName ?? agent.role}
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
                style={{ width: "100%", justifyContent: "flex-start", gap: "8px" }}
              >
                ⇄ Switch Project
              </Button>
            </section>
          )}
        </div>
      ) : (
        <div className={s.compactSidebar}>
          {props.workspace && (
            <Tooltip content={props.workspace.name} relationship="label" positioning="after">
              <div className={s.compactSidebarMarker}>
                {initials(props.workspace.name)}
              </div>
            </Tooltip>
          )}
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
    </aside>
  );
});

/* ── RoomButton — inline-editable room name ──────────────────────── */

function RoomButton(props: {
  room: RoomSnapshot;
  isActive: boolean;
  dotColor: string;
  roomAgents: AgentDefinition[];
  thinkingAgentIds?: Set<string>;
  onSelect: () => void;
  onRenamed: () => void;
  s: ReturnType<typeof useStyles>;
}) {
  const { room, isActive, dotColor, roomAgents, thinkingAgentIds, onSelect, onRenamed, s } = props;
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(room.name);
  const inputRef = useRef<HTMLInputElement>(null);

  const commitRename = useCallback(async () => {
    const trimmed = draft.trim();
    if (!trimmed || trimmed === room.name) {
      setEditing(false);
      setDraft(room.name);
      return;
    }
    try {
      await renameRoom(room.id, trimmed);
      onRenamed();
    } catch { /* revert on failure */ }
    setEditing(false);
  }, [draft, room.id, room.name, onRenamed]);

  return (
    <button
      className={mergeClasses(s.roomButton, s.roomButtonHover, isActive ? s.roomButtonActive : undefined)}
      onClick={onSelect}
      aria-label={`Select room ${room.name}`}
      type="button"
    >
      <div className={s.roomButtonIcon}>{initials(room.name)}</div>
      <div className={s.roomButtonBody}>
        <div className={s.roomButtonTopRow}>
          <div className={s.roomButtonMeta}>
            <span className={s.roomPhaseDot} style={{ backgroundColor: dotColor }} />
            <span className={s.roomPhaseLabel}>{room.currentPhase}</span>
          </div>
          <div className={s.roomButtonCount}>
            {room.participants.length} agents
          </div>
        </div>
        <div className={s.roomButtonTitleWrap}>
          {editing ? (
            <input
              ref={inputRef}
              className={s.roomButtonName}
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              onBlur={() => void commitRename()}
              onKeyDown={(e) => {
                if (e.key === "Enter") void commitRename();
                if (e.key === "Escape") { setEditing(false); setDraft(room.name); }
                e.stopPropagation();
              }}
              onClick={(e) => e.stopPropagation()}
              autoFocus
              style={{
                background: "transparent",
                border: "1px solid #555",
                borderRadius: "4px",
                color: "inherit",
                font: "inherit",
                padding: "1px 4px",
                width: "100%",
                outline: "none",
              }}
            />
          ) : (
            <div
              className={s.roomButtonName}
              onDoubleClick={(e) => {
                e.stopPropagation();
                setDraft(room.name);
                setEditing(true);
              }}
              title="Double-click to rename"
            >
              {room.name}
            </div>
          )}
        </div>
        <div className={s.roomButtonTask}>
          {room.activeTask?.title ?? "No active task assigned"}
        </div>
      </div>
      {roomAgents.length > 0 && (
        <div className={s.roomAgentList}>
          {roomAgents.map((agent) => {
            const rc = roleColor(agent.role);
            const isThinking = thinkingAgentIds?.has(agent.id) ?? false;
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
                        border: "2px solid transparent",
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
}

export default SidebarPanel;
