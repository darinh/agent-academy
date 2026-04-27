import { memo, useState, useCallback, useEffect, useRef } from "react";
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

/* ── View navigation groups ─────────────────────────────────────
 *
 * Six logical groups replace the previous flat 18-item nav. Order roughly
 * follows the user-supplied 2026-04-08 priority spec
 * (overview → conversation → messages → plan → task → timeline → metrics → commands)
 * while keeping conversation as room-click rather than a tab (per 2026-04-09 spec).
 * Each group can be collapsed by the user; collapsed state persists per group
 * via localStorage. Defaults: all expanded so first-time / mobile users see
 * everything; this also preserves existing test assertions that all nav items
 * render.
 */

interface NavItem {
  value: string;
  icon: string;
  label: string;
}

interface NavGroup {
  id: string;
  label: string;
  items: NavItem[];
}

const NAV_GROUPS: NavGroup[] = [
  {
    id: "workspace",
    label: "Workspace",
    items: [
      { value: "overview", icon: "🔲", label: "Overview" },
      { value: "plan", icon: "📄", label: "Plan" },
      { value: "sprint", icon: "🏃", label: "Sprint" },
      { value: "goalCards", icon: "🎯", label: "Goals" },
    ],
  },
  {
    id: "comms",
    label: "Communication",
    items: [
      { value: "directMessages", icon: "✉️", label: "Messages" },
      { value: "search", icon: "🔍", label: "Search" },
    ],
  },
  {
    id: "work",
    label: "Work",
    items: [
      { value: "tasks", icon: "📋", label: "Tasks" },
      { value: "artifacts", icon: "📦", label: "Artifacts" },
      { value: "forge", icon: "🔥", label: "Forge" },
    ],
  },
  {
    id: "activity",
    label: "Activity",
    items: [
      { value: "timeline", icon: "⏱️", label: "Timeline" },
      { value: "activity", icon: "⚡", label: "Activity" },
      { value: "dashboard", icon: "📊", label: "Metrics" },
    ],
  },
  {
    id: "knowledge",
    label: "Knowledge",
    items: [
      { value: "memories", icon: "🧠", label: "Memory" },
      { value: "knowledge", icon: "📖", label: "Knowledge" },
      { value: "specs", icon: "📜", label: "Specs" },
      { value: "digests", icon: "📚", label: "Digests" },
      { value: "retrospectives", icon: "🔬", label: "Retros" },
    ],
  },
  {
    id: "tools",
    label: "Tools",
    items: [
      { value: "commands", icon: "⌨️", label: "Commands" },
    ],
  },
];

const NAV_GROUP_STORAGE_KEY = "aa.sidebar.collapsedGroups.v1";
const SIDEBAR_WIDTH_STORAGE_KEY = "aa.sidebar.width.v1";
const SIDEBAR_WIDTH_DEFAULT = 200;
const SIDEBAR_WIDTH_MIN = 180;
const SIDEBAR_WIDTH_MAX = 560;

function loadSidebarWidth(): number {
  if (typeof window === "undefined") return SIDEBAR_WIDTH_DEFAULT;
  try {
    const raw = window.localStorage?.getItem(SIDEBAR_WIDTH_STORAGE_KEY);
    if (!raw) return SIDEBAR_WIDTH_DEFAULT;
    const n = Number(raw);
    if (!Number.isFinite(n)) return SIDEBAR_WIDTH_DEFAULT;
    return Math.min(SIDEBAR_WIDTH_MAX, Math.max(SIDEBAR_WIDTH_MIN, Math.round(n)));
  } catch {
    return SIDEBAR_WIDTH_DEFAULT;
  }
}

function saveSidebarWidth(width: number): void {
  if (typeof window === "undefined") return;
  try {
    window.localStorage?.setItem(SIDEBAR_WIDTH_STORAGE_KEY, String(width));
  } catch {
    /* privacy mode / quota — degrade silently */
  }
}

function loadCollapsedGroups(): Set<string> {
  if (typeof window === "undefined") return new Set();
  try {
    const storage = window.localStorage;
    if (!storage) return new Set();
    const raw = storage.getItem(NAV_GROUP_STORAGE_KEY);
    if (!raw) return new Set();
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return new Set();
    return new Set(parsed.filter((s): s is string => typeof s === "string"));
  } catch {
    // Privacy mode, disabled storage, quota errors, or malformed JSON — degrade silently
    return new Set();
  }
}

function saveCollapsedGroups(set: Set<string>): void {
  if (typeof window === "undefined") return;
  try {
    const storage = window.localStorage;
    if (!storage) return;
    storage.setItem(NAV_GROUP_STORAGE_KEY, JSON.stringify([...set]));
  } catch {
    // Privacy mode / quota / permission errors — degrade silently
  }
}

/* ── Sidebar Panel ───────────────────────────────────────────────── */

