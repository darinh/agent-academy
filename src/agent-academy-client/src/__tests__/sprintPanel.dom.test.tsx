// @vitest-environment jsdom
/**
 * Interactive RTL tests for SprintPanel.
 *
 * Uses @testing-library/react + jsdom.
 * Covers: initial data fetch, loading/error/empty states, stage pipeline clicks,
 * sprint lifecycle actions (start, advance, complete, cancel), sign-off
 * approve/reject, artifact expand/collapse, sprint history selection,
 * metrics bar, refresh button, and error feedback.
 */
import "@testing-library/jest-dom/vitest";
import {
  cleanup,
  render,
  screen,
  waitFor,
  act,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

const mockGetActiveSprint = vi.fn();
const mockGetSprints = vi.fn();
const mockGetSprintDetail = vi.fn();
const mockGetSprintArtifacts = vi.fn();
const mockStartSprint = vi.fn();
const mockAdvanceSprint = vi.fn();
const mockCompleteSprint = vi.fn();
const mockCancelSprint = vi.fn();
const mockApproveSprintAdvance = vi.fn();
const mockRejectSprintAdvance = vi.fn();

vi.mock("../api", () => ({
  getActiveSprint: (...args: unknown[]) => mockGetActiveSprint(...args),
  getSprints: (...args: unknown[]) => mockGetSprints(...args),
  getSprintDetail: (...args: unknown[]) => mockGetSprintDetail(...args),
  getSprintArtifacts: (...args: unknown[]) => mockGetSprintArtifacts(...args),
  startSprint: (...args: unknown[]) => mockStartSprint(...args),
  advanceSprint: (...args: unknown[]) => mockAdvanceSprint(...args),
  completeSprint: (...args: unknown[]) => mockCompleteSprint(...args),
  cancelSprint: (...args: unknown[]) => mockCancelSprint(...args),
  approveSprintAdvance: (...args: unknown[]) => mockApproveSprintAdvance(...args),
  rejectSprintAdvance: (...args: unknown[]) => mockRejectSprintAdvance(...args),
}));

vi.mock("react-markdown", () => ({
  default: ({ children }: { children?: string }) =>
    createElement("div", { "data-testid": "markdown" }, children),
}));

vi.mock("remark-gfm", () => ({ default: () => {} }));

vi.mock("../V3Badge", () => ({
  default: ({
    children,
    color,
  }: {
    children: React.ReactNode;
    color: string;
  }) => createElement("span", { "data-testid": `badge-${color}` }, children),
}));

vi.mock("../EmptyState", () => ({
  default: ({
    title,
    detail,
    action,
  }: {
    icon?: React.ReactNode;
    title: string;
    detail?: string;
    action?: { label: string; onClick: () => void };
  }) =>
    createElement(
      "div",
      { "data-testid": "empty-state" },
      createElement("span", null, title),
      detail && createElement("span", null, detail),
      action &&
        createElement("button", { onClick: action.onClick }, action.label),
    ),
}));

vi.mock("../ErrorState", () => ({
  default: ({
    message,
    onRetry,
  }: {
    message: string;
    detail?: string;
    onRetry?: () => void;
  }) =>
    createElement(
      "div",
      { "data-testid": "error-state" },
      createElement("span", null, message),
      onRetry && createElement("button", { onClick: onRetry }, "Retry"),
    ),
}));

vi.mock("../SkeletonLoader", () => ({
  default: ({ rows }: { rows: number }) =>
    createElement("div", { "data-testid": "skeleton-loader" }, `Loading ${rows} rows`),
}));

import SprintPanel from "../SprintPanel";
import type {
  SprintSnapshot,
  SprintArtifact,
  SprintDetailResponse,
  SprintStage,
} from "../api";

// ── Factories ──────────────────────────────────────────────────────────

function makeSprint(overrides: Partial<SprintSnapshot> = {}): SprintSnapshot {
  return {
    id: "sprint-1",
    number: 1,
    status: "Active",
    currentStage: "Intake",
    overflowFromSprintId: null,
    awaitingSignOff: false,
    pendingStage: null,
    signOffRequestedAt: null,
    createdAt: "2026-04-01T00:00:00Z",
    completedAt: null,
    ...overrides,
  };
}

function makeArtifact(overrides: Partial<SprintArtifact> = {}): SprintArtifact {
  return {
    id: 1,
    sprintId: "sprint-1",
    stage: "Intake",
    type: "RequirementsDocument",
    content: "Some artifact content here",
    createdByAgentId: "architect",
    createdAt: "2026-04-01T01:00:00Z",
    updatedAt: null,
    ...overrides,
  };
}

function makeDetail(
  sprintOverrides: Partial<SprintSnapshot> = {},
  artifacts: SprintArtifact[] = [],
): SprintDetailResponse {
  return {
    sprint: makeSprint(sprintOverrides),
    artifacts,
    stages: [
      "Intake",
      "Planning",
      "Discussion",
      "Validation",
      "Implementation",
      "FinalSynthesis",
    ] as SprintStage[],
  };
}

// ── Render helper ──────────────────────────────────────────────────────

function renderPanel(props: Partial<Parameters<typeof SprintPanel>[0]> = {}) {
  return render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(SprintPanel, {
        sprintVersion: 0,
        lastSprintEvent: null,
        ...props,
      }),
    ),
  );
}

