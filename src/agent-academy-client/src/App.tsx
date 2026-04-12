import { lazy, Suspense, useCallback, useEffect, useMemo, useState } from "react";
import {
  Button,
  FluentProvider,
  webDarkTheme,
  mergeClasses,
  Spinner,
  Toaster,
  useToastController,
  useId,
  Toast,
  ToastTitle,
  ToastBody,
} from "@fluentui/react-components";
import type { Theme, MenuCheckedValueChangeData } from "@fluentui/react-components";
import { useLayoutStyles, useWorkspaceStyles, useRecoveryStyles } from "./styles";
import { useWorkspace } from "./useWorkspace";
import { useDesktopNotifications } from "./useDesktopNotifications";
import { getActiveWorkspace, switchWorkspace, createRoom, createRoomSession, addAgentToRoom, removeAgentFromRoom } from "./api";
import type { OnboardResult, WorkspaceMeta, ActivityEvent, ActivityEventType, CollaborationPhase } from "./api";
import SidebarPanel from "./SidebarPanel";
import ChatPanel from "./ChatPanel";
import { loadFilters, saveFilters } from "./chatUtils";
import type { MessageFilter } from "./chatUtils";
import ConfirmDialog from "./ConfirmDialog";
import ChunkErrorBoundary from "./ChunkErrorBoundary";
import StatusBanners from "./StatusBanners";
import WorkspaceHeader from "./WorkspaceHeader";
import type { HeaderModel } from "./WorkspaceHeader";
import WorkspaceToolbar from "./WorkspaceToolbar";
import type { ToolbarModel } from "./WorkspaceToolbar";
import WorkspaceContent from "./WorkspaceContent";
import { useAuth } from "./useAuth";
import { useKeyboardShortcuts } from "./useKeyboardShortcuts";
import { useTaskData } from "./useTaskData";
import { useCircuitBreakerPolling } from "./useCircuitBreakerPolling";
import {
  getCopilotStatusCopy,
  hasDisplayUser,
  isWorkspaceLimited,
  shouldRenderWorkspace,
} from "./authPresentation";

