// @vitest-environment jsdom
/**
 * DOM tests for GateStatus.
 *
 * Covers: initial check button, loading state, gate met/not met display,
 * missing checks, recheck button.
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

import GateStatus from "../taskList/GateStatus";
import type { GateCheckResult } from "../api";

afterEach(cleanup);

function makeGate(overrides: Partial<GateCheckResult> = {}): GateCheckResult {
  return {
    taskId: "task-1",
    currentPhase: "Implementation",
    targetPhase: "FinalSynthesis",
    met: true,
    requiredChecks: 3,
    passedChecks: 3,
    missingChecks: [],
    evidence: [],
    message: "All gates passed",
    ...overrides,
  };
}

interface RenderOpts {
  gate?: GateCheckResult | null;
  loading?: boolean;
  onCheck?: () => void;
}

function renderGate(opts: RenderOpts = {}) {
  const { gate = null, loading = false, onCheck = vi.fn() } = opts;
  return render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(GateStatus, { gate, loading, onCheck }),
    ),
  );
}

describe("GateStatus", () => {
  it("shows 'Check Gates' button when no gate result yet", () => {
    renderGate();
    expect(screen.getByRole("button", { name: /Check Gates/i })).toBeInTheDocument();
  });

  it("calls onCheck when button clicked", async () => {
    const onCheck = vi.fn();
    renderGate({ onCheck });
    await userEvent.click(screen.getByRole("button", { name: /Check Gates/i }));
    expect(onCheck).toHaveBeenCalledOnce();
  });

  it("disables button while loading", () => {
    renderGate({ loading: true });
    expect(screen.getByRole("button")).toBeDisabled();
  });

  it("shows 'Recheck' when gate result exists", () => {
    renderGate({ gate: makeGate() });
    expect(screen.getByRole("button", { name: /Recheck/i })).toBeInTheDocument();
  });

  it("shows gate met badge when all checks pass", () => {
    renderGate({ gate: makeGate({ met: true }) });
    expect(screen.getByText("Gate met")).toBeInTheDocument();
  });

  it("shows required/passed count when gate not met", () => {
    renderGate({
      gate: makeGate({
        met: false,
        requiredChecks: 3,
        passedChecks: 1,
      }),
    });
    expect(screen.getByText("1/3 required")).toBeInTheDocument();
  });

  it("shows phase transition info", () => {
    renderGate({
      gate: makeGate({
        currentPhase: "Validation",
        targetPhase: "Implementation",
      }),
    });
    expect(screen.getByText(/Validation/)).toBeInTheDocument();
    expect(screen.getByText(/Implementation/)).toBeInTheDocument();
  });

  it("shows missing checks when present", () => {
    renderGate({
      gate: makeGate({
        met: false,
        missingChecks: ["build-pass", "test-pass"],
      }),
    });
    expect(screen.getByText(/build-pass/)).toBeInTheDocument();
    expect(screen.getByText(/test-pass/)).toBeInTheDocument();
  });
});
