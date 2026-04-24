// @vitest-environment jsdom
/**
 * DOM tests for SprintTasks.
 *
 * Covers: rendering tasks linked to a sprint, removing a task, opening
 * the picker dialog, filtering candidates, and adding a task to the sprint.
 */
import "@testing-library/jest-dom/vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import type { ReactNode } from "react";

vi.mock("@fluentui/react-components", () => {
  const passthrough = (tag: string) => (props: any) =>
    createElement(tag, props, props.children);
  return {
    makeStyles: () => () => new Proxy({}, { get: () => "" }),
    shorthands: new Proxy({}, { get: () => () => ({}) }),
    Button: ({ children, onClick, disabled, ...rest }: any) =>
      createElement(
        "button",
        { onClick, disabled, ...rest },
        children,
      ),
    Input: ({ value, onChange, placeholder, ...rest }: any) =>
      createElement("input", {
        value,
        placeholder,
        onChange: (e: any) => onChange?.(e, { value: e.target.value }),
        ...rest,
      }),
    Spinner: ({ label }: { label?: string }) =>
      createElement("div", { role: "status" }, label ?? "loading"),
    Dialog: ({ open, children }: { open: boolean; children: ReactNode }) =>
      open ? createElement("div", { role: "dialog" }, children) : null,
    DialogSurface: passthrough("div"),
    DialogBody: passthrough("div"),
    DialogTitle: passthrough("h2"),
    DialogContent: passthrough("div"),
    DialogActions: passthrough("div"),
  };
});

const getTasksMock = vi.fn();
const updateTaskSprintMock = vi.fn();

vi.mock("../api", () => ({
  getTasks: (...args: unknown[]) => getTasksMock(...args),
  updateTaskSprint: (...args: unknown[]) => updateTaskSprintMock(...args),
}));

import SprintTasks from "../sprint/SprintTasks";

const taskA = {
  id: "task-a",
  title: "Add OAuth login",
  status: "Active",
  priority: "High",
  sprintId: "sprint-1",
};
const taskB = {
  id: "task-b",
  title: "Refactor router",
  status: "Queued",
  priority: "Medium",
  sprintId: null as string | null,
};
const taskC = {
  id: "task-c",
  title: "Document API",
  status: "Queued",
  priority: "Low",
  sprintId: "sprint-2",
};

beforeEach(() => {
  getTasksMock.mockReset();
  updateTaskSprintMock.mockReset();
});

afterEach(() => {
  cleanup();
});

describe("SprintTasks", () => {
  it("renders tasks linked to the sprint", async () => {
    getTasksMock.mockResolvedValueOnce([taskA]);

    render(<SprintTasks sprintId="sprint-1" />);

    expect(await screen.findByText("Add OAuth login")).toBeInTheDocument();
    expect(getTasksMock).toHaveBeenCalledWith("sprint-1");
  });

  it("shows an empty state when no tasks are linked", async () => {
    getTasksMock.mockResolvedValueOnce([]);

    render(<SprintTasks sprintId="sprint-1" />);

    expect(
      await screen.findByText(/no tasks linked to this sprint/i),
    ).toBeInTheDocument();
  });

  it("removes a task when the Remove button is clicked", async () => {
    getTasksMock.mockResolvedValueOnce([taskA]);
    updateTaskSprintMock.mockResolvedValueOnce({ ...taskA, sprintId: null });
    getTasksMock.mockResolvedValueOnce([]);

    render(<SprintTasks sprintId="sprint-1" />);

    const removeBtn = await screen.findByTestId("sprint-task-remove");
    await userEvent.click(removeBtn);

    await waitFor(() =>
      expect(updateTaskSprintMock).toHaveBeenCalledWith("task-a", null),
    );
    await waitFor(() =>
      expect(
        screen.getByText(/no tasks linked to this sprint/i),
      ).toBeInTheDocument(),
    );
  });

  it("opens the picker, filters candidates, and adds a task", async () => {
    // initial load (sprint tasks)
    getTasksMock.mockResolvedValueOnce([taskA]);
    // picker load (all tasks)
    getTasksMock.mockResolvedValueOnce([taskA, taskB, taskC]);
    updateTaskSprintMock.mockResolvedValueOnce({ ...taskB, sprintId: "sprint-1" });
    // refresh after add
    getTasksMock.mockResolvedValueOnce([taskA, { ...taskB, sprintId: "sprint-1" }]);

    render(<SprintTasks sprintId="sprint-1" />);

    await screen.findByText("Add OAuth login");

    await userEvent.click(screen.getByTestId("sprint-tasks-add"));

    // The dialog opens. taskA is already in sprint-1 so it must NOT appear
    // as a candidate. taskB and taskC should appear.
    const candidates = await screen.findAllByTestId("sprint-tasks-candidate");
    expect(candidates).toHaveLength(2);
    expect(screen.getByText("Refactor router")).toBeInTheDocument();
    expect(screen.getByText("Document API")).toBeInTheDocument();

    // Filter to "router"
    await userEvent.type(screen.getByTestId("sprint-tasks-filter"), "router");
    await waitFor(() =>
      expect(screen.getAllByTestId("sprint-tasks-candidate")).toHaveLength(1),
    );

    // Add the filtered task
    const addBtn = screen.getAllByText("Add")[0];
    await userEvent.click(addBtn);

    await waitFor(() =>
      expect(updateTaskSprintMock).toHaveBeenCalledWith("task-b", "sprint-1"),
    );
  });

  it("surfaces a load error", async () => {
    getTasksMock.mockRejectedValueOnce(new Error("network down"));

    render(<SprintTasks sprintId="sprint-1" />);

    expect(await screen.findByText("network down")).toBeInTheDocument();
  });
});
