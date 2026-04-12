import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Button,
  FluentProvider,
  webDarkTheme,
  mergeClasses,
  Menu,
  MenuTrigger,
  MenuPopover,
  MenuList,
  MenuItemCheckbox,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Toaster,
  useToastController,
  useId,
  Toast,
  ToastTitle,
  ToastBody,
} from "@fluentui/react-components";
import V3Badge from "./V3Badge";
import type { Theme, MenuCheckedValueChangeData } from "@fluentui/react-components";
import { useStyles } from "./useStyles";
import { useWorkspace } from "./useWorkspace";
import { useDesktopNotifications } from "./useDesktopNotifications";
import { apiBaseUrl, getActiveWorkspace, switchWorkspace, getTasks, getActiveSprint, getAuthStatus, logout, createRoom, createRoomSession, addAgentToRoom, removeAgentFromRoom } from "./api";
import type { OnboardResult, WorkspaceMeta, TaskSnapshot, AuthStatus, ActivityEvent, ActivityEventType, CollaborationPhase } from "./api";
import SidebarPanel from "./SidebarPanel";
import ChatPanel from "./ChatPanel";
import { loadFilters, saveFilters } from "./chatUtils";
import type { MessageFilter } from "./chatUtils";
import RecoveryBanner from "./RecoveryBanner";
import CircuitBreakerBanner from "./CircuitBreakerBanner";
import ConfirmDialog from "./ConfirmDialog";
import ChunkErrorBoundary from "./ChunkErrorBoundary";

// Lazy-loaded panels — each becomes its own chunk
const ProjectSelectorPage = lazy(() => import("./ProjectSelectorPage"));
const PlanPanel = lazy(() => import("./PlanPanel"));
const TimelinePanel = lazy(() => import("./TimelinePanel"));
const DashboardPanel = lazy(() => import("./DashboardPanel"));
const WorkspaceOverviewPanel = lazy(() => import("./WorkspaceOverviewPanel"));
const TaskListPanel = lazy(() => import("./TaskListPanel"));
const LoginPage = lazy(() => import("./LoginPage"));
const SettingsPanel = lazy(() => import("./SettingsPanel"));
const DmPanel = lazy(() => import("./DmPanel"));
const AgentSessionPanel = lazy(() => import("./AgentSessionPanel"));
const CommandsPanel = lazy(() => import("./CommandsPanel"));
const SprintPanel = lazy(() => import("./SprintPanel"));
const CommandPalette = lazy(() => import("./CommandPalette"));
const SearchPanel = lazy(() => import("./SearchPanel"));
const KeyboardShortcutsDialog = lazy(() => import("./KeyboardShortcutsDialog"));
import { useCircuitBreakerPolling } from "./useCircuitBreakerPolling";
import {
  getCopilotStatusCopy,
  hasDisplayUser,
  isWorkspaceLimited,
  shouldRenderWorkspace,
} from "./authPresentation";
import {
  AUTH_STATUS_POLL_MS,
  clearAutoReauthAttempt,
  clearManualLogout,
  getAuthTransitionEffect,
  markAutoReauthAttempt,
  markManualLogout,
  shouldAttemptAutoReauth,
} from "./authMonitor";

/** View title lookup for header bar. */
const VIEW_TITLES: Record<string, { title: string; meta: string }> = {
  chat: { title: "Conversation", meta: "Live room stream" },
  tasks: { title: "Tasks", meta: "Delivery queue" },
  plan: { title: "Room Plan", meta: "" },
  commands: { title: "Command Deck", meta: "" },
  sprint: { title: "Sprint", meta: "Active iteration" },
  timeline: { title: "Activity Timeline", meta: "" },
  dashboard: { title: "Metrics", meta: "System telemetry" },
  overview: { title: "Overview", meta: "Room state" },
  directMessages: { title: "Direct Messages", meta: "" },
  search: { title: "Search", meta: "Find messages & tasks" },
};

