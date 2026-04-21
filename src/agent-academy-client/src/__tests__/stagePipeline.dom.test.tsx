// @vitest-environment jsdom
import "@testing-library/jest-dom/vitest";
import { describe, expect, it, vi, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent } from "@testing-library/react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import StagePipeline from "../sprint/StagePipeline";
import type { SprintDetailResponse, SprintStage } from "../api";
import type { StageMetrics } from "../sprint/sprintMetrics";

function makeDetail(overrides: {
  currentStage?: SprintStage;
  status?: "Active" | "Completed" | "Cancelled";
  artifacts?: SprintDetailResponse["artifacts"];
} = {}): SprintDetailResponse {
  return {
    sprint: {
      id: "sprint-1",
      number: 1,
      status: overrides.status ?? "Active",
      currentStage: overrides.currentStage ?? "Planning",
      overflowFromSprintId: null,
      awaitingSignOff: false,
      pendingStage: null,
      signOffRequestedAt: null,
      createdAt: "2026-04-10T12:00:00Z",
      completedAt: null,
    },
    artifacts: overrides.artifacts ?? [],
    stages: ["Intake", "Planning", "Discussion", "Validation", "Implementation", "FinalSynthesis"],
  };
}

function makeStageMetrics(stage: SprintStage, overrides: Partial<StageMetrics> = {}): StageMetrics {
  return {
    stage,
    durationMs: null,
    artifactCount: 0,
    totalWords: 0,
    ...overrides,
  };
}

function renderPipeline(props: {
  detail?: SprintDetailResponse;
  selectedStage?: SprintStage | null;
  stageMetrics?: StageMetrics[] | null;
  onSelectStage?: (stage: SprintStage) => void;
} = {}) {
  return render(
    <FluentProvider theme={webDarkTheme}>
      <StagePipeline
        detail={props.detail ?? makeDetail()}
        selectedStage={props.selectedStage ?? null}
        stageMetrics={props.stageMetrics ?? null}
        onSelectStage={props.onSelectStage ?? vi.fn()}
      />
    </FluentProvider>,
  );
}

describe("StagePipeline", () => {
  afterEach(() => { cleanup(); document.body.innerHTML = ""; });

  // ── Renders all 6 stages ──

  it("renders all 6 stage cards", () => {
    renderPipeline();
    expect(screen.getByText("Intake")).toBeInTheDocument();
    expect(screen.getByText(/Planning/)).toBeInTheDocument();
    expect(screen.getByText("Discussion")).toBeInTheDocument();
    expect(screen.getByText("Validation")).toBeInTheDocument();
    expect(screen.getByText("Implementation")).toBeInTheDocument();
    expect(screen.getByText("Final Synthesis")).toBeInTheDocument();
  });

  // ── Current stage indicator ──

  it("marks current stage with ● indicator", () => {
    renderPipeline({ detail: makeDetail({ currentStage: "Discussion" }) });
    expect(screen.getByText(/Discussion ●/)).toBeInTheDocument();
    // Other stages should NOT have the indicator
    expect(screen.queryByText(/Intake ●/)).not.toBeInTheDocument();
  });

  // ── Stage click callback ──

  it("calls onSelectStage when a stage is clicked", () => {
    const onSelect = vi.fn();
    renderPipeline({ onSelectStage: onSelect });
    fireEvent.click(screen.getByText("Intake"));
    expect(onSelect).toHaveBeenCalledWith("Intake");
  });

  it("calls onSelectStage on Enter keydown", () => {
    const onSelect = vi.fn();
    renderPipeline({ onSelectStage: onSelect });
    const card = screen.getByText("Validation").closest("[role='button']")!;
    fireEvent.keyDown(card, { key: "Enter" });
    expect(onSelect).toHaveBeenCalledWith("Validation");
  });

  // ── Artifact counts ──

  it("shows artifact count when stage has artifacts", () => {
    const detail = makeDetail({
      currentStage: "Implementation",
      artifacts: [
        { id: 1, sprintId: "sprint-1", stage: "Planning", type: "SprintPlan", content: "plan", createdByAgentId: null, createdAt: "2026-04-10T12:00:00Z", updatedAt: null },
        { id: 2, sprintId: "sprint-1", stage: "Planning", type: "RequirementsDocument", content: "reqs", createdByAgentId: null, createdAt: "2026-04-10T12:00:00Z", updatedAt: null },
      ],
    });
    renderPipeline({ detail });
    expect(screen.getByText("2 artifacts")).toBeInTheDocument();
  });

  it("shows singular 'artifact' for 1 artifact", () => {
    const detail = makeDetail({
      artifacts: [
        { id: 1, sprintId: "sprint-1", stage: "Intake", type: "RequirementsDocument", content: "reqs", createdByAgentId: null, createdAt: "2026-04-10T12:00:00Z", updatedAt: null },
      ],
    });
    renderPipeline({ detail });
    expect(screen.getByText("1 artifact")).toBeInTheDocument();
  });

  // ── Stage metrics display ──

  it("shows duration and word count from stage metrics", () => {
    const metrics = [
      makeStageMetrics("Intake", { durationMs: 3600000, totalWords: 500 }),
    ];
    renderPipeline({ stageMetrics: metrics });
    expect(screen.getByText(/1h 0m/)).toBeInTheDocument();
    expect(screen.getByText(/500w/)).toBeInTheDocument();
  });

  // ── Default descriptions for empty stages ──

  it("shows description for stages with no artifacts", () => {
    renderPipeline();
    expect(screen.getByText("Requirements gathering and scope definition")).toBeInTheDocument();
    expect(screen.getByText("Active development and task execution")).toBeInTheDocument();
  });
});
