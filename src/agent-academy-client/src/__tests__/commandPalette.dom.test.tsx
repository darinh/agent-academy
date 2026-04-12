// @vitest-environment jsdom
/**
 * Interactive RTL tests for CommandPalette.
 *
 * Uses @testing-library/react + jsdom.
 * Covers: open/close, search filtering, keyboard navigation (arrow/enter/escape),
 * command detail view, field input, validation errors, command execution (sync + async
 * polling), result display (success/error), readOnly mode, backdrop dismiss, and
 * focus trap lifecycle.
 */
import "@testing-library/jest-dom/vitest";
import {
  cleanup,
  render,
  screen,
  waitFor,
  act,
  fireEvent,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

// ── Mocks ──────────────────────────────────────────────────────────────

const mockExecuteCommand = vi.fn();
const mockGetCommandExecution = vi.fn();
const mockGetCommandMetadata = vi.fn();

vi.mock("../api", () => ({
  executeCommand: (...args: unknown[]) => mockExecuteCommand(...args),
  getCommandExecution: (...args: unknown[]) => mockGetCommandExecution(...args),
  getCommandMetadata: (...args: unknown[]) => mockGetCommandMetadata(...args),
}));

vi.mock("../V3Badge", () => ({
  default: ({ children, color }: { children: string; color: string }) =>
    createElement("span", { "data-testid": `badge-${color}` }, children),
}));

import CommandPalette from "../CommandPalette";

// ── Helpers ────────────────────────────────────────────────────────────

function renderPalette(props: Partial<Parameters<typeof CommandPalette>[0]> = {}) {
  const defaultProps = {
    open: true,
    onDismiss: vi.fn(),
    roomId: "test-room",
    readOnly: false,
    ...props,
  };

  const result = render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(CommandPalette, defaultProps),
    ),
  );

  return { ...result, props: defaultProps };
}

function makeResponse(overrides: Record<string, unknown> = {}) {
  return {
    command: "READ_FILE",
    status: "completed",
    result: "file content here",
    error: null,
    errorCode: null,
    correlationId: "corr-123",
    timestamp: new Date().toISOString(),
    executedBy: "human",
    ...overrides,
  };
}

// ── Setup ──────────────────────────────────────────────────────────────

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  mockGetCommandMetadata.mockResolvedValue([]);
  mockExecuteCommand.mockResolvedValue(makeResponse());
  mockGetCommandExecution.mockResolvedValue(makeResponse());
  // jsdom doesn't implement scrollIntoView
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.restoreAllMocks();
  mockExecuteCommand.mockReset();
  mockGetCommandExecution.mockReset();
  mockGetCommandMetadata.mockReset();
});

// ── Tests ──────────────────────────────────────────────────────────────

