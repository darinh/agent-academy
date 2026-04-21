// @vitest-environment jsdom
/**
 * DOM tests for SignOffBanner.
 *
 * Covers: hidden when not awaiting sign-off, visible with stage info,
 * elapsed time display.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { createElement } from "react";

import SignOffBanner from "../sprint/SignOffBanner";
import type { SprintDetailResponse, SprintSnapshot } from "../api";

afterEach(cleanup);

function makeDetail(
  sprintOverrides: Partial<SprintSnapshot> = {},
): SprintDetailResponse {
  return {
    sprint: {
      id: "sprint-1",
      number: 1,
      status: "Active",
      currentStage: "Planning",
      overflowFromSprintId: null,
      awaitingSignOff: false,
      pendingStage: null,
      signOffRequestedAt: null,
      createdAt: "2026-04-15T08:00:00Z",
      completedAt: null,
      ...sprintOverrides,
    },
    artifacts: [],
    stages: ["Intake", "Planning", "Discussion", "Validation", "Implementation"],
  };
}

function renderBanner(detail: SprintDetailResponse) {
  return render(createElement(SignOffBanner, { detail }));
}

describe("SignOffBanner", () => {
  it("renders nothing when not awaiting sign-off", () => {
    const { container } = renderBanner(makeDetail());
    expect(container.firstChild).toBeNull();
  });

  it("renders banner when awaiting sign-off", () => {
    renderBanner(
      makeDetail({
        awaitingSignOff: true,
        currentStage: "Planning",
        pendingStage: "Discussion",
      }),
    );
    expect(screen.getByText(/User sign-off required/)).toBeInTheDocument();
    // "Planning" appears multiple times — verify the full phrase
    expect(screen.getByText(/advance from/)).toBeInTheDocument();
    expect(screen.getByText(/to/)).toBeInTheDocument();
  });

  it("shows elapsed time when signOffRequestedAt is set", () => {
    const fiveMinAgo = new Date(Date.now() - 5 * 60_000).toISOString();
    renderBanner(
      makeDetail({
        awaitingSignOff: true,
        currentStage: "Validation",
        pendingStage: "Implementation",
        signOffRequestedAt: fiveMinAgo,
      }),
    );
    expect(screen.getByText(/Waiting \d+m/)).toBeInTheDocument();
  });

  it("shows hours when waiting > 60 minutes", () => {
    const twoHoursAgo = new Date(Date.now() - 125 * 60_000).toISOString();
    renderBanner(
      makeDetail({
        awaitingSignOff: true,
        currentStage: "Discussion",
        pendingStage: "Validation",
        signOffRequestedAt: twoHoursAgo,
      }),
    );
    expect(screen.getByText(/Waiting 2h 5m/)).toBeInTheDocument();
  });

  it("shows the hourglass emoji", () => {
    renderBanner(
      makeDetail({
        awaitingSignOff: true,
        pendingStage: "Implementation",
      }),
    );
    expect(screen.getByText("⏳")).toBeInTheDocument();
  });
});
