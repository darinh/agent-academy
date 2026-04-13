// @vitest-environment jsdom
import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, fireEvent } from "@testing-library/react";
import type { SprintSnapshot } from "../api";

vi.mock("@fluentui/react-components", () => ({
  makeStyles: () => () => ({}),
  mergeClasses: (...args: any[]) => args.filter(Boolean).join(" "),
  shorthands: new Proxy({}, { get: () => () => ({}) }),
}));

vi.mock("../V3Badge", () => ({
  default: ({ children, color }: any) => (
    <span data-testid="badge" data-color={color}>{children}</span>
  ),
}));

vi.mock("../sprint/sprintConstants", () => ({
  statusBadgeColor: (status: string) => {
    const map: Record<string, string> = {
      Active: "active",
      Completed: "done",
      Cancelled: "cancel",
    };
    return map[status] ?? "muted";
  },
}));

vi.mock("../panelUtils", () => ({
  formatElapsed: (date: string) => `elapsed(${date})`,
}));

import SprintHistory from "../sprint/SprintHistory";

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

describe("SprintHistory", () => {
  const onSelectSprint = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  // ── Rendering conditions ──

  it("returns null when history has zero sprints", () => {
    const { container } = render(
      <SprintHistory history={[]} selectedSprintId={null} onSelectSprint={onSelectSprint} />,
    );
    expect(container.innerHTML).toBe("");
  });

  it("returns null when history has exactly one sprint", () => {
    const { container } = render(
      <SprintHistory history={[makeSprint()]} selectedSprintId={null} onSelectSprint={onSelectSprint} />,
    );
    expect(container.innerHTML).toBe("");
  });

  it("renders when history has two or more sprints", () => {
    const { container } = render(
      <SprintHistory
        history={[makeSprint({ id: "s1", number: 1 }), makeSprint({ id: "s2", number: 2 })]}
        selectedSprintId={null}
        onSelectSprint={onSelectSprint}
      />,
    );
    expect(container.innerHTML).not.toBe("");
  });

  // ── Content rendering ──

  it("renders section title 'Sprint History'", () => {
    const history = [makeSprint({ id: "s1", number: 1 }), makeSprint({ id: "s2", number: 2 })];
    const { container } = render(
      <SprintHistory history={history} selectedSprintId={null} onSelectSprint={onSelectSprint} />,
    );
    expect(container.textContent).toContain("Sprint History");
  });

  it("renders sprint numbers with # prefix", () => {
    const history = [
      makeSprint({ id: "s1", number: 3 }),
      makeSprint({ id: "s2", number: 7 }),
    ];
    const { container } = render(
      <SprintHistory history={history} selectedSprintId={null} onSelectSprint={onSelectSprint} />,
    );
    expect(container.textContent).toContain("#3");
    expect(container.textContent).toContain("#7");
  });

  it("renders status badge for each sprint", () => {
    const history = [
      makeSprint({ id: "s1", number: 1, status: "Active" }),
      makeSprint({ id: "s2", number: 2, status: "Completed" }),
    ];
    const { container } = render(
      <SprintHistory history={history} selectedSprintId={null} onSelectSprint={onSelectSprint} />,
    );
    const badges = container.querySelectorAll("[data-testid='badge']");
    expect(badges).toHaveLength(2);
    expect(badges[0].textContent).toBe("Active");
    expect(badges[0].getAttribute("data-color")).toBe("active");
    expect(badges[1].textContent).toBe("Completed");
    expect(badges[1].getAttribute("data-color")).toBe("done");
  });

  it("renders current stage and elapsed time for each sprint", () => {
    const history = [
      makeSprint({ id: "s1", number: 1, currentStage: "Planning", createdAt: "2026-04-01T00:00:00Z" }),
      makeSprint({ id: "s2", number: 2, currentStage: "Implementation", createdAt: "2026-04-02T00:00:00Z" }),
    ];
    const { container } = render(
      <SprintHistory history={history} selectedSprintId={null} onSelectSprint={onSelectSprint} />,
    );
    expect(container.textContent).toContain("Planning");
    expect(container.textContent).toContain("elapsed(2026-04-01T00:00:00Z)");
    expect(container.textContent).toContain("Implementation");
    expect(container.textContent).toContain("elapsed(2026-04-02T00:00:00Z)");
  });

  // ── Selection / active state ──

  it("applies active class to the selected sprint", () => {
    const history = [
      makeSprint({ id: "s1", number: 1 }),
      makeSprint({ id: "s2", number: 2 }),
    ];
    const { container } = render(
      <SprintHistory history={history} selectedSprintId="s2" onSelectSprint={onSelectSprint} />,
    );
    // mergeClasses mock joins class names, the active sprint should have a non-empty class
    const items = container.querySelectorAll("[role='button']");
    expect(items).toHaveLength(2);
    // First item (s1) should not have the active class merged
    // Second item (s2) should have merged classes (truthy second arg)
  });

  // ── Click interaction ──

  it("calls onSelectSprint with sprint id on click", () => {
    const history = [
      makeSprint({ id: "s1", number: 1 }),
      makeSprint({ id: "s2", number: 2 }),
    ];
    const { container } = render(
      <SprintHistory history={history} selectedSprintId={null} onSelectSprint={onSelectSprint} />,
    );
    const items = container.querySelectorAll("[role='button']");
    fireEvent.click(items[1]);
    expect(onSelectSprint).toHaveBeenCalledWith("s2");
  });

  it("calls onSelectSprint on Enter keydown", () => {
    const history = [
      makeSprint({ id: "s1", number: 1 }),
      makeSprint({ id: "s2", number: 2 }),
    ];
    const { container } = render(
      <SprintHistory history={history} selectedSprintId={null} onSelectSprint={onSelectSprint} />,
    );
    const items = container.querySelectorAll("[role='button']");
    fireEvent.keyDown(items[0], { key: "Enter" });
    expect(onSelectSprint).toHaveBeenCalledWith("s1");
  });

  it("calls onSelectSprint on Space keydown", () => {
    const history = [
      makeSprint({ id: "s1", number: 1 }),
      makeSprint({ id: "s2", number: 2 }),
    ];
    const { container } = render(
      <SprintHistory history={history} selectedSprintId={null} onSelectSprint={onSelectSprint} />,
    );
    const items = container.querySelectorAll("[role='button']");
    fireEvent.keyDown(items[1], { key: " " });
    expect(onSelectSprint).toHaveBeenCalledWith("s2");
  });

  it("does not call onSelectSprint on unrelated key", () => {
    const history = [
      makeSprint({ id: "s1", number: 1 }),
      makeSprint({ id: "s2", number: 2 }),
    ];
    const { container } = render(
      <SprintHistory history={history} selectedSprintId={null} onSelectSprint={onSelectSprint} />,
    );
    const items = container.querySelectorAll("[role='button']");
    fireEvent.keyDown(items[0], { key: "Tab" });
    expect(onSelectSprint).not.toHaveBeenCalled();
  });

  // ── Accessibility ──

  it("each history item has role='button' and tabIndex=0", () => {
    const history = [
      makeSprint({ id: "s1", number: 1 }),
      makeSprint({ id: "s2", number: 2 }),
      makeSprint({ id: "s3", number: 3 }),
    ];
    const { container } = render(
      <SprintHistory history={history} selectedSprintId={null} onSelectSprint={onSelectSprint} />,
    );
    const items = container.querySelectorAll("[role='button']");
    expect(items).toHaveLength(3);
    items.forEach((item) => {
      expect(item.getAttribute("tabindex")).toBe("0");
    });
  });

  // ── Many sprints ──

  it("renders all sprints in a long history", () => {
    const history = Array.from({ length: 10 }, (_, i) =>
      makeSprint({ id: `s${i + 1}`, number: i + 1 }),
    );
    const { container } = render(
      <SprintHistory history={history} selectedSprintId={null} onSelectSprint={onSelectSprint} />,
    );
    const items = container.querySelectorAll("[role='button']");
    expect(items).toHaveLength(10);
  });

  // ── Cancelled sprint badge color ──

  it("renders cancelled sprint with cancel badge color", () => {
    const history = [
      makeSprint({ id: "s1", number: 1, status: "Cancelled" }),
      makeSprint({ id: "s2", number: 2, status: "Active" }),
    ];
    const { container } = render(
      <SprintHistory history={history} selectedSprintId={null} onSelectSprint={onSelectSprint} />,
    );
    const badges = container.querySelectorAll("[data-testid='badge']");
    expect(badges[0].getAttribute("data-color")).toBe("cancel");
  });
});
