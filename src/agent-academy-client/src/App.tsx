import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  FluentProvider,
  webDarkTheme,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Tab,
  TabList,
} from "@fluentui/react-components";
import {
  ChatRegular,
  CodeRegular,
  DocumentRegular,
  TimelineRegular,
  GridRegular,
  BoardRegular,
  TaskListLtrRegular,
  MailRegular,
} from "@fluentui/react-icons";
import { useStyles } from "./useStyles";
import { useWorkspace } from "./useWorkspace";
import { apiBaseUrl, getActiveWorkspace, switchWorkspace, getTasks, getAuthStatus, logout } from "./api";
import type { OnboardResult, WorkspaceMeta, TaskSnapshot, AuthStatus } from "./api";
import ProjectSelectorPage from "./ProjectSelectorPage";
import SidebarPanel from "./SidebarPanel";
import ChatPanel from "./ChatPanel";
import PlanPanel from "./PlanPanel";
import TimelinePanel from "./TimelinePanel";
import DashboardPanel from "./DashboardPanel";
import WorkspaceOverviewPanel from "./WorkspaceOverviewPanel";
import TaskListPanel from "./TaskListPanel";
import LoginPage from "./LoginPage";
import UserBadge from "./UserBadge";
import SettingsPanel from "./SettingsPanel";
import DmPanel from "./DmPanel";
import AgentSessionPanel from "./AgentSessionPanel";
import CommandsPanel from "./CommandsPanel";
import RecoveryBanner from "./RecoveryBanner";
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
} from "./authMonitor";

const CONNECTION_STATUS_COPY = {
  connected: "Live sync",
  connecting: "Connecting",
  reconnecting: "Reconnecting",
  disconnected: "Offline",
} as const;

const TAB_ITEMS = [
  { value: "chat", label: "Conversation", detail: "Live room stream", Icon: ChatRegular },
  { value: "tasks", label: "Tasks", detail: "Delivery queue", Icon: TaskListLtrRegular },
  { value: "plan", label: "Plan", detail: "Shared approach", Icon: DocumentRegular },
  { value: "commands", label: "Commands", detail: "Human tools", Icon: CodeRegular },
  { value: "timeline", label: "Timeline", detail: "Activity trace", Icon: TimelineRegular },
  { value: "dashboard", label: "Dashboard", detail: "System telemetry", Icon: GridRegular },
  { value: "overview", label: "Overview", detail: "Room state", Icon: BoardRegular },
  { value: "directMessages", label: "Messages", detail: "Private threads", Icon: MailRegular },
] as const;

const TAB_DESCRIPTIONS: Record<string, string> = {
  chat: "Follow the live room thread, watch thinking agents surface, and keep the operator in flow.",
  tasks: "Track active assignments, approvals, and branch movement without dropping out of the workspace.",
  plan: "Keep the room plan visible so implementation decisions stay tied to the shared approach.",
  commands: "Run the human command surface from one curated control deck.",
  timeline: "Scan system activity as a narrative, not a pile of logs.",
  dashboard: "Monitor the wider fleet posture, room load, and orchestration health.",
  overview: "Adjust room state with a clearer sense of progress, pacing, and participation.",
  directMessages: "Read private threads without losing the main-room context around them.",
};

export default function App() {
  return (
    <FluentProvider theme={webDarkTheme}>
      <AppShell />
    </FluentProvider>
  );
}