// Default mock setup: active sprint at Intake with one artifact
function setupActiveSprint(
  sprintOverrides: Partial<SprintSnapshot> = {},
  artifacts: SprintArtifact[] = [makeArtifact()],
) {
  const detail = makeDetail(sprintOverrides, artifacts);
  mockGetActiveSprint.mockResolvedValue(detail);
  mockGetSprints.mockResolvedValue({
    sprints: [detail.sprint],
    total: 1,
  });
  return detail;
}

function setupNoSprints() {
  mockGetActiveSprint.mockResolvedValue(null);
  mockGetSprints.mockResolvedValue({ sprints: [], total: 0 });
}

// ── Setup / Teardown ───────────────────────────────────────────────────

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  setupActiveSprint();
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  mockGetActiveSprint.mockReset();
  mockGetSprints.mockReset();
  mockGetSprintDetail.mockReset();
  mockGetSprintArtifacts.mockReset();
  mockStartSprint.mockReset();
  mockAdvanceSprint.mockReset();
  mockCompleteSprint.mockReset();
  mockCancelSprint.mockReset();
  mockApproveSprintAdvance.mockReset();
  mockRejectSprintAdvance.mockReset();
  vi.restoreAllMocks();
});

// ── Tests ──────────────────────────────────────────────────────────────

