import { lazy, Suspense, useCallback, useEffect, useMemo, useState } from "react";
import {
  Button,
  FluentProvider,
  mergeClasses,
  Spinner,
  Toaster,
  useToastController,
  useId,
  Toast,
  ToastTitle,
  ToastBody,
} from "@fluentui/react-components";
import { useLayoutStyles, useWorkspaceStyles, useRecoveryStyles } from "./styles";
import { useWorkspace } from "./useWorkspace";
import { useDesktopNotifications } from "./useDesktopNotifications";
import type { ActivityEvent, CollaborationPhase, WorkspaceMeta } from "./api";
import SidebarPanel from "./SidebarPanel";
import ChatPanel from "./ChatPanel";
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
import { VIEW_TITLES, TOAST_EVENT_TYPES, toastIntent, matrixTheme } from "./appConstants";
import { useChatFilters } from "./useChatFilters";
import { useProjectSelection } from "./useProjectSelection";
import { useRoomCallbacks } from "./useRoomCallbacks";

// Lazy-loaded panels
const ProjectSelectorPage = lazy(() => import("./ProjectSelectorPage"));
const LoginPage = lazy(() => import("./LoginPage"));
const SettingsPanel = lazy(() => import("./SettingsPanel"));
const AgentSessionPanel = lazy(() => import("./AgentSessionPanel"));
const CommandPalette = lazy(() => import("./CommandPalette"));
const KeyboardShortcutsDialog = lazy(() => import("./KeyboardShortcutsDialog"));

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
  const { hiddenFilters, chatFilterChecked, onChatFilterChange } = useChatFilters();

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
    retroVersion,
    digestVersion,
    memoryVersion,
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

  /* ── Workspace / project selection state ── */
  const {
    workspace,
    loading,
    showProjectSelector,
    setShowProjectSelector,
    switchError,
    handleProjectSelected,
    handleProjectOnboarded,
  } = useProjectSelection({
    onSwitched: handleManualRefresh,
    onSwitchedToast: useCallback((ws: WorkspaceMeta) => {
      dispatchToast(
        <Toast>
          <ToastTitle>Switched to {ws.projectName ?? ws.path}</ToastTitle>
        </Toast>,
        { intent: "success", timeout: 2000 },
      );
    }, [dispatchToast]),
  });

  const {
    phaseTransitioning,
    selectedWorkspaceId,
    wrappedPhaseTransition,
    wrappedRoomSelect,
    handleCreateRoom,
    handleWorkspaceSelect,
    handleCreateSession,
    handleToggleAgent,
  } = useRoomCallbacks({
    handlePhaseTransition,
    handleRoomSelect,
    handleManualRefresh,
    setTab,
  });

  /* ── Task data ── */
  const { allTasks, tasksLoading, tasksError, activeSprintId, refreshTasks } = useTaskData({
    enabled: !showProjectSelector && tab === "tasks",
  });

  /* ── Task focus (cross-panel navigation) ── */
  const [focusTaskId, setFocusTaskId] = useState<string | null>(null);
  const handleNavigateToTask = useCallback((taskId: string) => {
    setFocusTaskId(taskId);
    setTab("tasks");
  }, [setTab]);
  const handleFocusTaskHandled = useCallback(() => {
    setFocusTaskId(null);
  }, []);

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

  const [showSettings, setShowSettings] = useState(false);

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
                retroVersion={retroVersion}
                digestVersion={digestVersion}
                memoryVersion={memoryVersion}
                activity={activity}
                onSelectRoom={(id) => { handleRoomSelect(id); setTab("chat"); }}
                onNavigateToTasks={() => setTab("tasks")}
                onNavigateToTask={handleNavigateToTask}
                focusTaskId={focusTaskId}
                onFocusTaskHandled={handleFocusTaskHandled}
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