function AppShell() {
  const s = useStyles();
  const {
    ov,
    room,
    activity,
    thinkingAgentList,
    thinkingByRoom,
    recoveryBanner,
    connectionStatus,
    breakoutRooms,
    err,
    busy,
    tab,
    setTab,
    sidebarOpen,
    roomSummary,
    handleRoomSelect,
    handleManualRefresh,
    handleToggleSidebar,
    handleSendMessage,
    handlePhaseTransition,
  } = useWorkspace();

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
  const [auth, setAuth] = useState<AuthStatus | null>(null);
  const [selectedWorkspaceId, setSelectedWorkspaceId] = useState<string | null>(null);
  const [showSettings, setShowSettings] = useState(false);
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
    }

    previousAuthRef.current = auth;
  }, [auth, loginUrl]);

  // Fetch tasks when workspace is active and tab is "tasks"
  useEffect(() => {
    if (showProjectSelector || tab !== "tasks") return;
    let cancelled = false;
    setTasksError(false);
    getTasks()
      .then((tasks) => { if (!cancelled) setAllTasks(tasks); })
      .catch(() => { if (!cancelled) { setAllTasks([]); setTasksError(true); } });
    return () => { cancelled = true; };
  }, [showProjectSelector, tab]);

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
      } catch (e) {
        setSwitchError(e instanceof Error ? e.message : "Failed to switch workspace");
      } finally {
        setSwitching(false);
      }
    },
    [handleManualRefresh, switching],
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
    },
    [handleRoomSelect],
  );

  const handleWorkspaceSelect = useCallback(
    (breakoutId: string) => {
      setSelectedWorkspaceId(breakoutId);
    },
    [],
  );

  const handleLogout = useCallback(async () => {
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
    return <LoginPage copilotStatus={auth.copilotStatus} user={auth.user ?? null} />;
  }

  const workspaceLimited = isWorkspaceLimited(auth);
  const degradedCopy = workspaceLimited ? getCopilotStatusCopy("degraded", auth.user ?? null) : null;
  const connectionLabel = CONNECTION_STATUS_COPY[connectionStatus];
  const currentTab = TAB_ITEMS.find((item) => item.value === tab) ?? TAB_ITEMS[0];
  const activeRoomCount = ov.rooms.filter((candidate) => candidate.status === "Active" || candidate.status === "AttentionRequired").length;
  const workingAgentCount = (ov.agentLocations ?? []).filter((location) => location.state === "Working").length;
  const activeBreakoutCount = breakoutRooms.filter((breakout) => breakout.status === "Active").length;
  const activeTaskCount = ov.rooms.filter((candidate) => candidate.activeTask).length;
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
  const workspaceTitle = sessionAgent
    ? `${sessionAgent.name}'s Sessions`
    : selectedBreakout
      ? `${selectedAgent?.name ?? "Agent"}'s Workspace`
      : room?.name ?? "No active room";
  const workspaceSubtitle = sessionAgent
    ? sessionAgent.role
    : selectedBreakout
      ? selectedBreakout.name
      : roomSummary;
  const spotlightTitle = sessionAgent
    ? "Agent workbench"
    : selectedBreakout
      ? "Breakout review"
      : currentTab.label;
  const spotlightBody = sessionAgent
    ? `${sessionAgent.name} is currently ${sessionAgentLocation?.state?.toLowerCase() ?? "idle"}. Review room placement, current workload, and session context from a single pane.`
    : selectedBreakout
      ? `Inspect ${selectedBreakout.name} in read-only mode while keeping the parent room context visible in the shell.`
      : TAB_DESCRIPTIONS[currentTab.value] ?? currentTab.detail;
  const spotlightMetrics = [
    { label: "Live rooms", value: activeRoomCount.toString() },
    { label: "Working agents", value: workingAgentCount.toString() },
    { label: "Breakouts", value: activeBreakoutCount.toString() },
    { label: "Active tasks", value: activeTaskCount.toString() },
  ];

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

      {showProjectSelector ? (
        <ProjectSelectorPage
          onProjectSelected={handleProjectSelected}
          onProjectOnboarded={handleProjectOnboarded}
          user={auth.user ?? null}
          onLogout={handleLogout}
        />
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
            workspace={
              workspace
                ? { name: workspace.projectName ?? workspace.path, path: workspace.path }
                : null
            }
            onSwitchProject={() => setShowProjectSelector(true)}
          />

          <main className={s.workspace} aria-label="Workspace content">
            <>
              <div className={s.workspaceHeader}>
                <div className={s.workspaceHeaderBody}>
                  <div className={s.workspaceHeaderTopRow}>
                    <div className={s.workspaceEyebrow}>
                      {sessionAgent ? "Agent session" : selectedBreakout ? "Breakout review" : "Workspace shell"}
                    </div>
                    <div className={s.workspaceHeaderSignals}>
                      <div className={mergeClasses(s.workspaceSignal, workspaceLimited && s.workspaceSignalWarning)}>
                        {workspaceLimited ? degradedCopy?.eyebrow ?? "Limited mode" : "Copilot ready"}
                      </div>
                      <div className={s.workspaceSignal}>{connectionLabel}</div>
                    </div>
                  </div>
                  <div className={s.workspaceTitle}>{workspaceTitle}</div>
                  <div className={s.workspaceSubtitle}>{workspaceSubtitle}</div>
                  <div className={s.workspaceLead}>
                    {workspaceLimited
                      ? "The shell stays readable while Copilot reconnects, so you can keep context and avoid losing the thread."
                      : "A calmer control surface for rooms, tasks, and agent movement — tuned to keep the current decision in focus."}
                  </div>
                  <div className={s.workspaceMetaGrid}>
                    <div className={s.workspaceMetaCard}>
                      <div className={s.workspaceMetaLabel}>
                        {sessionAgent ? "Agent role" : selectedBreakout ? "Breakout status" : "Current phase"}
                      </div>
                      <div className={s.workspaceMetaValue}>
                        {sessionAgent
                          ? sessionAgent.role
                          : selectedBreakout
                            ? selectedBreakout.status
                            : room?.currentPhase ?? "No phase"}
                      </div>
                      <div className={s.workspaceMetaDetail}>
                        {sessionAgentLocation?.state ?? (selectedBreakout ? "Workspace review" : "Awaiting room activity")}
                      </div>
                    </div>
                    <div className={s.workspaceMetaCard}>
                      <div className={s.workspaceMetaLabel}>
                        {sessionAgent ? "Assigned room" : selectedBreakout ? "Assigned agent" : "Active task"}
                      </div>
                      <div className={s.workspaceMetaValue}>
                        {sessionAgent
                          ? (sessionAgentLocation
                            ? ov.rooms.find((candidate) => candidate.id === sessionAgentLocation.roomId)?.name ?? "Unassigned"
                            : "Unassigned")
                          : selectedBreakout
                            ? (selectedAgent?.name ?? "Unknown")
                            : room?.activeTask?.title ?? "No active task"}
                      </div>
                      <div className={s.workspaceMetaDetail}>
                        {sessionAgent
                          ? (sessionAgentLocation?.state ?? "Idle")
                          : selectedBreakout
                            ? selectedBreakout.name
                            : `${room?.participants.length ?? 0} active participants`}
                      </div>
                    </div>
                    <div className={s.workspaceMetaCard}>
                      <div className={s.workspaceMetaLabel}>Runtime</div>
                      <div className={s.workspaceMetaValue}>{connectionLabel}</div>
                      <div className={s.workspaceMetaDetail}>
                        {workspaceLimited ? "Read-only while Copilot reconnects" : "Workspace actions available"}
                      </div>
                    </div>
                  </div>
                </div>
                <div className={s.workspaceHeaderActions}>
                  <div className={s.workspaceHeaderUtilityRow}>
                    {room && !sessionAgent && (
                      <div className={s.phasePill}>
                        <span className={s.phasePillDot} />
                        {room.currentPhase}
                      </div>
                    )}
                    {hasDisplayUser(auth.user) && auth.user && (
                      <UserBadge user={auth.user} onLogout={handleLogout} onOpenSettings={() => setShowSettings(true)} />
                    )}
                  </div>
                  <div className={s.workspaceSpotlightCard}>
                    <div className={s.workspaceSpotlightLabel}>Current view</div>
                    <div className={s.workspaceSpotlightTitle}>{spotlightTitle}</div>
                    <div className={s.workspaceSpotlightBody}>{spotlightBody}</div>
                    <div className={s.workspaceSpotlightGrid}>
                      {spotlightMetrics.map((metric) => (
                        <div key={metric.label} className={s.workspaceSpotlightMetric}>
                          <div className={s.workspaceSpotlightMetricValue}>{metric.value}</div>
                          <div className={s.workspaceSpotlightMetricLabel}>{metric.label}</div>
                        </div>
                      ))}
                    </div>
                  </div>
                </div>
              </div>

              {workspaceLimited && degradedCopy && (
                <div className={s.limitedModeBanner}>
                  <div className={s.limitedModeBadge}>{degradedCopy.eyebrow}</div>
                  <div className={s.limitedModeTitle}>{degradedCopy.title}</div>
                  <div className={s.limitedModeDescription}>
                    {degradedCopy.description}
                  </div>
                </div>
              )}

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
              ) : (<>
                <div className={s.tabBar}>
                  <div className={s.tabBarHeader}>
                    <div className={s.tabBarCopy}>
                      <div className={s.tabBarEyebrow}>Workspace deck</div>
                      <div className={s.tabBarTitle}>{currentTab.label}</div>
                    </div>
                    <div className={s.tabBarDescription}>{currentTab.detail}</div>
                  </div>
                  <TabList
                    className={s.tabList}
                    selectedValue={tab}
                    onTabSelect={(_, data) => setTab(data.value as string)}
                    size="small"
                  >
                    {TAB_ITEMS.map((item) => {
                      const Icon = item.Icon;
                      return (
                        <Tab key={item.value} value={item.value} icon={<Icon />}>
                          <span className={s.tabLabelStack}>
                            <span className={s.tabLabelTitle}>{item.label}</span>
                            <span className={s.tabLabelDetail}>{item.detail}</span>
                          </span>
                        </Tab>
                      );
                    })}
                  </TabList>
                </div>

                <section className={s.tabContent}>
                  {tab === "chat" && (
                    <ChatPanel
                      room={room}
                      thinkingAgents={thinkingAgentList}
                      connectionStatus={connectionStatus}
                      onSendMessage={handleSendMessage}
                      readOnly={workspaceLimited}
                    />
                  )}
                  {tab === "tasks" && (
                    <TaskListPanel tasks={allTasks} error={tasksError} />
                  )}
                  {tab === "plan" && (
                    <PlanPanel key={room?.id ?? "no-room"} roomId={room?.id ?? null} />
                  )}
                  {tab === "commands" && (
                    <CommandsPanel roomId={room?.id ?? null} readOnly={workspaceLimited} />
                  )}
                  {tab === "timeline" && (
                    <TimelinePanel activity={activity} />
                  )}
                  {tab === "dashboard" && (
                    <DashboardPanel overview={ov} />
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
                </section>
              </>)}
            </>
          </main>
        </div>
      )}
      {showSettings && (
        <SettingsPanel onClose={() => setShowSettings(false)} />
      )}
    </div>
  );
}
