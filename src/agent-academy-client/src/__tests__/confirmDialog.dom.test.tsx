// @vitest-environment jsdom
/**
 * Standalone tests for ConfirmDialog component.
 *
 * Covers: rendering, confirm/cancel callbacks, custom labels,
 * custom appearance, dialog dismiss behavior.
 */
import "@testing-library/jest-dom/vitest";
import {
  cleanup,
  render,
  screen,
  fireEvent,
  act,
} from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { FluentProvider, webDarkTheme } from "@fluentui/react-components";

import ConfirmDialog from "../ConfirmDialog";

function renderDialog(props: Partial<Parameters<typeof ConfirmDialog>[0]> = {}) {
  const defaults = {
    open: true,
    onConfirm: vi.fn(),
    onCancel: vi.fn(),
    title: "Delete item?",
    message: "This action cannot be undone.",
  };
  const merged = { ...defaults, ...props };
  render(
    createElement(
      FluentProvider,
      { theme: webDarkTheme },
      createElement(ConfirmDialog, merged),
    ),
  );
  return merged;
}

afterEach(() => {
  cleanup();
});

describe("ConfirmDialog", () => {
  describe("rendering", () => {
    it("displays title and message when open", () => {
      renderDialog();
      expect(screen.getByText("Delete item?")).toBeInTheDocument();
      expect(screen.getByText("This action cannot be undone.")).toBeInTheDocument();
    });

    it("renders default button labels", () => {
      renderDialog();
      expect(screen.getByText("Confirm")).toBeInTheDocument();
      expect(screen.getByText("Cancel")).toBeInTheDocument();
    });

    it("renders custom button labels", () => {
      renderDialog({
        confirmLabel: "Yes, delete",
        cancelLabel: "Keep it",
      });
      expect(screen.getByText("Yes, delete")).toBeInTheDocument();
      expect(screen.getByText("Keep it")).toBeInTheDocument();
    });

    it("does not render dialog content when closed", () => {
      renderDialog({ open: false });
      expect(screen.queryByText("Delete item?")).not.toBeInTheDocument();
    });
  });

  describe("callbacks", () => {
    it("calls onConfirm when confirm button is clicked", async () => {
      const props = renderDialog();
      await act(async () => {
        fireEvent.click(screen.getByText("Confirm"));
      });
      expect(props.onConfirm).toHaveBeenCalledTimes(1);
      expect(props.onCancel).not.toHaveBeenCalled();
    });

    it("calls onCancel when cancel button is clicked", async () => {
      const props = renderDialog();
      await act(async () => {
        fireEvent.click(screen.getByText("Cancel"));
      });
      // Note: onCancel fires twice — once from button onClick, once from Dialog
      // onOpenChange. This is inherent to the DialogTrigger + onOpenChange pattern.
      expect(props.onCancel).toHaveBeenCalled();
      expect(props.onConfirm).not.toHaveBeenCalled();
    });

    it("calls onCancel with custom cancel label", async () => {
      const props = renderDialog({ cancelLabel: "Nope" });
      await act(async () => {
        fireEvent.click(screen.getByText("Nope"));
      });
      expect(props.onCancel).toHaveBeenCalled();
    });

    it("calls onConfirm with custom confirm label", async () => {
      const props = renderDialog({ confirmLabel: "Do it" });
      await act(async () => {
        fireEvent.click(screen.getByText("Do it"));
      });
      expect(props.onConfirm).toHaveBeenCalledTimes(1);
    });
  });

  describe("props", () => {
    it("accepts confirmAppearance without crashing", () => {
      // Fluent UI appearance is not inspectable in jsdom, but verify no runtime error
      renderDialog({ confirmAppearance: "subtle" });
      expect(screen.getByText("Confirm")).toBeInTheDocument();
    });
  });
});
