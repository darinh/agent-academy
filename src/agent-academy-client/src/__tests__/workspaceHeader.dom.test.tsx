// @vitest-environment jsdom
/**
 * DOM-based RTL tests for WorkspaceHeader.
 *
 * Covers: title display, rename button visibility, edit mode flow (enter/cancel/save),
 * keyboard shortcuts (Enter to save, Escape to cancel), state reset on title change,
 * saving spinner state, signals (limited mode, circuit breaker, connected pill),
 * and meta text display.
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
import { afterEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

import WorkspaceHeader from "../WorkspaceHeader";
import type { HeaderModel } from "../WorkspaceHeader";

// ── Factories ──────────────────────────────────────────────────────────

function makeModel(overrides: Partial<HeaderModel> = {}): HeaderModel {
  return {
    title: "Main Room",
    meta: null,
    showPhasePill: false,
    workspaceLimited: false,
    degradedEyebrow: null,
    circuitBreakerState: "Closed",
    canRename: false,
    ...overrides,
  };
}

const mockStyles: Record<string, string> = {
  workspaceHeader: "workspaceHeader",
  workspaceHeaderBody: "workspaceHeaderBody",
  workspaceHeaderTopRow: "workspaceHeaderTopRow",
  workspaceTitle: "workspaceTitle",
  workspaceMetaText: "workspaceMetaText",
  headerDivider: "headerDivider",
  workspaceHeaderSignals: "workspaceHeaderSignals",
  workspaceSignal: "workspaceSignal",
  workspaceSignalWarning: "workspaceSignalWarning",
  phasePill: "phasePill",
  phasePillDot: "phasePillDot",
};

function renderHeader(model: HeaderModel) {
  return render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(WorkspaceHeader, { model, styles: mockStyles }),
    ),
  );
}

// ── Setup ──────────────────────────────────────────────────────────────

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

// ── Tests ──────────────────────────────────────────────────────────────

describe("WorkspaceHeader", () => {
  describe("title display", () => {
    it("renders the title", () => {
      renderHeader(makeModel());
      expect(screen.getByText("Main Room")).toBeInTheDocument();
    });

    it("does not show edit button when canRename is false", () => {
      renderHeader(makeModel({ canRename: false }));
      expect(screen.queryByTitle("Rename room")).not.toBeInTheDocument();
    });

    it("shows edit button when canRename is true", () => {
      renderHeader(makeModel({ canRename: true }));
      expect(screen.getByTitle("Rename room")).toBeInTheDocument();
    });
  });

  describe("rename flow", () => {
    const onRename = vi.fn();

    it("enters edit mode on edit button click", () => {
      renderHeader(makeModel({ canRename: true, onRename }));
      fireEvent.click(screen.getByTitle("Rename room"));
      // Input should appear with current title
      const input = screen.getByRole("textbox");
      expect(input).toBeInTheDocument();
      expect(input).toHaveValue("Main Room");
    });

    it("saves on Enter key", async () => {
      onRename.mockResolvedValueOnce(undefined);
      renderHeader(makeModel({ canRename: true, onRename }));
      fireEvent.click(screen.getByTitle("Rename room"));

      const input = screen.getByRole("textbox");
      await userEvent.clear(input);
      await userEvent.type(input, "New Name{Enter}");

      await waitFor(() => {
        expect(onRename).toHaveBeenCalledWith("New Name");
      });
    });

    it("cancels on Escape key", async () => {
      renderHeader(makeModel({ canRename: true, onRename }));
      fireEvent.click(screen.getByTitle("Rename room"));

      const input = screen.getByRole("textbox");
      await userEvent.clear(input);
      await userEvent.type(input, "Draft Name{Escape}");

      // Should exit edit mode and show original title
      expect(screen.queryByRole("textbox")).not.toBeInTheDocument();
      expect(screen.getByText("Main Room")).toBeInTheDocument();
      expect(onRename).not.toHaveBeenCalled();
    });

    it("cancels on Cancel button click", () => {
      renderHeader(makeModel({ canRename: true, onRename }));
      fireEvent.click(screen.getByTitle("Rename room"));
      fireEvent.click(screen.getByTitle("Cancel"));

      expect(screen.queryByRole("textbox")).not.toBeInTheDocument();
      expect(screen.getByText("Main Room")).toBeInTheDocument();
    });

    it("saves on Save button click", async () => {
      onRename.mockResolvedValueOnce(undefined);
      renderHeader(makeModel({ canRename: true, onRename }));
      fireEvent.click(screen.getByTitle("Rename room"));

      const input = screen.getByRole("textbox");
      await userEvent.clear(input);
      await userEvent.type(input, "Renamed Room");
      fireEvent.click(screen.getByTitle("Save"));

      await waitFor(() => {
        expect(onRename).toHaveBeenCalledWith("Renamed Room");
      });
    });

    it("does not save if draft is empty (trims to empty)", async () => {
      renderHeader(makeModel({ canRename: true, onRename }));
      fireEvent.click(screen.getByTitle("Rename room"));

      const input = screen.getByRole("textbox");
      await userEvent.clear(input);
      await userEvent.type(input, "   {Enter}");

      // Should cancel instead
      expect(onRename).not.toHaveBeenCalled();
      expect(screen.queryByRole("textbox")).not.toBeInTheDocument();
    });

    it("does not save if draft equals current title", async () => {
      renderHeader(makeModel({ canRename: true, onRename }));
      fireEvent.click(screen.getByTitle("Rename room"));

      // Don't change the value, just press Enter
      fireEvent.keyDown(screen.getByRole("textbox"), { key: "Enter" });

      expect(onRename).not.toHaveBeenCalled();
    });

    it("stays in edit mode on save error", async () => {
      onRename.mockRejectedValueOnce(new Error("Rename failed"));
      renderHeader(makeModel({ canRename: true, onRename }));
      fireEvent.click(screen.getByTitle("Rename room"));

      const input = screen.getByRole("textbox");
      await userEvent.clear(input);
      await userEvent.type(input, "Bad Name{Enter}");

      await waitFor(() => {
        expect(onRename).toHaveBeenCalled();
      });

      // Should still be in edit mode
      expect(screen.getByRole("textbox")).toBeInTheDocument();
    });

    it("resets edit state when model.title changes (room switch)", () => {
      const { rerender } = renderHeader(makeModel({ canRename: true, onRename }));
      fireEvent.click(screen.getByTitle("Rename room"));
      expect(screen.getByRole("textbox")).toBeInTheDocument();

      // Simulate room change by re-rendering with new title
      rerender(
        createElement(
          FluentProvider,
          { theme: webDarkTheme },
          createElement(WorkspaceHeader, {
            model: makeModel({ canRename: true, onRename, title: "Other Room" }),
            styles: mockStyles,
          }),
        ),
      );

      // Should exit edit mode and show new title
      expect(screen.queryByRole("textbox")).not.toBeInTheDocument();
      expect(screen.getByText("Other Room")).toBeInTheDocument();
    });
  });

  describe("meta text", () => {
    it("renders meta when provided", () => {
      renderHeader(makeModel({ meta: "3 agents • 12 messages" }));
      expect(screen.getByText("3 agents • 12 messages")).toBeInTheDocument();
    });

    it("hides meta when null", () => {
      renderHeader(makeModel({ meta: null }));
      expect(screen.queryByText("agents")).not.toBeInTheDocument();
    });

    it("hides meta in edit mode", () => {
      renderHeader(makeModel({ meta: "some meta", canRename: true }));
      fireEvent.click(screen.getByTitle("Rename room"));
      // Meta should be hidden while editing
      expect(screen.queryByText("some meta")).not.toBeInTheDocument();
    });
  });

  describe("signals", () => {
    it("shows limited mode when workspaceLimited", () => {
      renderHeader(makeModel({ workspaceLimited: true }));
      expect(screen.getByText("Limited mode")).toBeInTheDocument();
    });

    it("shows custom degradedEyebrow when provided", () => {
      renderHeader(makeModel({ workspaceLimited: true, degradedEyebrow: "Rate limited" }));
      expect(screen.getByText("Rate limited")).toBeInTheDocument();
    });

    it("shows circuit breaker warning when Open", () => {
      renderHeader(makeModel({ circuitBreakerState: "Open" }));
      expect(screen.getByText("Circuit open")).toBeInTheDocument();
    });

    it("shows circuit breaker probing when HalfOpen", () => {
      renderHeader(makeModel({ circuitBreakerState: "HalfOpen" }));
      expect(screen.getByText("Circuit probing")).toBeInTheDocument();
    });

    it("shows no circuit breaker signal when Closed", () => {
      renderHeader(makeModel({ circuitBreakerState: "Closed" }));
      expect(screen.queryByText(/Circuit/)).not.toBeInTheDocument();
    });

    it("shows Connected pill when showPhasePill is true", () => {
      renderHeader(makeModel({ showPhasePill: true }));
      expect(screen.getByText("Connected")).toBeInTheDocument();
    });

    it("hides Connected pill when showPhasePill is false", () => {
      renderHeader(makeModel({ showPhasePill: false }));
      expect(screen.queryByText("Connected")).not.toBeInTheDocument();
    });
  });
});
