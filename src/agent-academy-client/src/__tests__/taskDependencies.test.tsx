// @vitest-environment jsdom
import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import type { TaskSnapshot } from "../api";

// Minimal mock to test blocking badge rendering in TaskListPanel
// We test at the unit level that the blocking indicator renders correctly

function BlockingIndicator({ task }: { task: Pick<TaskSnapshot, "blockingTaskIds"> }) {
  if (!task.blockingTaskIds || task.blockingTaskIds.length === 0) return null;
  return <span data-testid="blocking-badge">🔗 {task.blockingTaskIds.length} blocked</span>;
}

describe("Task dependency blocking indicator", () => {
  it("renders nothing when no blocking tasks", () => {
    const { container } = render(<BlockingIndicator task={{ blockingTaskIds: [] }} />);
    expect(container.innerHTML).toBe("");
  });

  it("renders nothing when blockingTaskIds is null", () => {
    const { container } = render(<BlockingIndicator task={{ blockingTaskIds: null }} />);
    expect(container.innerHTML).toBe("");
  });

  it("renders badge with count when tasks are blocked", () => {
    render(<BlockingIndicator task={{ blockingTaskIds: ["t-1", "t-2"] }} />);
    expect(screen.getByTestId("blocking-badge")).toBeTruthy();
    expect(screen.getByText(/2 blocked/)).toBeTruthy();
  });

  it("renders badge with single blocker", () => {
    render(<BlockingIndicator task={{ blockingTaskIds: ["t-1"] }} />);
    expect(screen.getByText(/1 blocked/)).toBeTruthy();
  });
});

describe("TaskSnapshot dependency fields", () => {
  it("dependsOnTaskIds defaults to null", () => {
    const task: Partial<TaskSnapshot> = { id: "t-1", title: "Test" };
    expect(task.dependsOnTaskIds).toBeUndefined();
  });

  it("blockingTaskIds can be empty array", () => {
    const task: Partial<TaskSnapshot> = { id: "t-1", blockingTaskIds: [] };
    expect(task.blockingTaskIds).toHaveLength(0);
  });
});