describe("SprintPanel (interactive)", () => {
  // ── Loading / Error / Empty ────────────────────────────────────────

  describe("loading, error, and empty states", () => {
    it("shows skeleton loader initially", async () => {
      // Defer resolution so we catch loading state
      mockGetActiveSprint.mockReturnValue(new Promise(() => {}));
      mockGetSprints.mockReturnValue(new Promise(() => {}));

      renderPanel();
      expect(screen.getByTestId("skeleton-loader")).toBeInTheDocument();
    });

    it("shows error state when fetch fails", async () => {
      mockGetActiveSprint.mockRejectedValue(new Error("Server down"));
      mockGetSprints.mockRejectedValue(new Error("Server down"));

      renderPanel();
      await waitFor(() => {
        expect(screen.getByTestId("error-state")).toBeInTheDocument();
      });
      expect(screen.getByText("Server down")).toBeInTheDocument();
    });

    it("shows empty state with Start Sprint button when no sprints", async () => {
      setupNoSprints();
      renderPanel();

      await waitFor(() => {
        expect(screen.getByTestId("empty-state")).toBeInTheDocument();
      });
      expect(screen.getByText("No sprints yet")).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Start Sprint" })).toBeInTheDocument();
    });

    it("Start Sprint in empty state calls startSprint API", async () => {
      setupNoSprints();
      mockStartSprint.mockResolvedValue(undefined);
      // After starting, return a new sprint
      const newDetail = makeDetail({ id: "sprint-new", number: 1 });
      let callCount = 0;
      mockGetActiveSprint.mockImplementation(() => {
        callCount++;
        if (callCount <= 1) return Promise.resolve(null);
        return Promise.resolve(newDetail);
      });
      mockGetSprints.mockImplementation(() => {
        if (callCount <= 1) return Promise.resolve({ sprints: [], total: 0 });
        return Promise.resolve({ sprints: [newDetail.sprint], total: 1 });
      });

      renderPanel();
      await waitFor(() =>
        expect(screen.getByRole("button", { name: "Start Sprint" })).toBeInTheDocument(),
      );

      await userEvent.click(screen.getByRole("button", { name: "Start Sprint" }));
      await waitFor(() => {
        expect(mockStartSprint).toHaveBeenCalledTimes(1);
      });
    });
  });

  // ── Active Sprint Display ──────────────────────────────────────────

  describe("active sprint display", () => {
    it("renders sprint header with number and status badge", async () => {
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Sprint #1")).toBeInTheDocument();
      });
      expect(screen.getByText("Active")).toBeInTheDocument();
    });

    it("renders all six stage cards in pipeline", async () => {
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Sprint #1")).toBeInTheDocument();
      });

      // Stage cards have role="button" — 6 stage cards in the pipeline
      const stageButtons = screen.getAllByRole("button").filter((btn) => {
        const text = btn.textContent ?? "";
        return /Intake|Planning|Discussion|Validation|Implementation|Final Synthesis/.test(text);
      });
      // Should have at least 6 stage card buttons
      expect(stageButtons.length).toBeGreaterThanOrEqual(6);
    });

    it("marks current stage with ●", async () => {
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/Intake ●/)).toBeInTheDocument();
      });
    });

    it("shows metrics bar with word count and artifact count", async () => {
      setupActiveSprint({}, [
        makeArtifact({ content: "word one two three four" }),
      ]);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Sprint #1")).toBeInTheDocument();
      });
      // Metrics bar has labels "words" and "artifact(s)"
      expect(screen.getByText("words")).toBeInTheDocument();
      // "1" artifact, plus label "artifact" (singular)
      const artifactLabels = screen.getAllByText(/artifact/);
      expect(artifactLabels.length).toBeGreaterThanOrEqual(1);
    });
  });

  // ── Stage Pipeline Clicks ──────────────────────────────────────────

  describe("stage pipeline interactions", () => {
    it("clicking a stage selects it and shows its artifacts section", async () => {
      setupActiveSprint({}, [
        makeArtifact({ stage: "Intake", content: "Intake content" }),
      ]);
      renderPanel();
      await waitFor(() => expect(screen.getByText("Sprint #1")).toBeInTheDocument());

      // Intake is auto-selected as current stage
      await waitFor(() => {
        expect(screen.getByText(/Intake artifacts/)).toBeInTheDocument();
      });
    });

    it("clicking a different stage shows that stage's artifacts", async () => {
      setupActiveSprint({ currentStage: "Implementation" }, [
        makeArtifact({ stage: "Intake", content: "Intake stuff" }),
        makeArtifact({ id: 2, stage: "Planning", content: "Planning stuff", type: "SprintPlan" }),
      ]);
      renderPanel();
      await waitFor(() => expect(screen.getByText("Sprint #1")).toBeInTheDocument());

      // Click Planning stage
      await userEvent.click(screen.getByText(/Planning/));
      await waitFor(() => {
        expect(screen.getByText(/Planning artifacts/)).toBeInTheDocument();
      });
      expect(screen.getByText("Planning stuff")).toBeInTheDocument();
    });

    it("shows 'No artifacts for this stage yet' for empty stage", async () => {
      setupActiveSprint({ currentStage: "Intake" }, []);
      renderPanel();
      await waitFor(() => expect(screen.getByText("Sprint #1")).toBeInTheDocument());

      // Intake is current and auto-selected — no artifacts
      await waitFor(() => {
        expect(screen.getByText("No artifacts for this stage yet")).toBeInTheDocument();
      });
    });
  });

  // ── Artifact Expand/Collapse ───────────────────────────────────────

  describe("artifact expand/collapse", () => {
    it("shows truncated content with 'Show full content' for long artifacts", async () => {
      const longContent = "A ".repeat(150); // > 200 chars
      setupActiveSprint({}, [
        makeArtifact({ content: longContent }),
      ]);
      renderPanel();
      await waitFor(() => expect(screen.getByText("Sprint #1")).toBeInTheDocument());

      await waitFor(() => {
        expect(screen.getByText("Show full content")).toBeInTheDocument();
      });
    });

    it("clicking 'Show full content' expands the artifact", async () => {
      const longContent = "A ".repeat(150);
      setupActiveSprint({}, [
        makeArtifact({ content: longContent }),
      ]);
      renderPanel();
      await waitFor(() => expect(screen.getByText("Show full content")).toBeInTheDocument());

      await userEvent.click(screen.getByText("Show full content"));
      await waitFor(() => {
        expect(screen.getByText("Collapse")).toBeInTheDocument();
      });
    });

    it("clicking 'Collapse' hides the full content", async () => {
      const longContent = "A ".repeat(150);
      setupActiveSprint({}, [
        makeArtifact({ content: longContent }),
      ]);
      renderPanel();
      await waitFor(() => expect(screen.getByText("Show full content")).toBeInTheDocument());

      await userEvent.click(screen.getByText("Show full content"));
      await waitFor(() => expect(screen.getByText("Collapse")).toBeInTheDocument());

      await userEvent.click(screen.getByText("Collapse"));
      await waitFor(() => {
        expect(screen.getByText("Show full content")).toBeInTheDocument();
      });
    });

    it("shows full content directly for short artifacts (no toggle)", async () => {
      setupActiveSprint({}, [
        makeArtifact({ content: "Short content" }),
      ]);
      renderPanel();
      await waitFor(() => expect(screen.getByText("Sprint #1")).toBeInTheDocument());

      await waitFor(() => {
        expect(screen.getByText("Short content")).toBeInTheDocument();
      });
      expect(screen.queryByText("Show full content")).not.toBeInTheDocument();
      expect(screen.queryByText("Collapse")).not.toBeInTheDocument();
    });

    it("shows artifact type label and agent badge", async () => {
      setupActiveSprint({}, [
        makeArtifact({ type: "RequirementsDocument", createdByAgentId: "architect" }),
      ]);
      renderPanel();
      await waitFor(() => expect(screen.getByText("Sprint #1")).toBeInTheDocument());

      await waitFor(() => {
        expect(screen.getByText("Requirements Document")).toBeInTheDocument();
        expect(screen.getByText("architect")).toBeInTheDocument();
      });
    });
  });

  // ── Sprint Lifecycle Actions ───────────────────────────────────────

  describe("sprint lifecycle actions", () => {
    it("shows Advance Stage button for active non-final sprint", async () => {
      setupActiveSprint({ currentStage: "Intake" });
      renderPanel();
      await waitFor(() => expect(screen.getByText("Sprint #1")).toBeInTheDocument());

      expect(screen.getByRole("button", { name: "Advance Stage" })).toBeInTheDocument();
    });

    it("clicking Advance Stage calls advanceSprint API", async () => {
      setupActiveSprint({ currentStage: "Intake" });
      mockAdvanceSprint.mockResolvedValue(undefined);
      renderPanel();
      await waitFor(() => expect(screen.getByRole("button", { name: "Advance Stage" })).toBeInTheDocument());

      await userEvent.click(screen.getByRole("button", { name: "Advance Stage" }));
      await waitFor(() => {
        expect(mockAdvanceSprint).toHaveBeenCalledWith("sprint-1");
      });
    });

    it("shows Complete Sprint button for final stage", async () => {
      setupActiveSprint({ currentStage: "FinalSynthesis" });
      renderPanel();
      await waitFor(() => expect(screen.getByText("Sprint #1")).toBeInTheDocument());

      expect(screen.getByRole("button", { name: "Complete Sprint" })).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "Advance Stage" })).not.toBeInTheDocument();
    });

    it("clicking Complete Sprint calls completeSprint API", async () => {
      setupActiveSprint({ currentStage: "FinalSynthesis" });
      mockCompleteSprint.mockResolvedValue(undefined);
      renderPanel();
      await waitFor(() => expect(screen.getByRole("button", { name: "Complete Sprint" })).toBeInTheDocument());

      await userEvent.click(screen.getByRole("button", { name: "Complete Sprint" }));
      await waitFor(() => {
        expect(mockCompleteSprint).toHaveBeenCalledWith("sprint-1");
      });
    });

    it("shows Cancel button for active sprint", async () => {
      setupActiveSprint();
      renderPanel();
      await waitFor(() => expect(screen.getByText("Sprint #1")).toBeInTheDocument());

      expect(screen.getByRole("button", { name: "Cancel" })).toBeInTheDocument();
    });

    it("clicking Cancel calls cancelSprint API", async () => {
      setupActiveSprint();
      mockCancelSprint.mockResolvedValue(undefined);
      renderPanel();
      await waitFor(() => expect(screen.getByRole("button", { name: "Cancel" })).toBeInTheDocument());

      await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
      await waitFor(() => {
        expect(mockCancelSprint).toHaveBeenCalledWith("sprint-1");
      });
    });

    it("shows error when advance fails", async () => {
      setupActiveSprint();
      mockAdvanceSprint.mockRejectedValue(new Error("Failed to advance sprint"));
      renderPanel();
      await waitFor(() => expect(screen.getByRole("button", { name: "Advance Stage" })).toBeInTheDocument());

      await userEvent.click(screen.getByRole("button", { name: "Advance Stage" }));
      await waitFor(() => {
        expect(screen.getByText("Failed to advance sprint")).toBeInTheDocument();
      });
    });

    it("shows Start Sprint when no active sprint but history exists", async () => {
      const completedSprint = makeSprint({ id: "s-old", number: 1, status: "Completed", completedAt: "2026-04-01T12:00:00Z" });
      const completedDetail = makeDetail({ id: "s-old", number: 1, status: "Completed", completedAt: "2026-04-01T12:00:00Z" });
      mockGetActiveSprint.mockResolvedValue(null);
      mockGetSprints.mockResolvedValue({ sprints: [completedSprint], total: 1 });
      mockGetSprintDetail.mockResolvedValue(completedDetail);

      renderPanel();
      await waitFor(() => {
        expect(screen.getByRole("button", { name: "Start Sprint" })).toBeInTheDocument();
      });
    });
  });

  // ── Sign-off Gate ──────────────────────────────────────────────────

  describe("sign-off gate", () => {
    it("shows sign-off banner when awaitingSignOff is true", async () => {
      setupActiveSprint({
        awaitingSignOff: true,
        pendingStage: "Planning",
        currentStage: "Intake",
      });
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText(/User sign-off required/)).toBeInTheDocument();
      });
      // Banner mentions transitioning from current to pending stage
      expect(screen.getByText(/agents want to advance from/)).toBeInTheDocument();
    });

    it("shows Approve and Reject buttons during sign-off", async () => {
      setupActiveSprint({
        awaitingSignOff: true,
        pendingStage: "Planning",
        currentStage: "Intake",
      });
      renderPanel();
      await waitFor(() => {
        expect(screen.getByRole("button", { name: /Approve/})).toBeInTheDocument();
        expect(screen.getByRole("button", { name: /Reject/ })).toBeInTheDocument();
      });
    });

    it("does not show Advance Stage during sign-off", async () => {
      setupActiveSprint({
        awaitingSignOff: true,
        pendingStage: "Planning",
        currentStage: "Intake",
      });
      renderPanel();
      await waitFor(() => expect(screen.getByText("Sprint #1")).toBeInTheDocument());

      expect(screen.queryByRole("button", { name: "Advance Stage" })).not.toBeInTheDocument();
    });

    it("clicking Approve calls approveSprintAdvance API", async () => {
      setupActiveSprint({
        awaitingSignOff: true,
        pendingStage: "Planning",
        currentStage: "Intake",
      });
      mockApproveSprintAdvance.mockResolvedValue(undefined);
      renderPanel();
      await waitFor(() => expect(screen.getByRole("button", { name: /Approve/ })).toBeInTheDocument());

      await userEvent.click(screen.getByRole("button", { name: /Approve/ }));
      await waitFor(() => {
        expect(mockApproveSprintAdvance).toHaveBeenCalledWith("sprint-1");
      });
    });

    it("clicking Reject calls rejectSprintAdvance API", async () => {
      setupActiveSprint({
        awaitingSignOff: true,
        pendingStage: "Planning",
        currentStage: "Intake",
      });
      mockRejectSprintAdvance.mockResolvedValue(undefined);
      renderPanel();
      await waitFor(() => expect(screen.getByRole("button", { name: /Reject/ })).toBeInTheDocument());

      await userEvent.click(screen.getByRole("button", { name: /Reject/ }));
      await waitFor(() => {
        expect(mockRejectSprintAdvance).toHaveBeenCalledWith("sprint-1");
      });
    });
  });

  // ── Sprint History ─────────────────────────────────────────────────

  describe("sprint history", () => {
    it("shows Sprint History section when multiple sprints exist", async () => {
      const sprint1 = makeSprint({ id: "s1", number: 1, status: "Completed", completedAt: "2026-04-01T12:00:00Z" });
      const sprint2 = makeSprint({ id: "s2", number: 2, status: "Active" });
      const detail2 = makeDetail({ id: "s2", number: 2, status: "Active" });
      mockGetActiveSprint.mockResolvedValue(detail2);
      mockGetSprints.mockResolvedValue({ sprints: [sprint2, sprint1], total: 2 });

      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Sprint History")).toBeInTheDocument();
      });
      expect(screen.getByText("#1")).toBeInTheDocument();
      expect(screen.getByText("#2")).toBeInTheDocument();
    });

    it("does not show Sprint History section with only one sprint", async () => {
      renderPanel();
      await waitFor(() => expect(screen.getByText("Sprint #1")).toBeInTheDocument());

      expect(screen.queryByText("Sprint History")).not.toBeInTheDocument();
    });

    it("clicking a history item loads that sprint's detail", async () => {
      const sprint1 = makeSprint({ id: "s1", number: 1, status: "Completed", completedAt: "2026-04-01T12:00:00Z" });
      const sprint2 = makeSprint({ id: "s2", number: 2, status: "Active" });
      const detail2 = makeDetail({ id: "s2", number: 2, status: "Active" });
      const detail1 = makeDetail({ id: "s1", number: 1, status: "Completed", completedAt: "2026-04-01T12:00:00Z" });
      mockGetActiveSprint.mockResolvedValue(detail2);
      mockGetSprints.mockResolvedValue({ sprints: [sprint2, sprint1], total: 2 });
      mockGetSprintDetail.mockResolvedValue(detail1);

      renderPanel();
      await waitFor(() => expect(screen.getByText("Sprint History")).toBeInTheDocument());

      // Click sprint #1 in history
      await userEvent.click(screen.getByText("#1"));
      await waitFor(() => {
        expect(mockGetSprintDetail).toHaveBeenCalledWith("s1");
      });
    });
  });

  // ── Completed Sprint Display ───────────────────────────────────────

  describe("completed sprint display", () => {
    it("shows Completed status badge", async () => {
      const completedDetail = makeDetail({ status: "Completed", completedAt: "2026-04-01T12:00:00Z" });
      mockGetActiveSprint.mockResolvedValue(completedDetail);
      mockGetSprints.mockResolvedValue({ sprints: [completedDetail.sprint], total: 1 });

      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Completed")).toBeInTheDocument();
      });
    });

    it("does not show lifecycle action buttons for completed sprint", async () => {
      const completedDetail = makeDetail({ status: "Completed", completedAt: "2026-04-01T12:00:00Z" });
      mockGetActiveSprint.mockResolvedValue(completedDetail);
      mockGetSprints.mockResolvedValue({ sprints: [completedDetail.sprint], total: 1 });

      renderPanel();
      await waitFor(() => expect(screen.getByText("Sprint #1")).toBeInTheDocument());

      expect(screen.queryByRole("button", { name: "Advance Stage" })).not.toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "Complete Sprint" })).not.toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "Cancel" })).not.toBeInTheDocument();
    });
  });

  // ── Cancelled Sprint Display ───────────────────────────────────────

  describe("cancelled sprint display", () => {
    it("shows Cancelled status badge", async () => {
      const cancelledDetail = makeDetail({ status: "Cancelled", completedAt: "2026-04-01T12:00:00Z" });
      mockGetActiveSprint.mockResolvedValue(cancelledDetail);
      mockGetSprints.mockResolvedValue({ sprints: [cancelledDetail.sprint], total: 1 });

      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Cancelled")).toBeInTheDocument();
      });
    });
  });

  // ── Edge Cases ─────────────────────────────────────────────────────

  describe("edge cases", () => {
    it("handles sprint with zero artifacts without crashing", async () => {
      setupActiveSprint({}, []);
      renderPanel();
      await waitFor(() => {
        expect(screen.getByText("Sprint #1")).toBeInTheDocument();
      });
    });

    it("handles multiple artifacts across stages", async () => {
      setupActiveSprint({ currentStage: "Implementation" }, [
        makeArtifact({ id: 1, stage: "Intake", content: "Intake doc" }),
        makeArtifact({ id: 2, stage: "Planning", content: "Plan doc", type: "SprintPlan" }),
        makeArtifact({ id: 3, stage: "Implementation", content: "Code artifact", type: "CodeChange" }),
      ]);
      renderPanel();
      await waitFor(() => expect(screen.getByText("Sprint #1")).toBeInTheDocument());

      // Default selected stage is Implementation (current)
      await waitFor(() => {
        expect(screen.getByText(/Implementation artifacts/)).toBeInTheDocument();
      });
    });
  });
});
