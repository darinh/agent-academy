import { useCallback, useEffect, useState } from "react";
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
} from "@fluentui/react-icons";
import { useStyles } from "./useStyles";
import { useWorkspace } from "./useWorkspace";
import { getActiveWorkspace, switchWorkspace } from "./api";
import type { OnboardResult, WorkspaceMeta } from "./api";
import ProjectSelectorPage from "./ProjectSelectorPage";
import SidebarPanel from "./SidebarPanel";
import ChatPanel from "./ChatPanel";
import PlanPanel from "./PlanPanel";
import TimelinePanel from "./TimelinePanel";
import DashboardPanel from "./DashboardPanel";
import WorkspaceOverviewPanel from "./WorkspaceOverviewPanel";

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
    roster,
    activity,
    thinkingAgentList,
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

  const [workspace, setWorkspace] = useState<WorkspaceMeta | null>(null);
  const [showProjectSelector, setShowProjectSelector] = useState(false);
  const [phaseTransitioning, setPhaseTransitioning] = useState(false);
  const [loading, setLoading] = useState(true);
  const [switching, setSwitching] = useState(false);
  const [switchError, setSwitchError] = useState("");

  // On mount, check for active workspace — gate UI on this check
  useEffect(() => {
    getActiveWorkspace()
      .then((data) => {
        if (data.active) {
          setWorkspace(data.active);
        } else {
          setShowProjectSelector(true);
        }
      })
      .catch(() => {
        setShowProjectSelector(true);
      })
      .finally(() => {
        setLoading(false);
      });
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

  useEffect(() => {
    const roomName = room?.name ?? "Agent Academy";
    const phase = room?.currentPhase ?? "";
    document.title = phase
      ? `${roomName} · ${phase} | Agent Academy`
      : `${roomName} | Agent Academy`;
  }, [room?.name, room?.currentPhase]);

  if (loading) {
    return <div className={s.root} />;
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
        />
      ) : (
        <div className={mergeClasses(s.shell, sidebarOpen ? s.shellOpen : s.shellCollapsed)}>
          <SidebarPanel
            sidebarOpen={sidebarOpen}
            busy={busy}
            rooms={ov.rooms}
            room={room}
            roster={roster}
            onRefresh={handleManualRefresh}
            onToggleSidebar={handleToggleSidebar}
            onSelectRoom={handleRoomSelect}
            workspace={
              workspace
                ? { name: workspace.projectName ?? workspace.path, path: workspace.path }
                : null
            }
            onSwitchProject={() => setShowProjectSelector(true)}
          />

          <main className={s.workspace} aria-label="Workspace content">
            <div className={s.workspaceHeader}>
              <div>
                <div className={s.workspaceTitle}>{room?.name ?? "No active room"}</div>
                <div className={s.workspaceSubtitle}>{roomSummary}</div>
              </div>
              {room && (
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
            </div>

            <div className={s.tabBar}>
              <TabList
                selectedValue={tab}
                onTabSelect={(_, data) => setTab(data.value as string)}
                size="small"
              >
                <Tab value="chat" icon={<ChatRegular />}>Conversation</Tab>
                <Tab value="plan" icon={<DocumentRegular />}>Plan</Tab>
                <Tab value="timeline" icon={<TimelineRegular />}>Timeline</Tab>
                <Tab value="dashboard" icon={<GridRegular />}>Dashboard</Tab>
                <Tab value="overview" icon={<BoardRegular />}>Overview</Tab>
              </TabList>
            </div>

            <section className={s.tabContent}>
              {tab === "chat" && (
                <ChatPanel
                  room={room}
                  thinkingAgents={thinkingAgentList}
                  onSendMessage={handleSendMessage}
                />
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
            </section>
          </main>
        </div>
      )}
    </div>
  );
}
