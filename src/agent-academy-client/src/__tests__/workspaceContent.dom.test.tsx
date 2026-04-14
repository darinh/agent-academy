// @vitest-environment jsdom
/**
 * Tests for WorkspaceContent — the tab-based content router.
 * Mocks all child panels and verifies the right panel renders for each tab.
 */
import "@testing-library/jest-dom/vitest";
import { describe, expect, it, vi, afterEach } from "vitest";
import { render, screen, cleanup, waitFor } from "@testing-library/react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import WorkspaceContent from "../WorkspaceContent";
import type { WorkspaceContentProps } from "../WorkspaceContent";

// Mock all lazy-loaded panels so Suspense resolves synchronously
vi.mock("../PlanPanel", () => ({ default: () => <div data-testid="plan-panel" /> }));
vi.mock("../TimelinePanel", () => ({ default: () => <div data-testid="timeline-panel" /> }));
vi.mock("../DashboardPanel", () => ({ default: () => <div data-testid="dashboard-panel" /> }));
vi.mock("../WorkspaceOverviewPanel", () => ({ default: () => <div data-testid="overview-panel" /> }));
vi.mock("../TaskListPanel", () => ({ default: () => <div data-testid="task-list-panel" /> }));
vi.mock("../DmPanel", () => ({ default: () => <div data-testid="dm-panel" /> }));
vi.mock("../CommandsPanel", () => ({ default: () => <div data-testid="commands-panel" /> }));
vi.mock("../SprintPanel", () => ({ default: () => <div data-testid="sprint-panel" /> }));
vi.mock("../SearchPanel", () => ({ default: () => <div data-testid="search-panel" /> }));
vi.mock("../ChatPanel", () => ({ default: () => <div data-testid="chat-panel" /> }));
vi.mock("../ChunkErrorBoundary", () => ({
  default: ({ children }: { children: React.ReactNode }) => <div data-testid="error-boundary">{children}</div>,
}));

const baseStyles: Record<string, string> = { tabContent: "" };

function makeProps(overrides: Partial<WorkspaceContentProps> = {}): WorkspaceContentProps {
  return {
    tab: "chat",
    room: null,
    busy: false,
    thinkingAgents: [],
    connectionStatus: "connected",
    workspaceLimited: false,
    hiddenFilters: new Set(),
    agentLocations: [],
    configuredAgents: [],
    onSendMessage: vi.fn(),
    onCreateSession: vi.fn(),
    onToggleAgent: vi.fn(),
    allTasks: [],
    tasksLoading: false,
    tasksError: false,
    activeSprintId: null,
    onRefreshTasks: vi.fn(),
    onPhaseTransition: vi.fn(),
    phaseTransitioning: false,
    overview: {
      configuredAgents: [],
      rooms: [],
      recentActivity: [],
      agentLocations: [],
      breakoutRooms: [],
      generatedAt: new Date().toISOString(),
    },
    circuitBreakerState: "Closed",
    sprintVersion: 0,
    lastSprintEvent: null,
    retroVersion: 0,
    digestVersion: 0,
    memoryVersion: 0,
    activity: [],
    onSelectRoom: vi.fn(),
    onNavigateToTasks: vi.fn(),
    onNavigateToTask: vi.fn(),
    focusTaskId: null,
    onFocusTaskHandled: vi.fn(),
    styles: baseStyles,
    ...overrides,
  };
}

function renderContent(overrides: Partial<WorkspaceContentProps> = {}) {
  return render(
    <FluentProvider theme={webDarkTheme}>
      <WorkspaceContent {...makeProps(overrides)} />
    </FluentProvider>,
  );
}

describe("WorkspaceContent", () => {
  afterEach(() => {
    cleanup();
    document.body.innerHTML = "";
  });

  it("wraps content in ChunkErrorBoundary", () => {
    renderContent({ tab: "chat" });
    expect(screen.getByTestId("error-boundary")).toBeInTheDocument();
  });

  it("renders ChatPanel when tab is 'chat'", () => {
    renderContent({ tab: "chat" });
    expect(screen.getByTestId("chat-panel")).toBeInTheDocument();
  });

  it("renders TaskListPanel when tab is 'tasks'", async () => {
    renderContent({ tab: "tasks" });
    expect(await screen.findByTestId("task-list-panel")).toBeInTheDocument();
  });

  it("renders PlanPanel when tab is 'plan'", async () => {
    renderContent({ tab: "plan" });
    expect(await screen.findByTestId("plan-panel")).toBeInTheDocument();
  });

  it("renders CommandsPanel when tab is 'commands'", async () => {
    renderContent({ tab: "commands" });
    expect(await screen.findByTestId("commands-panel")).toBeInTheDocument();
  });

  it("renders SprintPanel when tab is 'sprint'", async () => {
    renderContent({ tab: "sprint" });
    expect(await screen.findByTestId("sprint-panel")).toBeInTheDocument();
  });

  it("renders TimelinePanel when tab is 'timeline'", async () => {
    renderContent({ tab: "timeline" });
    expect(await screen.findByTestId("timeline-panel")).toBeInTheDocument();
  });

  it("renders DashboardPanel when tab is 'dashboard'", async () => {
    renderContent({ tab: "dashboard" });
    expect(await screen.findByTestId("dashboard-panel")).toBeInTheDocument();
  });

  it("renders WorkspaceOverviewPanel when tab is 'overview'", async () => {
    renderContent({ tab: "overview" });
    expect(await screen.findByTestId("overview-panel")).toBeInTheDocument();
  });

  it("renders DmPanel when tab is 'directMessages'", async () => {
    renderContent({ tab: "directMessages" });
    expect(await screen.findByTestId("dm-panel")).toBeInTheDocument();
  });

  it("renders SearchPanel when tab is 'search'", async () => {
    renderContent({ tab: "search" });
    expect(await screen.findByTestId("search-panel")).toBeInTheDocument();
  });

  it("does not render any known panel for unknown tab", async () => {
    renderContent({ tab: "unknown" });
    // Wait for Suspense to resolve, then verify no panel rendered
    await waitFor(() => {
      expect(screen.queryByText("Loading…")).not.toBeInTheDocument();
    });
    expect(screen.queryByTestId("chat-panel")).not.toBeInTheDocument();
    expect(screen.queryByTestId("task-list-panel")).not.toBeInTheDocument();
    expect(screen.queryByTestId("plan-panel")).not.toBeInTheDocument();
  });

  it("only renders one panel at a time", async () => {
    renderContent({ tab: "tasks" });
    expect(await screen.findByTestId("task-list-panel")).toBeInTheDocument();
    expect(screen.queryByTestId("chat-panel")).not.toBeInTheDocument();
    expect(screen.queryByTestId("plan-panel")).not.toBeInTheDocument();
  });
});
