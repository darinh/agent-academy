// @vitest-environment jsdom
/**
 * DOM tests for TaskDetail.
 *
 * Covers: description, success criteria, review rounds, implementation/validation
 * summaries, tests created, retro link, priority badge. Sub-sections are tested
 * separately in taskDetailSections.dom.test.tsx.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

vi.mock("../V3Badge", () => ({
  default: ({
    children,
    color,
  }: {
    children: React.ReactNode;
    color: string;
  }) => createElement("span", { "data-testid": `badge-${color}` }, children),
}));

// Mock sub-components to isolate TaskDetail's own rendering
vi.mock("../taskList/SpecLinksSection", () => ({
  default: () => createElement("div", { "data-testid": "spec-links" }),
}));
vi.mock("../taskList/DependenciesSection", () => ({
  default: () => createElement("div", { "data-testid": "dependencies" }),
}));
vi.mock("../taskList/EvidenceLedger", () => ({
  default: () => createElement("div", { "data-testid": "evidence-ledger" }),
}));
vi.mock("../taskList/GateStatus", () => ({
  default: () => createElement("div", { "data-testid": "gate-status" }),
}));
vi.mock("../taskList/CommentsSection", () => ({
  default: () => createElement("div", { "data-testid": "comments" }),
}));
vi.mock("../taskList/TaskActionsBar", () => ({
  default: () => createElement("div", { "data-testid": "actions-bar" }),
}));
vi.mock("../taskList/TaskPropertyControls", () => ({
  default: () => createElement("div", { "data-testid": "property-controls" }),
}));

// Mock the useTaskDetail hook
const mockUseTaskDetail = vi.fn();
vi.mock("../taskList/useTaskDetail", () => ({
  useTaskDetail: (...args: unknown[]) => mockUseTaskDetail(...args),
}));

import TaskDetail from "../taskList/TaskDetail";
import type { TaskSnapshot } from "../api";

afterEach(cleanup);

function makeTask(overrides: Partial<TaskSnapshot> = {}): TaskSnapshot {
  return {
    id: "task-1",
    title: "Build auth",
    description: "Implement JWT authentication",
    successCriteria: "Users can log in and receive tokens",
    status: "Active",
    currentPhase: "Implementation",
    currentPlan: "",
    validationStatus: "",
    validationSummary: "",
    implementationStatus: "",
    implementationSummary: "",
    preferredRoles: [],
    createdAt: "2026-04-15T08:00:00Z",
    updatedAt: "2026-04-15T10:00:00Z",
    ...overrides,
  };
}

const defaultHookResult = {
  comments: [],
  commentsLoading: false,
  commentsError: false,
  fetchComments: vi.fn(),
  specLinks: [],
  specLinksLoading: false,
  dependsOn: [],
  dependedOnBy: [],
  depsLoading: false,
  evidence: [],
  evidenceLoading: false,
  evidenceLoaded: false,
  fetchEvidence: vi.fn(),
  gate: null,
  gateLoading: false,
  checkGates: vi.fn(),
  actions: [],
  actionPending: null,
  actionResult: null,
  reasonAction: null,
  reasonText: "",
  setReasonText: vi.fn(),
  handleAction: vi.fn(),
  cancelReason: vi.fn(),
  showAssignPicker: false,
  setShowAssignPicker: vi.fn(),
  assignPending: false,
  handleAssign: vi.fn(),
  canCheckGates: false,
  canAssign: false,
};

function renderDetail(
  taskOverrides: Partial<TaskSnapshot> = {},
  opts: { onViewRetros?: (id: string) => void } = {},
) {
  mockUseTaskDetail.mockReturnValue(defaultHookResult);
  const task = makeTask(taskOverrides);
  return render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(TaskDetail, {
        task,
        agents: [],
        onRefresh: vi.fn(),
        onViewRetros: opts.onViewRetros,
      }),
    ),
  );
}

describe("TaskDetail", () => {
  it("renders description", () => {
    renderDetail({ description: "Implement JWT authentication" });
    expect(screen.getByText("Description")).toBeInTheDocument();
    expect(screen.getByText("Implement JWT authentication")).toBeInTheDocument();
  });

  it("renders success criteria", () => {
    renderDetail({ successCriteria: "Users can log in" });
    expect(screen.getByText("Success Criteria")).toBeInTheDocument();
    expect(screen.getByText("Users can log in")).toBeInTheDocument();
  });

  it("renders priority badge", () => {
    renderDetail({ priority: "High" });
    expect(screen.getByText("High")).toBeInTheDocument();
  });

  it("does not render priority when not set", () => {
    renderDetail({ priority: undefined });
    expect(screen.queryByText(/Priority/)).not.toBeInTheDocument();
  });

  it("renders review info when present", () => {
    renderDetail({
      reviewRounds: 2,
      reviewerAgentId: "reviewer-1",
      mergeCommitSha: "abcdef1234567890",
    });
    expect(screen.getByText(/Review round 2/)).toBeInTheDocument();
    expect(screen.getByText(/reviewer-1/)).toBeInTheDocument();
    expect(screen.getByText(/abcdef12/)).toBeInTheDocument();
  });

  it("does not render review section when reviewRounds is 0", () => {
    renderDetail({ reviewRounds: 0 });
    expect(screen.queryByText(/Review round/)).not.toBeInTheDocument();
  });

  it("renders implementation summary", () => {
    renderDetail({ implementationSummary: "Added 3 endpoints" });
    expect(screen.getByText("Implementation")).toBeInTheDocument();
    expect(screen.getByText("Added 3 endpoints")).toBeInTheDocument();
  });

  it("renders validation summary", () => {
    renderDetail({ validationSummary: "All tests pass" });
    expect(screen.getByText("Validation")).toBeInTheDocument();
    expect(screen.getByText("All tests pass")).toBeInTheDocument();
  });

  it("renders tests created", () => {
    renderDetail({ testsCreated: ["AuthTest.cs", "TokenTest.cs"] });
    expect(screen.getByText("Tests Created")).toBeInTheDocument();
  });

  it("does not render tests section when empty", () => {
    renderDetail({ testsCreated: [] });
    expect(screen.queryByText("Tests Created")).not.toBeInTheDocument();
  });

  it("renders view retrospectives link", () => {
    const onViewRetros = vi.fn();
    renderDetail({}, { onViewRetros });
    expect(screen.getByText(/View retrospectives/)).toBeInTheDocument();
  });

  it("calls onViewRetros when clicked", async () => {
    const onViewRetros = vi.fn();
    renderDetail({ id: "my-task" }, { onViewRetros });
    await userEvent.click(screen.getByText(/View retrospectives/));
    expect(onViewRetros).toHaveBeenCalledWith("my-task");
  });

  it("renders all sub-section placeholders", () => {
    renderDetail();
    expect(screen.getByTestId("spec-links")).toBeInTheDocument();
    expect(screen.getByTestId("dependencies")).toBeInTheDocument();
    expect(screen.getByTestId("evidence-ledger")).toBeInTheDocument();
    expect(screen.getByTestId("comments")).toBeInTheDocument();
    expect(screen.getByTestId("property-controls")).toBeInTheDocument();
    expect(screen.getByTestId("actions-bar")).toBeInTheDocument();
  });
});
