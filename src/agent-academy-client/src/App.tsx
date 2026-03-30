import { useCallback, useEffect, useMemo, useState } from "react";
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
  DocumentRegular,
  TimelineRegular,
  GridRegular,
  BoardRegular,
  TaskListLtrRegular,
  MailRegular,
} from "@fluentui/react-icons";
import { useStyles } from "./useStyles";
import { useWorkspace } from "./useWorkspace";
import { getActiveWorkspace, switchWorkspace, getTasks, getAuthStatus, logout } from "./api";
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
          setAuth({ authEnabled: false, authenticated: false });
        }
      }
    }
    void checkAuth(0);
    return () => { cancelled = true; if (retryTimer) clearTimeout(retryTimer); };
  }, []);

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
    try {
      await logout();
      setAuth({ authEnabled: true, authenticated: false });
    } catch { /* best-effort */ }
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

  // Show login page if auth is enabled but user is not authenticated
  if (auth.authEnabled && !auth.authenticated) {
    return <LoginPage />;
  }

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

      {showProjectSelector ? (
        <ProjectSelectorPage
          onProjectSelected={handleProjectSelected}
          onProjectOnboarded={handleProjectOnboarded}
          user={auth?.authenticated ? auth.user : null}
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
            {(() => {
              // Check if an agent session is selected (agent:agentId format)
              const isAgentView = selectedWorkspaceId?.startsWith("agent:");
              const selectedAgentId = isAgentView
                ? selectedWorkspaceId!.slice("agent:".length)
                : null;
              const sessionAgent = selectedAgentId
                ? ov.configuredAgents.find((a) => a.id === selectedAgentId)
                : null;
              const sessionAgentLocation = selectedAgentId
                ? (ov.agentLocations ?? []).find((l) => l.agentId === selectedAgentId)
                : undefined;

              // Legacy: direct breakout room selection (kept for compatibility)
              const selectedBreakout = selectedWorkspaceId && !isAgentView
                ? breakoutRooms.find((br) => br.id === selectedWorkspaceId)
                : null;
              const selectedAgent = selectedBreakout
                ? ov.configuredAgents.find((a) => a.id === selectedBreakout.assignedAgentId)
                : null;

              return (<>
                <div className={s.workspaceHeader}>
                  <div>
                    <div className={s.workspaceTitle}>
                      {sessionAgent
                        ? `${sessionAgent.name}'s Sessions`
                        : selectedBreakout
                          ? `${selectedAgent?.name ?? "Agent"}'s Workspace`
                          : room?.name ?? "No active room"}
                    </div>
                    <div className={s.workspaceSubtitle}>
                      {sessionAgent
                        ? sessionAgent.role
                        : selectedBreakout
                          ? selectedBreakout.name
                          : roomSummary}
                    </div>
                  </div>
              <div style={{ display: "flex", alignItems: "center", gap: "16px" }}>
                {room && !sessionAgent && (
                  <div className={s.phasePill}>
                    <span
                      style={{
                        width: "8px",
                        height: "8px",
                        borderRadius: "999px",
                        backgroundColor: "#6cb6ff",
                      }}
                    />
                    {room.currentPhase}
                  </div>
                )}
                {auth?.authenticated && auth.user && (
                  <UserBadge user={auth.user} onLogout={handleLogout} onOpenSettings={() => setShowSettings(true)} />
                )}
              </div>
            </div>

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
                <TabList
                  selectedValue={tab}
                  onTabSelect={(_, data) => setTab(data.value as string)}
                  size="small"
                >
                  <Tab value="chat" icon={<ChatRegular />}>Conversation</Tab>
                  <Tab value="tasks" icon={<TaskListLtrRegular />}>Tasks</Tab>
                  <Tab value="plan" icon={<DocumentRegular />}>Plan</Tab>
                  <Tab value="timeline" icon={<TimelineRegular />}>Timeline</Tab>
                  <Tab value="dashboard" icon={<GridRegular />}>Dashboard</Tab>
                  <Tab value="overview" icon={<BoardRegular />}>Overview</Tab>
                  <Tab value="directMessages" icon={<MailRegular />}>Messages</Tab>
                </TabList>
              </div>

              <section className={s.tabContent}>
                {tab === "chat" && (
                  <ChatPanel
                    room={room}
                    thinkingAgents={thinkingAgentList}
                    connectionStatus={connectionStatus}
                    onSendMessage={handleSendMessage}
                  />
                )}
                {tab === "tasks" && (
                  <TaskListPanel tasks={allTasks} error={tasksError} />
                )}
                {tab === "plan" && (
                  <PlanPanel key={room?.id ?? "no-room"} roomId={room?.id ?? null} />
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
                  />
                )}
                {tab === "directMessages" && (
                  <DmPanel
                    agents={ov.configuredAgents.map((a) => ({
                      id: a.id,
                      name: a.name,
                      role: a.role,
                    }))}
                  />
                )}
              </section>
            </>)}
            </>);
            })()}
          </main>
        </div>
      )}
      {showSettings && (
        <SettingsPanel onClose={() => setShowSettings(false)} />
      )}
    </div>
  );
}