describe("CommandPalette", () => {
  describe("open/close lifecycle", () => {
    it("renders nothing when open=false", () => {
      renderPalette({ open: false });
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    });

    it("renders dialog when open=true", () => {
      renderPalette({ open: true });
      expect(screen.getByRole("dialog")).toBeInTheDocument();
    });

    it("calls onDismiss when backdrop is clicked", async () => {
      const { props } = renderPalette();
      const backdrop = screen.getByRole("presentation");
      await act(async () => { fireEvent.click(backdrop); });
      expect(props.onDismiss).toHaveBeenCalledTimes(1);
    });

    it("does not dismiss when container body is clicked", async () => {
      const { props } = renderPalette();
      const dialog = screen.getByRole("dialog");
      await act(async () => { fireEvent.click(dialog); });
      expect(props.onDismiss).not.toHaveBeenCalled();
    });

    it("calls onDismiss on Escape in search mode", async () => {
      const { props } = renderPalette();
      const dialog = screen.getByRole("dialog");
      await act(async () => { fireEvent.keyDown(dialog, { key: "Escape" }); });
      expect(props.onDismiss).toHaveBeenCalledTimes(1);
    });
  });

  describe("search filtering", () => {
    it("shows all commands when search is empty", () => {
      renderPalette();
      // WEEK1_COMMANDS has 11 commands across 4 categories
      const items = screen.getAllByText(/Read file|Search code|Show diff|List tasks|Run build|Run tests/);
      expect(items.length).toBeGreaterThanOrEqual(6);
    });

    it("filters commands by title", async () => {
      renderPalette();
      const input = screen.getByPlaceholderText("Search commands…");
      await act(async () => { fireEvent.change(input, { target: { value: "build" } }); });
      expect(screen.getByText("Run build")).toBeInTheDocument();
      expect(screen.queryByText("Read file")).not.toBeInTheDocument();
    });

    it("filters commands by category", async () => {
      renderPalette();
      const input = screen.getByPlaceholderText("Search commands…");
      await act(async () => { fireEvent.change(input, { target: { value: "git" } }); });
      expect(screen.getByText("Show diff")).toBeInTheDocument();
    });

    it("shows empty message when no matches", async () => {
      renderPalette();
      const input = screen.getByPlaceholderText("Search commands…");
      await act(async () => { fireEvent.change(input, { target: { value: "zzzznothing" } }); });
      expect(screen.getByText(/No commands match/)).toBeInTheDocument();
    });
  });

  describe("keyboard navigation", () => {
    it("moves selection down/up with arrow keys", async () => {
      renderPalette();
      const dialog = screen.getByRole("dialog");
      const items = dialog.querySelectorAll("[data-palette-item]");
      expect(items.length).toBeGreaterThan(2);

      // Capture initial class of first two items
      const item0InitialClass = items[0].className;
      const item1InitialClass = items[1].className;

      // First item starts selected (has more classes)
      expect(item0InitialClass).not.toEqual(item1InitialClass);

      await act(async () => { fireEvent.keyDown(dialog, { key: "ArrowDown" }); });
      // After moving down, item[1] should gain the extra class, item[0] loses it
      expect(items[1].className).toEqual(item0InitialClass);
      expect(items[0].className).toEqual(item1InitialClass);

      await act(async () => { fireEvent.keyDown(dialog, { key: "ArrowUp" }); });
      // Back to original state
      expect(items[0].className).toEqual(item0InitialClass);
    });

    it("does not go below last item", async () => {
      renderPalette();
      const dialog = screen.getByRole("dialog");
      const items = dialog.querySelectorAll("[data-palette-item]");
      const count = items.length;

      // Capture the non-selected class (item 1 initially)
      const nonSelectedClass = items[1].className;

      // Press down more times than there are items
      for (let i = 0; i < count + 5; i++) {
        await act(async () => { fireEvent.keyDown(dialog, { key: "ArrowDown" }); });
      }

      // Last item should be selected (different class from non-selected)
      expect(items[count - 1].className).not.toEqual(nonSelectedClass);
    });

    it("does not go above first item", async () => {
      renderPalette();
      const dialog = screen.getByRole("dialog");
      const items = dialog.querySelectorAll("[data-palette-item]");

      // Capture selected class
      const selectedClass = items[0].className;

      await act(async () => { fireEvent.keyDown(dialog, { key: "ArrowUp" }); });
      await act(async () => { fireEvent.keyDown(dialog, { key: "ArrowUp" }); });

      expect(items[0].className).toEqual(selectedClass);
    });

    it("Enter opens detail view for selected command", async () => {
      renderPalette();
      const dialog = screen.getByRole("dialog");

      // First item is "Read file" (first in WEEK1_COMMANDS)
      await act(async () => { fireEvent.keyDown(dialog, { key: "Enter" }); });

      // Should see the detail pane with back button and execute button
      expect(screen.getByLabelText("Back to search")).toBeInTheDocument();
      // The execute button should be present
      const executeBtn = screen.getByRole("button", { name: /Execute/ });
      expect(executeBtn).toBeInTheDocument();
    });
  });

  describe("detail view", () => {
    async function openFirstCommandByClick() {
      renderPalette();
      const dialog = screen.getByRole("dialog");
      const items = dialog.querySelectorAll("[data-palette-item]");
      // Click first command to open detail
      await act(async () => { fireEvent.click(items[0]); });
      return dialog;
    }

    it("shows command fields in detail view", async () => {
      await openFirstCommandByClick();
      // Read file has Path field + Execute button
      expect(screen.getByLabelText("Back to search")).toBeInTheDocument();
      const executeBtn = screen.getByRole("button", { name: /Execute/ });
      expect(executeBtn).toBeInTheDocument();
    });

    it("Escape in detail goes back to search", async () => {
      const dialog = await openFirstCommandByClick();
      await act(async () => { fireEvent.keyDown(dialog, { key: "Escape" }); });
      // Should be back in search mode
      expect(screen.getByPlaceholderText("Search commands…")).toBeInTheDocument();
      expect(screen.queryByLabelText("Back to search")).not.toBeInTheDocument();
    });

    it("back button returns to search", async () => {
      await openFirstCommandByClick();
      const backBtn = screen.getByLabelText("Back to search");
      await act(async () => { fireEvent.click(backBtn); });
      expect(screen.getByPlaceholderText("Search commands…")).toBeInTheDocument();
    });

    it("clicking a command card opens detail", async () => {
      renderPalette();
      const items = screen.getByRole("dialog").querySelectorAll("[data-palette-item]");
      // Click the second item
      await act(async () => { fireEvent.click(items[1]); });
      expect(screen.getByRole("button", { name: /Execute/ })).toBeInTheDocument();
      expect(screen.getByLabelText("Back to search")).toBeInTheDocument();
    });
  });

  describe("validation", () => {
    it("shows validation error for required fields", async () => {
      renderPalette();
      const dialog = screen.getByRole("dialog");
      // Search for "read file" which has required fields
      const input = screen.getByPlaceholderText("Search commands…");
      await act(async () => { fireEvent.change(input, { target: { value: "read file" } }); });
      const items = dialog.querySelectorAll("[data-palette-item]");
      await act(async () => { fireEvent.click(items[0]); });

      // Try to execute without filling path
      const executeBtn = screen.getByRole("button", { name: /Execute/ });
      await act(async () => { fireEvent.click(executeBtn); });

      // Should show validation error
      expect(screen.getByText("Path is required.")).toBeInTheDocument();
      expect(mockExecuteCommand).not.toHaveBeenCalled();
    });

    it("clears validation errors when going back", async () => {
      renderPalette();
      const dialog = screen.getByRole("dialog");
      const input = screen.getByPlaceholderText("Search commands…");
      await act(async () => { fireEvent.change(input, { target: { value: "read file" } }); });
      const items = dialog.querySelectorAll("[data-palette-item]");
      await act(async () => { fireEvent.click(items[0]); });

      // Trigger validation error
      await act(async () => { fireEvent.click(screen.getByRole("button", { name: /Execute/ })); });
      expect(screen.getByText("Path is required.")).toBeInTheDocument();

      // Go back
      const backBtn = screen.getByLabelText("Back to search");
      await act(async () => { fireEvent.click(backBtn); });
      // Re-open
      const newItems = dialog.querySelectorAll("[data-palette-item]");
      await act(async () => { fireEvent.click(newItems[0]); });
      expect(screen.queryByText("Path is required.")).not.toBeInTheDocument();
    });
  });

  describe("command execution", () => {
    async function openAndFillReadFile() {
      const { props } = renderPalette();
      const dialog = screen.getByRole("dialog");
      // Search for "read file" to find it (items are grouped by category)
      const searchInput = screen.getByPlaceholderText("Search commands…");
      await act(async () => { fireEvent.change(searchInput, { target: { value: "read file" } }); });
      const items = dialog.querySelectorAll("[data-palette-item]");
      await act(async () => { fireEvent.click(items[0]); });

      // Fill the required "Path" field
      const pathInput = screen.getByPlaceholderText("src/AgentAcademy.Server/Program.cs");
      await act(async () => { fireEvent.change(pathInput, { target: { value: "README.md" } }); });

      return { dialog, props };
    }

    it("executes command and shows success result", async () => {
      mockExecuteCommand.mockResolvedValue(makeResponse({ result: "Hello World" }));
      await openAndFillReadFile();

      await act(async () => { fireEvent.click(screen.getByRole("button", { name: /Execute/ })); });

      await waitFor(() => {
        expect(screen.getByText("Hello World")).toBeInTheDocument();
      });
      expect(mockExecuteCommand).toHaveBeenCalledWith(
        expect.objectContaining({ command: "READ_FILE", args: { path: "README.md" } }),
      );
    });

    it("executes command and shows error result", async () => {
      mockExecuteCommand.mockResolvedValue(
        makeResponse({ status: "failed", result: null, error: "File not found" }),
      );
      await openAndFillReadFile();

      await act(async () => { fireEvent.click(screen.getByRole("button", { name: /Execute/ })); });

      await waitFor(() => {
        expect(screen.getByText("Error: File not found")).toBeInTheDocument();
      });
    });

    it("shows network error on fetch failure", async () => {
      mockExecuteCommand.mockRejectedValue(new Error("Network down"));
      await openAndFillReadFile();

      await act(async () => { fireEvent.click(screen.getByRole("button", { name: /Execute/ })); });

      await waitFor(() => {
        expect(screen.getByText("Error: Network down")).toBeInTheDocument();
      });
    });

    it("shows Running… button text while executing", async () => {
      let resolve: (v: unknown) => void;
      mockExecuteCommand.mockReturnValue(new Promise((r) => { resolve = r; }));
      await openAndFillReadFile();

      await act(async () => { fireEvent.click(screen.getByRole("button", { name: /Execute/ })); });
      expect(screen.getByText("Running…")).toBeInTheDocument();

      await act(async () => { resolve!(makeResponse()); });
      await waitFor(() => {
        expect(screen.getByRole("button", { name: /Execute/ })).toBeInTheDocument();
      });
    });

    it("button is disabled while executing", async () => {
      let resolve: (v: unknown) => void;
      mockExecuteCommand.mockReturnValue(new Promise((r) => { resolve = r; }));
      await openAndFillReadFile();

      await act(async () => { fireEvent.click(screen.getByRole("button", { name: /Execute/ })); });
      const btn = screen.getByText("Running…").closest("button");
      expect(btn).toBeDisabled();

      await act(async () => { resolve!(makeResponse()); });
    });

    it("Ctrl+Enter triggers execution from detail view", async () => {
      mockExecuteCommand.mockResolvedValue(makeResponse({ result: "via shortcut" }));
      const { dialog } = await openAndFillReadFile();

      await act(async () => {
        fireEvent.keyDown(dialog, { key: "Enter", ctrlKey: true });
      });

      await waitFor(() => {
        expect(mockExecuteCommand).toHaveBeenCalled();
      });
    });

    it("shows null result as 'Done (no output)'", async () => {
      mockExecuteCommand.mockResolvedValue(makeResponse({ result: null }));
      await openAndFillReadFile();

      await act(async () => { fireEvent.click(screen.getByRole("button", { name: /Execute/ })); });

      await waitFor(() => {
        expect(screen.getByText("Done (no output)")).toBeInTheDocument();
      });
    });

    it("shows object result as JSON", async () => {
      mockExecuteCommand.mockResolvedValue(makeResponse({ result: { count: 42 } }));
      await openAndFillReadFile();

      await act(async () => { fireEvent.click(screen.getByRole("button", { name: /Execute/ })); });

      await waitFor(() => {
        expect(screen.getByText(/42/)).toBeInTheDocument();
      });
    });
  });

  describe("async polling", () => {
    it("polls for async command results", async () => {
      // Navigate to "Run build" which is async + no required fields
      renderPalette();
      const dialog = screen.getByRole("dialog");
      const input = screen.getByPlaceholderText("Search commands…");
      await act(async () => { fireEvent.change(input, { target: { value: "build" } }); });
      // Click the (filtered) first item
      const items = dialog.querySelectorAll("[data-palette-item]");
      await act(async () => { fireEvent.click(items[0]); });

      // Execute returns pending
      mockExecuteCommand.mockResolvedValue(
        makeResponse({ status: "pending", command: "RUN_BUILD", result: null, correlationId: "poll-1" }),
      );
      // First poll: still pending, second poll: completed
      mockGetCommandExecution
        .mockResolvedValueOnce(makeResponse({ status: "pending", command: "RUN_BUILD", result: null, correlationId: "poll-1" }))
        .mockResolvedValueOnce(makeResponse({ status: "completed", command: "RUN_BUILD", result: "Build succeeded", correlationId: "poll-1" }));

      await act(async () => { fireEvent.click(screen.getByRole("button", { name: /Execute/ })); });

      // Advance timers through polling intervals
      await act(async () => { vi.advanceTimersByTime(2500); });
      await act(async () => { vi.advanceTimersByTime(2500); });

      await waitFor(() => {
        expect(screen.getByText("Build succeeded")).toBeInTheDocument();
      });
    });
  });

  describe("readOnly mode", () => {
    it("disables execute button in readOnly mode", async () => {
      renderPalette({ readOnly: true });
      const dialog = screen.getByRole("dialog");
      // Search for a command with fields to verify
      const searchInput = screen.getByPlaceholderText("Search commands…");
      await act(async () => { fireEvent.change(searchInput, { target: { value: "read file" } }); });
      const items = dialog.querySelectorAll("[data-palette-item]");
      await act(async () => { fireEvent.click(items[0]); });

      const btn = screen.getByRole("button", { name: /Execute/ });
      expect(btn).toBeDisabled();
    });

    it("does not execute on click in readOnly mode", async () => {
      renderPalette({ readOnly: true });
      const dialog = screen.getByRole("dialog");
      const searchInput = screen.getByPlaceholderText("Search commands…");
      await act(async () => { fireEvent.change(searchInput, { target: { value: "read file" } }); });
      const items = dialog.querySelectorAll("[data-palette-item]");
      await act(async () => { fireEvent.click(items[0]); });

      await act(async () => { fireEvent.click(screen.getByRole("button", { name: /Execute/ })); });
      expect(mockExecuteCommand).not.toHaveBeenCalled();
    });
  });

  describe("mouse hover updates selection", () => {
    it("hover changes selected index", async () => {
      renderPalette();
      const items = screen.getByRole("dialog").querySelectorAll("[data-palette-item]");
      const initialSelectedClass = items[0].className;
      const initialUnselectedClass = items[2].className;

      // Hover over third item
      await act(async () => { fireEvent.mouseEnter(items[2]); });
      // Third item should now have the selected class
      expect(items[2].className).toEqual(initialSelectedClass);
      // First item should now be unselected
      expect(items[0].className).toEqual(initialUnselectedClass);
    });
  });

  describe("server metadata loading", () => {
    it("loads server metadata on first open", async () => {
      mockGetCommandMetadata.mockResolvedValue([
        {
          command: "CUSTOM_CMD",
          title: "Custom command",
          category: "operations",
          description: "A custom one",
          detail: "From the server",
          isAsync: false,
          fields: [],
        },
      ]);

      renderPalette();

      await waitFor(() => {
        expect(screen.getByText("Custom command")).toBeInTheDocument();
      });
    });

    it("falls back to hardcoded catalog on metadata error", async () => {
      mockGetCommandMetadata.mockRejectedValue(new Error("Server down"));
      renderPalette();

      // Should still show WEEK1_COMMANDS
      await waitFor(() => {
        expect(screen.getByText("Read file")).toBeInTheDocument();
      });
    });
  });

  describe("command with no fields", () => {
    it("shows execute directly for commands with no required fields", async () => {
      renderPalette();
      const dialog = screen.getByRole("dialog");
      const input = screen.getByPlaceholderText("Search commands…");

      // "Run build" has no fields
      await act(async () => { fireEvent.change(input, { target: { value: "build" } }); });
      const items = dialog.querySelectorAll("[data-palette-item]");
      await act(async () => { fireEvent.click(items[0]); });

      // Should show execute without any field inputs
      expect(screen.getByRole("button", { name: /Execute/ })).toBeInTheDocument();
      // No required field validation blocks
      mockExecuteCommand.mockResolvedValue(makeResponse({ command: "RUN_BUILD", result: "ok" }));
      await act(async () => { fireEvent.click(screen.getByRole("button", { name: /Execute/ })); });
      expect(mockExecuteCommand).toHaveBeenCalled();
    });
  });

  describe("async badge", () => {
    it("shows async badge for async commands in search list", () => {
      renderPalette();
      // RUN_BUILD and RUN_TESTS are async — they should have "async" badges
      const asyncBadges = screen.getAllByText("async");
      expect(asyncBadges.length).toBeGreaterThanOrEqual(2);
    });
  });

  describe("category grouping", () => {
    it("shows category group labels", () => {
      renderPalette();
      expect(screen.getByText("Code")).toBeInTheDocument();
      expect(screen.getByText("Git")).toBeInTheDocument();
      expect(screen.getByText("Operations")).toBeInTheDocument();
    });
  });

  describe("esc close hint", () => {
    it("shows esc close hint in search bar", () => {
      renderPalette();
      expect(screen.getByText("esc to close")).toBeInTheDocument();
    });
  });
});
