// @vitest-environment jsdom
import "@testing-library/jest-dom/vitest";
import { describe, expect, it, afterEach } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import SprintMetricsBar from "../sprint/SprintMetricsBar";
import type { SprintMetricsResult, StageMetrics } from "../sprint/sprintMetrics";

function makeMetrics(overrides: Partial<SprintMetricsResult> = {}): SprintMetricsResult {
  return {
    stages: [],
    totalWords: 1500,
    totalDurationMs: 7200000, // 2h
    ...overrides,
  };
}

function makeStageMetrics(overrides: Partial<StageMetrics> = {}): StageMetrics {
  return {
    stage: "Planning",
    durationMs: 1800000, // 30m
    artifactCount: 2,
    totalWords: 400,
    ...overrides,
  };
}

function renderBar(props: {
  metrics?: SprintMetricsResult;
  activeStageMetrics?: StageMetrics | null;
  totalArtifacts?: number;
} = {}) {
  return render(
    <FluentProvider theme={webDarkTheme}>
      <SprintMetricsBar
        metrics={props.metrics ?? makeMetrics()}
        activeStageMetrics={props.activeStageMetrics ?? null}
        totalArtifacts={props.totalArtifacts ?? 5}
      />
    </FluentProvider>,
  );
}

describe("SprintMetricsBar", () => {
  afterEach(() => { cleanup(); document.body.innerHTML = ""; });

  // ── Total duration ──

  it("renders total duration", () => {
    renderBar({ metrics: makeMetrics({ totalDurationMs: 7200000 }) });
    expect(screen.getByText("2h 0m")).toBeInTheDocument();
    expect(screen.getByText("total")).toBeInTheDocument();
  });

  // ── Total words ──

  it("renders total word count", () => {
    renderBar({ metrics: makeMetrics({ totalWords: 1500 }) });
    expect(screen.getByText("1,500")).toBeInTheDocument();
    expect(screen.getByText("words")).toBeInTheDocument();
  });

  // ── Artifact count ──

  it("renders artifact count with plural", () => {
    renderBar({ totalArtifacts: 5 });
    expect(screen.getByText("5")).toBeInTheDocument();
    expect(screen.getByText("artifacts")).toBeInTheDocument();
  });

  it("renders artifact count with singular", () => {
    renderBar({ totalArtifacts: 1 });
    expect(screen.getByText("1")).toBeInTheDocument();
    expect(screen.getByText("artifact")).toBeInTheDocument();
  });

  // ── Active stage metrics ──

  it("shows active stage duration and word count when provided", () => {
    renderBar({
      activeStageMetrics: makeStageMetrics({ stage: "Planning", durationMs: 1800000, totalWords: 400 }),
    });
    expect(screen.getByText("30m")).toBeInTheDocument();
    expect(screen.getByText("planning")).toBeInTheDocument();
    expect(screen.getByText("400")).toBeInTheDocument();
    expect(screen.getByText("in planning")).toBeInTheDocument();
  });

  it("hides active stage section when no activeStageMetrics", () => {
    renderBar({ activeStageMetrics: null });
    expect(screen.queryByText("planning")).not.toBeInTheDocument();
  });

  it("hides active stage word count when 0", () => {
    renderBar({
      activeStageMetrics: makeStageMetrics({ stage: "Intake", durationMs: 600000, totalWords: 0 }),
    });
    expect(screen.getByText("10m")).toBeInTheDocument();
    expect(screen.queryByText("in intake")).not.toBeInTheDocument();
  });

  // ── Short duration formatting ──

  it("renders '<1m' for very short durations", () => {
    renderBar({ metrics: makeMetrics({ totalDurationMs: 30000 }) });
    expect(screen.getByText("<1m")).toBeInTheDocument();
  });

  // ── Day-level durations ──

  it("renders days for long sprints", () => {
    renderBar({ metrics: makeMetrics({ totalDurationMs: 90000000 }) }); // 25h
    expect(screen.getByText("1d 1h")).toBeInTheDocument();
  });
});
