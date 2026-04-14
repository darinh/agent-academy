import { lazy, Suspense } from "react";
import { Spinner } from "@fluentui/react-components";
import ChatPanel from "./ChatPanel";
import ChunkErrorBoundary from "./ChunkErrorBoundary";
import type { AgentDefinition, AgentLocation, CollaborationPhase, RoomSnapshot, TaskSnapshot, ActivityEvent, SprintRealtimeEvent, WorkspaceOverview } from "./api";
import type { ThinkingAgent } from "./useWorkspace";
import type { ConnectionStatus } from "./useActivityHub";
import type { CircuitBreakerState } from "./useCircuitBreakerPolling";
import type { MessageFilter } from "./chatUtils";

// Lazy-loaded panels — each becomes its own chunk
const PlanPanel = lazy(() => import("./PlanPanel"));
const TimelinePanel = lazy(() => import("./TimelinePanel"));
const DashboardPanel = lazy(() => import("./DashboardPanel"));
const WorkspaceOverviewPanel = lazy(() => import("./WorkspaceOverviewPanel"));
const TaskListPanel = lazy(() => import("./TaskListPanel"));
const DmPanel = lazy(() => import("./DmPanel"));
const CommandsPanel = lazy(() => import("./CommandsPanel"));
const SprintPanel = lazy(() => import("./SprintPanel"));
const SearchPanel = lazy(() => import("./SearchPanel"));
const MemoryBrowserPanel = lazy(() => import("./MemoryBrowserPanel"));
const DigestPanel = lazy(() => import("./DigestPanel"));
const RetrospectivePanel = lazy(() => import("./RetrospectivePanel"));

export interface WorkspaceContentProps {
  tab: string;
  room: RoomSnapshot | null;
  busy: boolean;
  thinkingAgents: ThinkingAgent[];
  connectionStatus: ConnectionStatus;
  workspaceLimited: boolean;
  hiddenFilters: Set<MessageFilter>;
  agentLocations: AgentLocation[];
  configuredAgents: AgentDefinition[];
  onSendMessage: (targetRoomId: string, content: string) => Promise<boolean>;
  onCreateSession: (roomId: string) => Promise<void>;
  onToggleAgent: (roomId: string, agentId: string, currentlyInRoom: boolean) => Promise<void>;
  allTasks: TaskSnapshot[];
  tasksLoading: boolean;
  tasksError: boolean;
  activeSprintId: string | null;
  onRefreshTasks: () => void;
  onPhaseTransition: (phase: CollaborationPhase) => Promise<void>;
  phaseTransitioning: boolean;
  overview: WorkspaceOverview;
  circuitBreakerState: CircuitBreakerState;
  sprintVersion: number;
  lastSprintEvent: SprintRealtimeEvent | null;
  activity: ActivityEvent[];
  onSelectRoom: (id: string) => void;
  onNavigateToTasks: () => void;
  styles: Record<string, string>;
}

export default function WorkspaceContent(props: WorkspaceContentProps) {
  const { tab, styles: s } = props;

  return (
    <ChunkErrorBoundary>
      <Suspense fallback={<section className={s.tabContent}><Spinner label="Loading…" /></section>}>
        <section className={s.tabContent}>
          {tab === "chat" && (
            <ChatPanel
              room={props.room}
              loading={props.busy}
              thinkingAgents={props.thinkingAgents}
              connectionStatus={props.connectionStatus}
              onSendMessage={props.onSendMessage}
              readOnly={props.workspaceLimited}
              hiddenFilters={props.hiddenFilters}
              agentLocations={props.agentLocations}
              configuredAgents={props.configuredAgents}
              onCreateSession={props.onCreateSession}
              onToggleAgent={props.onToggleAgent}
            />
          )}
          {tab === "tasks" && (
            <TaskListPanel
              tasks={props.allTasks}
              loading={props.tasksLoading}
              error={props.tasksError}
              onRefresh={props.onRefreshTasks}
              activeSprintId={props.activeSprintId}
              agents={props.configuredAgents}
            />
          )}
          {tab === "plan" && (
            <PlanPanel key={props.room?.id ?? "no-room"} roomId={props.room?.id ?? null} />
          )}
          {tab === "commands" && (
            <CommandsPanel roomId={props.room?.id ?? null} readOnly={props.workspaceLimited} />
          )}
          {tab === "sprint" && <SprintPanel sprintVersion={props.sprintVersion} lastSprintEvent={props.lastSprintEvent} />}
          {tab === "timeline" && (
            <TimelinePanel activity={props.activity} loading={props.busy} />
          )}
          {tab === "dashboard" && (
            <DashboardPanel overview={props.overview} circuitBreakerState={props.circuitBreakerState} />
          )}
          {tab === "overview" && (
            <WorkspaceOverviewPanel
              overview={props.overview}
              room={props.room}
              onPhaseTransition={props.onPhaseTransition}
              transitioning={props.phaseTransitioning}
              readOnly={props.workspaceLimited}
            />
          )}
          {tab === "directMessages" && (
            <DmPanel
              agents={props.configuredAgents.map((a) => ({
                id: a.id,
                name: a.name,
                role: a.role,
              }))}
              readOnly={props.workspaceLimited}
            />
          )}
          {tab === "search" && (
            <SearchPanel
              onNavigateToRoom={(roomId) => { props.onSelectRoom(roomId); }}
              onNavigateToTasks={props.onNavigateToTasks}
            />
          )}
          {tab === "memories" && (
            <MemoryBrowserPanel agents={props.configuredAgents} />
          )}
          {tab === "digests" && <DigestPanel />}
          {tab === "retrospectives" && <RetrospectivePanel />}
        </section>
      </Suspense>
    </ChunkErrorBoundary>
  );
}
