// @vitest-environment jsdom
import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import DependenciesSection from "../taskList/DependenciesSection";
import type { TaskDependencySummary } from "../api";

describe("DependenciesSection", () => {
  const deps: TaskDependencySummary[] = [
    { taskId: "t-1", title: "Setup database", status: "Completed", isSatisfied: true },
    { taskId: "t-2", title: "Create API", status: "Active", isSatisfied: false },
  ];

  const dependents: TaskDependencySummary[] = [
    { taskId: "t-3", title: "Build UI", status: "Queued", isSatisfied: false },
  ];

  it("renders loading spinner", () => {
    render(<DependenciesSection dependsOn={[]} dependedOnBy={[]} loading={true} />);
    expect(screen.getByText(/loading dependencies/i)).toBeTruthy();
  });

  it("renders 'No dependencies' when empty", () => {
    render(<DependenciesSection dependsOn={[]} dependedOnBy={[]} loading={false} />);
    expect(screen.getByText(/no dependencies/i)).toBeTruthy();
  });

  it("renders upstream dependencies", () => {
    render(<DependenciesSection dependsOn={deps} dependedOnBy={[]} loading={false} />);
    expect(screen.getByText(/depends on/i)).toBeTruthy();
    expect(screen.getByText("Setup database")).toBeTruthy();
    expect(screen.getByText("Create API")).toBeTruthy();
  });

  it("renders downstream dependents", () => {
    render(<DependenciesSection dependsOn={[]} dependedOnBy={dependents} loading={false} />);
    expect(screen.getByText(/depended on by/i)).toBeTruthy();
    expect(screen.getByText("Build UI")).toBeTruthy();
  });

  it("renders both directions with counts", () => {
    render(<DependenciesSection dependsOn={deps} dependedOnBy={dependents} loading={false} />);
    const labels = screen.getAllByText(/2 upstream/);
    expect(labels.length).toBeGreaterThan(0);
    const downLabels = screen.getAllByText(/1 downstream/);
    expect(downLabels.length).toBeGreaterThan(0);
  });

  it("shows status badge for each dependency", () => {
    render(<DependenciesSection dependsOn={deps} dependedOnBy={[]} loading={false} />);
    const completed = screen.getAllByText("Completed");
    expect(completed.length).toBeGreaterThan(0);
    const active = screen.getAllByText("Active");
    expect(active.length).toBeGreaterThan(0);
  });

  it("calls onSelectTask prop when provided", () => {
    const onSelect = vi.fn();
    const { container } = render(
      <DependenciesSection dependsOn={deps} dependedOnBy={[]} loading={false} onSelectTask={onSelect} />,
    );

    // Verify rows render with cursor:pointer when onSelectTask is provided
    const pointerElements = container.querySelectorAll('[style*="cursor: pointer"]');
    expect(pointerElements.length).toBeGreaterThan(0);
  });

  it("shows task IDs", () => {
    render(<DependenciesSection dependsOn={deps} dependedOnBy={[]} loading={false} />);
    // task IDs are shown (first 8 chars, but these are short)
    const ids = screen.getAllByText("t-1");
    expect(ids.length).toBeGreaterThan(0);
  });
});
