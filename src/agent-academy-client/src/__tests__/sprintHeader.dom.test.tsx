// @vitest-environment jsdom
import "@testing-library/jest-dom/vitest";
import { describe, expect, it, vi, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent } from "@testing-library/react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";
import SprintHeader from "../sprint/SprintHeader";
import type { SprintDetailResponse, SprintStage } from "../api";

type SprintHeaderProps = React.ComponentProps<typeof SprintHeader>;

function makeDetail(overrides: {
  currentStage?: SprintStage;
  status?: "Active" | "Completed" | "Cancelled";
  awaitingSignOff?: boolean;
  pendingStage?: SprintStage | null;
  completedAt?: string | null;
} = {}): SprintDetailResponse {
  return {
    sprint: {
      id: "sprint-1",
      number: 3,
      status: overrides.status ?? "Active",
      currentStage: overrides.currentStage ?? "Planning",
      overflowFromSprintId: null,
      awaitingSignOff: overrides.awaitingSignOff ?? false,
      pendingStage: overrides.pendingStage ?? null,
      signOffRequestedAt: null,
      createdAt: "2026-04-10T12:00:00Z",
      completedAt: overrides.completedAt ?? null,
    },
    artifacts: [],
    stages: [],
  };
}

const defaultProps = {
  detail: makeDetail(),
  hasActiveSprint: true,
  actionBusy: false,
  onRefresh: vi.fn(),
  onStartSprint: vi.fn(),
  onAdvanceSprint: vi.fn(),
  onCompleteSprint: vi.fn(),
  onCancelSprint: vi.fn(),
  onApproveAdvance: vi.fn(),
  onRejectAdvance: vi.fn(),
};

function renderHeader(overrides: Partial<SprintHeaderProps> = {}) {
  return render(
    <FluentProvider theme={webDarkTheme}>
      <SprintHeader {...defaultProps} {...overrides} />
    </FluentProvider>,
  );
}

describe("SprintHeader", () => {
  afterEach(() => { cleanup(); document.body.innerHTML = ""; });

  // ── Title and status ──

  it("renders sprint number and status badge", () => {
    renderHeader();
    expect(screen.getByText("Sprint #3")).toBeInTheDocument();
    expect(screen.getByText("Active")).toBeInTheDocument();
  });

  it("renders 'Sprints' when no detail", () => {
    renderHeader({ detail: null });
    expect(screen.getByText("Sprints")).toBeInTheDocument();
  });

  it("shows completed time when sprint is completed", () => {
    renderHeader({
      detail: makeDetail({ status: "Completed", completedAt: "2026-04-11T12:00:00Z" }),
      hasActiveSprint: false,
    });
    expect(screen.getByText("Completed")).toBeInTheDocument();
    expect(screen.getByText(/finished/)).toBeInTheDocument();
  });

  // ── Start Sprint button ──

  it("shows Start Sprint when no active sprint", () => {
    renderHeader({ hasActiveSprint: false });
    expect(screen.getByText("Start Sprint")).toBeInTheDocument();
  });

  it("hides Start Sprint when sprint is active", () => {
    renderHeader({ hasActiveSprint: true });
    expect(screen.queryByText("Start Sprint")).not.toBeInTheDocument();
  });

  it("calls onStartSprint on click", () => {
    const fn = vi.fn();
    renderHeader({ hasActiveSprint: false, onStartSprint: fn });
    fireEvent.click(screen.getByText("Start Sprint"));
    expect(fn).toHaveBeenCalledTimes(1);
  });

  // ── Advance Stage button ──

  it("shows Advance Stage for active non-final sprint", () => {
    renderHeader({ detail: makeDetail({ currentStage: "Planning", status: "Active" }) });
    expect(screen.getByText("Advance Stage")).toBeInTheDocument();
  });

  it("hides Advance Stage at FinalSynthesis", () => {
    renderHeader({ detail: makeDetail({ currentStage: "FinalSynthesis", status: "Active" }) });
    expect(screen.queryByText("Advance Stage")).not.toBeInTheDocument();
  });

  it("hides Advance Stage when awaiting sign-off", () => {
    renderHeader({ detail: makeDetail({ awaitingSignOff: true, pendingStage: "Discussion" }) });
    expect(screen.queryByText("Advance Stage")).not.toBeInTheDocument();
  });

  it("calls onAdvanceSprint on click", () => {
    const fn = vi.fn();
    renderHeader({ onAdvanceSprint: fn });
    fireEvent.click(screen.getByText("Advance Stage"));
    expect(fn).toHaveBeenCalledTimes(1);
  });

  // ── Sign-off buttons ──

  it("shows Approve and Reject when awaiting sign-off", () => {
    renderHeader({ detail: makeDetail({ awaitingSignOff: true, pendingStage: "Discussion" }) });
    expect(screen.getByText(/Approve → Discussion/)).toBeInTheDocument();
    expect(screen.getByText("✗ Reject")).toBeInTheDocument();
  });

  it("calls onApproveAdvance on Approve click", () => {
    const fn = vi.fn();
    renderHeader({ detail: makeDetail({ awaitingSignOff: true, pendingStage: "Discussion" }), onApproveAdvance: fn });
    fireEvent.click(screen.getByText(/Approve/));
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it("calls onRejectAdvance on Reject click", () => {
    const fn = vi.fn();
    renderHeader({ detail: makeDetail({ awaitingSignOff: true, pendingStage: "Discussion" }), onRejectAdvance: fn });
    fireEvent.click(screen.getByText("✗ Reject"));
    expect(fn).toHaveBeenCalledTimes(1);
  });

  // ── Complete Sprint button ──

  it("shows Complete Sprint at FinalSynthesis", () => {
    renderHeader({ detail: makeDetail({ currentStage: "FinalSynthesis", status: "Active" }) });
    expect(screen.getByText("Complete Sprint")).toBeInTheDocument();
  });

  it("calls onCompleteSprint on click", () => {
    const fn = vi.fn();
    renderHeader({ detail: makeDetail({ currentStage: "FinalSynthesis", status: "Active" }), onCompleteSprint: fn });
    fireEvent.click(screen.getByText("Complete Sprint"));
    expect(fn).toHaveBeenCalledTimes(1);
  });

  // ── Cancel button ──

  it("shows Cancel for active sprint", () => {
    renderHeader();
    expect(screen.getByText("Cancel")).toBeInTheDocument();
  });

  it("calls onCancelSprint on click", () => {
    const fn = vi.fn();
    renderHeader({ onCancelSprint: fn });
    fireEvent.click(screen.getByText("Cancel"));
    expect(fn).toHaveBeenCalledTimes(1);
  });

  // ── Disabled state ──

  it("disables action buttons when actionBusy is true", () => {
    renderHeader({ actionBusy: true });
    expect(screen.getByText("Advance Stage").closest("button")).toBeDisabled();
    expect(screen.getByText("Cancel").closest("button")).toBeDisabled();
  });
});
