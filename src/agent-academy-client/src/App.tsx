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
import { useStyles } from "./useStyles";
import { useWorkspace } from "./useWorkspace";
import type { OnboardResult, WorkspaceMeta } from "./api";
import ProjectSelectorPage from "./ProjectSelectorPage";
import SidebarPanel from "./SidebarPanel";
import ChatPanel from "./ChatPanel";

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
    handleTaskSubmit,
    handleSendMessage,
  } = useWorkspace();

  const [workspace, setWorkspace] = useState<WorkspaceMeta | null>(null);
  const [showProjectSelector, setShowProjectSelector] = useState(true);

  // If overview loads successfully, we have a workspace
  useEffect(() => {
    if (ov.rooms.length > 0 || ov.configuredAgents.length > 0) {
      setShowProjectSelector(false);
    }
  }, [ov.rooms.length, ov.configuredAgents.length]);

  const handleProjectSelected = useCallback(
    (workspacePath: string) => {
      setWorkspace({ path: workspacePath, projectName: workspacePath.split("/").pop() });
      setShowProjectSelector(false);
      handleManualRefresh();
    },
    [handleManualRefresh],
  );

  const handleProjectOnboarded = useCallback(
    (result: OnboardResult) => {
      setWorkspace(result.workspace);
      setShowProjectSelector(false);
      handleManualRefresh();
    },
    [handleManualRefresh],
  );

  useEffect(() => {
    const roomName = room?.name ?? "Agent Academy";
    const phase = room?.currentPhase ?? "";
    document.title = phase
      ? `${roomName} · ${phase} | Agent Academy`
      : `${roomName} | Agent Academy`;
  }, [room?.name, room?.currentPhase]);

  return (
    <div className={s.root}>
      {err && (
        <div className={s.errorBar}>
          <MessageBar intent="error">
            <MessageBarBody>
              <MessageBarTitle>Error</MessageBarTitle>
              {err}
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
            onSubmitTask={handleTaskSubmit}
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
                <Tab value="chat">Conversation</Tab>
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
            </section>
          </main>
        </div>
      )}
    </div>
  );
}