const SidebarPanel = memo(function SidebarPanel(props: {
  sidebarOpen: boolean;
  sidebarPinned?: boolean;
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
  onToggleSidebarPin?: () => void;
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
  const [collapsedGroups, setCollapsedGroups] = useState<Set<string>>(() => loadCollapsedGroups());
  const [sidebarWidth, setSidebarWidth] = useState<number>(() => loadSidebarWidth());
  const [resizing, setResizing] = useState(false);
  const sidebarRef = useRef<HTMLElement | null>(null);
  // Holds the cleanup fn for an in-flight drag so we can detach window
  // listeners if the component unmounts (or the sidebar collapses) mid-drag.
  const dragCleanupRef = useRef<(() => void) | null>(null);

  useEffect(() => () => {
    dragCleanupRef.current?.();
    dragCleanupRef.current = null;
  }, []);

  useEffect(() => {
    saveCollapsedGroups(collapsedGroups);
  }, [collapsedGroups]);

  // Persist sidebar width on every change. Cheap (single localStorage write)
  // and means the value survives reloads even if the user never mouses up
  // (e.g., a crash mid-drag).
  useEffect(() => {
    saveSidebarWidth(sidebarWidth);
  }, [sidebarWidth]);

  const handleResizeMouseDown = useCallback((e: React.MouseEvent) => {
    e.preventDefault();
    // If a previous drag never received mouseup (rare; e.g. rapid remount),
    // tear down its listeners before starting a new one.
    dragCleanupRef.current?.();
    setResizing(true);
    const startX = e.clientX;
    const startWidth = sidebarRef.current?.getBoundingClientRect().width ?? sidebarWidth;
    const onMove = (ev: MouseEvent) => {
      const delta = ev.clientX - startX;
      const next = Math.min(SIDEBAR_WIDTH_MAX, Math.max(SIDEBAR_WIDTH_MIN, Math.round(startWidth + delta)));
      setSidebarWidth(next);
    };
    const cleanup = () => {
      setResizing(false);
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
      dragCleanupRef.current = null;
    };
    const onUp = () => cleanup();
    dragCleanupRef.current = cleanup;
    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
  }, [sidebarWidth]);

  const handleResizeKeyDown = useCallback((e: ReactKeyboardEvent<HTMLDivElement>) => {
    let delta = 0;
    if (e.key === "ArrowLeft") delta = -16;
    else if (e.key === "ArrowRight") delta = 16;
    else if (e.key === "Home") {
      e.preventDefault();
      setSidebarWidth(SIDEBAR_WIDTH_DEFAULT);
      return;
    }
    if (delta === 0) return;
    e.preventDefault();
    setSidebarWidth((cur) => Math.min(SIDEBAR_WIDTH_MAX, Math.max(SIDEBAR_WIDTH_MIN, cur + delta)));
  }, []);

  const handleResizeDoubleClick = useCallback(() => {
    setSidebarWidth(SIDEBAR_WIDTH_DEFAULT);
  }, []);

  const toggleGroup = useCallback((groupId: string) => {
    setCollapsedGroups((prev) => {
      const next = new Set(prev);
      if (next.has(groupId)) {
        next.delete(groupId);
      } else {
        next.add(groupId);
      }
      return next;
    });
  }, []);

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

  /* Group agents by their current room so they can render under each room card.
     Agents whose location.roomId does not match any room in `rooms` (or who have
     no location at all) are surfaced in the fallback "Agents" section below.
     Duplicate locations per agent (e.g., stale + current) are resolved by
     keeping the most recently updated entry, then used everywhere placement
     or state needs to be derived. */
  const roomIds = new Set(props.rooms.map((r) => r.id));
  const agentLocationById = new Map<string, AgentLocation>();
  for (const loc of props.agentLocations) {
    const existing = agentLocationById.get(loc.agentId);
    if (!existing || existing.updatedAt < loc.updatedAt) {
      agentLocationById.set(loc.agentId, loc);
    }
  }
  const agentsByRoom = new Map<string, AgentDefinition[]>();
  const unassignedAgents: AgentDefinition[] = [];
  for (const agent of props.configuredAgents) {
    const loc = agentLocationById.get(agent.id);
    if (loc && roomIds.has(loc.roomId)) {
      const list = agentsByRoom.get(loc.roomId) ?? [];
      list.push(agent);
      agentsByRoom.set(loc.roomId, list);
    } else {
      unassignedAgents.push(agent);
    }
  }

  const renderAgentButton = (agent: AgentDefinition) => {
    const loc = agentLocationById.get(agent.id);
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
  };

  return (
    <aside
      ref={sidebarRef}
      className={mergeClasses(s.sidebar, !props.sidebarOpen && s.sidebarCollapsed)}
      style={props.sidebarOpen ? { width: `${sidebarWidth}px`, minWidth: `${sidebarWidth}px` } : undefined}
    >
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
              <>
                <Button
                  appearance="subtle"
                  size="small"
                  className={s.sidebarIconButton}
                  onClick={props.onRefresh}
                  aria-label="Refresh"
                >
                  ↻
                </Button>
                {props.onToggleSidebarPin && (
                  <Tooltip content={props.sidebarPinned ? "Unpin sidebar" : "Pin sidebar open"} relationship="label">
                    <Button
                      appearance="subtle"
                      size="small"
                      className={s.sidebarIconButton}
                      onClick={props.onToggleSidebarPin}
                      aria-label={props.sidebarPinned ? "Unpin sidebar" : "Pin sidebar"}
                      style={{ fontSize: "13px", transform: props.sidebarPinned ? "rotate(0deg)" : "rotate(45deg)" }}
                    >
                      📌
                    </Button>
                  </Tooltip>
                )}
              </>
            )}
            <Tooltip content={props.sidebarOpen ? "Collapse sidebar" : "Expand sidebar"} relationship="label">
              <Button
                appearance="subtle"
                size="small"
                className={s.sidebarIconButton}
                onClick={props.onToggleSidebar}
                aria-label={props.sidebarOpen ? "Collapse sidebar" : "Expand sidebar"}
              >
                {props.sidebarOpen ? "«" : "»"}
              </Button>
            </Tooltip>
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

          {/* View Navigation (grouped, collapsible) */}
          <div className={s.navSection}>
            {NAV_GROUPS.map((group) => {
              const isCollapsed = collapsedGroups.has(group.id);
              const groupContainsActive = group.items.some((i) => i.value === props.activeView);
              return (
                <div key={group.id} className={s.navGroup}>
                  <button
                    type="button"
                    className={s.navGroupHeader}
                    onClick={() => toggleGroup(group.id)}
                    aria-expanded={!isCollapsed}
                    aria-controls={isCollapsed ? undefined : `nav-group-${group.id}`}
                  >
                    <span className={s.navGroupChevron} aria-hidden="true">
                      {isCollapsed ? "▸" : "▾"}
                    </span>
                    <span className={s.navGroupLabel}>{group.label}</span>
                    {isCollapsed && groupContainsActive && (
                      <span className={s.navGroupActiveDot} aria-hidden="true" />
                    )}
                  </button>
                  <div id={`nav-group-${group.id}`} hidden={isCollapsed}>
                    {group.items.map((item) => (
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
                </div>
              );
            })}
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
            <div className={s.roomListScrollable}>
              {props.rooms.map((candidate) => {
                const dotColor = phaseDotColor(candidate.currentPhase);
                const roomAgents = agentsByRoom.get(candidate.id) ?? [];
                return (
                  <div key={candidate.id}>
                    <button
                      className={mergeClasses(s.roomButton, s.roomButtonHover, props.room?.id === candidate.id ? s.roomButtonActive : undefined)}
                      onClick={() => props.onSelectRoom(candidate.id)}
                      aria-label={`Select room ${candidate.name}`}
                      type="button"
                    >
                      <span className={s.roomPhaseDot} style={{ backgroundColor: dotColor }} />
                      <span className={s.roomButtonName}>{candidate.name}</span>
                      <span className={s.roomButtonCount}>{candidate.participants.length}</span>
                    </button>
                    {roomAgents.length > 0 && (
                      <>
                        <div className={s.roomAgentListSeparator} role="separator" />
                        <div className={s.roomAgentList} aria-label={`Agents in ${candidate.name}`}>
                          {roomAgents.map(renderAgentButton)}
                        </div>
                      </>
                    )}
                  </div>
                );
              })}
            </div>
          </section>

          {/* Agents (unassigned — agents in rooms render under their room above) */}
          {props.configuredAgents.length > 0 && unassignedAgents.length > 0 && (
            <section className={s.section}>
              <div className={s.sectionHeader}>
                <div className={s.sectionLabel}>Agents</div>
                <div className={s.sectionCount}>{unassignedAgents.length}</div>
              </div>
              <div className={s.roomList}>
                {unassignedAgents.map(renderAgentButton)}
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
          {/* Nav icons in collapsed mode (flattened across all groups) */}
          {NAV_GROUPS.flatMap((g) => g.items).map((item) => (
            <Tooltip key={item.value} content={item.label} relationship="label" positioning="after">
              <button
                className={mergeClasses(s.compactButton, props.activeView === item.value ? s.compactButtonActive : undefined)}
                onClick={() => { props.onViewChange(item.value); props.onToggleSidebar(); }}
                aria-label={item.label}
                type="button"
              >
                {item.icon}
              </button>
            </Tooltip>
          ))}

          {/* Divider */}
          <div style={{ borderTop: "1px solid var(--aa-border)", margin: "4px 6px" }} />

          {/* Room dots */}
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

      {/* Drag-to-resize handle (only when expanded). Persisted via localStorage. */}
      {props.sidebarOpen && (
        <div
          className={mergeClasses(s.sidebarResizeHandle, resizing ? s.sidebarResizeHandleActive : undefined)}
          onMouseDown={handleResizeMouseDown}
          onDoubleClick={handleResizeDoubleClick}
          onKeyDown={handleResizeKeyDown}
          role="separator"
          aria-label="Resize sidebar"
          aria-orientation="vertical"
          aria-valuenow={sidebarWidth}
          aria-valuemin={SIDEBAR_WIDTH_MIN}
          aria-valuemax={SIDEBAR_WIDTH_MAX}
          tabIndex={0}
          title="Drag to resize · double-click to reset"
        />
      )}
    </aside>
  );
});

export default SidebarPanel;