// Lazy-loaded panels
const ProjectSelectorPage = lazy(() => import("./ProjectSelectorPage"));
const LoginPage = lazy(() => import("./LoginPage"));
const SettingsPanel = lazy(() => import("./SettingsPanel"));
const AgentSessionPanel = lazy(() => import("./AgentSessionPanel"));
const CommandPalette = lazy(() => import("./CommandPalette"));
const KeyboardShortcutsDialog = lazy(() => import("./KeyboardShortcutsDialog"));

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
  const s = { ...useLayoutStyles(), ...useWorkspaceStyles(), ...useRecoveryStyles() };

  const toasterId = useId("workspace-toaster");
  const { dispatchToast } = useToastController(toasterId);

  /* ── Auth ── */
  const { auth, loginUrl, logoutDialog } = useAuth({
    onSessionWarning: useCallback(() => {
      dispatchToast(
        <Toast>
          <ToastTitle>Session needs reconnection</ToastTitle>
        </Toast>,
        { intent: "warning", timeout: 5000 },
      );
    }, [dispatchToast]),
  });

  /* ── Chat filter state (lifted here so toolbar + content can share) ── */
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

  /* ── Desktop notifications ── */
  const desktopNotif = useDesktopNotifications();

  /* ── Activity toast handler ── */
  const handleActivityToast = useCallback((evt: ActivityEvent) => {
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

  /* ── Workspace core ── */
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

  const { circuitBreakerState } = useCircuitBreakerPolling();

  /* ── Task data ── */
  const [showProjectSelector, setShowProjectSelector] = useState(false);
  const { allTasks, tasksLoading, tasksError, activeSprintId, refreshTasks } = useTaskData({
    enabled: !showProjectSelector && tab === "tasks",
  });

  /* ── Keyboard shortcuts ── */
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [shortcutsOpen, setShortcutsOpen] = useState(false);
  useKeyboardShortcuts({
    onTogglePalette: useCallback(() => setPaletteOpen((prev) => !prev), []),
    onSearch: useCallback(() => setTab("search"), [setTab]),
    onToggleShortcuts: useCallback(() => setShortcutsOpen((prev) => !prev), []),
  });

  /* ── Per-room thinking sets for sidebar ── */
  const thinkingByRoomIds = useMemo(() => {
    const result = new Map<string, Set<string>>();
    for (const [rid, agentMap] of thinkingByRoom) {
      result.set(rid, new Set(agentMap.keys()));
    }
    return result;
  }, [thinkingByRoom]);

  /* ── Workspace / project selection state ── */
  const [workspace, setWorkspace] = useState<WorkspaceMeta | null>(null);
  const [phaseTransitioning, setPhaseTransitioning] = useState(false);
  const [loading, setLoading] = useState(true);
  const [switching, setSwitching] = useState(false);
  const [switchError, setSwitchError] = useState("");
  const [selectedWorkspaceId, setSelectedWorkspaceId] = useState<string | null>(null);
  const [showSettings, setShowSettings] = useState(false);

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

  /* ── Callbacks ── */
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
        const newRoom = await createRoom(name);
        handleRoomSelect(newRoom.id);
        setSelectedWorkspaceId(null);
        setTab("chat");
        handleManualRefresh();
      } catch (e) {
        console.error("Failed to create room:", e);
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
      } catch (e) {
        console.error("Failed to create session:", e);
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
      } catch (e) {
        console.error("Failed to toggle agent:", e);
      }
    },
    [handleManualRefresh],
  );

  /* ── Document title ── */
  useEffect(() => {
    const roomName = room?.name ?? "Agent Academy";
    const phase = room?.currentPhase ?? "";
    document.title = phase
      ? `${roomName} · ${phase} | Agent Academy`
      : `${roomName} | Agent Academy`;
  }, [room?.name, room?.currentPhase]);

  /* ── Early returns ── */
  if (auth === null || loading) {
    return <div className={s.root} />;
  }

  if (auth.authEnabled && !shouldRenderWorkspace(auth)) {
    return <ChunkErrorBoundary><Suspense fallback={<div className={s.root} />}><LoginPage copilotStatus={auth.copilotStatus} user={auth.user ?? null} /></Suspense></ChunkErrorBoundary>;
  }

  /* ── Derived state ── */
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

  /* ── Header model ── */
  const headerModel: HeaderModel = {
    title: sessionAgent
      ? `${sessionAgent.name}'s Sessions`
      : selectedBreakout
        ? `${selectedAgent?.name ?? "Agent"}'s Workspace`
        : tab === "chat"
          ? room?.name ?? "No active room"
          : viewInfo.title,
    meta: sessionAgent || selectedBreakout
      ? null
      : tab === "chat" && room
        ? `${room.participants.length} agents · ${room.currentPhase}`
        : tab !== "chat" && viewInfo.meta
          ? viewInfo.meta
          : null,
    showPhasePill: !!(room && !sessionAgent && !selectedBreakout),
    workspaceLimited,
    degradedEyebrow: degradedCopy?.eyebrow ?? null,
    circuitBreakerState,
  };

  /* ── Toolbar model ── */
  const toolbarModel: ToolbarModel = {
    tab,
    chatToolbar: tab === "chat" && room ? {
      currentPhase: room.currentPhase,
      onPhaseChange: (phase: CollaborationPhase) => void wrappedPhaseTransition(phase),
      disabled: workspaceLimited,
      filterChecked: chatFilterChecked,
      hiddenFilterCount: hiddenFilters.size,
      onFilterChange: onChatFilterChange,
    } : null,
  };

  return (
    <div className={s.root}>
      <StatusBanners
        err={err}
        switchError={switchError}
        recoveryBanner={recoveryBanner}
        circuitBreakerState={circuitBreakerState}
        connectionDetail={connectionDetail}
        styles={s}
      />

      {showProjectSelector ? (
        <ChunkErrorBoundary>
        <Suspense fallback={<div className={s.root}><Spinner label="Loading…" /></div>}>
        <ProjectSelectorPage
          onProjectSelected={handleProjectSelected}
          onProjectOnboarded={handleProjectOnboarded}
          user={auth.user ?? null}
          onLogout={logoutDialog.request}
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
            onLogout={logoutDialog.request}
            onOpenSettings={() => setShowSettings(true)}
            sprintVersion={sprintVersion}
          />

          <main className={s.workspace} aria-label="Workspace content">
            <WorkspaceHeader model={headerModel} styles={s} />

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

            {!sessionAgent && !selectedBreakout && (
              <WorkspaceToolbar model={toolbarModel} styles={s} />
            )}

            {/* ─ Content ─ */}
            {sessionAgent ? (
              <ChunkErrorBoundary>
                <Suspense fallback={<section className={s.tabContent}><Spinner label="Loading…" /></section>}>
                  <section className={s.tabContent}>
                    <AgentSessionPanel
                      agent={sessionAgent}
                      location={sessionAgentLocation}
                      thinkingAgents={thinkingAgentList}
                      connectionStatus={connectionStatus}
                      onSendMessage={handleSendMessage}
                    />
                  </section>
                </Suspense>
              </ChunkErrorBoundary>
            ) : selectedBreakout ? (
              <ChunkErrorBoundary>
                <Suspense fallback={<section className={s.tabContent}><Spinner label="Loading…" /></section>}>
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
                </Suspense>
              </ChunkErrorBoundary>
            ) : (
              <WorkspaceContent
                tab={tab}
                room={room}
                busy={busy}
                thinkingAgents={thinkingAgentList}
                connectionStatus={connectionStatus}
                workspaceLimited={workspaceLimited}
                hiddenFilters={hiddenFilters}
                agentLocations={ov.agentLocations ?? []}
                configuredAgents={ov.configuredAgents}
                onSendMessage={handleSendMessage}
                onCreateSession={handleCreateSession}
                onToggleAgent={handleToggleAgent}
                allTasks={allTasks}
                tasksLoading={tasksLoading}
                tasksError={tasksError}
                activeSprintId={activeSprintId}
                onRefreshTasks={refreshTasks}
                onPhaseTransition={wrappedPhaseTransition}
                phaseTransitioning={phaseTransitioning}
                overview={ov}
                circuitBreakerState={circuitBreakerState}
                sprintVersion={sprintVersion}
                lastSprintEvent={lastSprintEvent}
                activity={activity}
                onSelectRoom={(id) => { handleRoomSelect(id); setTab("chat"); }}
                onNavigateToTasks={() => setTab("tasks")}
                styles={s}
              />
            )}
          </main>
        </div>
      )}

      {/* ── Overlays ── */}
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
        open={logoutDialog.open}
        onConfirm={logoutDialog.confirm}
        onCancel={logoutDialog.cancel}
        title="Sign out?"
        message="You'll lose live updates and need to sign in again to resume. Any unsaved draft messages are preserved locally."
        confirmLabel="Sign out"
      />
      <Toaster toasterId={toasterId} position="bottom-end" />
    </div>
  );
}
