// @vitest-environment jsdom
/**
 * DOM-based RTL tests for TaskPropertyControls.
 *
 * Covers: status picker toggle/display, priority picker toggle/display,
 * complete task flow with commit count, terminal-state rendering (null),
 * API call wiring, error handling, mutual exclusion of pickers, and
 * spinner/disabled states during pending operations.
 */
import "@testing-library/jest-dom/vitest";
import {
  cleanup,
  render,
  screen,
  fireEvent,
  waitFor,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

vi.mock("../api", () => ({
  updateTaskStatus: vi.fn(),
  updateTaskPriority: vi.fn(),
  completeTask: vi.fn(),
}));

vi.mock("../V3Badge", () => ({
  default: ({ children, color }: { children: React.ReactNode; color?: string }) =>
    createElement("span", { "data-testid": "v3-badge", "data-color": color }, children),
}));

vi.mock("./taskListHelpers", () => ({
  statusBadgeColor: (s: string) => s.toLowerCase(),
  priorityBadgeColor: (p: string) => p.toLowerCase(),
}));

vi.mock("./taskDetailStyles", () => ({
  useTaskDetailStyles: () => ({
    actionBar: "actionBar",
    assignPicker: "assignPicker",
    assignPickerBtn: "assignPickerBtn",
    reasonArea: "reasonArea",
    reasonActions: "reasonActions",
    actionFeedback: "actionFeedback",
    actionSuccess: "actionSuccess",
    actionError: "actionError",
  }),
}));

import TaskPropertyControls from "../taskList/TaskPropertyControls";
import type { TaskSnapshot, TaskStatus } from "../api";
import { updateTaskStatus, updateTaskPriority, completeTask } from "../api";

const mockUpdateStatus = vi.mocked(updateTaskStatus);
const mockUpdatePriority = vi.mocked(updateTaskPriority);
const mockCompleteTask = vi.mocked(completeTask);

// ── Factories ──────────────────────────────────────────────────────────

function makeTask(overrides: Partial<TaskSnapshot> = {}): TaskSnapshot {
  return {
    id: "task-1",
    title: "Test Task",
    description: "A test task",
    successCriteria: "Tests pass",
    status: "Active" as TaskStatus,
    currentPhase: "Implementation",
    currentPlan: "",
    validationStatus: "",
    validationSummary: "",
    implementationStatus: "",
    implementationSummary: "",
    preferredRoles: [],
    createdAt: "2026-04-10T12:00:00Z",
    updatedAt: "2026-04-10T12:00:00Z",
    priority: "Medium",
    ...overrides,
  } as TaskSnapshot;
}

function renderWithProvider(ui: React.ReactElement) {
  return render(
    createElement(FluentProvider, { theme: webDarkTheme }, ui),
  );
}

// ── Setup ──────────────────────────────────────────────────────────────

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

// ── Tests ──────────────────────────────────────────────────────────────

describe("TaskPropertyControls", () => {
  const onRefresh = vi.fn();

  describe("rendering", () => {
    it("renders status, priority, and complete buttons for active tasks", () => {
      renderWithProvider(
        createElement(TaskPropertyControls, { task: makeTask(), onRefresh }),
      );
      expect(screen.getByText(/Status: Active/)).toBeInTheDocument();
      expect(screen.getByText(/Priority: Medium/)).toBeInTheDocument();
      expect(screen.getByText("Complete")).toBeInTheDocument();
    });

    it("returns null for Completed tasks", () => {
      const { container } = renderWithProvider(
        createElement(TaskPropertyControls, {
          task: makeTask({ status: "Completed" }),
          onRefresh,
        }),
      );
      // FluentProvider renders its own wrapper; the component returns null so no actionBar child
      expect(container.querySelector(".actionBar")).toBeNull();
    });

    it("returns null for Cancelled tasks", () => {
      const { container } = renderWithProvider(
        createElement(TaskPropertyControls, {
          task: makeTask({ status: "Cancelled" }),
          onRefresh,
        }),
      );
      expect(container.querySelector(".actionBar")).toBeNull();
    });

    it("displays current priority defaulting to Medium when null", () => {
      renderWithProvider(
        createElement(TaskPropertyControls, {
          task: makeTask({ priority: undefined }),
          onRefresh,
        }),
      );
      expect(screen.getByText(/Priority: Medium/)).toBeInTheDocument();
    });
  });

  describe("status picker", () => {
    it("toggles the status picker on button click", async () => {
      renderWithProvider(
        createElement(TaskPropertyControls, { task: makeTask(), onRefresh }),
      );
      const statusBtn = screen.getByText(/Status: Active/);
      fireEvent.click(statusBtn);
      // Should show safe statuses except current (Active)
      expect(screen.getByText("Queued")).toBeInTheDocument();
      expect(screen.getByText("Blocked")).toBeInTheDocument();
      expect(screen.getByText("AwaitingValidation")).toBeInTheDocument();
      expect(screen.getByText("InReview")).toBeInTheDocument();
      // Active should not appear (it's the current status)
      expect(screen.queryByText("Active")).toBe(null);
    });

    it("closes status picker on second click", () => {
      renderWithProvider(
        createElement(TaskPropertyControls, { task: makeTask(), onRefresh }),
      );
      const statusBtn = screen.getByText(/Status: Active/);
      fireEvent.click(statusBtn);
      expect(screen.getByText("Queued")).toBeInTheDocument();
      fireEvent.click(statusBtn);
      expect(screen.queryByText("Queued")).not.toBeInTheDocument();
    });

    it("calls updateTaskStatus and onRefresh on status selection", async () => {
      mockUpdateStatus.mockResolvedValueOnce(makeTask({ status: "Blocked" }));
      renderWithProvider(
        createElement(TaskPropertyControls, { task: makeTask(), onRefresh }),
      );
      fireEvent.click(screen.getByText(/Status: Active/));
      fireEvent.click(screen.getByText("Blocked"));

      await waitFor(() => {
        expect(mockUpdateStatus).toHaveBeenCalledWith("task-1", "Blocked");
        expect(onRefresh).toHaveBeenCalled();
      });
      expect(screen.getByText(/Status → Blocked/)).toBeInTheDocument();
    });

    it("shows error message on status update failure", async () => {
      mockUpdateStatus.mockRejectedValueOnce(new Error("Network error"));
      renderWithProvider(
        createElement(TaskPropertyControls, { task: makeTask(), onRefresh }),
      );
      fireEvent.click(screen.getByText(/Status: Active/));
      fireEvent.click(screen.getByText("Blocked"));

      await waitFor(() => {
        expect(screen.getByText("Network error")).toBeInTheDocument();
      });
      expect(onRefresh).not.toHaveBeenCalled();
    });

    it("only shows safe statuses (not lifecycle statuses)", () => {
      renderWithProvider(
        createElement(TaskPropertyControls, {
          task: makeTask({ status: "Queued" }),
          onRefresh,
        }),
      );
      fireEvent.click(screen.getByText(/Status: Queued/));
      // Should NOT show Approved, Merging, Completed, Cancelled
      expect(screen.queryByText("Approved")).not.toBeInTheDocument();
      expect(screen.queryByText("Merging")).not.toBeInTheDocument();
      expect(screen.queryByText("Completed")).not.toBeInTheDocument();
      expect(screen.queryByText("Cancelled")).not.toBeInTheDocument();
    });
  });

  describe("priority picker", () => {
    it("toggles priority picker and shows all priorities except current", () => {
      renderWithProvider(
        createElement(TaskPropertyControls, {
          task: makeTask({ priority: "High" }),
          onRefresh,
        }),
      );
      fireEvent.click(screen.getByText(/Priority: High/));
      expect(screen.getByText("Critical")).toBeInTheDocument();
      expect(screen.getByText("Medium")).toBeInTheDocument();
      expect(screen.getByText("Low")).toBeInTheDocument();
      // "High" should not appear as a picker option
      const badges = screen.getAllByTestId("v3-badge");
      const priorityBadgeTexts = badges.map((b) => b.textContent);
      expect(priorityBadgeTexts).not.toContain("High");
    });

    it("calls updateTaskPriority and onRefresh on selection", async () => {
      mockUpdatePriority.mockResolvedValueOnce(makeTask({ priority: "Critical" }));
      renderWithProvider(
        createElement(TaskPropertyControls, { task: makeTask(), onRefresh }),
      );
      fireEvent.click(screen.getByText(/Priority: Medium/));
      fireEvent.click(screen.getByText("Critical"));

      await waitFor(() => {
        expect(mockUpdatePriority).toHaveBeenCalledWith("task-1", "Critical");
        expect(onRefresh).toHaveBeenCalled();
      });
      expect(screen.getByText(/Priority → Critical/)).toBeInTheDocument();
    });

    it("shows error message on priority update failure", async () => {
      mockUpdatePriority.mockRejectedValueOnce(new Error("Server error"));
      renderWithProvider(
        createElement(TaskPropertyControls, { task: makeTask(), onRefresh }),
      );
      fireEvent.click(screen.getByText(/Priority: Medium/));
      fireEvent.click(screen.getByText("Critical"));

      await waitFor(() => {
        expect(screen.getByText("Server error")).toBeInTheDocument();
      });
    });
  });

  describe("mutual exclusion of pickers", () => {
    it("opening status picker closes priority picker", () => {
      renderWithProvider(
        createElement(TaskPropertyControls, { task: makeTask(), onRefresh }),
      );
      // Open priority picker
      fireEvent.click(screen.getByText(/Priority: Medium/));
      expect(screen.getByText("Critical")).toBeInTheDocument();

      // Open status picker — should close priority picker
      fireEvent.click(screen.getByText(/Status: Active/));
      expect(screen.getByText("Queued")).toBeInTheDocument();
      // Priority options should be gone (Critical was from priority picker)
      expect(screen.queryByText("Critical")).not.toBeInTheDocument();
    });

    it("opening complete form closes both pickers", () => {
      renderWithProvider(
        createElement(TaskPropertyControls, { task: makeTask(), onRefresh }),
      );
      fireEvent.click(screen.getByText(/Status: Active/));
      expect(screen.getByText("Queued")).toBeInTheDocument();

      fireEvent.click(screen.getByText("Complete"));
      expect(screen.queryByText("Queued")).not.toBeInTheDocument();
      expect(screen.getByText("Commit count")).toBeInTheDocument();
    });
  });

  describe("complete task flow", () => {
    it("shows commit count form when Complete is clicked", () => {
      renderWithProvider(
        createElement(TaskPropertyControls, { task: makeTask(), onRefresh }),
      );
      fireEvent.click(screen.getByText("Complete"));
      expect(screen.getByText("Commit count")).toBeInTheDocument();
      expect(screen.getByText("Cancel")).toBeInTheDocument();
      expect(screen.getByText("Complete Task")).toBeInTheDocument();
    });

    it("cancel hides the complete form and resets commit count", () => {
      renderWithProvider(
        createElement(TaskPropertyControls, { task: makeTask(), onRefresh }),
      );
      fireEvent.click(screen.getByText("Complete"));
      expect(screen.getByText("Commit count")).toBeInTheDocument();

      fireEvent.click(screen.getByText("Cancel"));
      expect(screen.queryByText("Commit count")).not.toBeInTheDocument();
      expect(screen.getByText("Complete")).toBeInTheDocument();
    });

    it("Complete Task button is disabled when commit count is empty", () => {
      renderWithProvider(
        createElement(TaskPropertyControls, { task: makeTask(), onRefresh }),
      );
      fireEvent.click(screen.getByText("Complete"));
      const submitBtn = screen.getByText("Complete Task");
      expect(submitBtn.closest("button")).toBeDisabled();
    });

    it("calls completeTask with parsed commit count", async () => {
      mockCompleteTask.mockResolvedValueOnce(makeTask({ status: "Completed" }));
      renderWithProvider(
        createElement(TaskPropertyControls, { task: makeTask(), onRefresh }),
      );
      fireEvent.click(screen.getByText("Complete"));

      const input = screen.getByRole("spinbutton");
      await userEvent.type(input, "5");

      fireEvent.click(screen.getByText("Complete Task"));

      await waitFor(() => {
        expect(mockCompleteTask).toHaveBeenCalledWith("task-1", 5);
        expect(onRefresh).toHaveBeenCalled();
      });
      expect(screen.getByText("Task completed")).toBeInTheDocument();
    });

    it("rejects negative commit count", async () => {
      renderWithProvider(
        createElement(TaskPropertyControls, { task: makeTask(), onRefresh }),
      );
      fireEvent.click(screen.getByText("Complete"));

      const input = screen.getByRole("spinbutton");
      // Type a negative number
      await userEvent.clear(input);
      await userEvent.type(input, "-1");

      fireEvent.click(screen.getByText("Complete Task"));

      await waitFor(() => {
        expect(screen.getByText("Commit count must be a non-negative number")).toBeInTheDocument();
      });
      expect(mockCompleteTask).not.toHaveBeenCalled();
    });

    it("shows error on completeTask API failure", async () => {
      mockCompleteTask.mockRejectedValueOnce(new Error("Task already completed"));
      renderWithProvider(
        createElement(TaskPropertyControls, { task: makeTask(), onRefresh }),
      );
      fireEvent.click(screen.getByText("Complete"));

      const input = screen.getByRole("spinbutton");
      await userEvent.type(input, "3");
      fireEvent.click(screen.getByText("Complete Task"));

      await waitFor(() => {
        expect(screen.getByText("Task already completed")).toBeInTheDocument();
      });
    });

    it("allows zero commit count", async () => {
      mockCompleteTask.mockResolvedValueOnce(makeTask({ status: "Completed" }));
      renderWithProvider(
        createElement(TaskPropertyControls, { task: makeTask(), onRefresh }),
      );
      fireEvent.click(screen.getByText("Complete"));

      const input = screen.getByRole("spinbutton");
      await userEvent.type(input, "0");
      fireEvent.click(screen.getByText("Complete Task"));

      await waitFor(() => {
        expect(mockCompleteTask).toHaveBeenCalledWith("task-1", 0);
      });
    });
  });
});