const TOAST_EVENT_TYPES: ReadonlySet<ActivityEventType> = new Set([
  "AgentErrorOccurred",
  "AgentWarningOccurred",
  "SubagentFailed",
  "AgentFinished",
  "TaskCreated",
  "PhaseChanged",
  "SubagentCompleted",
]);

function toastIntent(evt: ActivityEvent): "error" | "warning" | "info" {
  if (evt.severity === "Error" || evt.type === "AgentErrorOccurred" || evt.type === "SubagentFailed") return "error";
  if (evt.severity === "Warning" || evt.type === "AgentWarningOccurred") return "warning";
  return "info";
}

/** Override Fluent UI's 14px/20px defaults to match v3 mockup's 13px/1.5 base. */
const matrixTheme: Theme = {
  ...webDarkTheme,
  fontSizeBase200: "11px",
  fontSizeBase300: "13px",
  fontSizeBase400: "14px",
  fontSizeBase500: "16px",
  lineHeightBase200: "16px",
  lineHeightBase300: "18px",
  lineHeightBase400: "20px",
  lineHeightBase500: "22px",
};

export default function App() {
  return (
    <FluentProvider theme={matrixTheme}>
      <AppShell />
    </FluentProvider>
  );
}

function AppShell() {
  const s = useStyles();

  const toasterId = useId("workspace-toaster");
  const { dispatchToast } = useToastController(toasterId);
  const tabRef = useRef("chat");

  /* Chat filter state (lifted here so it can render in the toolbar) */
  const [hiddenFilters, setHiddenFilters] = useState<Set<MessageFilter>>(loadFilters);
  const chatFilterChecked = useMemo(() => {
    const visible: string[] = [];
    if (!hiddenFilters.has("system")) visible.push("system");
    if (!hiddenFilters.has("commands")) visible.push("commands");
    return { show: visible };
  }, [hiddenFilters]);
  const onChatFilterChange = useCallback((_: unknown, data: MenuCheckedValueChangeData) => {
    const nowVisible = new Set(data.checkedItems);
    const next = new Set<MessageFilter>();
    if (!nowVisible.has("system")) next.add("system");
    if (!nowVisible.has("commands")) next.add("commands");
    setHiddenFilters(next);
    saveFilters(next);
  }, []);

  const desktopNotif = useDesktopNotifications();

  const handleActivityToast = useCallback((evt: ActivityEvent) => {
    // Desktop notification (fires only when tab is hidden + user opted in)
    desktopNotif.notify(evt);

    if (!TOAST_EVENT_TYPES.has(evt.type)) return;

    const intent = toastIntent(evt);
    const timeout = intent === "error" ? 8000 : 4000;
    dispatchToast(
      <Toast>
        <ToastTitle>{evt.type.replace(/([A-Z])/g, " $1").trim()}</ToastTitle>
        <ToastBody>{evt.message}</ToastBody>
      </Toast>,
      { intent, timeout },
    );
  }, [dispatchToast, desktopNotif]);

  const {
    ov,
    room,
    activity,
    thinkingAgentList,
    thinkingByRoom,
    recoveryBanner,
    connectionStatus,
    breakoutRooms,
    sprintVersion,
    lastSprintEvent,
    err,
    busy,
    tab,
    setTab,
    sidebarOpen,
    handleRoomSelect,
    handleManualRefresh,
    handleToggleSidebar,
    handleSendMessage,
    handlePhaseTransition,
  } = useWorkspace({ onActivityEvent: handleActivityToast });

  // Keep tabRef in sync and clear unseen counter when switching to timeline
  useEffect(() => {
    tabRef.current = tab;
  }, [tab]);

  const { circuitBreakerState } = useCircuitBreakerPolling();

  // Per-room thinking sets for sidebar; per-active-room set for other uses
  const thinkingByRoomIds = useMemo(() => {
    const result = new Map<string, Set<string>>();
    for (const [rid, agentMap] of thinkingByRoom) {
      result.set(rid, new Set(agentMap.keys()));
    }
    return result;
  }, [thinkingByRoom]);

  const [workspace, setWorkspace] = useState<WorkspaceMeta | null>(null);
  const [showProjectSelector, setShowProjectSelector] = useState(false);
  const [phaseTransitioning, setPhaseTransitioning] = useState(false);
  const [loading, setLoading] = useState(true);
  const [switching, setSwitching] = useState(false);
  const [switchError, setSwitchError] = useState("");
  const [allTasks, setAllTasks] = useState<TaskSnapshot[]>([]);
  const [tasksError, setTasksError] = useState(false);
  const [tasksLoading, setTasksLoading] = useState(false);
  const [tasksFetchKey, setTasksFetchKey] = useState(0);
  const [activeSprintId, setActiveSprintId] = useState<string | null>(null);
  const [auth, setAuth] = useState<AuthStatus | null>(null);
  const [selectedWorkspaceId, setSelectedWorkspaceId] = useState<string | null>(null);
  const [showSettings, setShowSettings] = useState(false);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [shortcutsOpen, setShortcutsOpen] = useState(false);
  const [logoutConfirmOpen, setLogoutConfirmOpen] = useState(false);
  const previousAuthRef = useRef<AuthStatus | null>(null);
  const authRefreshInFlight = useRef(false);
  const loginUrl = `${apiBaseUrl}/api/auth/login`;

  const refreshAuthStatus = useCallback(async () => {
    if (authRefreshInFlight.current) return;

    authRefreshInFlight.current = true;
    try {
      const status = await getAuthStatus();
      setAuth(status);
    } finally {
      authRefreshInFlight.current = false;
    }
  }, []);

  // Check auth status on mount — retry if backend is still starting
  useEffect(() => {
    let cancelled = false;
    let retryTimer: ReturnType<typeof setTimeout> | null = null;
    async function checkAuth(attempt: number) {
      if (cancelled) return;
      try {
        const status = await getAuthStatus();
        if (!cancelled) setAuth(status);
      } catch {
        if (cancelled) return;
        if (attempt < 3) {
          retryTimer = setTimeout(() => void checkAuth(attempt + 1), 2000);
        } else {
          setAuth({
            authEnabled: false,
            authenticated: false,
            copilotStatus: "unavailable",
          });
        }
      }
    }
    void checkAuth(0);
    return () => { cancelled = true; if (retryTimer) clearTimeout(retryTimer); };
  }, []);

  useEffect(() => {
    if (auth === null) return undefined;

    const timer = window.setInterval(() => {
      void refreshAuthStatus().catch(() => undefined);
    }, AUTH_STATUS_POLL_MS);

    return () => window.clearInterval(timer);
  }, [auth, refreshAuthStatus]);

  useEffect(() => {
    if (!auth) return;

    const transitionEffect = getAuthTransitionEffect(previousAuthRef.current, auth);

    if (transitionEffect.clearAutoReauthAttempt) {
      clearAutoReauthAttempt();
    }

    if (transitionEffect.clearManualLogoutSuppression) {
      clearManualLogout();
    }

    if (transitionEffect.redirectToLogin) {
      markAutoReauthAttempt();
      window.location.assign(loginUrl);
    } else if (
      shouldAttemptAutoReauth(previousAuthRef.current, auth)
      && !transitionEffect.redirectToLogin
    ) {
      // Auto-reauth was suppressed (already attempted or manual logout) — warn the user
      dispatchToast(
        <Toast>
          <ToastTitle>Session needs reconnection</ToastTitle>
        </Toast>,
        { intent: "warning", timeout: 5000 },
      );
    }

    previousAuthRef.current = auth;
  }, [auth, dispatchToast, loginUrl]);

  // Fetch tasks when workspace is active and tab is "tasks"
  useEffect(() => {
    if (showProjectSelector || tab !== "tasks") return;
    let cancelled = false;
    setTasksError(false);
    setTasksLoading(true);
    getTasks()
      .then((tasks) => { if (!cancelled) setAllTasks(tasks); })
      .catch(() => { if (!cancelled) { setAllTasks([]); setTasksError(true); } })
      .finally(() => { if (!cancelled) setTasksLoading(false); });
    getActiveSprint()
      .then((detail) => { if (!cancelled) setActiveSprintId(detail?.sprint.id ?? null); })
      .catch(() => { if (!cancelled) setActiveSprintId(null); });
    return () => { cancelled = true; };
  }, [showProjectSelector, tab, tasksFetchKey]);

  // On mount, check for active workspace — retry on failure (backend may still be starting)
  useEffect(() => {
    let cancelled = false;
    let retryTimer: ReturnType<typeof setTimeout> | null = null;
    async function checkWorkspace(attempt: number) {
      if (cancelled) return;
      try {
        const data = await getActiveWorkspace();
        if (cancelled) return;
        if (data.active) {
          setWorkspace(data.active);
        } else {
          setShowProjectSelector(true);
        }
        setLoading(false);
      } catch {
        if (cancelled) return;
        if (attempt < 3) {
          retryTimer = setTimeout(() => void checkWorkspace(attempt + 1), 2000);
        } else {
          setShowProjectSelector(true);
          setLoading(false);
        }
      }
    }
    void checkWorkspace(0);
    return () => {
      cancelled = true;
      if (retryTimer) clearTimeout(retryTimer);
    };
  }, []);

  // Cmd+K / Ctrl+K to open command palette (skip when focus is in an input)
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const tag = (e.target as HTMLElement)?.tagName;
      const editable = (e.target as HTMLElement)?.isContentEditable;
      const inInput = tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || editable;
      if ((e.metaKey || e.ctrlKey) && e.key === "k") {
        if (inInput) return;
        e.preventDefault();
        setPaletteOpen((prev) => !prev);
      }
      // "/" to open search (skip when focus is in an input)
      if (e.key === "/" && !e.metaKey && !e.ctrlKey && !e.altKey && !inInput) {
        e.preventDefault();
        setTab("search");
      }
      // "?" to open keyboard shortcuts help (skip when focus is in an input)
      if (e.key === "?" && !e.metaKey && !e.ctrlKey && !e.altKey && !inInput) {
        e.preventDefault();
        setShortcutsOpen((prev) => !prev);
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, []);

  const handleProjectSelected = useCallback(
    async (workspacePath: string) => {
      if (switching) return;
      setSwitching(true);
      setSwitchError("");
      try {
        const ws = await switchWorkspace(workspacePath);
        setWorkspace(ws);
        setShowProjectSelector(false);
        handleManualRefresh();
        dispatchToast(
          <Toast>
            <ToastTitle>Switched to {ws.projectName ?? ws.path}</ToastTitle>
          </Toast>,
          { intent: "success", timeout: 2000 },
        );
      } catch (e) {
        setSwitchError(e instanceof Error ? e.message : "Failed to switch workspace");
      } finally {
        setSwitching(false);
      }
    },
    [dispatchToast, handleManualRefresh, switching],
  );

  const handleProjectOnboarded = useCallback(
    (result: OnboardResult) => {
      setWorkspace(result.workspace);
      setShowProjectSelector(false);
      handleManualRefresh();
    },
    [handleManualRefresh],
  );

  const wrappedPhaseTransition = useCallback(
    async (phase: Parameters<typeof handlePhaseTransition>[0]) => {
      setPhaseTransitioning(true);
      try { await handlePhaseTransition(phase); }
      finally { setPhaseTransitioning(false); }
    },
    [handlePhaseTransition],
  );

  const wrappedRoomSelect = useCallback(
    (id: string) => {
      setSelectedWorkspaceId(null);
      handleRoomSelect(id);
      setTab("chat");
    },
    [handleRoomSelect],
  );

  const handleCreateRoom = useCallback(
    async (name: string) => {
      try {
        const room = await createRoom(name);
        handleRoomSelect(room.id);
        setSelectedWorkspaceId(null);
        setTab("chat");
        handleManualRefresh();
      } catch (err) {
        console.error("Failed to create room:", err);
      }
    },
    [handleRoomSelect, handleManualRefresh, setTab],
  );

  const handleWorkspaceSelect = useCallback(
    (breakoutId: string) => {
      setSelectedWorkspaceId(breakoutId);
    },
    [],
  );

  const handleCreateSession = useCallback(
    async (roomId: string) => {
      try {
        await createRoomSession(roomId);
        handleManualRefresh();
      } catch (err) {
        console.error("Failed to create session:", err);
      }
    },
    [handleManualRefresh],
  );

  const handleToggleAgent = useCallback(
    async (roomId: string, agentId: string, currentlyInRoom: boolean) => {
      try {
        if (currentlyInRoom) {
          await removeAgentFromRoom(roomId, agentId);
        } else {
          await addAgentToRoom(roomId, agentId);
        }
        handleManualRefresh();
      } catch (err) {
        console.error("Failed to toggle agent:", err);
      }
    },
    [handleManualRefresh],
  );

  const doLogout = useCallback(async () => {
    markManualLogout();
    clearAutoReauthAttempt();

    try {
      await logout();
      setAuth({
        authEnabled: true,
        authenticated: false,
        copilotStatus: "unavailable",
        user: null,
      });
    } catch {
      clearManualLogout();
    }
  }, []);

  const handleLogout = useCallback(() => {
    setLogoutConfirmOpen(true);
  }, []);

  const confirmLogout = useCallback(() => {
    setLogoutConfirmOpen(false);
    void doLogout();
  }, [doLogout]);

  useEffect(() => {
    const roomName = room?.name ?? "Agent Academy";
    const phase = room?.currentPhase ?? "";
    document.title = phase
      ? `${roomName} · ${phase} | Agent Academy`
      : `${roomName} | Agent Academy`;
  }, [room?.name, room?.currentPhase]);

  // Wait for auth check before rendering anything
  if (auth === null || loading) {
    return <div className={s.root} />;
  }

  // Unavailable auth fail-closes to LoginPage; degraded stays visible in limited mode.
  if (auth.authEnabled && !shouldRenderWorkspace(auth)) {
    return <ChunkErrorBoundary><Suspense fallback={<div className={s.root} />}><LoginPage copilotStatus={auth.copilotStatus} user={auth.user ?? null} /></Suspense></ChunkErrorBoundary>;
  }

  const workspaceLimited = isWorkspaceLimited(auth);
  const degradedCopy = workspaceLimited ? getCopilotStatusCopy("degraded", auth.user ?? null) : null;
  const connectionDetail = connectionStatus === "disconnected"
    ? "Live updates paused. Previously loaded data is still visible."
    : null;
  const viewInfo = VIEW_TITLES[tab] ?? { title: tab, meta: "" };
  const isAgentView = selectedWorkspaceId?.startsWith("agent:") ?? false;
  const selectedAgentId = isAgentView ? selectedWorkspaceId!.slice("agent:".length) : null;
  const sessionAgent = selectedAgentId
    ? ov.configuredAgents.find((agent) => agent.id === selectedAgentId)
    : null;
  const sessionAgentLocation = selectedAgentId
    ? (ov.agentLocations ?? []).find((location) => location.agentId === selectedAgentId)
    : undefined;
  const selectedBreakout = selectedWorkspaceId && !isAgentView
    ? breakoutRooms.find((breakout) => breakout.id === selectedWorkspaceId)
    : null;
  const selectedAgent = selectedBreakout
    ? ov.configuredAgents.find((agent) => agent.id === selectedBreakout.assignedAgentId)
    : null;

  return (
    <div className={s.root}>
      {(err || switchError) && (
        <div className={s.errorBar}>
          <MessageBar intent="error">
            <MessageBarBody>
              <MessageBarTitle>Error</MessageBarTitle>
              {err || switchError}
            </MessageBarBody>
          </MessageBar>
        </div>
      )}

      {recoveryBanner && (
        <div className={s.recoveryBannerGlobal}>
          <RecoveryBanner state={recoveryBanner} />
        </div>
      )}

      <CircuitBreakerBanner state={circuitBreakerState} />

      {connectionDetail && (
        <div className={s.errorBar}>
          <MessageBar intent="warning">
            <MessageBarBody>
              <MessageBarTitle>Offline</MessageBarTitle>
              {connectionDetail}
            </MessageBarBody>
          </MessageBar>
        </div>
      )}

      {showProjectSelector ? (
        <ChunkErrorBoundary>
        <Suspense fallback={<div className={s.root}><Spinner label="Loading…" /></div>}>
        <ProjectSelectorPage
          onProjectSelected={handleProjectSelected}
          onProjectOnboarded={handleProjectOnboarded}
          user={auth.user ?? null}
          onLogout={handleLogout}
        />
        </Suspense>
        </ChunkErrorBoundary>
      ) : (
        <div className={mergeClasses(s.shell, sidebarOpen ? s.shellOpen : s.shellCollapsed)}>
          <SidebarPanel
            sidebarOpen={sidebarOpen}
            busy={busy}
            rooms={ov.rooms}
            room={selectedWorkspaceId ? null : room}
            agentLocations={ov.agentLocations ?? []}
            configuredAgents={ov.configuredAgents}
            breakoutRooms={breakoutRooms}
            selectedWorkspaceId={selectedWorkspaceId}
            thinkingByRoomIds={thinkingByRoomIds}
            onRefresh={handleManualRefresh}
            onToggleSidebar={handleToggleSidebar}
            onSelectRoom={wrappedRoomSelect}
            onSelectWorkspace={handleWorkspaceSelect}
            onCreateRoom={handleCreateRoom}
            activeView={tab}
            onViewChange={setTab}
            workspace={
              workspace
                ? { name: workspace.projectName ?? workspace.path, path: workspace.path }
                : null
            }
            onSwitchProject={() => setShowProjectSelector(true)}
            user={hasDisplayUser(auth.user) && auth.user ? auth.user : null}
            onLogout={handleLogout}
            onOpenSettings={() => setShowSettings(true)}
            sprintVersion={sprintVersion}
          />

          <main className={s.workspace} aria-label="Workspace content">
            <>
              {/* ─ Header bar (40px) ─ */}
              <div className={s.workspaceHeader}>
                <div className={s.workspaceHeaderBody}>
                  <div className={s.workspaceHeaderTopRow}>
                    <div className={s.workspaceTitle}>
                      {sessionAgent
                        ? `${sessionAgent.name}'s Sessions`
                        : selectedBreakout
                          ? `${selectedAgent?.name ?? "Agent"}'s Workspace`
                          : tab === "chat"
                            ? room?.name ?? "No active room"
                            : viewInfo.title}
                    </div>
                    {tab === "chat" && room && !sessionAgent && !selectedBreakout && (<>
                      <span className={s.headerDivider} />
                      <span className={s.workspaceMetaText}>
                        {room.participants.length} agents · {room.currentPhase}
                      </span>
                    </>)}
                    {tab !== "chat" && viewInfo.meta && (<>
                      <span className={s.headerDivider} />
                      <span className={s.workspaceMetaText}>{viewInfo.meta}</span>
                    </>)}
                    <div style={{ flex: 1 }} />
                    <div className={s.workspaceHeaderSignals}>
                      {workspaceLimited && (
                        <div className={mergeClasses(s.workspaceSignal, s.workspaceSignalWarning)}>
                          {degradedCopy?.eyebrow ?? "Limited mode"}
                        </div>
                      )}
                      {circuitBreakerState && circuitBreakerState !== "Closed" && (
                        <div className={mergeClasses(s.workspaceSignal, s.workspaceSignalWarning)}>
                          Circuit {circuitBreakerState === "Open" ? "open" : "probing"}
                        </div>
                      )}
                      {room && !sessionAgent && !selectedBreakout && (
                        <div className={s.phasePill}>
                          <span className={s.phasePillDot} />
                          Connected
                        </div>
                      )}
                    </div>
                  </div>
                </div>
              </div>

              {workspaceLimited && degradedCopy && (
                <div className={s.limitedModeBanner} role="alert">
                  <div className={s.limitedModeBadge}>{degradedCopy.eyebrow}</div>
                  <div className={s.limitedModeTitle}>{degradedCopy.title}</div>
                  <div className={s.limitedModeDescription}>
                    {degradedCopy.description}
                  </div>
                  <Button
                    appearance="primary"
                    size="small"
                    onClick={() => window.location.assign(loginUrl)}
                    style={{ marginTop: "8px", alignSelf: "flex-start" }}
                  >
                    {degradedCopy.actionLabel}
                  </Button>
                </div>
              )}

              {/* ─ Contextual Toolbar (34px) ─ */}
              {!sessionAgent && !selectedBreakout && (
                <div className={s.tabBar}>
                  <div className={s.tabStrip}>
                    {tab === "chat" && room && (<>
                      <select
                        className={s.toolbarSelect}
                        value={room.currentPhase}
                        onChange={(e) => void wrappedPhaseTransition(e.target.value as CollaborationPhase)}
                        disabled={workspaceLimited}
                        title="Change room phase"
                      >
                        <option value="Intake">Intake</option>
                        <option value="Planning">Planning</option>
                        <option value="Discussion">Discussion</option>
                        <option value="Implementation">Implementation</option>
                        <option value="Validation">Validation</option>
                        <option value="FinalSynthesis">Final Synthesis</option>
                      </select>
                      <Menu checkedValues={chatFilterChecked} onCheckedValueChange={onChatFilterChange}>
                        <MenuTrigger disableButtonEnhancement>
                          <Button size="small" appearance="subtle" className={s.filterMenuButton}>
                            ▾ Filter
                            {hiddenFilters.size > 0 && (
                              <V3Badge color="info" className={s.filterBadge}>
                                {hiddenFilters.size}
                              </V3Badge>
                            )}
                          </Button>
                        </MenuTrigger>
                        <MenuPopover>
                          <MenuList>
                            <MenuItemCheckbox name="show" value="system">System messages</MenuItemCheckbox>
                            <MenuItemCheckbox name="show" value="commands">Command results</MenuItemCheckbox>
                          </MenuList>
                        </MenuPopover>
                      </Menu>
                    </>)}
                    {tab === "tasks" && (
                      <span className={s.workspaceMetaText}>Sorted by newest</span>
                    )}
                    {tab === "commands" && (
                      <span className={s.workspaceMetaText}>Command Deck</span>
                    )}
                    {tab === "sprint" && (
                      <span className={s.workspaceMetaText}>Active iteration</span>
                    )}
                    {tab === "timeline" && (
                      <span className={s.workspaceMetaText}>All events</span>
                    )}
                    {tab === "dashboard" && (
                      <span className={s.workspaceMetaText}>System telemetry</span>
                    )}
                  </div>
                </div>
              )}

              {/* ─ Content ─ */}
              <ChunkErrorBoundary>
              <Suspense fallback={<section className={s.tabContent}><Spinner label="Loading…" /></section>}>
              {sessionAgent ? (
                <section className={s.tabContent}>
                  <AgentSessionPanel
                    agent={sessionAgent}
                    location={sessionAgentLocation}
                    thinkingAgents={thinkingAgentList}
                    connectionStatus={connectionStatus}
                    onSendMessage={handleSendMessage}
                  />
                </section>
              ) : selectedBreakout ? (
                <section className={s.tabContent}>
                  <ChatPanel
                    room={{
                      id: selectedBreakout.id,
                      name: selectedBreakout.name,
                      status: selectedBreakout.status,
                      currentPhase: "Implementation",
                      activeTask: null,
                      participants: [],
                      recentMessages: selectedBreakout.recentMessages,
                      createdAt: selectedBreakout.createdAt,
                      updatedAt: selectedBreakout.updatedAt,
                    }}
                    thinkingAgents={thinkingAgentList}
                    connectionStatus={connectionStatus}
                    onSendMessage={handleSendMessage}
                    readOnly
                  />
                </section>
              ) : (
                <section className={s.tabContent}>
                  {tab === "chat" && (
                    <ChatPanel
                      room={room}
                      loading={busy}
                      thinkingAgents={thinkingAgentList}
                      connectionStatus={connectionStatus}
                      onSendMessage={handleSendMessage}
                      readOnly={workspaceLimited}
                      hiddenFilters={hiddenFilters}
                      agentLocations={ov.agentLocations ?? []}
                      configuredAgents={ov.configuredAgents}
                      onCreateSession={handleCreateSession}
                      onToggleAgent={handleToggleAgent}
                    />
                  )}
                  {tab === "tasks" && (
                    <TaskListPanel
                      tasks={allTasks}
                      loading={tasksLoading}
                      error={tasksError}
                      onRefresh={() => setTasksFetchKey((k) => k + 1)}
                      activeSprintId={activeSprintId}
                      agents={ov.configuredAgents}
                    />
                  )}
                  {tab === "plan" && (
                    <PlanPanel key={room?.id ?? "no-room"} roomId={room?.id ?? null} />
                  )}
                  {tab === "commands" && (
                    <CommandsPanel roomId={room?.id ?? null} readOnly={workspaceLimited} />
                  )}
                  {tab === "sprint" && <SprintPanel sprintVersion={sprintVersion} lastSprintEvent={lastSprintEvent} />}
                  {tab === "timeline" && (
                    <TimelinePanel activity={activity} loading={busy} />
                  )}
                  {tab === "dashboard" && (
                    <DashboardPanel overview={ov} circuitBreakerState={circuitBreakerState} />
                  )}
                  {tab === "overview" && (
                    <WorkspaceOverviewPanel
                      overview={ov}
                      room={room}
                      onPhaseTransition={wrappedPhaseTransition}
                      transitioning={phaseTransitioning}
                      readOnly={workspaceLimited}
                    />
                  )}
                  {tab === "directMessages" && (
                    <DmPanel
                      agents={ov.configuredAgents.map((a) => ({
                        id: a.id,
                        name: a.name,
                        role: a.role,
                      }))}
                      readOnly={workspaceLimited}
                    />
                  )}
                  {tab === "search" && (
                    <SearchPanel
                      onNavigateToRoom={(roomId) => { handleRoomSelect(roomId); setTab("chat"); }}
                      onNavigateToTasks={() => setTab("tasks")}
                    />
                  )}
                </section>
              )}
              </Suspense>
              </ChunkErrorBoundary>
            </>
          </main>
        </div>
      )}
      {showSettings && (
        <ChunkErrorBoundary>
          <Suspense fallback={null}>
            <SettingsPanel onClose={() => setShowSettings(false)} desktopNotifications={desktopNotif} />
          </Suspense>
        </ChunkErrorBoundary>
      )}
      <ChunkErrorBoundary>
        <Suspense fallback={null}>
          <CommandPalette
            open={paletteOpen}
            onDismiss={() => setPaletteOpen(false)}
            roomId={room?.id ?? null}
            readOnly={workspaceLimited}
          />
        </Suspense>
      </ChunkErrorBoundary>
      <ChunkErrorBoundary>
        <Suspense fallback={null}>
          <KeyboardShortcutsDialog
            open={shortcutsOpen}
            onClose={() => setShortcutsOpen(false)}
          />
        </Suspense>
      </ChunkErrorBoundary>
      <ConfirmDialog
        open={logoutConfirmOpen}
        onConfirm={confirmLogout}
        onCancel={() => setLogoutConfirmOpen(false)}
        title="Sign out?"
        message="You'll lose live updates and need to sign in again to resume. Any unsaved draft messages are preserved locally."
        confirmLabel="Sign out"
      />
      <Toaster toasterId={toasterId} position="bottom-end" />
    </div>
  );
}
